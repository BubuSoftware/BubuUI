using System;
using System.Collections.Generic;
using System.Linq;
using EclipseUI.Elements;

namespace EclipseUI;

/// <summary>
/// 块内容的一次实例化：根元素、按作用域重跑的渲染动作、随作用域释放的资源。
/// 由块创建并传入模板；模板只通过 AddRoot/AddRenderAction/AddDisposable 登记。
/// </summary>
public sealed class BlockScope
{
    private readonly List<Element> _roots = new();
    private readonly List<Action> _renderActions = new();
    private readonly List<IDisposable> _disposables = new();

    internal BlockScope() { }

    public IReadOnlyList<Element> Roots => _roots;
    public IReadOnlyList<Action> RenderActions => _renderActions;
    public IReadOnlyList<IDisposable> Disposables => _disposables;

    public void AddRoot(Element root)
    {
        ArgumentNullException.ThrowIfNull(root);
        _roots.Add(root);
    }

    public void AddRenderAction(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _renderActions.Add(action);
    }

    public void AddDisposable(IDisposable disposable)
    {
        ArgumentNullException.ThrowIfNull(disposable);
        _disposables.Add(disposable);
    }

    internal void RunRenderActions()
    {
        for (int i = 0; i < _renderActions.Count; i++)
            _renderActions[i]();
    }

    /// <summary>逆序释放作用域内资源；根元素由元素树/块持有，不在这里 Dispose。</summary>
    internal void DisposeResources()
    {
        for (int i = _disposables.Count - 1; i >= 0; i--)
            _disposables[i].Dispose();
        _disposables.Clear();
        _renderActions.Clear();
        _roots.Clear();
    }
}

/// <summary>
/// @if 的块协调器：条件翻转时卸载旧作用域并重建内容；条件不变时只重跑渲染动作。
/// setter 只保存，首次求值由生成代码显式 <see cref="Refresh"/> 完成。
/// </summary>
[UiIgnore]
public sealed class ConditionalBlock : StackPanelBase, IDisposable
{
    /// <inheritdoc />
    public override bool IsVisualTransparent => true;

    private Func<bool>? _condition;
    private Action<BlockScope>? _contentBuilder;
    private BlockScope? _scope;
    private bool _active;
    private bool _disposed;

    public void SetCondition(Func<bool> condition) => _condition = condition;

    public void SetContent(Action<BlockScope> contentBuilder) => _contentBuilder = contentBuilder;

    public void Refresh()
    {
        if (_disposed || _condition is null) return;

        var active = _condition();
        if (active != _active)
        {
            _active = active;
            try
            {
                Rebuild();
            }
            catch
            {
                // 构建失败不留下“已激活但无内容”的状态；下次 Refresh 可重试。
                _active = false;
                throw;
            }
            return;
        }

        if (active)
            _scope?.RunRenderActions();
    }

    private void Rebuild()
    {
        DisposeScope();
        Children.Clear();
        if (!_active || _contentBuilder is null) return;

        var scope = new BlockScope();
        try
        {
            _contentBuilder(scope);
        }
        catch
        {
            scope.DisposeResources();
            throw;
        }

        _scope = scope;
        foreach (var root in scope.Roots)
            Children.Add(root);
    }

    private void DisposeScope()
    {
        _scope?.DisposeResources();
        _scope = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisposeScope();
        Children.Clear();
    }
}

/// <summary>
/// @foreach 的块协调器：Key 对齐 + item 身份复用；条件不变时只重跑存活条目的渲染动作。
/// setter 只保存，首次枚举由生成代码显式 <see cref="Refresh"/> 完成。
/// </summary>
[UiIgnore]
public sealed class ForEachBlock<T> : StackPanelBase, IDisposable
{
    /// <inheritdoc />
    public override bool IsVisualTransparent => true;

    private sealed class Entry
    {
        public Entry(object key, T item, BlockScope scope)
        {
            Key = key;
            Item = item;
            Scope = scope;
        }

        public object Key { get; }
        public T Item { get; }
        public BlockScope Scope { get; }
    }

    private static readonly object s_nullKey = new();

    private Func<IEnumerable<T>?>? _itemsGetter;
    private Func<T, object?>? _keySelector;
    private Action<T, BlockScope>? _template;
    private readonly List<Entry> _entries = new();
    private bool _disposed;

    public void SetItems(Func<IEnumerable<T>?> itemsGetter) => _itemsGetter = itemsGetter;

    public void SetKeySelector(Func<T, object?> keySelector) => _keySelector = keySelector;

    public void SetTemplate(Action<T, BlockScope> template) => _template = template;

    public void Refresh()
    {
        if (_disposed || _itemsGetter is null || _template is null) return;

        var source = _itemsGetter();
        var items = source is null ? new List<T>() : source.ToList();

        var freeByKey = new Dictionary<object, Queue<Entry>>();
        foreach (var entry in _entries)
        {
            if (!freeByKey.TryGetValue(entry.Key, out var queue))
                freeByKey[entry.Key] = queue = new Queue<Entry>();
            queue.Enqueue(entry);
        }

        var next = new List<Entry>(items.Count);
        foreach (var item in items)
        {
            var key = KeyOf(item);
            if (freeByKey.TryGetValue(key, out var queue) && queue.Count > 0)
            {
                var entry = queue.Dequeue();
                if (ItemEquals(entry.Item, item))
                {
                    entry.Scope.RunRenderActions();
                    next.Add(entry);
                    continue;
                }

                // 同 Key 不同 item：旧闭包捕获的 item 已失效，必须重建
                entry.Scope.DisposeResources();
            }

            next.Add(CreateEntry(item, key));
        }

        foreach (var queue in freeByKey.Values)
            foreach (var leftover in queue)
                leftover.Scope.DisposeResources();

        _entries.Clear();
        _entries.AddRange(next);
        UpdateChildren();
    }

    private Entry CreateEntry(T item, object key)
    {
        var scope = new BlockScope();
        try
        {
            _template!(item, scope);
        }
        catch
        {
            scope.DisposeResources();
            throw;
        }

        return new Entry(key, item, scope);
    }

    private object KeyOf(T item)
    {
        if (_keySelector is null)
            return (object?)item ?? s_nullKey;
        return _keySelector(item) ?? s_nullKey;
    }

    private static bool ItemEquals(T a, T b)
    {
        if (typeof(T).IsValueType)
            return EqualityComparer<T>.Default.Equals(a, b);
        return ReferenceEquals(a, b);
    }

    /// <summary>根序列实际变化时才改 Children，避免纯属性刷新触发额外布局。</summary>
    private void UpdateChildren()
    {
        int total = 0;
        foreach (var entry in _entries)
            total += entry.Scope.Roots.Count;

        bool same = total == Children.Count;
        if (same)
        {
            int index = 0;
            foreach (var entry in _entries)
            {
                foreach (var root in entry.Scope.Roots)
                {
                    if (!ReferenceEquals(Children[index++], root))
                    {
                        same = false;
                        break;
                    }
                }

                if (!same) break;
            }
        }

        if (same) return;

        Children.Clear();
        foreach (var entry in _entries)
            foreach (var root in entry.Scope.Roots)
                Children.Add(root);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var entry in _entries)
            entry.Scope.DisposeResources();
        _entries.Clear();
        Children.Clear();
    }
}

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
/// S6（K23 ③ / D87）扩展多分支：<see cref="AddBranch"/>（else if）与 <see cref="SetDefault"/>（else），
/// Refresh 取首个为真分支；单分支 SetCondition/SetContent 形态不变（golden 稳定）。
/// </summary>
[UiIgnore]
public sealed class ConditionalBlock : StackPanelBase, IDisposable
{
    /// <inheritdoc />
    public override bool IsVisualTransparent => true;

    /// <inheritdoc />
    public override bool IsLayoutTransparent => true;

    private Func<bool>? _condition;
    private Action<BlockScope>? _contentBuilder;
    private readonly List<(Func<bool> Condition, Action<BlockScope> Builder)> _branches = new();
    private Action<BlockScope>? _defaultBuilder;
    private BlockScope? _scope;
    private int _activeIndex = -1;
    private bool _disposed;

    public void SetCondition(Func<bool> condition) => _condition = condition;

    public void SetContent(Action<BlockScope> contentBuilder) => _contentBuilder = contentBuilder;

    /// <summary>追加一个 else if 分支（K23 ③）；按源码顺序判定，首个为真者胜。</summary>
    public void AddBranch(Func<bool> condition, Action<BlockScope> contentBuilder)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(contentBuilder);
        _branches.Add((condition, contentBuilder));
    }

    /// <summary>设置 else 分支（K23 ③）；所有分支为假时激活。至多一个，重复设置覆盖。</summary>
    public void SetDefault(Action<BlockScope> contentBuilder) => _defaultBuilder = contentBuilder;

    public void Refresh()
    {
        if (_disposed) return;

        int active;
        if (_branches.Count > 0)
        {
            active = -1;
            for (int i = 0; i < _branches.Count; i++)
            {
                if (_branches[i].Condition())
                {
                    active = i;
                    break;
                }
            }
            if (active < 0 && _defaultBuilder is not null)
                active = _branches.Count; // default 分支占索引 N
        }
        else
        {
            if (_condition is null) return;
            active = _condition() ? 0 : -1;
        }

        if (active != _activeIndex)
        {
            _activeIndex = active;
            try
            {
                Rebuild();
            }
            catch
            {
                // 构建失败不留下“已激活但无内容”的状态；下次 Refresh 可重试。
                _activeIndex = -1;
                throw;
            }
            return;
        }

        if (active >= 0)
            _scope?.RunRenderActions();
    }

    private void Rebuild()
    {
        DisposeScope();
        Children.Clear();
        Action<BlockScope>? builder;
        if (_branches.Count > 0)
            builder = _activeIndex < 0
                ? null
                : _activeIndex == _branches.Count ? _defaultBuilder : _branches[_activeIndex].Builder;
        else
            builder = _activeIndex >= 0 ? _contentBuilder : null;
        if (builder is null) return;

        var scope = new BlockScope();
        try
        {
            builder(scope);
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
/// @switch 的块协调器（S6，K29 / D87）：SetSelector 求值后按 EqualityComparer&lt;T&gt;.Default
/// 顺序匹配 AddCase 注册的常量 key；无匹配走 default（缺省时块为空）。分支切换销毁/重建，
/// 不变时只重跑渲染动作；无贯穿语义——每个 case 是独立分支。setter 只保存，首次求值由
/// 生成代码显式 <see cref="Refresh"/> 完成。
/// </summary>
[UiIgnore]
public sealed class SwitchBlock<T> : StackPanelBase, IDisposable
{
    /// <inheritdoc />
    public override bool IsVisualTransparent => true;

    /// <inheritdoc />
    public override bool IsLayoutTransparent => true;

    private Func<T>? _selector;
    private readonly List<(T Key, Action<BlockScope> Builder)> _cases = new();
    private Action<BlockScope>? _defaultBuilder;
    private BlockScope? _scope;
    private int _activeIndex = -1;
    private bool _disposed;

    public void SetSelector(Func<T> selector) => _selector = selector;

    /// <summary>追加一个 case 分支（K29：key 是编译期常量表达式，Build 时注册）。</summary>
    public void AddCase(T key, Action<BlockScope> contentBuilder)
    {
        ArgumentNullException.ThrowIfNull(contentBuilder);
        _cases.Add((key, contentBuilder));
    }

    /// <summary>设置 default 分支；无匹配 key 时激活。至多一个，重复设置覆盖。</summary>
    public void SetDefault(Action<BlockScope> contentBuilder) => _defaultBuilder = contentBuilder;

    public void Refresh()
    {
        if (_disposed || _selector is null) return;

        var value = _selector();
        int active = -1;
        for (int i = 0; i < _cases.Count; i++)
        {
            if (EqualityComparer<T>.Default.Equals(_cases[i].Key, value))
            {
                active = i;
                break;
            }
        }
        if (active < 0 && _defaultBuilder is not null)
            active = _cases.Count; // default 分支占索引 N

        if (active != _activeIndex)
        {
            _activeIndex = active;
            try
            {
                Rebuild();
            }
            catch
            {
                _activeIndex = -1;
                throw;
            }
            return;
        }

        if (active >= 0)
            _scope?.RunRenderActions();
    }

    private void Rebuild()
    {
        DisposeScope();
        Children.Clear();
        var builder = _activeIndex < 0
            ? null
            : _activeIndex == _cases.Count ? _defaultBuilder : _cases[_activeIndex].Builder;
        if (builder is null) return;

        var scope = new BlockScope();
        try
        {
            builder(scope);
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

    /// <inheritdoc />
    public override bool IsLayoutTransparent => true;

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

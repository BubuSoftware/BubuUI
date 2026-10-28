using System;
using System.Collections.Generic;
using System.Linq;
using EclipseUI.Elements;

namespace EclipseUI;

/// <summary>item 模板的一次实例化：根元素 + 按 item 重跑的定向更新函数 + 作用域内订阅。</summary>
public sealed class BlockScope
{
    public Element[] Roots { get; }
    public Action<object>[] Updaters { get; }

    /// <summary>作用域内产生的订阅（嵌套块依赖等）；scope 被移除时由 ForEachBlock 释放。</summary>
    public List<IDisposable> Disposables { get; }

    public BlockScope(Element[] roots, Action<object>[]? updaters = null, List<IDisposable>? disposables = null)
    {
        Roots = roots ?? Array.Empty<Element>();
        Updaters = updaters ?? Array.Empty<Action<object>>();
        Disposables = disposables ?? new List<IDisposable>();
    }
}

/// <summary>
/// @if 的块协调器：条件翻转时挂载/卸载内容；
/// 内容内动态绑定产生的订阅随内容卸载而释放（无泄漏窗口）。
/// </summary>
public sealed class ConditionalBlock : StackPanelBase, IDisposable
{
    /// <inheritdoc />
    public override bool IsVisualTransparent => true;
    private Func<bool>? _condition;
    private Func<List<IDisposable>, Element[]>? _contentBuilder;
    private List<IDisposable>? _depSubscriptions;
    private List<IDisposable>? _contentSubscriptions;
    private bool _active;

    public void SetCondition(Func<bool> condition, params ISignal[] dependencies)
    {
        _condition = condition;
        DisposeAll(_depSubscriptions);
        _depSubscriptions = new List<IDisposable>();
        foreach (var dep in dependencies)
            _depSubscriptions.Add(dep.Subscribe(Refresh));
        SetActive(_condition());
    }

    public void SetContent(Func<List<IDisposable>, Element[]> contentBuilder)
    {
        _contentBuilder = contentBuilder;
        // 条件在 SetContent 之前已为真时也要挂载（@if 初始为真的场景）
        Rebuild();
    }

    private void Refresh()
    {
        if (_condition is null) return;
        SetActive(_condition());
    }

    private void SetActive(bool active)
    {
        if (active == _active) return;
        _active = active;
        Rebuild();
    }

    private void Rebuild()
    {
        DisposeAll(_contentSubscriptions);
        _contentSubscriptions = null;
        Children.Clear();

        if (_active && _contentBuilder is not null)
        {
            _contentSubscriptions = new List<IDisposable>();
            foreach (var root in _contentBuilder(_contentSubscriptions))
                Children.Add(root);
        }
    }

    public void Dispose()
    {
        DisposeAll(_depSubscriptions);
        DisposeAll(_contentSubscriptions);
    }

    private static void DisposeAll(List<IDisposable>? list)
    {
        if (list is null) return;
        foreach (var disposable in list)
            disposable.Dispose();
        list.Clear();
    }
}

/// <summary>
/// @foreach 的块协调器：按 Key 对齐新旧条目，列表更新 O(变动数)；
/// 复用条目时按新 item 重跑定向更新函数。
/// </summary>
public sealed class ForEachBlock<T> : StackPanelBase, IDisposable
{
    /// <inheritdoc />
    public override bool IsVisualTransparent => true;

    private sealed class Entry
    {
        public Entry(object key, BlockScope scope)
        {
            Key = key;
            Scope = scope;
        }

        public object Key { get; }
        public BlockScope Scope { get; }
    }

    private static readonly object s_nullKey = new();

    private Func<IEnumerable<T>>? _itemsGetter;
    private List<IDisposable>? _depSubscriptions;
    private Func<T, BlockScope>? _template;
    private Func<T, object?>? _keySelector;
    private readonly List<Entry> _entries = new();

    public void SetItems(Func<IEnumerable<T>> itemsGetter, params ISignal[] dependencies)
    {
        _itemsGetter = itemsGetter;
        DisposeAll(_depSubscriptions);
        _depSubscriptions = new List<IDisposable>();
        foreach (var dep in dependencies)
            _depSubscriptions.Add(dep.Subscribe(Refresh));
        Refresh();
    }

    public void SetKeySelector(Func<T, object?> keySelector) => _keySelector = keySelector;

    public void SetTemplate(Func<T, BlockScope> template)
    {
        _template = template;
        Refresh();
    }

    private void Refresh()
    {
        if (_itemsGetter is null || _template is null) return;

        var items = _itemsGetter().ToList();

        var freeByKey = new Dictionary<object, Queue<Entry>>();
        foreach (var entry in _entries)
        {
            if (!freeByKey.TryGetValue(entry.Key, out var queue))
                freeByKey[entry.Key] = queue = new Queue<Entry>();
            queue.Enqueue(entry);
        }

        var newEntries = new List<Entry>(items.Count);
        foreach (var item in items)
        {
            var key = _keySelector?.Invoke(item) ?? s_nullKey;
            if (freeByKey.TryGetValue(key, out var queue) && queue.Count > 0)
            {
                var entry = queue.Dequeue();
                foreach (var updater in entry.Scope.Updaters)
                    updater(item!);
                newEntries.Add(entry);
            }
            else
            {
                newEntries.Add(new Entry(key, _template(item)));
            }
        }

        _entries.Clear();
        _entries.AddRange(newEntries);

        // 未复用的旧 scope：释放其作用域内订阅（嵌套块依赖等），防泄漏
        foreach (var pair in freeByKey)
            foreach (var leftover in pair.Value)
                DisposeAll(leftover.Scope.Disposables);

        Children.Clear();
        foreach (var entry in _entries)
            foreach (var root in entry.Scope.Roots)
                Children.Add(root);
    }

    public void Dispose() => DisposeAll(_depSubscriptions);

    private static void DisposeAll(List<IDisposable>? list)
    {
        if (list is null) return;
        foreach (var disposable in list)
            disposable.Dispose();
        list.Clear();
    }
}

using System;
using System.Collections.Generic;
using System.Linq;

namespace EclipseUI;

/// <summary>
/// 组件实例 = code-behind 对象 + 编译生成的模板 RenderUnit。
/// 无组件树 diff、无参数 diff；父子通信靠参数推送 / 直接引用。
/// </summary>
public abstract class Component : IDisposable
{
    private const int MaxRenderIterations = 100;

    private readonly List<IDisposable> _tracked = new();
    private readonly object _invalidationLock = new();
    private readonly Queue<PendingRender> _pendingRenders = new();
    private readonly HashSet<Component> _pendingSet = new();
    private Component? _owner;
    private long _nextSequence;
    private volatile bool _disposed;
    private volatile bool _isMounted;
    private string[]? _ownRootClasses;
    private IReadOnlyList<string>? _outerClasses;

    public Element Root { get; private set; } = null!;

    public bool IsMounted => _isMounted;

    /// <summary>组件是否已释放（Dispose 后为 true；卸载根元素会触发释放，G4/K33）。</summary>
    public bool IsDisposed => _disposed;

    internal int Depth
    {
        get
        {
            var depth = 0;
            for (var node = _owner; node is not null; node = node._owner)
                depth++;
            return depth;
        }
    }

    public void Mount()
    {
        if (_isMounted) return;
        ObjectDisposedException.ThrowIf(_disposed, this);
        Root = Build();
        // 组件根被卸载（直接 Unmount，或祖先 Unmount 传播到根）→ 组件随之释放：
        // Dispose 递归释放被 Track 的子组件与作用域资源，绑定监听随之解除（G4/K33）。
        Root.AddHandler(Element.UnmountedEvent, HandleRootUnmounted);
        _isMounted = true;
        // 标签上的 Class= 可能早于 Mount 登记（手写调用），此时 Root 还不存在——这里补做一次。
        if (_outerClasses is { Count: > 0 })
            ApplyOuterClasses();
    }

    /// <summary>
    /// 组件标签上的 <c>Class=</c>（spec/02 §4.12.6）：类集作用在 <see cref="Root"/> 上。
    /// 组件不派生 <see cref="Element"/>（spec/03 §5.3），标签上的元素级属性只能在根元素上兑现；
    /// 这里把外部类与根自身声明的类合并——自身类在前、外部类在后（同属性冲突时外部覆盖内部，
    /// 与 <c>Class</c> 在控件上「后写者胜」的应用序一致）。
    /// </summary>
    /// <remarks>
    /// 可重复调用：根级 <c>Class={expr}</c> 每轮 <see cref="Render"/> 都会调，自身类只在首次调用时
    /// 快照，所以不会累积。传空集 = 撤销外部类、只留自身类（与 <c>Class=""</c> 的语义对齐）。
    /// </remarks>
    public void ApplyClasses(IReadOnlyList<string>? names)
    {
        if (_disposed) return;
        _outerClasses = names ?? Array.Empty<string>();
        if (_isMounted)
            ApplyOuterClasses();
    }

    private void ApplyOuterClasses()
    {
        _ownRootClasses ??= Root.Classes.Count > 0 ? Root.Classes.ToArray() : Array.Empty<string>();
        var outer = _outerClasses ?? Array.Empty<string>();
        if (outer.Count == 0)
        {
            Root.Classes = _ownRootClasses;
            return;
        }

        var merged = new string[_ownRootClasses.Length + outer.Count];
        Array.Copy(_ownRootClasses, merged, _ownRootClasses.Length);
        for (var i = 0; i < outer.Count; i++)
            merged[_ownRootClasses.Length + i] = outer[i];
        Root.Classes = merged;
    }

    private void HandleRootUnmounted(RoutedEventArgs args)
        => Dispose();

    protected abstract Element Build();

    /// <summary>标记组件过期；可从任意线程调用。未挂载或已释放时 no-op。</summary>
    public void Invalidate()
    {
        if (_disposed || !_isMounted) return;
        RootComponent.EnqueueInvalidation(this);
    }

    /// <summary>重算本组件的根级动态表达式；只由 <see cref="ProcessInvalidations"/> 调用。</summary>
    protected virtual void Render()
    {
    }

    protected virtual bool ShouldRender() => true;

    /// <summary>
    /// 处理根组件的失效队列：按深度升序渲染，同深度按入队顺序。
    /// 返回本轮是否执行过至少一次 <see cref="Render"/>。
    /// </summary>
    public bool ProcessInvalidations()
    {
        var root = RootComponent;
        if (!ReferenceEquals(root, this))
            return root.ProcessInvalidations();
        if (_disposed)
            return false;

        var rendered = false;
        var iterations = 0;

        while (true)
        {
            List<PendingRender> batch;
            lock (_invalidationLock)
            {
                if (_pendingRenders.Count == 0)
                    break;
                batch = new List<PendingRender>(_pendingRenders);
                _pendingRenders.Clear();
                _pendingSet.Clear();
            }

            batch.Sort(static (a, b) =>
            {
                var byDepth = a.Component.Depth.CompareTo(b.Component.Depth);
                return byDepth != 0 ? byDepth : a.Sequence.CompareTo(b.Sequence);
            });

            foreach (var pending in batch)
            {
                var component = pending.Component;
                if (component._disposed || !component._isMounted)
                    continue;
                if (!component.ShouldRender())
                    continue;
                if (++iterations > MaxRenderIterations)
                    throw new InvalidOperationException(
                        $"Render loop exceeded {MaxRenderIterations} iterations at component '{component.GetType().FullName}'.");
                component.Render();
                rendered = true;
            }
        }

        return rendered;
    }

    public void Adopt(Component child)
    {
        ArgumentNullException.ThrowIfNull(child);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (child._disposed)
            throw new ObjectDisposedException(child.GetType().FullName);
        if (ReferenceEquals(child, this))
            throw new InvalidOperationException("A component cannot adopt itself.");
        if (ReferenceEquals(child._owner, this))
            return;
        if (child._owner is not null)
            throw new InvalidOperationException(
                $"Component '{child.GetType().FullName}' already has an owner.");

        for (var ancestor = this; ancestor is not null; ancestor = ancestor._owner)
            if (ReferenceEquals(ancestor, child))
                throw new InvalidOperationException("Component ownership cycle detected.");

        child._owner = this;
        MigratePending(child);
    }

    // 接管前入队的失效躺在子组件自己的根队列里，所有权变更后该队列不会再被排空；
    // 按入队顺序重入新根，与新队列里已有失效按待渲染标记合并。
    private void MigratePending(Component child)
    {
        List<PendingRender> drained;
        lock (child._invalidationLock)
        {
            if (child._pendingRenders.Count == 0)
                return;
            drained = new List<PendingRender>(child._pendingRenders);
            child._pendingRenders.Clear();
            child._pendingSet.Clear();
        }
        var root = RootComponent;
        foreach (var pending in drained)
            root.EnqueueInvalidation(pending.Component);
    }

    /// <summary>接管子组件所有权并随本组件释放（生成的 Build 内调用）。</summary>
    protected void Track(Component child)
    {
        Adopt(child);
        TrackResource(child);
    }

    public void TrackContent(Component child) => Track(child);

    public void TrackContent(IDisposable disposable) => Track(disposable);

    /// <summary>跟踪生命周期随组件的资源（生成的 Build 内调用）。</summary>
    protected void Track(IDisposable disposable) => TrackResource(disposable);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        // 退订根元素的 UnmountedEvent：否则存活的元素树经委托强引用已释放组件，
        // 破坏「Dispose 后可回收」的硬化口径（RuntimeHardeningTests 泄漏回归）。
        Root?.RemoveHandler(Element.UnmountedEvent, HandleRootUnmounted);
        _isMounted = false;
        // 清空自身失效队列：已释放组件不会再被 ProcessInvalidations 排空
        // （_disposed 时直接返回），残留队列会多持有一轮强引用。
        // 与 EnqueueInvalidation / ProcessInvalidations / MigratePending 共用同一把锁。
        lock (_invalidationLock)
        {
            _pendingRenders.Clear();
            _pendingSet.Clear();
        }
        foreach (var disposable in _tracked)
            disposable.Dispose();
        _tracked.Clear();
        _owner = null;
    }

    private Component RootComponent
    {
        get
        {
            var current = this;
            while (current._owner is not null)
                current = current._owner;
            return current;
        }
    }

    private void EnqueueInvalidation(Component component)
    {
        lock (_invalidationLock)
        {
            if (!_pendingSet.Add(component))
                return;
            _pendingRenders.Enqueue(new PendingRender(component, _nextSequence++));
        }
    }

    private void TrackResource(IDisposable disposable)
    {
        if (_disposed)
        {
            disposable.Dispose();
            return;
        }
        _tracked.Add(disposable);
    }

    private readonly record struct PendingRender(Component Component, long Sequence);
}

/// <summary>无头布局宿主：不开窗口跑 measure/arrange 全链路（测试与工具链用）。</summary>
public sealed class HeadlessRoot
{
    private readonly Element _content;
    private readonly Component? _rootComponent;

    public HeadlessRoot(Element content)
    {
        _content = content;
        Input = new InputRouter(content);
    }

    public HeadlessRoot(Component component)
    {
        ArgumentNullException.ThrowIfNull(component);
        if (!component.IsMounted)
            component.Mount();
        _rootComponent = component;
        _content = component.Root;
        Input = new InputRouter(_content);
    }

    public Size AvailableSize { get; set; } = new Size(1280, 800);

    public Element Content => _content;

    public Component? RootComponent => _rootComponent;

    /// <summary>指针/键盘路由（与真实窗口宿主共用）。</summary>
    public InputRouter Input { get; }

    public FocusManager Focus => Input.Focus;

    public void UpdateLayout()
    {
        // 卸载/摘除后焦点元素可能已离树：布局前修剪，避免 Tab 从游离元素起算。
        Focus.ClearFocusIfDetached();
        _rootComponent?.ProcessInvalidations();
        _content.Measure(AvailableSize);
        _content.Arrange(new Rect(Point.Zero, _content.DesiredSize));
    }

    /// <summary>滚轮输入（无头测试与 Win32 宿主共用同一 InputRouter 路径）。</summary>
    public void PointerWheel(Point point, double delta) => Input.PointerWheel(point, delta);

    // ---- 输入（M4 无头形态：与平台宿主共用同一 hit test / 路由路径）----
    // 捕获语义：按下即捕获命中元素，释放路由给捕获元素（拖出后释放不会丢状态，
    // 也不会误触其他控件）；释放完成后捕获解除。

    public Element? HitTest(Point point) => Input.HitTest(point);

    public void PointerPress(Point point) => Input.PointerPress(point);

    public void PointerRelease(Point point) => Input.PointerRelease(point);

    public void PointerMove(Point point) => Input.PointerMove(point);

    /// <summary>按键。Tab 由焦点系统拦截做遍历；其余 KeyDown 从焦点元素（或根）路由。</summary>
    public void KeyPress(Key key) => Input.KeyPress(key);
}

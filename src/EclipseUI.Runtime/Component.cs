using System;
using System.Collections.Generic;

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

    public Element Root { get; private set; } = null!;

    public bool IsMounted => _isMounted;

    internal int Depth { get; private set; }

    public void Mount()
    {
        if (_isMounted) return;
        ObjectDisposedException.ThrowIf(_disposed, this);
        Root = Build();
        _isMounted = true;
    }

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
        child.Depth = Depth + 1;
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
        _rootComponent?.ProcessInvalidations();
        _content.Measure(AvailableSize);
        _content.Arrange(new Rect(Point.Zero, _content.DesiredSize));
    }

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

public static class Bind
{
    /// <summary>
    /// 双向绑定：signal → 属性 + 属性 → signal。
    /// 值短路保证 set 回环终止。
    /// </summary>
    public static IDisposable TwoWay<T>(Element element, StyledProperty<T> property, Signal<T> signal)
    {
        void ToProp() => element.SetValue(property, signal.Value);
        var subscription = signal.Subscribe(ToProp);
        ToProp();

        void FromProp(Element sender, StyledProperty changed)
        {
            if (ReferenceEquals(changed, property))
                signal.Value = sender.GetValue(property);
        }
        element.PropertyChanged += FromProp;

        return new CombinedDisposable(subscription, () => element.PropertyChanged -= FromProp);
    }

    private sealed class CombinedDisposable : IDisposable
    {
        private IDisposable? _first;
        private Action? _second;

        public CombinedDisposable(IDisposable first, Action second)
        {
            _first = first;
            _second = second;
        }

        public void Dispose()
        {
            _first?.Dispose();
            _first = null;
            _second?.Invoke();
            _second = null;
        }
    }
}

using System;
using System.Collections.Generic;

namespace EclipseUI;

/// <summary>
/// 组件实例 = code-behind 对象 + 编译生成的模板 RenderUnit。
/// 无组件树 diff、无参数 diff；父子通信靠 signals / 直接引用。
/// </summary>
public abstract class Component : IDisposable
{
    private readonly List<IDisposable> _tracked = new();
    private bool _disposed;

    public Element Root { get; private set; } = null!;

    public bool IsMounted { get; private set; }

    public void Mount()
    {
        if (IsMounted) return;
        Root = Build();
        IsMounted = true;
    }

    protected abstract Element Build();

    /// <summary>跟踪生命周期随组件的订阅/资源（生成的 Build 内调用）。</summary>
    protected void Track(IDisposable disposable)
    {
        if (_disposed)
        {
            disposable.Dispose();
            return;
        }
        _tracked.Add(disposable);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var disposable in _tracked)
            disposable.Dispose();
        _tracked.Clear();
    }
}

/// <summary>无头布局宿主：不开窗口跑 measure/arrange 全链路（测试与工具链用）。</summary>
public sealed class HeadlessRoot
{
    private readonly Element _content;

    public HeadlessRoot(Element content)
    {
        _content = content;
        Focus = new FocusManager(content);
    }

    public Size AvailableSize { get; set; } = new Size(1280, 800);

    public Element Content => _content;

    public FocusManager Focus { get; }

    public void UpdateLayout()
    {
        _content.Measure(AvailableSize);
        _content.Arrange(new Rect(Point.Zero, _content.DesiredSize));
    }

    // ---- 输入（M4 无头形态：与未来平台宿主共用同一 hit test / 路由路径）----
    // 捕获语义：按下即捕获命中元素，释放路由给捕获元素（拖出后释放不会丢状态，
    // 也不会误触其他控件）；释放完成后捕获解除。

    private Element? _captured;

    public Element? HitTest(Point point) => _content.HitTest(point);

    public void PointerPress(Point point)
    {
        _captured = _content.HitTest(point);
        if (_captured is null) return;
        _captured.RaiseEvent(new PointerEventArgs(InputEvents.PointerPressedEvent, point) { Source = _captured });
    }

    public void PointerRelease(Point point)
    {
        var target = _captured ?? _content.HitTest(point);
        _captured = null;
        if (target is null) return;
        target.RaiseEvent(new PointerEventArgs(InputEvents.PointerReleasedEvent, point) { Source = target });
    }

    public void PointerMove(Point point)
    {
        var target = _captured ?? _content.HitTest(point);
        if (target is null) return;
        target.RaiseEvent(new PointerEventArgs(InputEvents.PointerMovedEvent, point) { Source = target });
    }

    /// <summary>按键。Tab 由焦点系统拦截做遍历；其余 KeyDown 从焦点元素（或根）路由。</summary>
    public void KeyPress(Key key)
    {
        if (key == Key.Tab)
        {
            Focus.MoveNext();
            return;
        }
        var target = Focus.Focused ?? _content;
        target.RaiseEvent(new KeyEventArgs(InputEvents.KeyDownEvent, key) { Source = target });
    }
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

using System;

namespace EclipseUI;

/// <summary>
/// 指针/键盘路由：命中测试 → 捕获 → 路由事件 → 焦点。
/// 无头宿主（<see cref="HeadlessRoot"/>）与 Win32 窗口宿主共用同一实现，
/// 保证“测试里点的”和“窗口里点的”走同一条路径。
/// </summary>
public sealed class InputRouter
{
    private readonly Element _root;

    public InputRouter(Element root)
    {
        _root = root ?? throw new ArgumentNullException(nameof(root));
        Focus = new FocusManager(root);
    }

    public FocusManager Focus { get; }

    /// <summary>当前指针捕获元素（按下后到释放前）。</summary>
    public Element? Captured { get; private set; }

    public Element? HitTest(Point point) => _root.HitTest(point);

    /// <summary>
    /// 指针按下：捕获命中元素，并把键盘焦点交给最近的可聚焦祖先（平台标准行为）。
    /// 捕获元素随后被卸载时，释放不会路由到树外（见 Headless 回归测试）。
    /// </summary>
    public void PointerPress(Point point)
    {
        Captured = _root.HitTest(point);

        var focusable = Captured;
        while (focusable is not null && !focusable.IsFocusable)
            focusable = focusable.Parent;
        if (focusable is not null)
            Focus.Focus(focusable);

        if (Captured is null) return;
        Captured.RaiseEvent(new PointerEventArgs(InputEvents.PointerPressedEvent, point) { Source = Captured });
    }

    /// <summary>指针释放：优先路由给捕获元素，随后解除捕获。</summary>
    public void PointerRelease(Point point)
    {
        var target = Captured ?? _root.HitTest(point);
        Captured = null;
        if (target is null) return;
        target.RaiseEvent(new PointerEventArgs(InputEvents.PointerReleasedEvent, point) { Source = target });
    }

    public void PointerMove(Point point)
    {
        var target = Captured ?? _root.HitTest(point);
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

        var target = Focus.Focused ?? _root;
        target.RaiseEvent(new KeyEventArgs(InputEvents.KeyDownEvent, key) { Source = target });
    }
}

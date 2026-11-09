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

    /// <summary>当前悬停元素（D102 伪类）：<see cref="Element.IsHovered"/> 随命中目标翻转；
    /// 捕获期间冻结在捕获元素上，释放后重算。</summary>
    public Element? Hovered { get; private set; }

    public Element? HitTest(Point point) => _root.HitTest(point);

    /// <summary>
    /// 指针按下：捕获命中元素，并把键盘焦点交给最近的可聚焦祖先（平台标准行为）。
    /// 捕获元素随后被卸载时，释放不会路由到树外（见 Headless 回归测试）。
    /// 按下即置 <see cref="Element.IsPressed"/>（事件路由**之前**——handler 可读到按下态），
    /// 释放时**先路由后清除**（D102：Button 的 Click 门控读 <see cref="Element.IsPressed"/>）。
    /// E3：命中有效禁用元素时整体吞掉——不捕获、不悬停、不聚焦、不路由。
    /// </summary>
    public void PointerPress(Point point)
    {
        var hit = _root.HitTest(point);
        if (hit is not null && !hit.IsEffectiveEnabled)
            return; // :disabled 吞按下

        Captured = hit;
        UpdateHover(Captured);

        var focusable = Captured;
        while (focusable is not null && !focusable.IsFocusable)
            focusable = focusable.Parent;
        if (focusable is not null)
            Focus.Focus(focusable);

        if (Captured is null) return;
        Captured.IsPressed = true;
        Captured.RaiseEvent(new PointerEventArgs(Element.PointerPressedEvent, point) { Source = Captured });
    }

    /// <summary>指针释放：优先路由给捕获元素（先于清 <see cref="Element.IsPressed"/>），随后解除捕获。
    /// E3：按下后中途被禁用（捕获期间父级 <see cref="Element.IsEnabled"/> 翻转）时，
    /// 只清按下位、不路由事件——禁用元素的 Released 不产生 Click。</summary>
    public void PointerRelease(Point point)
    {
        var target = Captured ?? _root.HitTest(point);
        Captured = null;
        if (target is not null)
        {
            if (target.IsEffectiveEnabled)
                target.RaiseEvent(new PointerEventArgs(Element.PointerReleasedEvent, point) { Source = target });
            target.IsPressed = false; // 事件之后清除：Released handler 内「按下才触发」门控仍成立
        }
        UpdateHover(_root.HitTest(point)); // 释放后重算悬停（指针可能在按住期间移出）
    }

    public void PointerMove(Point point)
    {
        var target = Captured ?? _root.HitTest(point);
        UpdateHover(target);
        if (target is null || !target.IsEffectiveEnabled) return; // 禁用元素不路由移动事件
        target.RaiseEvent(new PointerEventArgs(Element.PointerMovedEvent, point) { Source = target });
    }

    /// <summary>悬停目标变化时翻转 <see cref="Element.IsHovered"/>（同目标 no-op，指针移动零开销）。
    /// E3：有效禁用的目标视为无悬停目标——:disabled 不触发 :hover。</summary>
    private void UpdateHover(Element? target)
    {
        if (target is not null && !target.IsEffectiveEnabled)
            target = null;
        if (ReferenceEquals(Hovered, target)) return;
        var old = Hovered;
        Hovered = target;
        if (old is not null) old.IsHovered = false;
        if (target is not null) target.IsHovered = true;
    }

    /// <summary>
    /// 滚轮：从命中元素起冒泡路由（<see cref="Element.PointerWheelChangedEvent"/>）。
    /// 滚轮不做捕获——命中谁就路由给谁，可滚动容器在冒泡路径上消费。E3：禁用元素吞滚轮。
    /// </summary>
    public void PointerWheel(Point point, double delta)
    {
        var target = _root.HitTest(point);
        if (target is null || !target.IsEffectiveEnabled) return;
        target.RaiseEvent(new WheelEventArgs(Element.PointerWheelChangedEvent, point, delta) { Source = target });
    }

    /// <summary>按键。Tab 由焦点系统拦截做遍历；其余 KeyDown 从焦点元素（或根）路由。
    /// E3：焦点元素被禁用（中途 <see cref="Element.IsEnabled"/> 翻转）时吞键。</summary>
    public void KeyPress(Key key)
    {
        if (key == Key.Tab)
        {
            Focus.MoveNext();
            return;
        }

        var target = Focus.Focused ?? _root;
        if (!target.IsEffectiveEnabled) return;
        target.RaiseEvent(new KeyEventArgs(Element.KeyDownEvent, key) { Source = target });
    }
}

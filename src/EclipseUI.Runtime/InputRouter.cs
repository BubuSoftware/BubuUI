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
    private Element? _hoverOwner;
    private PointerButton _button;
    private KeyModifiers _modifiers;

    public InputRouter(Element root)
    {
        _root = root ?? throw new ArgumentNullException(nameof(root));
        Focus = new FocusManager(root);
        root._focusManager = Focus;
    }

    public FocusManager Focus { get; }

    /// <summary>当前指针捕获元素（按下后到释放前）。</summary>
    public Element? Captured { get; private set; }

    /// <summary>当前悬停元素（D102 伪类）：<see cref="Element.IsHovered"/> 随命中目标翻转；
    /// 捕获期间冻结在捕获元素上，释放后重算。</summary>
    public Element? Hovered { get; private set; }

    /// <summary>Cancels a drag/hold without synthesizing a release or click.</summary>
    public void CancelPointerCapture()
    {
        var target = Captured; Captured = null;
        if (target is not null) EndPointerInteraction(target);
    }

    private static void EndPointerInteraction(Element target)
    {
        try { target.CancelPointerInteraction(); }
        finally
        {
            for (var ancestor = target.Parent; ancestor is not null; ancestor = ancestor.Parent)
                if (ancestor is Elements.Button or Elements.CheckBox) ancestor.IsPressed = false;
            target.IsPressed = false;
        }
    }

    private bool CanRoutePointer(Element target)
    {
        if (target.IsDisposed || !target.IsEffectiveEnabled || !target.IsEffectivelyVisible || _root.IsDisposed) return false;
        for (Element? node = target; node is not null; node = node.Parent)
        {
            if (!node.IsHitTestVisible) return false;
            if (ReferenceEquals(node, _root)) return true;
        }
        return false;
    }

    /// <summary>Ends input ownership without synthesizing a release/click on the previous tree.</summary>
    internal void Reset()
    {
        var hovered = Hovered;
        var owner = _hoverOwner;
        _hoverOwner = null;
        Hovered = null;
        List<Exception>? failures = null;
        try { CancelPointerCapture(); }
        catch (Exception error) { (failures ??= new()).Add(error); }
        try { if (hovered is not null) hovered.IsHovered = false; }
        catch (Exception error) { (failures ??= new()).Add(error); }
        try { if (owner is not null && !ReferenceEquals(owner, hovered)) owner.IsHovered = false; }
        catch (Exception error) { (failures ??= new()).Add(error); }
        try { Focus.ClearFocus(); }
        catch (Exception error) { (failures ??= new()).Add(error); }
        if (failures is not null) throw new AggregateException("Input reset failed.", failures);
    }

    public Element? HitTest(Point point)
    {
        foreach (var popup in OpenPopups().Reverse())
            if (popup.HitTest(point) is { } hit) return hit;
        return _root.HitTest(point);
    }
    private IEnumerable<Elements.Popup> OpenPopups() => _root.Descendants().OfType<Elements.Popup>()
        .Where(p => p.IsOpen && p.IsEffectivelyVisible);


    /// <summary>
    /// 指针按下：捕获命中元素，并把键盘焦点交给最近的可聚焦祖先（平台标准行为）。
    /// 捕获元素随后被卸载时，释放不会路由到树外（见 Headless 回归测试）。
    /// 按下即置 <see cref="Element.IsPressed"/>（事件路由**之前**——handler 可读到按下态），
    /// 释放时**先路由后清除**（D102：Button 的 Click 门控读 <see cref="Element.IsPressed"/>）。
    /// E3：命中有效禁用元素时整体吞掉——不捕获、不悬停、不聚焦、不路由。
    /// </summary>
    public void PointerPress(Point point, KeyModifiers modifiers = KeyModifiers.None, PointerButton button = PointerButton.Left)
    {
        using var scope = Focus.EnterInput(FocusOrigin.Pointer, modifiers);
        Focus.NotifyInput(FocusOrigin.Pointer);
        CancelPointerCapture();
        foreach (var popup in OpenPopups().Reverse().ToArray())
            if (!popup.StaysOpen && !popup.ContainsRootPoint(point) && !popup.Descendants().OfType<Elements.Popup>().Any(p => p.IsOpen && p.ContainsRootPoint(point))) popup.Close();
        var hit = HitTest(point);
        if (hit is not null && !hit.IsEffectiveEnabled)
            return; // :disabled 吞按下

        _button = button; _modifiers = modifiers;
        Captured = hit;
        UpdateHover(Captured);

        var focusable = Captured;
        while (focusable is not null && !focusable.IsFocusable)
            focusable = focusable.Parent;
        if (focusable is not null)
            Focus.Focus(focusable);

        if (Captured is null) return;
        var preview = new PointerEventArgs(Element.PreviewPointerPressedEvent, point, modifiers, button) { Source = Captured };
        var pressed = Captured;
        pressed.RaiseEvent(preview);
        if (preview.Handled || !ReferenceEquals(Captured, pressed) || pressed.IsDisposed || !pressed.IsEffectivelyVisible) { Captured = null; return; }
        Captured.IsPressed = true;
        for (var ancestor = Captured.Parent; ancestor is not null; ancestor = ancestor.Parent)
            if (ancestor is Elements.Button or Elements.CheckBox) ancestor.IsPressed = true;
        Captured.RaiseEvent(new PointerEventArgs(Element.PointerPressedEvent, point, modifiers, button) { Source = Captured });
    }

    /// <summary>指针释放：优先路由给捕获元素（先于清 <see cref="Element.IsPressed"/>），随后解除捕获。
    /// E3：按下后中途被禁用（捕获期间父级 <see cref="Element.IsEnabled"/> 翻转）时，
    /// 只清按下位、不路由事件——禁用元素的 Released 不产生 Click。</summary>
    public void PointerRelease(Point point)
    {
        using var scope = Focus.EnterInput(FocusOrigin.Pointer, _modifiers);
        var target = Captured ?? HitTest(point);
        Captured = null;
        if (target is not null)
        {
            try
            {
                if (CanRoutePointer(target))
                    target.RaiseEvent(new PointerEventArgs(Element.PointerReleasedEvent, point, _modifiers, _button) { Source = target });
            }
            finally { EndPointerInteraction(target); } // Released handlers still observe IsPressed.
        }
        UpdateHover(HitTest(point)); // 释放后重算悬停（指针可能在按住期间移出）
    }

    public void PointerMove(Point point)
    {
        if (Captured is { } captured && !CanRoutePointer(captured)) CancelPointerCapture();
        var target = Captured ?? HitTest(point);
        UpdateHover(target);
        if (target is null || !CanRoutePointer(target)) return;
        target.RaiseEvent(new PointerEventArgs(Element.PointerMovedEvent, point, _modifiers, _button) { Source = target });
    }

    /// <summary>悬停目标变化时翻转 <see cref="Element.IsHovered"/>（同目标 no-op，指针移动零开销）。
    /// E3：有效禁用的目标视为无悬停目标——:disabled 不触发 :hover。</summary>
    private void UpdateHover(Element? target)
    {
        if (target is not null && !target.IsEffectiveEnabled)
            target = null;
        if (ReferenceEquals(Hovered, target)) return;
        var old = Hovered;
        var oldOwner = _hoverOwner;
        Hovered = target;
        _hoverOwner = HoverOwner(target);
        if (old is not null) old.IsHovered = false;
        if (target is not null) target.IsHovered = true;
        if (oldOwner is not null && !ReferenceEquals(oldOwner, _hoverOwner) && !ReferenceEquals(oldOwner, target)) oldOwner.IsHovered = false;
        if (_hoverOwner is not null && !ReferenceEquals(_hoverOwner, target)) _hoverOwner.IsHovered = true;
        static bool Contains(Element ancestor, Element? child) { for (var e = child; e is not null; e = e.Parent) if (ReferenceEquals(e, ancestor)) return true; return false; }
        for (var e = old; e is not null; e = e.Parent) if (!Contains(e, target)) e.RaiseEvent(new RoutedEventArgs(Element.PointerExitedEvent));
        for (var e = target; e is not null; e = e.Parent) if (!Contains(e, old)) e.RaiseEvent(new RoutedEventArgs(Element.PointerEnteredEvent));
    }

    // Content is hit-tested normally; the enclosing interactive control also needs hover feedback.
    // Stop at the nearest control so hovering a nested tree row does not highlight every ancestor row.
    private static Element? HoverOwner(Element? target)
    {
        for (var node = target; node is not null; node = node.Parent)
            if (node is Elements.Button or Elements.CheckBox or Elements.ListBoxItem or Elements.TreeViewItem) return node;
        return null;
    }

    /// <summary>
    /// 滚轮：从命中元素起冒泡路由（<see cref="Element.PointerWheelChangedEvent"/>）。
    /// 滚轮不做捕获——命中谁就路由给谁，可滚动容器在冒泡路径上消费。E3：禁用元素吞滚轮。
    /// </summary>
    public void PointerWheel(Point point, double delta, KeyModifiers modifiers = KeyModifiers.None)
    {
        var target = HitTest(point);
        if (target is null || !target.IsEffectiveEnabled) return;
        target.RaiseEvent(new WheelEventArgs(Element.PointerWheelChangedEvent, point, delta, modifiers) { Source = target });
    }

    /// <summary>按键。Tab 由焦点系统拦截做遍历；其余 KeyDown 从焦点元素（或根）路由。
    /// E3：焦点元素被禁用（中途 <see cref="Element.IsEnabled"/> 翻转）时吞键。</summary>
    public void KeyPress(Key key, KeyModifiers modifiers = KeyModifiers.None, bool isRepeat = false)
    {
        if (key == Key.None) return;
        using var scope = Focus.EnterInput(FocusOrigin.Keyboard, modifiers);
        Focus.NotifyInput(FocusOrigin.Keyboard);
        if (key == Key.Escape && TryDismissPopup()) return;
        var target = Focus.Focused ?? _root;
        if (!target.IsEffectiveEnabled || !target.IsEffectivelyVisible) return;
        var preview = new KeyEventArgs(Element.PreviewKeyDownEvent, key, modifiers, isRepeat) { Source = target };
        target.RaiseEvent(preview);
        if (preview.Handled) return;
        var args = new KeyEventArgs(Element.KeyDownEvent, key, modifiers, isRepeat) { Source = target };
        if (key == Key.Tab)
        {
            if (modifiers.HasFlag(KeyModifiers.Control)) target.RaiseEvent(args);
            if (!args.Handled) { if (modifiers.HasFlag(KeyModifiers.Shift)) Focus.MovePrevious(); else Focus.MoveNext(); }
            return;
        }
        target.RaiseEvent(args);
    }

    public bool TryDismissPopup()
    {
        var popup = OpenPopups().LastOrDefault(p => !p.StaysOpen);
        if (popup is null) return false;
        popup.Close(); return true;
    }
    public void KeyRelease(Key key, KeyModifiers modifiers = KeyModifiers.None)
    {
        var target = Focus.Focused ?? _root;
        if (target.IsEffectiveEnabled && target.IsEffectivelyVisible)
            target.RaiseEvent(new KeyEventArgs(Element.KeyUpEvent, key, modifiers));
    }
    public void TextInput(string text)
    {
        var target = Focus.Focused;
        if (target is { IsEffectiveEnabled: true, IsEffectivelyVisible: true }) target.RaiseEvent(new TextInputEventArgs(text));
    }
    public void TextComposition(string text, CompositionStage stage)
    {
        var target = Focus.Focused;
        if (target is { IsEffectiveEnabled: true, IsEffectivelyVisible: true }) target.RaiseEvent(new TextCompositionEventArgs(text, stage));
    }
}

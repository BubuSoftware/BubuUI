using EclipseUI;
using EclipseUI.Elements;
using Xunit;

namespace EclipseUI.Headless.Tests;

/// <summary>
/// 伪类状态路由（D102）：InputRouter 维护 Hover/Pressed、FocusManager 维护 Focused——
/// 状态翻转、捕获语义（冻结/重算）与释放时序（先路由后清，保 Click 门控）。
/// </summary>
public class PseudoStateTests
{
    private static readonly Size Surface = new(400, 300);

    private static (HeadlessRoot Headless, Button Button, StackPanel Root) BuildTree()
    {
        var root = new StackPanel();
        var button = new Button { Label = "Go" };
        root.Children.Add(button);
        var headless = new HeadlessRoot(root) { AvailableSize = Surface };
        headless.UpdateLayout();
        return (headless, button, root);
    }

    private static Point CenterOf(Element element)
        => new(element.FinalRect.X + element.FinalRect.Width / 2,
               element.FinalRect.Y + element.FinalRect.Height / 2);

    [Fact]
    public void Hover_FollowsHitTarget_WithinAndOutside()
    {
        var (headless, button, _) = BuildTree();
        var center = CenterOf(button);

        headless.PointerMove(center);
        Assert.Same(button, headless.Input.Hovered);
        Assert.True(button.IsHovered);

        headless.PointerMove(new Point(Surface.Width + 200, Surface.Height + 200));
        Assert.Null(headless.Input.Hovered);
        Assert.False(button.IsHovered);
    }

    [Fact]
    public void Hover_FrozenDuringCapture_RecomputedOnRelease()
    {
        var (headless, button, root) = BuildTree();
        var center = CenterOf(button);
        headless.PointerPress(center); // 捕获 + 悬停都在 button
        Assert.True(button.IsHovered && button.IsPressed);

        headless.PointerMove(new Point(Surface.Width + 200, Surface.Height + 200)); // 拖出：捕获冻结 hover
        Assert.True(button.IsHovered);
        Assert.True(button.IsPressed);

        headless.PointerRelease(new Point(Surface.Width + 200, Surface.Height + 200)); // 释放：重算悬停
        Assert.False(button.IsPressed);
        Assert.Null(headless.Input.Hovered); // 指针在树外
        Assert.False(button.IsHovered);
    }

    [Fact]
    public void Pressed_ReadableDuringReleasedEvent_ThenCleared()
    {
        var (headless, button, _) = BuildTree();
        var center = CenterOf(button);
        bool seenPressedDuringRelease = false;
        button.AddHandler(Button.ClickEvent, () => seenPressedDuringRelease = button.IsPressed);

        headless.PointerPress(center);
        Assert.True(button.IsPressed);
        headless.PointerRelease(center);

        Assert.True(seenPressedDuringRelease); // 事件路由时仍是按下末尾（D102 时序契约）
        Assert.False(button.IsPressed);        // 路由后才清
    }

    [Fact]
    public void Button_Click_RequiresPressState()
    {
        var (headless, button, _) = BuildTree();
        var clicks = 0;
        button.AddHandler(Button.ClickEvent, () => clicks++);
        var center = CenterOf(button);

        // 未按下直接收到 Released（手工路由语义）：门挡住不触发。
        button.RaiseEvent(new PointerEventArgs(
            Element.PointerReleasedEvent, new Point(5, 5)) { Source = button });
        Assert.Equal(0, clicks);

        // 正常按下/释放路径触发一次（门控语义保持，D102 迁移回归）。
        headless.PointerPress(center);
        headless.PointerRelease(center);
        Assert.Equal(1, clicks);
    }

    [Fact]
    public void FocusManager_TogglesIsFocused()
    {
        var (headless, button, _) = BuildTree();

        headless.Focus.Focus(button);
        Assert.True(button.IsFocused);
        Assert.Equal(PseudoStates.Focused, button.PseudoStates);

        headless.Focus.ClearFocus();
        Assert.False(button.IsFocused);
        Assert.Equal(PseudoStates.None, button.PseudoStates);
    }

    [Fact]
    public void PressAndRelease_TogglesPressedState()
    {
        var (headless, button, _) = BuildTree();
        var center = CenterOf(button);

        headless.PointerPress(center);
        Assert.True(button.IsPressed);
        // 按下即命中（hover 同开）+ 可聚焦元素获得焦点（平台标准行为）。
        Assert.Equal(PseudoStates.Hover | PseudoStates.Pressed | PseudoStates.Focused, button.PseudoStates);

        headless.PointerRelease(center);
        Assert.False(button.IsPressed);
        Assert.Equal(PseudoStates.Hover | PseudoStates.Focused, button.PseudoStates); // 焦点仍在、悬停仍在

        headless.Focus.ClearFocus();
        headless.PointerMove(new Point(Surface.Width + 200, Surface.Height + 200));
        Assert.Equal(PseudoStates.None, button.PseudoStates);
    }
}

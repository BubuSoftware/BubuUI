using BubuUI;
using BubuUI.Elements;
using BubuUI.Rendering;
using Xunit;

namespace BubuUI.Headless.Tests;

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
        Assert.Equal(PseudoStates.Focused | PseudoStates.FocusWithin | PseudoStates.FocusVisible, button.PseudoStates);

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
        Assert.Equal(PseudoStates.Hover | PseudoStates.Pressed | PseudoStates.Focused | PseudoStates.FocusWithin, button.PseudoStates);

        headless.PointerRelease(center);
        Assert.False(button.IsPressed);
        Assert.Equal(PseudoStates.Hover | PseudoStates.Focused | PseudoStates.FocusWithin, button.PseudoStates); // 焦点仍在、悬停仍在

        headless.Focus.ClearFocus();
        headless.PointerMove(new Point(Surface.Width + 200, Surface.Height + 200));
        Assert.Equal(PseudoStates.None, button.PseudoStates);
    }

    // ---- E3：:disabled（有效值沿树继承 + 路由/焦点门控）----

    [Fact]
    public void IsEnabled_FlipsDisabledBit()
    {
        var (_, button, _) = BuildTree();

        Assert.True(button.IsEffectiveEnabled);
        Assert.Equal(PseudoStates.None, button.PseudoStates & PseudoStates.Disabled);

        button.IsEnabled = false;
        Assert.False(button.IsEffectiveEnabled);
        Assert.Equal(PseudoStates.Disabled, button.PseudoStates & PseudoStates.Disabled);

        button.IsEnabled = true;
        Assert.True(button.IsEffectiveEnabled);
        Assert.Equal(PseudoStates.None, button.PseudoStates & PseudoStates.Disabled);
    }

    [Fact]
    public void IsEnabled_Inherits_ThroughAncestors()
    {
        var (_, button, root) = BuildTree();

        root.IsEnabled = false; // 祖先禁用：后代有效禁用（桌面标准语义）
        Assert.True(button.IsEnabled);           // 自身声明值不变
        Assert.False(button.IsEffectiveEnabled); // 有效值继承
        Assert.Equal(PseudoStates.Disabled, button.PseudoStates & PseudoStates.Disabled);

        root.IsEnabled = true;
        Assert.True(button.IsEffectiveEnabled);
        Assert.Equal(PseudoStates.None, button.PseudoStates & PseudoStates.Disabled);
    }

    [Fact]
    public void Router_SwallowsPressHoverFocus_OnDisabled()
    {
        var (headless, button, _) = BuildTree();
        button.IsEnabled = false;
        var center = CenterOf(button);
        var clicks = 0;
        button.AddHandler(Button.ClickEvent, () => clicks++);

        headless.PointerPress(center);
        Assert.Null(headless.Input.Captured);      // 不捕获
        Assert.Null(headless.Input.Hovered);       // 不悬停（:disabled 不触发 :hover）
        Assert.Null(headless.Focus.Focused);       // 不聚焦
        Assert.False(button.IsPressed);
        Assert.Equal(PseudoStates.Disabled, button.PseudoStates); // 掩码只带 Disabled 位

        headless.PointerRelease(center);
        Assert.Equal(0, clicks);

        button.IsEnabled = true; // 恢复后同一路径正常
        headless.PointerPress(center);
        headless.PointerRelease(center);
        Assert.Equal(1, clicks);
    }

    [Fact]
    public void DisableDuringPress_ReleaseSwallowsClick()
    {
        var (headless, button, _) = BuildTree();
        var center = CenterOf(button);
        var clicks = 0;
        button.AddHandler(Button.ClickEvent, () => clicks++);

        headless.PointerPress(center);
        Assert.True(button.IsPressed);
        button.IsEnabled = false; // 按住期间被禁用
        headless.PointerRelease(center);

        Assert.Equal(0, clicks);  // Released 被吞：禁用元素的释放不产生 Click
        Assert.False(button.IsPressed);
    }

    [Fact]
    public void Focus_RejectsDisabled_DirectAndInherited()
    {
        var (headless, button, root) = BuildTree();

        button.IsEnabled = false;
        Assert.False(headless.Focus.Focus(button));
        Assert.Null(headless.Focus.Focused);

        button.IsEnabled = true;
        root.IsEnabled = false; // 祖先禁用同样拒绝（有效值判定）
        Assert.False(headless.Focus.Focus(button));
        Assert.Null(headless.Focus.Focused);
    }

    [Fact]
    public void TabTraversal_SkipsDisabled()
    {
        var root = new StackPanel();
        var a = new Button { Label = "a" };
        var b = new Button { Label = "b" };
        var c = new Button { Label = "c" };
        root.Children.Add(a);
        root.Children.Add(b);
        root.Children.Add(c);
        b.IsEnabled = false;
        var headless = new HeadlessRoot(root) { AvailableSize = Surface };
        headless.UpdateLayout();

        headless.Focus.Focus(a);
        Assert.Same(c, headless.Focus.MoveNext()); // b 禁用被跳过
    }

    // ---- E5：注册制伪类（PseudoStatesRegistry + SetRegisteredState + .State 变体）----

    [Fact]
    public void Register_AssignsUniqueBits_IsIdempotent()
    {
        var first = PseudoStatesRegistry.Register("e5-a");
        var again = PseudoStatesRegistry.Register("e5-a");
        var second = PseudoStatesRegistry.Register("e5-b");

        Assert.Equal(first, again);                    // 幂等：同名同掩码
        Assert.NotEqual(PseudoStates.None, first);
        Assert.NotEqual(first, second);                // 不同名不同位
        Assert.Equal(0, (int)(first & second));        // 位不重叠
        Assert.True(first > PseudoStates.Disabled);    // 注册位排在平台四位之后
    }

    [Fact]
    public void Register_NormalizesLeadingColon()
    {
        var bare = PseudoStatesRegistry.Register("e5-colon");
        var colon = PseudoStatesRegistry.Register(":e5-colon");

        Assert.Equal(bare, colon); // ':loading' 与 'loading' 是同一状态
    }

    [Fact]
    public void Register_RejectsPlatformNames_AndInvalidNames()
    {
        foreach (var platform in new[] { "hover", "pressed", "active", "focus", "focus-visible", "focus-within", "disabled" })
            Assert.Throws<ArgumentException>(() => PseudoStatesRegistry.Register(platform));
        Assert.Throws<ArgumentException>(() => PseudoStatesRegistry.Register(""));
        Assert.Throws<ArgumentException>(() => PseudoStatesRegistry.Register("has space"));
        Assert.Throws<ArgumentException>(() => PseudoStatesRegistry.Register("a;b"));
        Assert.Throws<ArgumentNullException>(() => PseudoStatesRegistry.Register(null!));
    }

    [Fact]
    public void SetRegisteredState_TogglesBit_InPseudoStates()
    {
        var (_, button, _) = BuildTree();
        var mask = PseudoStatesRegistry.Register("e5-toggle");

        Assert.Equal(PseudoStates.None, button.PseudoStates & mask);
        button.SetRegisteredState(mask, true);
        Assert.Equal(mask, button.PseudoStates & mask);

        button.SetRegisteredState(mask, false);
        Assert.Equal(PseudoStates.None, button.PseudoStates & mask);
    }

    [Fact]
    public void SetRegisteredState_RejectsPlatformBits_AndUnregistered()
    {
        var (_, button, _) = BuildTree();

        Assert.Throws<ArgumentException>(() => button.SetRegisteredState(PseudoStates.Hover, true));
        Assert.Throws<ArgumentException>(() => button.SetRegisteredState(PseudoStates.None, true));
        Assert.Throws<ArgumentException>(() => button.SetRegisteredState((PseudoStates)0x40000000, true));
    }

    [Fact]
    public void RegisteredState_DrivesStateVariantStyle_EndToEnd()
    {
        var (_, spinner, _) = BuildTree();
        var mask = PseudoStatesRegistry.Register("e5-spin");
        var provider = new StyleRegistry();
        provider.Define("spinner", s => s.Background(new Color(42, 48, 64)))
            .State(mask, s => s.Background(new Color(34, 197, 94)));
        Styling.Provider = provider;
        try
        {
            spinner.Classes = ["spinner"];
            Assert.Equal(new Color(42, 48, 64), Visual.GetBackground(spinner)); // 基础

            spinner.SetRegisteredState(mask, true);
            Assert.Equal(new Color(34, 197, 94), Visual.GetBackground(spinner)); // 注册位变体命中

            spinner.SetRegisteredState(mask, false);
            Assert.Equal(new Color(42, 48, 64), Visual.GetBackground(spinner)); // 翻回基础
        }
        finally
        {
            Styling.Provider = null;
        }
    }

    [Fact]
    public void RegisteredVariant_LaterDeclarationWins_OverHover()
    {
        // 声明序语义（design/17 §3.3）：State 声明在 Hover 之后 → 两位同开时注册位胜。
        var (headless, spinner, _) = BuildTree();
        var center = CenterOf(spinner);
        var mask = PseudoStatesRegistry.Register("e5-order");
        var provider = new StyleRegistry();
        provider.Define("spinner", s => s.Background(new Color(42, 48, 64)))
            .Hover(s => s.Background(new Color(1, 1, 1)))
            .State(mask, s => s.Background(new Color(2, 2, 2)));
        Styling.Provider = provider;
        try
        {
            spinner.Classes = ["spinner"];
            headless.PointerMove(center); // 悬停（IsHovered 由 InputRouter 维护）
            Assert.True(spinner.IsHovered);
            spinner.SetRegisteredState(mask, true);
            Assert.Equal(new Color(2, 2, 2), Visual.GetBackground(spinner)); // 后声明的 State 胜

            spinner.SetRegisteredState(mask, false);
            Assert.Equal(new Color(1, 1, 1), Visual.GetBackground(spinner)); // 只剩 hover
        }
        finally
        {
            Styling.Provider = null;
        }
    }
}

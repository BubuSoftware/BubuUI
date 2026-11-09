using System;
using System.Linq;
using EclipseUI;
using EclipseUI.Elements;
using EclipseUI.Rendering;
using Xunit;

namespace EclipseUI.Rendering.Tests;

/// <summary>
/// S-B Class 样式：有效值分层（local &gt; class &gt; 继承/默认）、类集变化重应用、
/// painter 读视觉覆盖（null 回落主题）。样式表用 StyleRegistry 类名直配，无选择器引擎。
/// </summary>
public class StyleTests : IDisposable
{
    private static readonly Size Surface = new(240, 160);
    private static readonly Color ClassColor = new(34, 160, 100);
    private static readonly Color HoverColor = new(70, 200, 140);
    private static readonly Color PressedColor = new(20, 110, 60);
    private static readonly Color FocusFg = new(255, 210, 90);
    private static readonly Color DisabledBg = new(90, 90, 96);

    private readonly StyleRegistry _registry = new();

    public StyleTests()
    {
        Styling.Provider = _registry;
        _registry.Define("accent", s => s.Background(ClassColor));
        _registry.Define("text", s => s.Foreground(new Color(10, 10, 10)));
        _registry.Define("papi", s => s.Background(ClassColor))
                 .Hover(s => s.Background(HoverColor))
                 .Pressed(s => s.Background(PressedColor))
                 .Focus(s => s.Foreground(FocusFg))
                 .Disabled(s => s.Background(DisabledBg));
        _registry.Define("thickring", s => s.FocusRingWidth(5));
    }

    public void Dispose() => Styling.Provider = null;

    [Fact]
    public void EffectiveValue_Layers_LocalOverClassOverDefault()
    {
        var element = new Button();
        Assert.Null(Visual.GetBackground(element)); // 默认：无覆盖

        element.SetStyleValue(Visual.BackgroundProperty, ClassColor);
        Assert.Equal(ClassColor, Visual.GetBackground(element)); // class 层生效

        var inline = new Color(200, 60, 60);
        element.SetValue(Visual.BackgroundProperty, inline);
        Assert.Equal(inline, Visual.GetBackground(element)); // local 压过 class

        Visual.SetBackground(element, null); // 显式取消覆盖（清 local 层）
        Assert.Equal(ClassColor, Visual.GetBackground(element)); // 回落 class
    }

    [Fact]
    public void ClearStyleValues_RemovesClassLayer_KeepsLocal()
    {
        var element = new Button();
        var inline = new Color(200, 60, 60);
        element.SetStyleValue(Visual.BackgroundProperty, ClassColor);
        element.SetValue(Visual.ForegroundProperty, inline);

        element.ClearStyleValues();

        Assert.Null(Visual.GetBackground(element));  // class 层被清
        Assert.Equal(inline, Visual.GetForeground(element)); // local 保留
    }

    [Fact]
    public void Classes_Change_ReappliesStyles()
    {
        var element = new Button { Classes = ["accent"] };
        Assert.Equal(ClassColor, Visual.GetBackground(element));

        _registry.Define("warn", s => s.Background(new Color(220, 140, 0)));
        element.Classes = ["warn"];

        Assert.Equal(new Color(220, 140, 0), Visual.GetBackground(element)); // 旧类值被清、新类生效
    }

    [Fact]
    public void Classes_UnknownName_IsIgnored()
    {
        var element = new Button { Classes = ["no-such-class"] };
        Assert.Null(Visual.GetBackground(element)); // 静默忽略，无异常
        Assert.Equal(["no-such-class"], element.Classes); // 声明仍保留
    }

    [Fact]
    public void ProviderAssignedLate_AppliesPendingClasses()
    {
        Styling.Provider = null; // 先声明后装配
        var element = new Button { Classes = ["accent"] };
        Assert.Null(Visual.GetBackground(element));

        Styling.Provider = _registry; // 装配时补应用

        Assert.Equal(ClassColor, Visual.GetBackground(element));
    }

    [Fact]
    public void Painter_ClassBackground_OverridesTheme()
    {
        var root = new StackPanel();
        var button = new Button { Label = "Go", Classes = ["accent"] };
        root.Children.Add(button);
        new HeadlessRoot(root) { AvailableSize = Surface }.UpdateLayout();

        var painter = new ElementPainter(ElementTheme.Dark);
        var list = painter.Build(root, Surface);

        // 按钮背景命令取 class 色，而非主题 Accent。
        var fill = list.Commands.OfType<FillRoundedRect>()
            .Select(f => f.Brush).OfType<SolidColorBrush>()
            .FirstOrDefault(b => b.Color == ClassColor);
        Assert.NotNull(fill);
        Assert.DoesNotContain(list.Commands.OfType<FillRoundedRect>(),
            f => f.Brush is SolidColorBrush { Color: var c } && c == ElementTheme.Dark.Accent);

        // 视觉变化进段缓存：复用帧背景命令仍在（段含覆盖色）。
        var reused = painter.Build(root, Surface);
        Assert.Equal((2, 0), painter.LastBuildStats);
        Assert.Contains(reused.Commands.OfType<FillRoundedRect>(),
            f => f.Brush is SolidColorBrush { Color: var c } && c == ClassColor);
    }

    // ---- D102 伪类状态样式 ----

    private static (HeadlessRoot Headless, Button Button) BuildButtonTree(StyleRegistry registry)
    {
        var (headless, button, _) = BuildButtonTreeWithRoot(registry);
        return (headless, button);
    }

    private static (HeadlessRoot Headless, Button Button, StackPanel Root) BuildButtonTreeWithRoot(StyleRegistry registry)
    {
        var root = new StackPanel();
        var button = new Button { Label = "Go", Classes = ["papi"] };
        root.Children.Add(button);
        var headless = new HeadlessRoot(root) { AvailableSize = Surface };
        headless.UpdateLayout();
        return (headless, button, root);
    }

    private static Point CenterOf(Element element)
        => new(element.FinalRect.X + element.FinalRect.Width / 2,
               element.FinalRect.Y + element.FinalRect.Height / 2);

    [Fact]
    public void HoverVariant_AppliesOnEnter_RevertsOnLeave()
    {
        var (headless, button) = BuildButtonTree(_registry);
        var center = CenterOf(button);
        Assert.Equal(ClassColor, Visual.GetBackground(button)); // 无状态：base

        headless.PointerMove(center);
        Assert.True(button.IsHovered);
        Assert.Equal(HoverColor, Visual.GetBackground(button)); // :hover 变体生效

        headless.PointerMove(new Point(Surface.Width + 500, Surface.Height + 500));
        Assert.False(button.IsHovered);
        Assert.Equal(ClassColor, Visual.GetBackground(button)); // 离开回落 base
    }

    [Fact]
    public void PressedVariant_WinsOverHover_ByDeclarationOrder()
    {
        var (headless, button) = BuildButtonTree(_registry);
        headless.PointerPress(CenterOf(button)); // 按下：hover+pressed 同开

        Assert.True(button.IsHovered);
        Assert.True(button.IsPressed);
        Assert.Equal(PressedColor, Visual.GetBackground(button)); // pressed 后声明 → 覆盖 hover

        headless.PointerRelease(CenterOf(button));
        Assert.False(button.IsPressed);
        Assert.Equal(HoverColor, Visual.GetBackground(button)); // 只剩 hover
    }

    [Fact]
    public void FocusVariant_AppliesOnlyToFocusable()
    {
        var (headless, button) = BuildButtonTree(_registry);

        headless.Focus.Focus(button);
        Assert.True(button.IsFocused);
        Assert.Equal(FocusFg, Visual.GetForeground(button)); // :focus 变体

        headless.Focus.ClearFocus();
        Assert.False(button.IsFocused);
        Assert.Null(Visual.GetForeground(button)); // 回落（无 base 前景）

        var panel = new StackPanel(); // 非 focusable：Focus 拒绝、状态不置位
        Assert.False(headless.Focus.Focus(panel));
        Assert.False(panel.IsFocused);
    }

    [Fact]
    public void PseudoState_Change_RebuildsSegment_ThenReuses()
    {
        var (headless, button) = BuildButtonTree(_registry);
        var painter = new ElementPainter(ElementTheme.Dark);

        painter.Build(headless.Content, Surface); // 首帧全画
        painter.Build(headless.Content, Surface);
        Assert.Equal((2, 0), painter.LastBuildStats); // 稳定后全复用

        headless.PointerMove(CenterOf(button)); // IsHovered → InvalidateRender → Version++
        var hoveredList = painter.Build(headless.Content, Surface);
        Assert.Equal((1, 1), painter.LastBuildStats); // button 段重建（含 hover 变体色）
        Assert.Contains(hoveredList.Commands.OfType<FillRoundedRect>(),
            f => f.Brush is SolidColorBrush { Color: var c } && c == HoverColor);

        painter.Build(headless.Content, Surface);
        Assert.Equal((2, 0), painter.LastBuildStats); // 状态稳定后恢复全复用
    }

    // ---- E3 :disabled 变体与置灰兜底 ----

    [Fact]
    public void DisabledVariant_AppliesOnDisable_RevertsOnEnable()
    {
        var (headless, button, _) = BuildButtonTreeWithRoot(_registry);
        Assert.Equal(ClassColor, Visual.GetBackground(button));

        button.IsEnabled = false;
        Assert.Equal(PseudoStates.Disabled, button.PseudoStates & PseudoStates.Disabled);
        Assert.Equal(DisabledBg, Visual.GetBackground(button)); // :disabled 变体写入 class 层

        button.IsEnabled = true;
        Assert.Equal(ClassColor, Visual.GetBackground(button)); // 回落 base
    }

    [Fact]
    public void DisabledVariant_Matches_InheritedFromAncestor()
    {
        var (headless, button, root) = BuildButtonTreeWithRoot(_registry);
        Assert.Equal(ClassColor, Visual.GetBackground(button));

        root.IsEnabled = false; // 祖先禁用：后代 Disabled 位命中变体（有效值合成进掩码）
        Assert.Equal(DisabledBg, Visual.GetBackground(button));

        root.IsEnabled = true;
        Assert.Equal(ClassColor, Visual.GetBackground(button));
    }

    [Fact]
    public void Painter_Disabled_FallsBackToGreyTokens()
    {
        var root = new StackPanel();
        var button = new Button { Label = "No", IsEnabled = false }; // 无类：走主题置灰令牌兜底
        root.Children.Add(button);
        new HeadlessRoot(root) { AvailableSize = Surface }.UpdateLayout();

        var painter = new ElementPainter(ElementTheme.Dark);
        var list = painter.Build(root, Surface);

        Assert.Contains(list.Commands.OfType<FillRoundedRect>(),
            f => f.Brush is SolidColorBrush { Color: var c } && c == ElementTheme.Dark.DisabledBackground);
        Assert.DoesNotContain(list.Commands.OfType<FillRoundedRect>(),
            f => f.Brush is SolidColorBrush { Color: var c } && c == ElementTheme.Dark.Accent);
    }

    [Fact]
    public void DisabledVariant_RebuildsSegment_ThenReuses()
    {
        var (headless, button, _) = BuildButtonTreeWithRoot(_registry);
        var painter = new ElementPainter(ElementTheme.Dark);

        painter.Build(headless.Content, Surface);
        painter.Build(headless.Content, Surface);
        Assert.Equal((2, 0), painter.LastBuildStats); // 稳定后全复用

        button.IsEnabled = false;
        var disabledList = painter.Build(headless.Content, Surface);
        Assert.Equal((1, 1), painter.LastBuildStats); // button 段重建（含 :disabled 变体色）
        Assert.Contains(disabledList.Commands.OfType<FillRoundedRect>(),
            f => f.Brush is SolidColorBrush { Color: var c } && c == DisabledBg);

        painter.Build(headless.Content, Surface);
        Assert.Equal((2, 0), painter.LastBuildStats);
    }

    [Fact]
    public void FocusRingWidth_ClassOverride_AppliesToRing()
    {
        var root = new StackPanel();
        var button = new Button { Label = "Go", Classes = ["thickring"] };
        root.Children.Add(button);
        var headless = new HeadlessRoot(root) { AvailableSize = Surface };
        headless.UpdateLayout();
        headless.Focus.Focus(button); // 焦点环出现（width 取 class 覆盖 5）

        var painter = new ElementPainter(ElementTheme.Dark);
        painter.Build(headless.Content, Surface);
        var list = painter.Build(headless.Content, Surface); // 复用帧也含环命令

        var stroke = Assert.Single(list.Commands.OfType<StrokePath>());
        Assert.Equal(5, stroke.Width); // 环宽取 class 值，而非主题令牌 2
    }
}

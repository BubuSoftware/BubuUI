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

    private readonly StyleRegistry _registry = new();

    public StyleTests()
    {
        Styling.Provider = _registry;
        _registry.Define("accent", s => s.Background(ClassColor));
        _registry.Define("text", s => s.Foreground(new Color(10, 10, 10)));
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
}

using System;
using BubuUI.Elements;
using BubuUI.Rendering;
using Xunit;

namespace BubuUI.Rendering.Tests;

/// <summary>
/// S-A 主题令牌化：ElementTheme 是可替换 token 表——ReplaceTheme 换表后段缓存必须
/// 整体失效（段内命令持有旧主题颜色快照）、下一帧整帧重绘且颜色取自新表。
/// </summary>
public class ThemeTests
{
    private static readonly Size Surface = new(240, 160);

    private static StackPanel Tree()
    {
        var root = new StackPanel();
        root.Children.Add(new Button { Label = "Ok" });
        return root;
    }

    private static void Layout(Element root)
    {
        using var host = new HeadlessRoot(FluentTest.Skin(root)) { AvailableSize = Surface };
        host.UpdateLayout();
    }

    [Fact]
    public void PresetThemes_DifferOnKeyTokens()
    {
        Assert.NotEqual(ElementTheme.Dark.Surface, ElementTheme.Light.Surface);
        Assert.NotEqual(ElementTheme.Dark.Foreground, ElementTheme.Light.Foreground);
        Assert.NotEqual(ElementTheme.Dark.Accent, ElementTheme.Light.Accent);
        // 形状令牌两套共享默认（可独立覆盖，这里锁定当前口径）
        Assert.Equal(ElementTheme.Dark.ButtonRadius, ElementTheme.Light.ButtonRadius);
    }

    [Fact]
    public void ReplaceTheme_DropsSegments_NextFrameUsesNewTokens()
    {
        var root = Tree();
        Layout(root);
        var painter = new ElementPainter(FluentTheme.Dark);

        _ = painter.Build(root, Surface);
        Assert.Equal((0, FluentTest.VisualCount(root)), painter.LastBuildStats); // First frame includes the control template visuals.

        _ = painter.Build(root, Surface);
        Assert.Equal((FluentTest.VisualCount(root), 0), painter.LastBuildStats); // 全复用

        painter.ReplaceTheme(FluentTheme.Light);
        painter.Prepare(root);
        Layout(root);
        var relit = painter.Build(root, Surface);
        Assert.Equal((0, FluentTest.VisualCount(root)), painter.LastBuildStats); // 段已随换表失效：全量重画

        // Scoped Fluent colors are reevaluated after switching the application palette.
        var background = relit.Commands.ConvertAll(c => c)
            .OfType<FillRoundedRect>()
            .Select(f => f.Brush)
            .OfType<SolidColorBrush>()
            .First(b => b.Color == FluentTheme.Light.ButtonBackground || b.Color == FluentTheme.Dark.ButtonBackground);
        Assert.Equal(FluentTheme.Light.ButtonBackground, background.Color);

        // 新主题下段恢复复用语义。
        _ = painter.Build(root, Surface);
        Assert.Equal((FluentTest.VisualCount(root), 0), painter.LastBuildStats);
    }

    [Fact]
    public void ReplaceTheme_SameInstance_IsNoOp()
    {
        var root = Tree();
        Layout(root);
        var theme = FluentTheme.Dark;
        var painter = new ElementPainter(theme);

        _ = painter.Build(root, Surface);
        _ = painter.Build(root, Surface);
        Assert.Equal((FluentTest.VisualCount(root), 0), painter.LastBuildStats);

        painter.ReplaceTheme(theme); // 同实例：不清段
        _ = painter.Build(root, Surface);
        Assert.Equal((FluentTest.VisualCount(root), 0), painter.LastBuildStats);
    }

    // ---- S-C：ThemeColor 主题资源 ----

    private static Color? CardFill(IReadOnlyList<DrawCommand> commands, Color dark, Color light)
        => commands.OfType<FillRoundedRect>()
            .Select(f => f.Brush)
            .OfType<SolidColorBrush>()
            .Select(b => (Color?)b.Color)
            .FirstOrDefault(c => c == dark || c == light);

    [Fact]
    public void ThemeColor_ResolvesByThemeLightness()
    {
        var resource = new ThemeColor(Dark: new Color(1, 2, 3), Light: new Color(4, 5, 6));
        Assert.Equal(new Color(1, 2, 3), resource.Resolve(ElementTheme.Dark));
        Assert.Equal(new Color(4, 5, 6), resource.Resolve(ElementTheme.Light));
    }

    [Fact]
    public void BackgroundResource_FollowsThemeSwitch_AndKeepsSegmentSemantics()
    {
        var dark = new Color(31, 37, 50);
        var light = new Color(255, 255, 255);
        // The Button template resolves the attached resource using its scoped palette.
        var root = new StackPanel();
        var card = new Button { Label = "Ok" };
        Visual.SetBackgroundResource(card, new ThemeColor(dark, light));
        root.Children.Add(card);
        Layout(root);

        var painter = new ElementPainter(FluentTheme.Dark);
        var first = painter.Build(root, Surface);
        Assert.Equal(dark, CardFill(first.Commands, dark, light));

        painter.ReplaceTheme(FluentTheme.Light);
        painter.Prepare(root);
        Layout(root);
        var relit = painter.Build(root, Surface);
        Assert.Equal((0, FluentTest.VisualCount(root)), painter.LastBuildStats); // 换表丢段：全量重画
        Assert.Equal(light, CardFill(relit.Commands, dark, light)); // 资源色随新主题解析

        _ = painter.Build(root, Surface);
        Assert.Equal((FluentTest.VisualCount(root), 0), painter.LastBuildStats); // 新主题下恢复复用
    }
}

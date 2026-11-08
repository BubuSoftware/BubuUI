using System;
using EclipseUI.Elements;
using EclipseUI.Rendering;
using Xunit;

namespace EclipseUI.Rendering.Tests;

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
        => new HeadlessRoot(root) { AvailableSize = Surface }.UpdateLayout();

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
        var painter = new ElementPainter(ElementTheme.Dark);

        _ = painter.Build(root, Surface);
        Assert.Equal((0, 2), painter.LastBuildStats); // 首帧全量：root + button

        _ = painter.Build(root, Surface);
        Assert.Equal((2, 0), painter.LastBuildStats); // 全复用

        painter.ReplaceTheme(ElementTheme.Light);
        var relit = painter.Build(root, Surface);
        Assert.Equal((0, 2), painter.LastBuildStats); // 段已随换表失效：全量重画

        // 按钮背景颜色取自新表（Accent），不是旧 Dark 值。
        var background = relit.Commands.ConvertAll(c => c)
            .OfType<FillRoundedRect>()
            .Select(f => f.Brush)
            .OfType<SolidColorBrush>()
            .First(b => b.Color == ElementTheme.Light.Accent || b.Color == ElementTheme.Dark.Accent);
        Assert.Equal(ElementTheme.Light.Accent, background.Color);

        // 新主题下段恢复复用语义。
        _ = painter.Build(root, Surface);
        Assert.Equal((2, 0), painter.LastBuildStats);
    }

    [Fact]
    public void ReplaceTheme_SameInstance_IsNoOp()
    {
        var root = Tree();
        Layout(root);
        var theme = new ElementTheme();
        var painter = new ElementPainter(theme);

        _ = painter.Build(root, Surface);
        _ = painter.Build(root, Surface);
        Assert.Equal((2, 0), painter.LastBuildStats);

        painter.ReplaceTheme(theme); // 同实例：不清段
        _ = painter.Build(root, Surface);
        Assert.Equal((2, 0), painter.LastBuildStats);
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using EclipseUI;
using EclipseUI.Elements;
using EclipseUI.Rendering;
using EclipseUI.Text.Shaping;
using Xunit;

namespace EclipseUI.Rendering.Tests;

/// <summary>
/// 场景图绘制（G2-10）：元素树 → display list 的语义与不变量。
/// 只断言“画了什么、画在哪、栈是否平衡”，像素由后端测试覆盖。
/// </summary>
public class ElementPainterTests
{
    private sealed record PaintedText(string Text, double FontSize, Point Origin, Color Color);

    private sealed class FakeTextPainter : ITextPainter
    {
        public List<PaintedText> Painted { get; } = new();

        public TextLineMetrics Measure(string text, double fontSize)
            => new(fontSize, fontSize * 0.4, text.Length * fontSize * 0.5);

        public void Paint(DisplayList list, string text, double fontSize, Point topLeft, Color color)
            => Painted.Add(new PaintedText(text, fontSize, topLeft, color));
    }

    private static readonly Size DefaultSize = new(600, 400);

    private static void Layout(Element root)
        => new HeadlessRoot(root) { AvailableSize = DefaultSize }.UpdateLayout();

    private static Rect Absolute(Element element)
    {
        double x = element.FinalRect.X, y = element.FinalRect.Y;
        for (var node = element.Parent; node is not null; node = node.Parent)
        {
            x += node.FinalRect.X;
            y += node.FinalRect.Y;
        }
        return new Rect(x, y, element.FinalRect.Width, element.FinalRect.Height);
    }

    private static Color BrushColor(Brush brush) => Assert.IsType<SolidColorBrush>(brush).Color;

    private static TextBlock Text(string text, double fontSize = 14)
    {
        var block = new TextBlock { FontSize = fontSize };
        block.AddRun(new TextRun(text));
        return block;
    }

    [Fact]
    public void Build_FillsSurfaceAndBalancesScopes()
    {
        var root = new StackPanel();
        Layout(root);

        var theme = new ElementTheme();
        var list = new ElementPainter(theme).Build(root, DefaultSize);

        Assert.Equal(DefaultSize, list.Size);
        var surface = Assert.IsType<FillRect>(list.Commands[0]);
        Assert.Equal(new Rect(0, 0, DefaultSize.Width, DefaultSize.Height), surface.Rect);
        Assert.Equal(theme.Surface, BrushColor(surface.Brush));
        Assert.Empty(list.Validate());
    }

    [Fact]
    public void Build_Button_PaintsAccentChromeAndCenteredLabel()
    {
        var root = new StackPanel();
        var button = new Button { Label = "OK" };
        root.Children.Add(button);
        Layout(root);

        var theme = new ElementTheme();
        var text = new FakeTextPainter();
        var list = new ElementPainter(theme, text).Build(root, DefaultSize);

        var rect = Absolute(button);
        var body = list.Commands.OfType<FillRoundedRect>().Single();
        Assert.Equal(rect, body.Rect);
        Assert.Equal(theme.ButtonRadius, body.Radius);
        Assert.Equal(theme.Accent, BrushColor(body.Brush));

        var label = Assert.Single(text.Painted);
        Assert.Equal("OK", label.Text);
        Assert.Equal(theme.OnAccent, label.Color);
        Assert.InRange(label.Origin.X, rect.X, rect.X + rect.Width);
        Assert.InRange(label.Origin.Y, rect.Y, rect.Y + rect.Height);

        Assert.Empty(list.Validate());
    }

    [Fact]
    public void Build_PressedButton_UsesPressedColor()
    {
        var root = new StackPanel();
        var button = new Button { Label = "OK" };
        root.Children.Add(button);
        Layout(root);

        var theme = new ElementTheme();
        var painter = new ElementPainter(theme, new FakeTextPainter());

        // 按下未释放：绘制按下态（不经过平台宿主，直接走 InputRouter）
        var input = new InputRouter(root);
        input.PointerPress(new Point(
            button.FinalRect.X + button.FinalRect.Width / 2,
            button.FinalRect.Y + button.FinalRect.Height / 2));

        var list = painter.Build(root, DefaultSize);
        var body = list.Commands.OfType<FillRoundedRect>().Single();
        Assert.Equal(theme.AccentPressed, BrushColor(body.Brush));
        Assert.True(button.IsPressed);
    }

    [Fact]
    public void Build_CheckBox_CheckedPaintsCheckMark()
    {
        var root = new StackPanel();
        var checkBox = new CheckBox { Label = "enable", Checked = true };
        root.Children.Add(checkBox);
        Layout(root);

        var theme = new ElementTheme();
        var text = new FakeTextPainter();
        var list = new ElementPainter(theme, text).Build(root, DefaultSize);

        var box = list.Commands.OfType<FillRoundedRect>().Single();
        Assert.Equal(theme.Accent, BrushColor(box.Brush));
        Assert.Equal(theme.CheckBoxRadius, box.Radius);
        Assert.Equal(box.Rect.Width, box.Rect.Height);

        var check = Assert.Single(list.Commands.OfType<StrokePath>());
        Assert.Equal(theme.OnAccent, BrushColor(check.Brush));

        var label = Assert.Single(text.Painted);
        Assert.Equal("enable", label.Text);
        Assert.Equal(theme.Foreground, label.Color);
        Assert.True(label.Origin.X >= box.Rect.X + box.Rect.Width, "label must sit right of the box");

        Assert.Empty(list.Validate());
    }

    [Fact]
    public void Build_CheckBox_UncheckedPaintsControlChrome()
    {
        var root = new StackPanel();
        var checkBox = new CheckBox { Label = "enable" };
        root.Children.Add(checkBox);
        Layout(root);

        var theme = new ElementTheme();
        var list = new ElementPainter(theme, new FakeTextPainter()).Build(root, DefaultSize);

        var box = list.Commands.OfType<FillRoundedRect>().Single();
        Assert.Equal(theme.ControlBackground, BrushColor(box.Brush));

        var border = Assert.Single(list.Commands.OfType<StrokePath>());
        Assert.Equal(theme.ControlBorder, BrushColor(border.Brush));
        Assert.Equal(theme.BorderWidth, border.Width);
        Assert.DoesNotContain(list.Commands.OfType<StrokePath>(), s => BrushColor(s.Brush) == theme.OnAccent);
    }

    [Fact]
    public void Build_TextBlock_PaintsRunsWithMutedColorBelowThreshold()
    {
        var root = new StackPanel();
        root.Children.Add(Text("Title", 20));
        root.Children.Add(Text("hint", 12));
        Layout(root);

        var theme = new ElementTheme();
        var text = new FakeTextPainter();
        new ElementPainter(theme, text).Build(root, DefaultSize);

        Assert.Equal(2, text.Painted.Count);
        Assert.Equal(theme.Foreground, text.Painted[0].Color);
        Assert.Equal(theme.MutedForeground, text.Painted[1].Color);
        Assert.True(text.Painted[1].Origin.Y > text.Painted[0].Origin.Y);
    }

    [Fact]
    public void Build_ClipToBounds_PushesClipAroundChildren()
    {
        var root = new StackPanel();
        var inner = new StackPanel { ClipToBounds = true };
        var button = new Button { Label = "clip me" };
        inner.Children.Add(button);
        root.Children.Add(inner);
        Layout(root);

        var list = new ElementPainter(new ElementTheme(), new FakeTextPainter()).Build(root, DefaultSize);

        var innerRect = Absolute(inner);
        var buttonRect = Absolute(button);
        var clips = list.Commands.OfType<PushClip>().ToList();
        Assert.Contains(clips, clip => clip.Rect == innerRect);

        int clipIndex = list.Commands.FindIndex(c => c is PushClip push && push.Rect == innerRect);
        int fillIndex = list.Commands.FindIndex(clipIndex, c => c is FillRoundedRect fill && fill.Rect == buttonRect);
        int popIndex = list.Commands.FindIndex(fillIndex, c => c is PopClip);
        Assert.True(clipIndex >= 0 && fillIndex > clipIndex && popIndex > fillIndex);
        Assert.Empty(list.Validate());
    }

    [Fact]
    public void Build_FocusedButton_DrawsFocusRingAroundControl()
    {
        var root = new StackPanel();
        var button = new Button { Label = "focus" };
        root.Children.Add(button);
        Layout(root);

        var theme = new ElementTheme();
        var list = new ElementPainter(theme, new FakeTextPainter()).Build(root, DefaultSize, focused: button);

        var ring = list.Commands.OfType<StrokePath>().Single(s => BrushColor(s.Brush) == theme.FocusRing);
        Assert.Equal(theme.FocusRingWidth, ring.Width);

        var rect = Absolute(button);
        Assert.Equal(rect.X - theme.FocusRingWidth, ring.Path.Bounds.X, 3);
        Assert.Equal(rect.Y - theme.FocusRingWidth, ring.Path.Bounds.Y, 3);
        Assert.Equal(rect.Width + theme.FocusRingWidth * 2, ring.Path.Bounds.Width, 3);
        Assert.Empty(list.Validate());
    }

    [Fact]
    public void Build_AppliesDpiScaleAsSingleRootTransform()
    {
        var root = new StackPanel();
        root.Children.Add(new Button { Label = "scaled" });
        Layout(root);

        var list = new ElementPainter(new ElementTheme()).Build(root, DefaultSize, scale: 1.5);

        var push = Assert.IsType<PushTransform>(list.Commands[0]);
        Assert.Equal(Transform.Scale(1.5), push.Transform);
        Assert.IsType<PopTransform>(list.Commands[^1]);
        Assert.Equal(DefaultSize, list.Size); // 逻辑单位；缩放只影响设备像素
        Assert.Empty(list.Validate());
    }

    [Fact]
    public void Build_NoScale_EmitsNoTransformScope()
    {
        var root = new StackPanel();
        Layout(root);

        var list = new ElementPainter(new ElementTheme()).Build(root, DefaultSize, scale: 1.0);

        Assert.DoesNotContain(list.Commands, c => c is PushTransform or PopTransform);
        Assert.Empty(list.Validate());
    }

    [Fact]
    public void Build_BlockCoordinators_AreVisuallyTransparent()
    {
        var flag = new Signal<bool>(true);
        var root = new StackPanel();
        var block = new ConditionalBlock();
        block.SetCondition(() => flag.Value, flag);
        block.SetContent(_ => new Element[] { Text("inside block", 16) });
        root.Children.Add(block);
        Layout(root);

        var theme = new ElementTheme();
        var text = new FakeTextPainter();
        var list = new ElementPainter(theme, text).Build(root, DefaultSize);

        Assert.True(block.IsVisualTransparent);
        Assert.True(new ForEachBlock<int>().IsVisualTransparent);

        // 块自身不画卡片；内容照常绘制。
        Assert.DoesNotContain(list.Commands.OfType<FillRoundedRect>(),
            f => BrushColor(f.Brush) == theme.PanelBackground);
        Assert.Contains(text.Painted, p => p.Text == "inside block");
        Assert.Empty(list.Validate());
    }

    [Fact]
    public void Build_PanelPaddingOffsetsChildren()
    {
        var root = new StackPanel { Padding = 12 };
        var button = new Button { Label = "padded" };
        root.Children.Add(button);
        Layout(root);

        var list = new ElementPainter(new ElementTheme()).Build(root, DefaultSize);
        var body = list.Commands.OfType<FillRoundedRect>().Single();
        Assert.Equal(12, body.Rect.X, 3);
        Assert.Equal(12, body.Rect.Y, 3);
        // HeadlessRoot 把根排到 DesiredSize：根宽 = 子宽 + 2 * Padding
        Assert.Equal(root.FinalRect.Width - 24, body.Rect.Width, 3);
    }

    [Fact]
    public void Build_WithHarfBuzz_EmitsShapedGlyphRun()
    {
        var fontPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf");
        if (!File.Exists(fontPath)) return;

        var root = new StackPanel();
        root.Children.Add(Text("Hi", 20));
        Layout(root);

        using var shaper = new HarfBuzzTextShaper(fontPath);
        using var textPainter = new HarfBuzzTextPainter(shaper);
        var list = new ElementPainter(new ElementTheme(), textPainter).Build(root, DefaultSize);

        var glyphs = Assert.Single(list.Commands.OfType<DrawGlyphs>());
        Assert.Equal("Hi", glyphs.Glyphs.Text);
        Assert.Equal(20, glyphs.Glyphs.FontSize);
        Assert.NotEmpty(glyphs.Glyphs.Glyphs);
        Assert.Same(shaper.Face, glyphs.Font);
        Assert.Empty(list.Validate());
    }

    [Fact]
    public void HarfBuzzTextPainter_AsTextMeasurer_MatchesPaintMetrics()
    {
        var fontPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf");
        if (!File.Exists(fontPath)) return;

        using var shaper = new HarfBuzzTextShaper(fontPath);
        using var painter = new HarfBuzzTextPainter(shaper);
        ITextMeasurer measurer = painter;

        var layout = measurer.Measure("G2 glyphs", 18);
        var paint = painter.Measure("G2 glyphs", 18);

        Assert.True(layout.Width > 0);
        Assert.Equal(paint.Width, layout.Width, 3);
        Assert.Equal(paint.Height, layout.Height, 3);
    }
}

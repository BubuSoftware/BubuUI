using System.Globalization;
using EclipseUI.Elements;
using EclipseUI.Text;
using EclipseUI.Text.Shaping;
using Xunit;

namespace EclipseUI.Rendering.Tests;

[CollectionDefinition("Mac text measurement", DisableParallelization = true)]
public sealed class MacTextMeasurementCollection { }

[Collection("Mac text measurement")]
public class MacEmojiRenderingTests
{
    private const string PrimaryFont = "/System/Library/Fonts/Supplemental/Arial.ttf";

    [Theory]
    [InlineData(14)] [InlineData(24)]
    public void TextBoxCaretUsesTheSameEmojiSpacingAsPainting(double size)
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var shaper = new HarfBuzzTextShaper(PrimaryFont);
        using var painter = new HarfBuzzTextPainter(shaper);
        var previous = TextMeasurer.Current;
        try
        {
            TextMeasurer.Current = painter;
            using var editor = new TextBox { Width = 240 };
            editor.SetValue(TextBlock.FontSizeProperty, size);
            using var host = new HeadlessRoot(editor); host.UpdateLayout(); host.Focus.Focus(editor);
            var start = editor.CaretRect.X;
            host.Input.TextInput("🎉🎉🎉"); host.UpdateLayout();
            var single = painter.Measure("🎉", size);
            Assert.Equal(start + single.Width * 3, editor.CaretRect.X, 5);
            Assert.True(editor.CaretRect.Height >= single.Height);
            host.Input.KeyPress(Key.Left);
            Assert.Equal(4, editor.CaretIndex);
            Assert.Equal(start + single.Width * 2, editor.CaretRect.X, 5);
            host.Input.KeyPress(Key.Left);
            Assert.Equal(2, editor.CaretIndex);
            Assert.Equal(start + single.Width, editor.CaretRect.X, 5);
        }
        finally { TextMeasurer.Current = previous; }
    }

    [Fact]
    public void BitmapGlyphOffsetsFollowLocalAndOuterTransforms()
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var shaper = new HarfBuzzTextShaper("/System/Library/Fonts/Apple Color Emoji.ttc");
        var glyph = Assert.Single(shaper.Shape("😀", 24).Glyphs);
        var positioned = new[] { glyph with { XOffset = 3, YOffset = 2 }, glyph with { XOffset = -1, YOffset = -2 } };
        var run = new ShapedBuffer { Text = "😀😀", FontSize = 24, Direction = TextDirection.LeftToRight, Glyphs = positioned };
        var local = Transform.Rotation(.2) * Transform.Translation(20, 8);
        var actual = new DisplayList(new(240, 160));
        actual.Add(new PushTransform(Transform.Scale(1.5)));
        actual.Add(new DrawGlyphs(shaper.Face, run, new(15, 45), Color.Black, local));
        actual.Add(new PopTransform());
        var expected = new DisplayList(new(240, 160));
        expected.Add(new PushTransform(Transform.Scale(1.5)));
        double pen = 0;
        foreach (var item in positioned)
        {
            var single = new ShapedBuffer { Text = "😀", FontSize = 24, Direction = TextDirection.LeftToRight, Glyphs = [item with { XOffset = 0, YOffset = 0 }] };
            expected.Add(new DrawGlyphs(shaper.Face, single, new(15 + pen + item.XOffset, 45 - item.YOffset), Color.Black, local));
            pen += item.XAdvance;
        }
        expected.Add(new PopTransform());
        Assert.Equal(Render.Frame(expected).ContentHash(), Render.Frame(actual).ContentHash());
    }

    [Theory]
    [InlineData(14, 1)]
    [InlineData(14, 1.25)]
    [InlineData(14, 1.5)]
    [InlineData(14, 2)]
    [InlineData(24, 1)]
    [InlineData(24, 1.25)]
    [InlineData(24, 1.5)]
    [InlineData(24, 2)]
    [InlineData(40, 1)]
    [InlineData(40, 1.25)]
    [InlineData(40, 1.5)]
    [InlineData(40, 2)]
    public void AdjacentAppleEmojiKeepTheirAdvanceAndVisibleBounds(double size, double scale)
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var shaper = new HarfBuzzTextShaper(PrimaryFont);
        using var painter = new HarfBuzzTextPainter(shaper);
        const string text = "😀😀😀";
        var single = painter.Measure("😀", size);
        var metrics = painter.Measure(text, size);
        using var system = new NativeEmojiFont(size);
        using var emojiShaper = new HarfBuzzTextShaper("/System/Library/Fonts/Apple Color Emoji.ttc");
        var emojiGlyph = Assert.Single(emojiShaper.Shape("😀", size).Glyphs);
        Assert.Equal(system.Advance(emojiGlyph.GlyphId), single.Width, 5);
        Assert.Equal(single.Width * 3, metrics.Width, 5);
        var list = new DisplayList(new(240, 100));
        list.Add(new PushTransform(Transform.Scale(scale)));
        painter.Paint(list, text, size, new(8, 8), Color.Black);
        list.Add(new PopTransform());
        var run = Assert.Single(list.Commands.OfType<DrawGlyphs>());
        Assert.Equal("Apple Color Emoji.ttc", run.Font.Name);
        Assert.Equal(3, run.Glyphs.Glyphs.Count);
        Assert.All(run.Glyphs.Glyphs, glyph => Assert.True(glyph.GlyphId != 0 && glyph.XAdvance > 0));
        var frame = Render.Frame(list, (int)(240 * scale), (int)(100 * scale));
        for (var index = 0; index < 3; index++)
        {
            var x0 = (int)Math.Floor((8 + single.Width * index) * scale);
            var x1 = (int)Math.Ceiling((8 + single.Width * (index + 1)) * scale);
            var painted = 0;
            for (var y = 0; y < frame.Height; y++)
                for (var x = x0; x < x1; x++)
                    if (frame.GetPixel(x, y).A > 100) painted++;
            Assert.True(painted > size * size * scale * scale * .2, $"Emoji {index} has only {painted} visible pixels at {size}px/{scale} scale.");
        }
        var colors = new HashSet<Color>();
        for (var y = 0; y < frame.Height; y++) for (var x = 0; x < frame.Width; x++)
        {
            var color = frame.GetPixel(x, y);
            if (color.A > 100)
            {
                colors.Add(color);
                Assert.InRange(x, (int)Math.Floor(8 * scale) - 1, (int)Math.Ceiling((8 + metrics.Width) * scale) + 1);
                Assert.InRange(y, (int)Math.Floor(8 * scale) - 1, (int)Math.Ceiling((8 + metrics.Height) * scale) + 1);
            }
        }
        Assert.True(colors.Count > 20, "Expected colored Apple emoji rather than a monochrome missing glyph.");
    }

    [Theory]
    [InlineData("😀")]
    [InlineData("👩‍💻")]
    [InlineData("👍🏽")]
    [InlineData("❤️")]
    [InlineData("1️⃣")]
    public void NarrowTextBlockWrapsAppleEmojiAsWholeTextElements(string emoji)
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var shaper = new HarfBuzzTextShaper(PrimaryFont);
        using var painter = new HarfBuzzTextPainter(shaper);
        var previous = TextMeasurer.Current;
        TextMeasurer.Current = painter;
        try
        {
            const double size = 24;
            using var block = new TextBlock { TextWrapping = TextWrapping.Wrap, Width = painter.Measure(emoji, size).Width + 1 };
            block.FontSize = size;
            block.AddRun(emoji + emoji + emoji);
            using var host = new HeadlessRoot(block) { AvailableSize = new(180, 500) };
            host.UpdateLayout();
            Assert.Equal(3, block.TextLines.Count);
            foreach (var line in block.TextLines)
            {
                var text = string.Concat(line.Segments.Select(segment => segment.Text));
                Assert.Equal(emoji, text);
                Assert.Single(StringInfo.ParseCombiningCharacters(text));
            }
            var display = new ElementPainter(ElementTheme.Light, painter).Build(block, new(180, 500));
            var runs = display.Commands.OfType<DrawGlyphs>().ToArray();
            Assert.Equal(3, runs.Length);
            Assert.All(runs, run =>
            {
                Assert.Equal("Apple Color Emoji.ttc", run.Font.Name);
                Assert.Equal(emoji, run.Glyphs.Text);
                Assert.All(run.Glyphs.Glyphs, glyph => Assert.NotEqual(0, glyph.GlyphId));
            });
            Assert.Empty(display.Validate());
            Assert.True(Render.Frame(display).CountPixelsDifferentFrom(Color.Transparent) > 100);
        }
        finally { TextMeasurer.Current = previous; }
    }
}

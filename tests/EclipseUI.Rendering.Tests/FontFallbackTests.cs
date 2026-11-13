using EclipseUI.Text.Shaping;
using Xunit;

namespace EclipseUI.Rendering.Tests;

public class FontFallbackTests
{
    private static string Font(string name) => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), name);

    [Fact]
    public void DefaultPainterUsesCjkAndEmojiFacesWithinOneLine()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var shaper = new HarfBuzzTextShaper(Font("segoeui.ttf"));
        Assert.Contains(shaper.Shape("中文😀", 28).Glyphs, glyph => glyph.GlyphId == 0);
        using var painter = new HarfBuzzTextPainter(shaper);
        const string text = "Hello 中文 😀 🎉";
        var list = new DisplayList(new(500, 90));
        painter.Paint(list, text, 28, new(8, 8), Color.Black);
        var runs = list.Commands.OfType<DrawGlyphs>().ToArray();
        Assert.Contains(runs, r => r.Font.Name == "segoeui.ttf");
        Assert.Contains(runs, r => r.Font.Name == "msyh.ttc");
        Assert.Contains(runs, r => r.Font.Name == "seguiemj.ttf");
        Assert.All(runs.SelectMany(r => r.Glyphs.Glyphs), glyph => Assert.NotEqual(0, glyph.GlyphId));
        Assert.Equal(text, string.Concat(runs.Select(r => r.Glyphs.Text)));
        Assert.Equal(painter.Measure(text, 28).Width, runs.Sum(r => r.Glyphs.Width), 6);
        Assert.Single(runs.Select(r => r.Origin.Y).Distinct());
        double x = 8;
        foreach (var run in runs) { Assert.Equal(x, run.Origin.X, 6); x += run.Glyphs.Width; }
    }

    [Theory]
    [InlineData("😀")]
    [InlineData("👩‍💻")]
    [InlineData("👍🏽")]
    [InlineData("❤️")]
    [InlineData("1️⃣")]
    public void EmojiTextElementStaysInOneColorFontRun(string text)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var shaper = new HarfBuzzTextShaper(Font("segoeui.ttf"));
        using var painter = new HarfBuzzTextPainter(shaper);
        var list = new DisplayList(new(180, 100));
        painter.Paint(list, text, 40, new(8, 8), Color.Black);
        var run = Assert.Single(list.Commands.OfType<DrawGlyphs>());
        Assert.Equal("seguiemj.ttf", run.Font.Name);
        Assert.Equal(text, run.Glyphs.Text);
        Assert.All(run.Glyphs.Glyphs, glyph => Assert.NotEqual(0, glyph.GlyphId));
    }

    [Fact]
    public void CjkAndEmojiProduceVisiblePixelsWithoutExplicitFontSelection()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var shaper = new HarfBuzzTextShaper(Font("segoeui.ttf"));
        using var painter = new HarfBuzzTextPainter(shaper);
        foreach (var text in new[] { "中文", "🎉" })
        {
            var list = new DisplayList(new(220, 110));
            painter.Paint(list, text, 48, new(8, 8), Color.Black);
            var run = Assert.Single(list.Commands.OfType<DrawGlyphs>());
            Assert.All(run.Glyphs.Glyphs, glyph => Assert.NotEqual(0, glyph.GlyphId));
            var frame = Render.Frame(list);
            Assert.True(frame.CountPixelsDifferentFrom(Color.Transparent) > 150);
            var colors = new HashSet<Color>();
            for (var y = 0; y < frame.Height; y++) for (var x = 0; x < frame.Width; x++)
            {
                var color = frame.GetPixel(x, y);
                if (color.A > 200) colors.Add(color);
            }
            if (text == "🎉") Assert.True(colors.Count > 50, $"Expected color emoji; got {colors.Count} colors.");
        }
    }

    [Fact]
    public void LatinTextKeepsItsOriginalShapingAndKerning()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var shaper = new HarfBuzzTextShaper(Font("segoeui.ttf"));
        using var painter = new HarfBuzzTextPainter(shaper);
        const string text = "AV office affine";
        var expected = shaper.Shape(text, 24);
        var list = new DisplayList(new(500, 80)); painter.Paint(list, text, 24, new(0, 0), Color.Black);
        var run = Assert.Single(list.Commands.OfType<DrawGlyphs>());
        Assert.Same(shaper.Face, run.Font);
        Assert.Equal(expected.Glyphs, run.Glyphs.Glyphs);
        Assert.Equal(expected.Width, painter.Measure(text, 24).Width);
    }

    [Fact]
    public void ExplicitFallbackChainSkipsMissingFontsAndCanBeDisabled()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var shaper = new HarfBuzzTextShaper(Font("segoeui.ttf"));
        using (var painter = new HarfBuzzTextPainter(shaper, [Font("not-installed.ttf"), Font("msyh.ttc"), Font("seguiemj.ttf")]))
        {
            var list = new DisplayList(new(300, 100)); painter.Paint(list, "中文😀", 24, Point.Zero, Color.Black);
            Assert.All(list.Commands.OfType<DrawGlyphs>().SelectMany(r => r.Glyphs.Glyphs), g => Assert.NotEqual(0, g.GlyphId));
        }
        using var singleFont = new HarfBuzzTextPainter(shaper, []);
        var disabled = new DisplayList(new(300, 100)); singleFont.Paint(disabled, "中文", 24, Point.Zero, Color.Black);
        Assert.Same(shaper.Face, Assert.Single(disabled.Commands.OfType<DrawGlyphs>()).Font);
        Assert.Contains(disabled.Commands.OfType<DrawGlyphs>().SelectMany(r => r.Glyphs.Glyphs), g => g.GlyphId == 0);
    }

    [Fact]
    public void CacheAndDisposalKeepTheCallerOwnedPrimaryFontUsable()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var shaper = new HarfBuzzTextShaper(Font("segoeui.ttf"));
        var painter = new HarfBuzzTextPainter(shaper);
        var first = new DisplayList(new(300, 100)); painter.Paint(first, "中文😀", 24, Point.Zero, Color.Black);
        var second = new DisplayList(new(300, 100)); painter.Paint(second, "中文😀", 24, Point.Zero, Color.Black);
        Assert.Equal(first.Commands, second.Commands);
        painter.Dispose(); painter.Dispose();
        Assert.Throws<ObjectDisposedException>(() => painter.Measure("中文", 24));
        Assert.True(shaper.Shape("still usable", 24).Width > 0);
    }

    [Fact]
    public void TextAndEmojiPresentationSelectorsCanCoexistOnTheSameLine()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var shaper = new HarfBuzzTextShaper(Font("segoeui.ttf"));
        using var painter = new HarfBuzzTextPainter(shaper);
        var list = new DisplayList(new(300, 100)); painter.Paint(list, "❤︎ ❤️", 28, Point.Zero, Color.Black);
        var runs = list.Commands.OfType<DrawGlyphs>().ToArray();
        Assert.NotEqual("seguiemj.ttf", runs[0].Font.Name);
        Assert.Contains("❤︎", runs[0].Glyphs.Text);
        Assert.Equal("seguiemj.ttf", runs[^1].Font.Name);
        Assert.Equal("❤︎ ❤️", string.Concat(runs.Select(r => r.Glyphs.Text)));
    }
}

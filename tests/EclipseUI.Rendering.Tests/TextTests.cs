using EclipseUI;
using EclipseUI.Rendering;
using EclipseUI.Text;
using EclipseUI.Text.Shaping;
using Xunit;

namespace EclipseUI.Rendering.Tests;

public class TextTests
{
    private static readonly string FontPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf");

    private static bool FontAvailable => File.Exists(FontPath);

    [Fact]
    public void GlyphRun_PaintsPixelsWithinShapedWidth()
    {
        if (!FontAvailable) return;

        using var shaper = new HarfBuzzTextShaper(FontPath);
        var shaped = shaper.Shape("AV", 48);
        var font = FontFace.Load(FontPath);

        var list = new DisplayList(new Size(200, 100))
            .Add(new DrawGlyphs(font, shaped, new Point(20, 70), Color.White));

        var frame = Render.Frame(list);

        Assert.True(frame.CountPixelsDifferentFrom(Color.Transparent) > 50, "glyph run painted nothing");

        var bounds = frame.OpaqueBounds();
        // 绘制宽度必须与布局测量一致（容差 1px/100px + 抗锯齿边缘）
        Assert.InRange(bounds.Width, shaped.Width - 2, shaped.Width + 4);
        Assert.InRange(bounds.Height, shaped.FontSize * 0.5, shaped.FontSize * 1.5);
    }

    [Fact]
    public void GlyphRun_RenderingIsDeterministic()
    {
        if (!FontAvailable) return;

        using var shaper = new HarfBuzzTextShaper(FontPath);
        var shaped = shaper.Shape("Render gate", 24);
        var font = FontFace.Load(FontPath);

        DisplayList Build() => new DisplayList(new Size(300, 80))
            .Add(new DrawGlyphs(font, shaped, new Point(10, 50), new Color(200, 220, 255)));

        var first = Render.Frame(Build());
        var second = Render.Frame(Build());

        Assert.Equal(first.ContentHash(), second.ContentHash());
    }

    [Fact]
    public void GlyphRun_EmptyText_DrawsNothing()
    {
        if (!FontAvailable) return;

        using var shaper = new HarfBuzzTextShaper(FontPath);
        var shaped = shaper.Shape("", 24);
        var font = FontFace.Load(FontPath);

        var list = new DisplayList(new Size(64, 64))
            .Add(new DrawGlyphs(font, shaped, new Point(8, 32), Color.White));

        var frame = Render.Frame(list);

        Assert.Equal(0, frame.CountPixelsDifferentFrom(Color.Transparent));
    }

    [Fact]
    public void GlyphRun_RespectsTransformInput()
    {
        if (!FontAvailable) return;

        using var shaper = new HarfBuzzTextShaper(FontPath);
        var shaped = shaper.Shape("M", 36);
        var font = FontFace.Load(FontPath);

        var list = new DisplayList(new Size(120, 60));
        list.Add(new DrawGlyphs(font, shaped, new Point(8, 40), Color.White, Transform.Scale(2)));
        var frame = Render.Frame(list);

        var unscaled = Render.Frame(new DisplayList(new Size(120, 60))
            .Add(new DrawGlyphs(font, shaped, new Point(8, 40), Color.White)));

        Assert.True(frame.OpaqueBounds().Width > unscaled.OpaqueBounds().Width * 1.6);
    }
}

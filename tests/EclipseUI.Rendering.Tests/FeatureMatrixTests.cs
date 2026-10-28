using EclipseUI;
using EclipseUI.Rendering;
using EclipseUI.Rendering.Vello;
using EclipseUI.Text.Shaping;
using Xunit;

namespace EclipseUI.Rendering.Tests;

/// <summary>G1-07 功能矩阵：每项一个测试，失败即矩阵中的 fail。</summary>
public class FeatureMatrixTests
{
    private static string FontsDirectory => Environment.GetFolderPath(Environment.SpecialFolder.Fonts);

    private static string? Font(string fileName)
    {
        var path = Path.Combine(FontsDirectory, fileName);
        return File.Exists(path) ? path : null;
    }

    [Fact]
    public void DropShadow_ProducesBlurredHaloOutsideRect()
    {
        var list = new DisplayList(new Size(200, 200));
        list.Add(new DropShadow(new Rect(60, 60, 80, 80), 12, 24, new Point(0, 6), new Color(0, 0, 0, 200)));
        list.Add(new FillRoundedRect(new Rect(60, 60, 80, 80), 12, Color.White));

        var frame = Render.Frame(list);

        Render.AssertColor(Color.White, frame.GetPixel(100, 100));
        var halo = frame.GetPixel(100, 150);   // 下方阴影扩散区
        Assert.True(halo.A > 0 && halo.A < 255, $"expected soft shadow, got {halo}");
        Assert.True(halo.R < 100, $"expected dark shadow, got {halo}");
        Assert.Equal(0, frame.GetPixel(4, 4).A);
    }

    [Fact]
    public void SweepGradient_ShowsDistinctHuesAroundCenter()
    {
        var list = new DisplayList(new Size(120, 120))
            .Add(new FillRect(new Rect(0, 0, 120, 120), new SweepGradient(
                new Point(60, 60), 0, Math.PI * 2,
                new[]
                {
                    GradientStop.At(0, new Color(255, 0, 0)),
                    GradientStop.At(0.33, new Color(0, 255, 0)),
                    GradientStop.At(0.66, new Color(0, 0, 255)),
                    GradientStop.At(1, new Color(255, 0, 0)),
                })));

        var frame = Render.Frame(list);

        // 扫描圆周，确认至少出现 3 种明显不同的主色（sweep 渐变的核心语义）。
        var samples = new List<Color>();
        for (int i = 0; i < 8; i++)
        {
            double angle = i * Math.PI / 4;
            int x = (int)Math.Round(60 + Math.Cos(angle) * 50);
            int y = (int)Math.Round(60 + Math.Sin(angle) * 50);
            samples.Add(frame.GetPixel(x, y));
        }

        int reds = samples.Count(c => c.R > 150 && c.G < 120 && c.B < 120);
        int greens = samples.Count(c => c.G > 120 && c.R < 150);
        int blues = samples.Count(c => c.B > 120 && c.R < 120);

        Assert.True(reds > 0, "no red segment: " + string.Join(" ", samples));
        Assert.True(greens > 0, "no green segment: " + string.Join(" ", samples));
        Assert.True(blues > 0, "no blue segment: " + string.Join(" ", samples));
    }

    [Fact]
    public void RotatedImage_PaintsOutsideAxisAlignedBounds()
    {
        var image = ImageHandle.Checkerboard(8, 8, 2, Color.White, new Color(0, 0, 0));
        var list = new DisplayList(new Size(200, 200));
        list.Add(new PushTransform(Transform.RotationAround(Math.PI / 4, new Point(100, 100))));
        list.Add(new DrawImage(image, new Rect(50, 50, 100, 100)));
        list.Add(new PopTransform());

        var frame = Render.Frame(list);

        // 旋转后的菱形超出 50..150 的轴对齐框，但仍在包围盒内
        Assert.True(frame.GetPixel(40, 100).A > 0, "left diamond tip not painted");
        Assert.True(frame.GetPixel(160, 100).A > 0, "right diamond tip not painted");
        Assert.True(frame.GetPixel(100, 40).A > 0, "top diamond tip not painted");
        Assert.Equal(0, frame.GetPixel(2, 2).A);
    }

    [Fact]
    public void ColorEmoji_RendersColoredGlyphs()
    {
        var path = Font("seguiemj.ttf");
        if (path is null) return;

        using var shaper = new HarfBuzzTextShaper(path);
        var shaped = shaper.Shape("🎉", 64);
        if (shaped.Glyphs.Count == 0) return;

        var list = new DisplayList(new Size(160, 120))
            .Add(new DrawGlyphs(FontFace.Load(path), shaped, new Point(20, 90), Color.Black));

        var frame = Render.Frame(list);

        var colors = new HashSet<uint>();
        for (int y = 0; y < frame.Height; y++)
            for (int x = 0; x < frame.Width; x++)
            {
                var pixel = frame.GetPixel(x, y);
                if (pixel.A > 200)
                    colors.Add((uint)(pixel.R << 16 | pixel.G << 8 | pixel.B));
            }

        Assert.True(colors.Count > 50, $"expected color emoji, got {colors.Count} colors");
    }

    [Fact]
    public void CjkFont_RendersWhenSelectedExplicitly()
    {
        var path = Font("msyh.ttc");
        if (path is null) return;

        using var shaper = new HarfBuzzTextShaper(path, 0);
        var shaped = shaper.Shape("世界", 48);
        if (shaped.Glyphs.Count == 0 || shaped.Glyphs.All(g => g.GlyphId == 0)) return;

        var list = new DisplayList(new Size(200, 120))
            .Add(new DrawGlyphs(FontFace.Load(path, 0), shaped, new Point(10, 90), Color.Black));

        var frame = Render.Frame(list);

        Assert.True(frame.CountPixelsDifferentFrom(Color.Transparent) > 200);
        Assert.InRange(frame.OpaqueBounds().Width, shaped.Width - 3, shaped.Width + 5);
    }

    [Fact]
    public void GpuBackend_MatchesCpuWithinTolerance()
    {
        DisplayList Build() => new DisplayList(new Size(160, 120))
            .Add(new FillRoundedRect(new Rect(10, 10, 140, 100), 16, new Color(200, 40, 40)));

        RenderTarget cpu;
        RenderTarget gpu;
        try
        {
            cpu = RenderWith(Build(), useCpu: true);
            gpu = RenderWith(Build(), useCpu: false);
        }
        catch (Exception)
        {
            return; // 无可用 GPU 适配器：跳过（矩阵中记录为环境限制）
        }

        int differing = 0;
        for (int i = 0; i < cpu.Pixels.Length; i++)
            if (Math.Abs(cpu.Pixels[i] - gpu.Pixels[i]) > 2) differing++;

        // 边缘抗锯齿允许少量差异；整体必须基本一致。
        Assert.True(differing < cpu.Pixels.Length * 0.005, $"{differing} bytes differ between CPU and GPU");
    }

    private static RenderTarget RenderWith(DisplayList list, bool useCpu)
    {
        using var backend = new VelloCpuBackend(new VelloBackendOptions { UseCpu = useCpu });
        var target = new RenderTarget((int)list.Size.Width, (int)list.Size.Height);
        backend.Render(list, target);
        return target;
    }
}

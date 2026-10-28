using System;
using System.IO;
using System.Linq;
using EclipseUI.Elements;
using EclipseUI.Text;
using EclipseUI.Text.Shaping;
using Xunit;

namespace EclipseUI.Headless.Tests;

/// <summary>
/// M1 文本 shaping spike：真实字体（Windows 自带 arial.ttf）下验证 shaping→测量。
/// 无字体的环境自动跳过断言（测试体直接返回）。
/// </summary>
public class ShapingTests
{
    private static string FontPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts", "arial.ttf");

    private static bool FontAvailable => File.Exists(FontPath);

    [Fact]
    public void Shaping_IsProportional_NotMonospace()
    {
        if (!FontAvailable) return;
        using var shaper = new HarfBuzzTextShaper(FontPath);
        double narrow = shaper.Shape("iiii", 16).Width;
        double wide = shaper.Shape("WWWW", 16).Width;
        Assert.True(wide > narrow * 1.5, $"expected {wide} >> {narrow}");
    }

    [Fact]
    public void Shaping_IsDeterministic()
    {
        if (!FontAvailable) return;
        using var shaper = new HarfBuzzTextShaper(FontPath);
        double first = shaper.Shape("Hello 世界", 14).Width;
        double second = shaper.Shape("Hello 世界", 14).Width;
        Assert.Equal(first, second, 10);
    }

    [Fact]
    public void Shaping_EmptyText_HasNoGlyphs()
    {
        if (!FontAvailable) return;
        using var shaper = new HarfBuzzTextShaper(FontPath);
        var shaped = shaper.Shape("", 14);
        Assert.Empty(shaped.Glyphs);
        Assert.Equal(0, shaped.Width, 10);
    }

    [Fact]
    public void Shaping_Rtl_ClustersDescend()
    {
        if (!FontAvailable) return;
        using var shaper = new HarfBuzzTextShaper(FontPath);
        var shaped = shaper.Shape("سلام", 16, TextDirection.RightToLeft);
        Assert.NotEmpty(shaped.Glyphs);
        Assert.True(shaped.Width > 0);
        var clusters = shaped.Glyphs.Select(g => g.Cluster).ToArray();
        Assert.Equal(clusters.OrderByDescending(c => c), clusters); // RTL 簇序递减
    }

    [Fact]
    public void Shaper_PlugsIntoTextMeasurer_AndLayout()
    {
        if (!FontAvailable) return;
        using var shaper = new HarfBuzzTextShaper(FontPath);
        var previous = TextMeasurer.Current;
        try
        {
            TextMeasurer.Current = shaper;
            var metrics = shaper.Measure("AV", 14);

            var block = new TextBlock();
            block.AddRun(new TextRun("AV"));
            block.Measure(new Size(1000, 1000));

            Assert.Equal(metrics.Width, block.DesiredSize.Width, 5); // 布局测量 = shaping 宽度
            Assert.InRange(metrics.Height, 14 * 0.8, 14 * 2.2); // arial 行盒应在字号量级
        }
        finally
        {
            TextMeasurer.Current = previous;
        }
    }
}

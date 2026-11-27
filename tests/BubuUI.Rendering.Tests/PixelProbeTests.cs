using System;
using BubuUI;
using BubuUI.Rendering;
using Xunit;
using Xunit.Sdk;

namespace BubuUI.Rendering.Tests;

/// <summary>
/// 像素探针 DSL 自身语义：用手工构造的帧验证各断言的成功/失败路径与诊断内容，
/// 不依赖渲染后端。
/// </summary>
public class PixelProbeTests
{
    // 8x6 帧：深灰背景 + x∈[2,6),y∈[1,4) 红块（12 像素）+ (6,4) 半透明绿点 → 背景 48-12-1=35。
    private static RenderTarget TestFrame()
    {
        var frame = new RenderTarget(8, 6);
        frame.Clear(new Color(20, 20, 20));
        for (int y = 1; y < 4; y++)
        {
            for (int x = 2; x < 6; x++)
                frame.SetPixel(x, y, new Color(200, 30, 30));
        }
        frame.SetPixel(6, 4, new Color(40, 200, 90, 128));
        return frame;
    }

    private static readonly Rect RedBlock = new(2, 1, 4, 3);

    [Fact]
    public void At_PassesOnMatch_AndAggregatesFailures()
    {
        var probe = PixelProbe.On(TestFrame(), "t")
            .At(3, 2, new Color(200, 30, 30))       // 命中
            .At(0, 0, new Color(20, 20, 20))        // 命中
            .At(0, 0, new Color(255, 255, 255))     // 错
            .At(99, 99, Color.Black);               // 越界

        Assert.Equal(2, probe.Failures.Count);
        Assert.Contains("(0,0)", probe.Failures[0]);
        Assert.Contains("#FFFFFF", probe.Failures[0]);
        Assert.Contains("越界", probe.Failures[1]);

        var ex = Assert.Throws<FailException>(() => probe.Verify());
        Assert.Contains("[t]", ex.Message);
        Assert.Contains("失败 2 项", ex.Message);
    }

    [Fact]
    public void AlphaAt_ChecksRange()
    {
        PixelProbe.On(TestFrame())
            .AlphaAt(6, 4, 100, 150)                // 半透明绿点
            .AlphaAt(0, 0, 250, 255)                // 背景不透明
            .Verify();

        var probe = PixelProbe.On(TestFrame()).AlphaAt(6, 4, 250, 255);
        var ex = Assert.Throws<FailException>(() => probe.Verify());
        Assert.Contains("A∈[250,255]", ex.Message);
        Assert.Contains("实际 128", ex.Message);
    }

    [Fact]
    public void DominantColor_RegionRatio_And_Diagnostics()
    {
        // 红块区域全覆盖。
        PixelProbe.On(TestFrame(), "t")
            .DominantColor(RedBlock, new Color(200, 30, 30), minRatio: 1.0)
            .Verify();

        // 区域扩到全帧：背景 35/48 ≈ 72.9%，红块 25.0%，绿点 1。
        var expanded = new Rect(0, 0, 8, 6);
        PixelProbe.On(TestFrame())
            .DominantColor(expanded, new Color(20, 20, 20), minRatio: 0.7)
            .Verify();

        // 反向期望失败：区域主色是背景（72.9%），不是红（25.0%）。
        var probe = PixelProbe.On(TestFrame()).DominantColor(expanded, new Color(200, 30, 30), minRatio: 0.5);
        var ex = Assert.Throws<FailException>(() => probe.Verify());
        Assert.Contains("覆盖率", ex.Message);
        Assert.Contains("25.0%", ex.Message);           // 匹配率诊断（12/48）
        Assert.Contains("35x#141414FF", ex.Message);    // 直方图 top1：20,20,20 = #141414
    }

    [Fact]
    public void Contains_NotContains_And_FirstHitDiagnostic()
    {
        PixelProbe.On(TestFrame(), "t")
            .Contains(RedBlock, new Color(200, 30, 30), minCount: 10)
            .NotContains(new Rect(0, 4, 8, 2), new Color(200, 30, 30))
            .Verify();

        // NotContains 失败给计数与首命中点。
        var probe = PixelProbe.On(TestFrame()).NotContains(RedBlock, new Color(200, 30, 30));
        var ex = Assert.Throws<FailException>(() => probe.Verify());
        Assert.Contains("实际 12 个", ex.Message);
        Assert.Contains("首个 (2,1)", ex.Message);

        // Contains 失败给实际计数。
        probe = PixelProbe.On(TestFrame()).Contains(RedBlock, Color.White, minCount: 1);
        ex = Assert.Throws<FailException>(() => probe.Verify());
        Assert.Contains("实际 0 个", ex.Message);
    }

    [Fact]
    public void CountIn_And_FullFrameCount()
    {
        PixelProbe.On(TestFrame(), "t")
            .CountIn(RedBlock, new Color(200, 30, 30), 12, 12)  // 区域计数精确
            .Count(new Color(20, 20, 20), 30, 40)               // 全帧背景 = 48-12-1 = 35
            .Verify();

        var probe = PixelProbe.On(TestFrame()).Count(new Color(20, 20, 20), 0, 30);
        var ex = Assert.Throws<FailException>(() => probe.Verify());
        Assert.Contains("实际 35", ex.Message);
    }

    [Fact]
    public void SameAs_SameAsOutside_ReportFirstMismatch()
    {
        var frame = TestFrame();
        var other = TestFrame();
        other.SetPixel(0, 0, Color.White);   // 区域外改动
        other.SetPixel(3, 2, Color.Black);   // 区域内改动

        // 区域外一致 → 检出区域内唯一差异。
        var probe = PixelProbe.On(frame, "t").SameAsOutside(other, RedBlock);
        Assert.Single(probe.Failures);
        Assert.Contains("区域外", probe.Failures[0]);
        Assert.Contains("(0,0)", probe.Failures[0]);

        // 区域内一致 → 检出区域外改动。
        probe = PixelProbe.On(frame).SameAs(other, RedBlock);
        Assert.Single(probe.Failures);
        Assert.Contains("(3,2)", probe.Failures[0]);

        // 无差异 → 通过。
        PixelProbe.On(frame).SameAs(TestFrame(), new Rect(0, 0, 8, 6)).Verify();

        // 长度不一致 → 单条失败。
        probe = PixelProbe.On(frame).SameAs(new byte[3], new Rect(0, 0, 8, 6));
        Assert.Single(probe.Failures);
        Assert.Contains("长度不一致", probe.Failures[0]);
    }

    [Fact]
    public void EmptyRegion_Semantics()
    {
        var outside = new Rect(0, 0, 0, 0); // 帧外空区域
        PixelProbe.On(TestFrame(), "t")
            .NotContains(outside, Color.Black)              // 空区域自然不含 → 通过
            .Verify();

        var probe = PixelProbe.On(TestFrame()).DominantColor(outside, Color.Black);
        Assert.Single(probe.Failures);                      // 空区域无主色 → 失败

        probe = PixelProbe.On(TestFrame()).Contains(outside, Color.Black, minCount: 1);
        Assert.Single(probe.Failures);                      // 空区域出现 0 < 1 → 失败
    }

    [Fact]
    public void Failures_ExposeWithoutVerify()
    {
        var probe = PixelProbe.On(TestFrame()).At(0, 0, Color.White);
        Assert.Single(probe.Failures);  // 不调 Verify 也能检查（用于 golden 聚合诊断）
    }
}

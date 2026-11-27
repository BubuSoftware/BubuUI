using BubuUI.Rendering.Vello;
using Xunit;

namespace BubuUI.Rendering.Tests;

public class GpuDamageRegionTests
{
    [Fact]
    public void ColdFrameAndExplicitFullDamageCoverTheSurface()
    {
        var empty = new FrameDamage();
        Assert.Equal(new GpuDamageRegion(0, 0, 800, 600), GpuDamageRegion.Resolve(empty, 800, 600, 2, false));
        empty.AddFull();
        Assert.Equal(new GpuDamageRegion(0, 0, 800, 600), GpuDamageRegion.Resolve(empty, 800, 600, 2, true));
    }

    [Fact]
    public void EmptyAndOffscreenDamageDoNotRasterize()
    {
        Assert.Equal(0, GpuDamageRegion.Resolve(new FrameDamage(), 800, 600, 2, true).Area);
        var outside = new FrameDamage(); outside.Add(new(500, 400, 20, 20));
        Assert.Equal(0, GpuDamageRegion.Resolve(outside, 800, 600, 2, true).Area);
    }

    [Theory]
    [InlineData(1, 10, 20, 31, 41)]
    [InlineData(1.25, 12, 25, 39, 51)]
    [InlineData(1.5, 15, 30, 46, 61)]
    [InlineData(2, 20, 40, 61, 81)]
    public void FractionalDamageScalesAndRoundsOutward(double scale, int x, int y, int width, int height)
    {
        var damage = new FrameDamage(); damage.Add(new(10.1, 20.1, 30.2, 40.2));
        Assert.Equal(new GpuDamageRegion(x, y, width, height), GpuDamageRegion.Resolve(damage, 800, 600, scale, true));
    }

    [Fact]
    public void MultipleDirtyAreasIncludeTheirBackgroundAndRemainInsideTheSurface()
    {
        var damage = new FrameDamage(); damage.Add(new(-5, -10, 20, 25)); damage.Add(new(70, 40, 50, 80));
        Assert.Equal(new GpuDamageRegion(0, 0, 100, 80), GpuDamageRegion.Resolve(damage, 100, 80, 1, true));
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)]
    public void InvalidScaleIsRejected(double scale)
        => Assert.Throws<ArgumentOutOfRangeException>(() => GpuDamageRegion.Resolve(new FrameDamage(), 100, 80, scale, true));
}

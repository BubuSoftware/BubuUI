using BubuUI.Rendering;
using BubuUI.Rendering.Vello;
using Xunit;

namespace BubuUI.Rendering.Tests;

public sealed class SoftwareShadowTests
{
    [Theory]
    [InlineData(24)]
    [InlineData(64)]
    public void PopupShadowHasSmoothGaussianFalloffAtLightAndDarkOpacity(byte opacity)
    {
        using var backend = new VelloSparseBackend();
        var target = new RenderTarget(160, 140);
        var list = new DisplayList(target.Size)
            .Add(new DropShadow(new(40, 40, 80, 60), 8, 8, new(0, 4), new(0, 0, 0, opacity)));
        backend.Render(list, target);
        var falloff = Enumerable.Range(104, 12).Select(y => target.GetPixel(80, y).A).ToArray();
        Assert.True(falloff.Distinct().Count() >= 5, string.Join(",", falloff));
        Assert.True(falloff.Zip(falloff.Skip(1), (near, far) => near >= far).All(decreasing => decreasing));
        Assert.InRange(falloff[0], 1, opacity - 1);
        // Four pixels beyond the edge is about one sigma for CSS blur-radius 8.
        Assert.InRange(target.GetPixel(80, 108).A,
            (int)Math.Round(opacity * 0.13) - 1, (int)Math.Round(opacity * 0.13) + 1);
        Assert.Equal(0, falloff[^1]);
        Assert.Equal(0, target.GetPixel(80, 117).A);
        Assert.Equal(opacity, target.GetPixel(80, 70).A);
        Assert.True(backend.Capabilities.HasFlag(RenderCapabilities.Blur));
    }

    [Fact]
    public void ColoredShadowUsesPremultipliedAlphaWithoutDarkFringes()
    {
        using var backend = new VelloSparseBackend();
        var target = new RenderTarget(160, 140);
        var shadow = new DropShadow(new(40, 40, 80, 60), 8, 8, Point.Zero, new(200, 100, 50, 128));
        backend.Render(new DisplayList(target.Size).Add(shadow), target);
        Render.AssertColor(new(100, 50, 25, 128), target.GetPixel(80, 70), 1);
        var edge = target.GetPixel(80, 102);
        Assert.InRange(edge.A, 1, 100);
        Render.AssertColor(new((byte)(200 * edge.A / 255), (byte)(100 * edge.A / 255),
            (byte)(50 * edge.A / 255), edge.A), edge, 1);

        backend.Render(new DisplayList(target.Size)
            .Add(new FillRect(new(Point.Zero, target.Size), Color.White)).Add(shadow), target);
        var composite = target.GetPixel(80, 102);
        Render.AssertColor(new((byte)(edge.R + 255 - edge.A), (byte)(edge.G + 255 - edge.A),
            (byte)(edge.B + 255 - edge.A)), composite, 1);
    }

    [Fact]
    public void RoundedShadowFollowsDpiOffsetTranslationAndClipping()
    {
        using var backend = new VelloSparseBackend();
        var transformed = new RenderTarget(220, 180);
        backend.Render(new DisplayList(transformed.Size)
            .Add(new PushTransform(Transform.Scale(2) * Transform.Translation(7, 9)))
            .Add(new DropShadow(new(20, 20, 40, 30), 8, 10, new(3, 5), new(0, 0, 0, 128)))
            .Add(new PopTransform()), transformed);
        var explicitGeometry = new RenderTarget(220, 180);
        backend.Render(new DisplayList(explicitGeometry.Size)
            .Add(new DropShadow(new(47, 49, 80, 60), 16, 20, new(6, 10), new(0, 0, 0, 128))), explicitGeometry);
        Assert.Equal(explicitGeometry.ContentHash(), transformed.ContentHash());
        Assert.True(transformed.GetPixel(53, 59).A < transformed.GetPixel(93, 59).A / 2);
        Assert.True(transformed.GetPixel(93, 121).A > 0);

        backend.Render(new DisplayList(transformed.Size)
            .Add(new PushClip(new(70, 70, 50, 40)))
            .Add(new DropShadow(new(47, 49, 80, 60), 16, 20, new(6, 10), new(0, 0, 0, 128)))
            .Add(new PopClip()), transformed);
        Assert.Equal(0, transformed.GetPixel(69, 90).A);
        Assert.True(transformed.GetPixel(71, 90).A > 0);
    }

    [Fact]
    public void ZeroBlurKeepsTheOffsetSilhouetteAndEmptyFramesClearCachedShadows()
    {
        using var backend = new VelloSparseBackend();
        var target = new RenderTarget(120, 100);
        backend.Render(new DisplayList(target.Size)
            .Add(new DropShadow(new(20, 20, 60, 40), 8, 0, new(4, 6), new(0, 0, 0, 64))), target);
        Assert.Equal(64, target.GetPixel(50, 50).A);
        Assert.Equal(0, target.GetPixel(50, 25).A);
        Assert.Equal(0, target.GetPixel(24, 26).A);
        backend.Render(new DisplayList(target.Size), target);
        Assert.Equal(0, target.GetPixel(50, 50).A);
    }

    [Fact]
    public void CachePressureAndRepeatedFramesKeepAllShadowImagesAliveUntilRasterization()
    {
        using var backend = new VelloSparseBackend();
        var target = new RenderTarget(820, 100);
        var list = new DisplayList(target.Size);
        for (var index = 0; index < 20; index++)
            list.Add(new DropShadow(new(12 + index * 40, 25, 20 + index / 10.0, 30),
                4, 8, new(0, 4), new(0, 0, 0, (byte)(64 + index))));
        backend.Render(list, target);
        var hash = target.ContentHash();
        for (var index = 0; index < 20; index++) Assert.InRange(target.GetPixel(22 + index * 40, 60).A, 1, 63);
        backend.Render(list, target);
        Assert.Equal(hash, target.ContentHash());
    }
}

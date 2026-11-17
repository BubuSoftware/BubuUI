using System.Numerics;
using EclipseUI.Rendering.Vello;
using VelloSharp;
using Xunit;
using LayerBlend = EclipseUI.Rendering.LayerBlend;

namespace EclipseUI.Rendering.Tests;

public class PatchRasterizationTests
{
    private static readonly Size LogicalSize = new(160, 120);
    private static readonly ImageHandle Image = ImageHandle.Checkerboard(16, 16, 2, new(220, 70, 30, 170), new(20, 130, 240, 230));

    [Theory]
    [InlineData("shadow", 1)] [InlineData("shadow", 1.25)] [InlineData("shadow", 1.5)] [InlineData("shadow", 2)]
    [InlineData("layer", 1)] [InlineData("layer", 1.25)] [InlineData("layer", 1.5)] [InlineData("layer", 2)]
    [InlineData("image", 1)] [InlineData("image", 1.25)] [InlineData("image", 1.5)] [InlineData("image", 2)]
    public void RasterizingASmallTranslatedScenePatchMatchesTheFullFrame(string kind, double scale)
    {
        var width = (int)(LogicalSize.Width * scale); var height = (int)(LogicalSize.Height * scale);
        var oldList = MakeScene(kind, scale, 0);
        var nextList = MakeScene(kind, scale, 1);
        var frame = Render.Frame(oldList, width, height);
        var expected = Render.Frame(nextList, width, height);
        var damage = new FrameDamage(); damage.Add(new(19.3, 18.7, 100.1, 77.4));
        var region = GpuDamageRegion.Resolve(damage, width, height, scale, true);
        Assert.True(region.Area > 0 && region.Area < (long)width * height);
        AssertUnchangedOutsideRegion(expected, frame, region);
        ApplyPatch(frame, nextList, region);
        AssertPixelsEqual(expected, frame);

        // Removing content has to restore the background in the same retained region.
        var removed = MakeScene("none", scale, 0);
        var removedFrame = Render.Frame(removed, width, height);
        AssertUnchangedOutsideRegion(removedFrame, frame, region);
        ApplyPatch(frame, removed, region);
        AssertPixelsEqual(removedFrame, frame);
    }

    private static DisplayList MakeScene(string kind, double scale, int step)
    {
        var list = new DisplayList(LogicalSize)
            .Add(new PushTransform(Transform.Scale(scale)))
            .Add(new FillRect(new(Point.Zero, LogicalSize), new Color(235, 230, 220)))
            .Add(new FillRoundedRect(new(8, 8, 140, 100), 8, new Color(170, 190, 210)))
            .Add(new PushClipPath(PathGeometry.RoundedRectangle(new(12, 12, 132, 92), 10)));
        switch (kind)
        {
            case "shadow":
                var rect = new Rect(40 + step * 3, 36 + step, 30, 24);
                list.Add(new DropShadow(rect, 6, 8, new(3, 5), new(0, 0, 0, 120)))
                    .Add(new FillRoundedRect(rect, 6, step == 0 ? new Color(200, 70, 30) : new Color(30, 150, 80)));
                break;
            case "layer":
                list.Add(new PushLayer(.6, LayerBlend.Multiply))
                    .Add(new FillRoundedRect(new(35 + step * 2, 30, 50, 40), 7, new Color(200, 60, 70)))
                    .Add(new FillEllipse(new(55, 45 + step * 3, 40, 30), new Color(30, 120, 230, 180)))
                    .Add(new PopLayer());
                break;
            case "image":
                list.Add(new DrawImage(Image, new(48 + step * 3, 32 + step * 2, 43, 37), .7));
                break;
        }
        return list.Add(new PopClip()).Add(new PopTransform());
    }

    private static void ApplyPatch(RenderTarget frame, DisplayList list, GpuDamageRegion region)
    {
        using var fullScene = new Scene();
        using var builder = new VelloSceneBuilder();
        builder.Build(fullScene, list, frame.Size);
        using var patchScene = new Scene();
        patchScene.Append(fullScene, Matrix3x2.CreateTranslation(-region.X, -region.Y));
        using var renderer = new Renderer((uint)region.Width, (uint)region.Height, new RendererOptions(useCpu: true));
        var patch = new byte[region.Width * region.Height * 4];
        renderer.Render(patchScene, new RenderParams((uint)region.Width, (uint)region.Height,
            RgbaColor.FromBytes(0, 0, 0, 255), AntialiasingMode.Area, RenderFormat.Rgba8), patch, region.Width * 4);
        for (var row = 0; row < region.Height; row++)
            patch.AsSpan(row * region.Width * 4, region.Width * 4).CopyTo(frame.Row(region.Y + row).Slice(region.X * 4, region.Width * 4));
    }

    private static void AssertPixelsEqual(RenderTarget expected, RenderTarget actual)
    {
        var differences = expected.Pixels.Zip(actual.Pixels, (left, right) => Math.Abs(left - right)).ToArray();
        // Vello's area rasterizer may round a channel one step differently after an
        // integer translation into a smaller target. Damage coverage remains exact.
        Assert.True(differences.All(delta => delta <= 1), $"Patch rasterization differs: {differences.Count(delta => delta != 0)} channels, largest delta {differences.Max()}.");
    }

    private static void AssertUnchangedOutsideRegion(RenderTarget expected, RenderTarget oldFrame, GpuDamageRegion region)
    {
        for (var y = 0; y < expected.Height; y++)
            for (var x = 0; x < expected.Width; x++)
                if (x < region.X || x >= region.X + region.Width || y < region.Y || y >= region.Y + region.Height)
                    Assert.Equal(expected.GetPixel(x, y), oldFrame.GetPixel(x, y));
    }
}

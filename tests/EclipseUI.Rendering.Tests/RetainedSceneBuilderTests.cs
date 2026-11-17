using EclipseUI.Rendering.Vello;
using VelloSharp;
using Xunit;
using LayerBlend = EclipseUI.Rendering.LayerBlend;

namespace EclipseUI.Rendering.Tests;

public class RetainedSceneBuilderTests
{
    private static readonly Size Surface = new(160, 120);

    [Fact]
    public void TransformedNestedClipsMatchTheFullSceneAcrossCachedFrames()
    {
        var list = Background()
            .Add(new PushTransform(Transform.Translation(13, 8)))
            .Add(new PushClip(new(0, 0, 110, 90)))
            .Add(new PushTransform(Transform.Scale(1.25, .75)))
            .Add(new PushClipPath(PathGeometry.RoundedRectangle(new(10, 5, 85, 80), 12)))
            .Add(new FillRoundedRect(new(-5, -5, 130, 100), 9, new Color(240, 100, 20, 180)))
            .Add(new PopClip())
            .Add(new PopTransform())
            .Add(new FillEllipse(new(60, 20, 90, 70), new Color(30, 80, 240, 130)))
            .Add(new PopClip()).Add(new PopTransform());
        using var builder = new VelloRetainedSceneBuilder();
        AssertMatchesFull(builder, list);
        Assert.True(builder.BuiltFragments > 0);
        AssertMatchesFull(builder, list);
        Assert.Equal(0, builder.BuiltFragments);
        Assert.True(builder.ReusedFragments > 0);
    }

    [Theory]
    [InlineData(LayerBlend.Normal)]
    [InlineData(LayerBlend.Multiply)]
    [InlineData(LayerBlend.Screen)]
    [InlineData(LayerBlend.Difference)]
    public void OpacityAndBlendGroupsRemainWhole(LayerBlend blend)
    {
        var list = Background()
            .Add(new FillRect(new(12, 12, 100, 85), new Color(180, 70, 120)))
            .Add(new PushLayer(.55, blend))
            .Add(new FillRect(new(25, 20, 75, 60), new Color(30, 210, 60)))
            .Add(new PushLayer(.7, LayerBlend.Screen))
            .Add(new FillEllipse(new(45, 30, 80, 65), new Color(40, 70, 240)))
            .Add(new PopLayer())
            .Add(new FillRect(new(60, 40, 65, 50), new Color(220, 190, 40, 150)))
            .Add(new PopLayer());
        using var builder = new VelloRetainedSceneBuilder();
        AssertMatchesFull(builder, list);
        AssertMatchesFull(builder, list);
        Assert.Equal(0, builder.BuiltFragments);
    }

    [Fact]
    public void RoundedClipPreservesCoverageWhenTranslucentDrawingsOverlapItsEdges()
    {
        var list = Background()
            .Add(new PushClipPath(PathGeometry.RoundedRectangle(new(20.25, 20.25, 90.5, 70.5), 19)))
            .Add(new FillRect(new(0, 0, 150, 110), new Color(220, 40, 20, 128)))
            .Add(new FillRect(new(0, 0, 150, 110), new Color(20, 40, 220, 128)))
            .Add(new PopClip());
        using var builder = new VelloRetainedSceneBuilder();
        AssertMatchesFull(builder, list);
        AssertMatchesFull(builder, list);
    }

    [Fact]
    public void TransformOpenedInsideALayerContinuesAfterThatLayer()
    {
        var list = Background()
            .Add(new PushLayer(.5))
            .Add(new PushTransform(Transform.Translation(30, 15)))
            .Add(new FillRect(new(0, 0, 25, 20), new Color(220, 30, 40)))
            .Add(new PopLayer())
            .Add(new FillRect(new(35, 20, 25, 20), new Color(20, 70, 230)))
            .Add(new PopTransform());
        using var builder = new VelloRetainedSceneBuilder();
        AssertMatchesFull(builder, list);
        AssertMatchesFull(builder, list);
    }

    [Fact]
    public void TransformClosedInsideALayerDoesNotLeakIntoFollowingDrawing()
    {
        var list = Background()
            .Add(new PushTransform(Transform.Translation(30, 15)))
            .Add(new PushLayer(.5))
            .Add(new FillRect(new(0, 0, 25, 20), new Color(220, 30, 40)))
            .Add(new PopTransform())
            .Add(new FillRect(new(35, 20, 25, 20), new Color(20, 70, 230)))
            .Add(new PopLayer())
            .Add(new FillRect(new(10, 70, 45, 20), new Color(20, 180, 70)));
        using var builder = new VelloRetainedSceneBuilder();
        AssertMatchesFull(builder, list);
        AssertMatchesFull(builder, list);
    }

    [Fact]
    public void ChangedCommandsReplaceOnlyAffectedFragmentsAndPreserveThePixels()
    {
        var list = Background()
            .Add(new PushClip(new(10, 10, 120, 85)))
            .Add(new FillRect(new(20, 20, 45, 45), new Color(220, 30, 40)))
            .Add(new FillEllipse(new(65, 35, 55, 45), new Color(20, 70, 230)))
            .Add(new PopClip());
        using var builder = new VelloRetainedSceneBuilder();
        AssertMatchesFull(builder, list);
        list.Commands[2] = new FillRect(new(30, 22, 40, 40), new Color(20, 180, 70));
        AssertMatchesFull(builder, list);
        Assert.True(builder.BuiltFragments > 0);
        Assert.True(builder.ReusedFragments > 0);
        list.Commands.RemoveAt(2);
        AssertMatchesFull(builder, list);
    }

    [Fact]
    public void AFailedBuildDoesNotDisposeFragmentsFromTheLastSuccessfulFrame()
    {
        var list = Background().Add(new FillRect(new(20, 20, 45, 45), new Color(220, 30, 40)));
        using var builder = new VelloRetainedSceneBuilder();
        AssertMatchesFull(builder, list);
        var invalid = Background()
            .Add(new FillEllipse(new(0, 0, 30, 30), new Color(20, 180, 70)))
            .Add(new FillRect(new(50, 50, 25, 25), new UnsupportedBrush()));
        using var scene = new Scene();
        Assert.Throws<NotSupportedException>(() => builder.Build(scene, invalid, Surface));
        AssertMatchesFull(builder, list);
        Assert.Equal(0, builder.BuiltFragments);
        Assert.True(builder.ReusedFragments > 0);
    }

    private static DisplayList Background() => new DisplayList(Surface).Add(new FillRect(new(Point.Zero, Surface), new Color(230, 225, 215)));
    private sealed record UnsupportedBrush : EclipseUI.Rendering.Brush;

    private static void AssertMatchesFull(VelloRetainedSceneBuilder builder, DisplayList list)
    {
        Assert.Empty(list.Validate());
        using var scene = new Scene();
        builder.Build(scene, list, Surface);
        using var renderer = new Renderer((uint)Surface.Width, (uint)Surface.Height, new RendererOptions(useCpu: true));
        var pixels = new byte[(int)(Surface.Width * Surface.Height * 4)];
        renderer.Render(scene, new RenderParams((uint)Surface.Width, (uint)Surface.Height, RgbaColor.FromBytes(0, 0, 0, 0), AntialiasingMode.Area, RenderFormat.Rgba8), pixels, (int)Surface.Width * 4);
        var expected = Render.Frame(list);
        var differences = expected.Pixels.Zip(pixels, (left, right) => Math.Abs(left - right)).ToArray();
        Assert.True(differences.All(delta => delta == 0), $"Cached native scene differs from a full scene render: {differences.Count(delta => delta != 0)} channels, largest delta {differences.Max()}.");
    }
}

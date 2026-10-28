using EclipseUI;
using EclipseUI.Rendering;
using Xunit;

namespace EclipseUI.Rendering.Tests;

public class DeterminismAndValidationTests
{
    [Fact]
    public void SameDisplayList_ProducesIdenticalPixels()
    {
        var checker = ImageHandle.Checkerboard(8, 8, 2, Color.White, new Color(0, 0, 0));
        DisplayList Build()
        {
            var list = new DisplayList(new Size(128, 96));
            list.Add(new FillRect(new Rect(0, 0, 128, 96), new LinearGradient(
                new Point(0, 0), new Point(128, 96),
                new[] { GradientStop.At(0, new Color(10, 20, 30)), GradientStop.At(1, new Color(200, 100, 50)) })));
            list.Add(new FillRoundedRect(new Rect(8, 8, 40, 40), 8, new Color(255, 255, 255, 128)));
            list.Add(new PushClip(new Rect(60, 8, 50, 50)));
            list.Add(new DrawImage(checker, new Rect(60, 8, 50, 50)));
            list.Add(new PopClip());
            list.Add(new PushLayer(0.4));
            list.Add(new FillEllipse(new Rect(16, 50, 80, 30), new Color(255, 0, 255)));
            list.Add(new PopLayer());
            return list;
        }

        var first = Render.Frame(Build());
        var second = Render.Frame(Build());

        Assert.Equal(first.Pixels, second.Pixels);
        Assert.Equal(first.ContentHash(), second.ContentHash());
    }

    [Fact]
    public void RenderTwiceIntoSameTarget_IsIdempotent()
    {
        var list = new DisplayList(new Size(48, 48))
            .Add(new FillRect(new Rect(8, 8, 32, 32), new Color(90, 160, 30)));

        using var backend = new EclipseUI.Rendering.Vello.VelloCpuBackend();
        var target = new RenderTarget(48, 48);
        backend.Render(list, target);
        var first = target.ContentHash();
        backend.Render(list, target);

        Assert.Equal(first, target.ContentHash());
    }

    [Fact]
    public void Validate_ReportsUnbalancedScopes()
    {
        var list = new DisplayList(new Size(10, 10));
        list.Add(new PushClip(new Rect(0, 0, 10, 10)));
        list.Add(new PushLayer(0.5));
        list.Add(new PopClip());

        var errors = list.Validate();

        Assert.Contains(errors, e => e.Contains("missing PopLayer"));
        Assert.DoesNotContain(errors, e => e.Contains("PopClip without"));
    }

    [Fact]
    public void UnbalancedDisplayList_ThrowsWhenValidationEnabled()
    {
        var list = new DisplayList(new Size(16, 16))
            .Add(new PushClip(new Rect(0, 0, 16, 16)))
            .Add(new FillRect(new Rect(0, 0, 16, 16), Color.White));

        using var backend = new EclipseUI.Rendering.Vello.VelloCpuBackend(
            new EclipseUI.Rendering.Vello.VelloBackendOptions { ValidateDisplayList = true });
        var target = new RenderTarget(16, 16);

        Assert.Throws<InvalidOperationException>(() => backend.Render(list, target));
    }

    [Fact]
    public void EmptyDisplayList_LeavesTargetTransparent()
    {
        var frame = Render.Frame(new DisplayList(new Size(32, 32)));

        Assert.Equal(0, frame.CountPixelsDifferentFrom(Color.Transparent));
        Assert.Equal(new Rect(0, 0, 0, 0), frame.OpaqueBounds());
    }

    [Fact]
    public void PngEncoder_WritesDecodableHeader()
    {
        var frame = Render.Frame(new DisplayList(new Size(16, 16))
            .Add(new FillRect(new Rect(0, 0, 16, 16), new Color(1, 2, 3))));

        var bytes = PngEncoder.Encode(frame);

        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, bytes.Take(8).ToArray());
        Assert.Equal("IHDR", System.Text.Encoding.ASCII.GetString(bytes, 12, 4));
        Assert.Equal(16, bytes[16] << 24 | bytes[17] << 16 | bytes[18] << 8 | bytes[19]);
    }
}

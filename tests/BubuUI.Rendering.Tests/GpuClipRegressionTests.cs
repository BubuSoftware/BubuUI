using BubuUI.Rendering.Vello;
using Xunit;

namespace BubuUI.Rendering.Tests;

public class GpuClipRegressionTests
{
    [Fact]
    public void NestedViewportClipsKeepTheGpuFrameVisibleAcrossSizeChanges()
    {
        if (!Render.TryCreateGpuBackend(out var gpu)) return;
        using (gpu)
        {
            foreach (var size in new[] { new Size(1040, 800), new Size(1920, 1129), new Size(875, 800), new Size(1040, 800) })
            {
                var background = new Color(243, 243, 243);
                var foreground = new Color(0, 95, 184);
                var list = new DisplayList(size)
                    .Add(new FillRect(new Rect(Point.Zero, size), background));
                // Layout/template containers can nest several viewport clips. These
                // must remain clips, not full-screen isolated compositing layers.
                for (var i = 0; i < 8; i++)
                    list.Add(new PushClip(new Rect(10, 10, size.Width - 20, size.Height - 20)));
                list.Add(new FillRect(new Rect(0, 0, 120, 80), foreground));
                for (var i = 0; i < 8; i++) list.Add(new PopClip());
                var target = new RenderTarget((int)size.Width, (int)size.Height);
                gpu.Render(list, target);
                AssertPixel(target, 0, 0, background);
                AssertPixel(target, 5, 30, background);
                AssertPixel(target, 30, 30, foreground);
                AssertPixel(target, target.Width - 1, target.Height - 1, background);
            }
        }
    }

    private static void AssertPixel(RenderTarget target, int x, int y, Color expected)
    {
        var offset = y * target.Stride + x * 4;
        Render.AssertColor(expected, new Color(target.Pixels[offset], target.Pixels[offset + 1],
            target.Pixels[offset + 2], target.Pixels[offset + 3]), because: $"GPU {target.Width}x{target.Height} at ({x},{y})");
    }
}

using EclipseUI.Rendering.Vello;
using EclipseUI.Text.Shaping;
using Xunit;

namespace EclipseUI.Rendering.Tests;

public class ColorEmojiScalingTests
{
    [Theory]
    [InlineData(14, 1)]
    [InlineData(24, 1)]
    [InlineData(24, 0.75)]
    [InlineData(24, 1.25)]
    [InlineData(24, 2)]
    [InlineData(24, 2.75)]
    [InlineData(40, 3)]
    public void NotoEmojiInkFitsItsShapedLine(double size, double scale)
    {
        var path = Environment.GetEnvironmentVariable("ECLIPSEUI_TEST_EMOJI_FONT")
            ?? "/system/fonts/NotoColorEmoji.ttf";
        if (!File.Exists(path)) return;
        using var shaper = new HarfBuzzTextShaper(path);
        using var painter = new HarfBuzzTextPainter(shaper, []);
        var metrics = painter.Measure("😀😀😀", size);
        var list = new DisplayList(new(600, 400));
        list.Add(new PushTransform(Transform.Scale(scale)));
        painter.Paint(list, "😀😀😀", size, new(8, 8), Color.Black);
        list.Add(new PopTransform());
        using var backend = new VelloSparseBackend();
        var frame = new RenderTarget(600, 400);
        backend.Render(list, frame);
        var count = 0;
        var minX = 600; var minY = 400; var maxX = 0; var maxY = 0;
        for (var y = 0; y < frame.Height; y++)
        for (var x = 0; x < frame.Width; x++)
            if (frame.GetPixel(x, y).A > 100)
            {
                count++;
                minX = Math.Min(minX, x); minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
            }
        Assert.True(count > size * size * scale * scale * .5);
        Assert.True(minX >= 8 * scale - 1 && minY >= 8 * scale - 1 &&
            maxX <= (8 + metrics.Width) * scale + 1 && maxY <= (8 + metrics.Height) * scale + 1,
            $"Ink [{minX},{minY}..{maxX},{maxY}] exceeds line {metrics} at {size}px / {scale}x.");

        // Each emoji must occupy its own shaped cell, with a visible gap at the
        // boundary. Checking only the overall ink box would miss compressed runs.
        var advance = metrics.Width / 3;
        for (var index = 0; index < 3; index++)
        {
            var left = (int)Math.Ceiling((8 + advance * index) * scale);
            var right = (int)Math.Floor((8 + advance * (index + 1)) * scale);
            var pixels = 0;
            for (var y = 0; y < frame.Height; y++)
            for (var x = left; x <= right; x++)
                if (frame.GetPixel(x, y).A > 100) pixels++;
            Assert.True(pixels > size * size * scale * scale * .2);
            for (var y = 0; y < frame.Height; y++)
                Assert.True(frame.GetPixel(right, y).A <= 100, $"Emoji overlap at cell {index}, {size}px / {scale}x.");
        }
    }
}

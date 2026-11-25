using CoreGraphics;
using EclipseUI.Rendering;
using NFloat = System.Runtime.InteropServices.NFloat;

namespace EclipseUI.Platform.iOS;

internal static class CoreGraphicsGradients
{
    private const CGGradientDrawingOptions Pad = CGGradientDrawingOptions.DrawsBeforeStartLocation
        | CGGradientDrawingOptions.DrawsAfterEndLocation;

    internal static void Draw(CGContext context, Brush brush)
    {
        var (stops, extend) = brush switch
        {
            LinearGradient g => (g.Stops, g.Extend),
            RadialGradient g => (g.Stops, g.Extend),
            SweepGradient g => (g.Stops, g.Extend),
            _ => throw new NotSupportedException($"Unknown brush {brush.GetType().Name}.")
        };
        if (stops.Count == 0) return;
        var bounds = context.GetClipBoundingBox();
        if (bounds.IsEmpty) return;
        if (extend == GradientExtend.Pad)
        {
            using var space = CGColorSpace.CreateSrgb()!;
            var components = new NFloat[4 * stops.Count];
            var locations = new NFloat[stops.Count];
            for (var i = 0; i < stops.Count; i++)
            {
                var stop = stops[i];
                components[i * 4] = stop.Color.R / 255f;
                components[i * 4 + 1] = stop.Color.G / 255f;
                components[i * 4 + 2] = stop.Color.B / 255f;
                components[i * 4 + 3] = stop.Color.A / 255f;
                locations[i] = (NFloat)stop.Offset;
            }
            using var gradient = new CGGradient(space, components, locations);
            switch (brush)
            {
                case LinearGradient line when line.Start != line.End:
                    context.DrawLinearGradient(gradient, new CGPoint(line.Start.X, line.Start.Y),
                        new CGPoint(line.End.X, line.End.Y), Pad);
                    return;
                case RadialGradient radial when radial.Radius > 0:
                    var center = new CGPoint(radial.Center.X, radial.Center.Y);
                    context.DrawRadialGradient(gradient, center, 0, center, (NFloat)radial.Radius, Pad);
                    return;
                case SweepGradient sweep when OperatingSystem.IsIOSVersionAtLeast(17)
                    && Math.Abs(sweep.EndAngle - sweep.StartAngle - Math.Tau) < 1e-6:
                    context.DrawConicGradient(gradient, new CGPoint(sweep.Center.X, sweep.Center.Y), (NFloat)sweep.StartAngle);
                    return;
            }
        }

        // Quartz has no repeat/reflect spread mode; sample the visible portion only.
        double scale = CoreGraphicsRenderer.RasterScale(context, bounds.Width, bounds.Height);
        int width = Math.Max(1, (int)Math.Ceiling(bounds.Width * scale));
        int height = Math.Max(1, (int)Math.Ceiling(bounds.Height * scale));
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            double sy = bounds.Y + (y + .5) * bounds.Height / height;
            for (var x = 0; x < width; x++)
            {
                double sx = bounds.X + (x + .5) * bounds.Width / width;
                double t = Position(brush, sx, sy);
                if (extend == GradientExtend.Repeat) t -= Math.Floor(t);
                if (extend == GradientExtend.Reflect) t = 1 - Math.Abs(t - Math.Floor(t / 2) * 2 - 1);
                var c = Sample(stops, t);
                int i = (y * width + x) * 4;
                pixels[i] = c.R;
                pixels[i + 1] = c.G;
                pixels[i + 2] = c.B;
                pixels[i + 3] = c.A;
            }
        }
        using var image = CoreGraphicsRenderer.CreateImage(new ImageHandle(width, height, pixels));
        CoreGraphicsRenderer.DrawImage(context, image, bounds);
    }

    private static double Position(Brush brush, double x, double y)
    {
        switch (brush)
        {
            case LinearGradient g:
                var dx = g.End.X - g.Start.X;
                var dy = g.End.Y - g.Start.Y;
                var lengthSquared = dx * dx + dy * dy;
                return lengthSquared > 0 ? ((x - g.Start.X) * dx + (y - g.Start.Y) * dy) / lengthSquared : 1;
            case RadialGradient g:
                return g.Radius > 0 ? double.Hypot(x - g.Center.X, y - g.Center.Y) / g.Radius : 1;
            case SweepGradient g:
                var span = g.EndAngle - g.StartAngle;
                if (span == 0) return 1;
                var angle = (Math.Atan2(y - g.Center.Y, x - g.Center.X) - g.StartAngle) * Math.Sign(span);
                return (angle - Math.Floor(angle / Math.Tau) * Math.Tau) / Math.Abs(span);
            default: throw new ArgumentOutOfRangeException(nameof(brush));
        }
    }

    private static Color Sample(IReadOnlyList<GradientStop> stops, double t)
    {
        if (t <= stops[0].Offset) return stops[0].Color;
        for (var i = 1; i < stops.Count; i++)
        {
            var end = stops[i];
            if (t > end.Offset) continue;
            var start = stops[i - 1];
            return Color.Lerp(start.Color, end.Color, (t - start.Offset) / (end.Offset - start.Offset));
        }
        return stops[^1].Color;
    }
}

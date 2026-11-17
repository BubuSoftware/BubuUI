using EclipseUI.Rendering;

namespace EclipseUI.Rendering.Vello;

/// <summary>A pixel-aligned raster region; empty damage reuses the retained GPU texture.</summary>
public readonly record struct GpuDamageRegion(int X, int Y, int Width, int Height)
{
    public long Area => (long)Width * Height;
    public static GpuDamageRegion Resolve(FrameDamage damage, int width, int height, double scale, bool hasFrame)
    {
        ArgumentNullException.ThrowIfNull(damage);
        if (!double.IsFinite(scale) || scale <= 0) throw new ArgumentOutOfRangeException(nameof(scale));
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (!hasFrame || damage.IsFull) return new(0, 0, width, height);
        if (damage.IsEmpty) return default;
        var bounds = damage.Bounds;
        var left = (int)Math.Clamp(Math.Floor(bounds.Left * scale), 0, width);
        var top = (int)Math.Clamp(Math.Floor(bounds.Top * scale), 0, height);
        var right = (int)Math.Clamp(Math.Ceiling(bounds.Right * scale), 0, width);
        var bottom = (int)Math.Clamp(Math.Ceiling(bounds.Bottom * scale), 0, height);
        return new(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }
}

using System.Buffers;
using BubuUI.Rendering;
using VelloSharp;

namespace BubuUI.Rendering.Vello;

/// <summary>Reusable Gaussian alpha masks for Sparse's image drawing path.</summary>
internal sealed class SparseShadowCache : IDisposable
{
    private const int MaxEntries = 16;
    private const int MaxBytes = 16 * 1024 * 1024;
    private const int MaxRasterPixels = 1024 * 1024;
    private const int MaxRasterDimension = 4096;
    private const double MaxRasterSigma = 24;
    private readonly Dictionary<Key, LinkedListNode<Entry>> _entries = new();
    private readonly LinkedList<Entry> _recent = new();
    private int _bytes;

    private readonly record struct Key(double Width, double Height, double Radius, double Sigma,
        Color Color, int RasterWidth, int RasterHeight);
    internal sealed record Entry(Image Image, int Width, int Height, double Padding,
        double LogicalWidth, double LogicalHeight, int Bytes);

    internal Entry Get(DropShadow shadow, Transform transform)
    {
        var sigma = Math.Max(0, shadow.BlurRadius) / 2;
        var padding = sigma * 3;
        var logicalWidth = shadow.Rect.Width + padding * 2;
        var logicalHeight = shadow.Rect.Height + padding * 2;
        var radius = Math.Clamp(shadow.CornerRadius, 0, Math.Min(shadow.Rect.Width, shadow.Rect.Height) / 2);
        var scale = RasterScale(transform);
        // Oversized shadows retain their Gaussian profile at a lower sample density.
        // This bounds temporary storage and convolution work even with extreme DPI/blur.
        scale = Math.Min(scale, MaxRasterSigma / sigma);
        scale = Math.Min(scale, (MaxRasterDimension - 1) / Math.Max(logicalWidth, logicalHeight));
        scale = Math.Min(scale, Math.Sqrt((MaxRasterPixels - 2.0 * MaxRasterDimension) / logicalWidth / logicalHeight));
        var width = Math.Max(1, (int)Math.Ceiling(logicalWidth * scale));
        var height = Math.Max(1, (int)Math.Ceiling(logicalHeight * scale));
        var key = new Key(shadow.Rect.Width, shadow.Rect.Height, radius, sigma, shadow.Color, width, height);
        if (_entries.TryGetValue(key, out var cached))
        {
            _recent.Remove(cached);
            _recent.AddFirst(cached);
            return cached.Value;
        }

        var pixels = Rasterize(width, height, logicalWidth, logicalHeight, shadow.Rect.Size, radius, sigma, shadow.Color);
        // Sparse's native image sampler accepts straight alpha only. Constant RGB
        // with convolved alpha is equivalent to blurring premultiplied shadow RGBA.
        var image = Image.FromPixels(pixels, width, height, RenderFormat.Rgba8, ImageAlphaMode.Straight, width * 4);
        var entry = new Entry(image, width, height, padding, logicalWidth, logicalHeight, pixels.Length);
        while (_recent.Count >= MaxEntries || _bytes + entry.Bytes > MaxBytes)
        {
            var oldest = _recent.Last!;
            _recent.RemoveLast();
            _entries.Remove(_entries.First(pair => ReferenceEquals(pair.Value, oldest)).Key);
            _bytes -= oldest.Value.Bytes;
            oldest.Value.Image.Dispose();
        }
        _entries.Add(key, _recent.AddFirst(entry));
        _bytes += entry.Bytes;
        return entry;
    }

    private static double RasterScale(Transform transform)
    {
        // Largest singular value: rotation/shear must not undersample a stretched axis.
        var a = (double)transform.M11 * transform.M11 + (double)transform.M12 * transform.M12;
        var b = (double)transform.M21 * transform.M21 + (double)transform.M22 * transform.M22;
        var c = (double)transform.M11 * transform.M21 + (double)transform.M12 * transform.M22;
        var scale = Math.Sqrt((a + b + Math.Sqrt((a - b) * (a - b) + 4 * c * c)) / 2);
        return Math.Max(0.25, Math.Ceiling(scale * 4) / 4);
    }

    private static byte[] Rasterize(int width, int height, double logicalWidth, double logicalHeight,
        Size shape, double radius, double sigma, Color color)
    {
        var length = checked(width * height);
        var mask = ArrayPool<float>.Shared.Rent(length);
        var scratch = ArrayPool<float>.Shared.Rent(length);
        try
        {
            var scaleX = width / logicalWidth;
            var scaleY = height / logicalHeight;
            var sampleScale = Math.Min(scaleX, scaleY);
            var centerX = sigma * 3 + shape.Width / 2;
            var centerY = sigma * 3 + shape.Height / 2;
            var innerWidth = shape.Width / 2 - radius;
            var innerHeight = shape.Height / 2 - radius;
            for (var y = 0; y < height; y++)
            {
                var qy = Math.Abs((y + 0.5) / scaleY - centerY) - innerHeight;
                for (var x = 0; x < width; x++)
                {
                    var qx = Math.Abs((x + 0.5) / scaleX - centerX) - innerWidth;
                    var outsideX = Math.Max(qx, 0);
                    var outsideY = Math.Max(qy, 0);
                    var distance = Math.Sqrt(outsideX * outsideX + outsideY * outsideY)
                        + Math.Min(Math.Max(qx, qy), 0) - radius;
                    mask[y * width + x] = (float)Math.Clamp(0.5 - distance * sampleScale, 0, 1);
                }
            }
            Convolve(mask, scratch, width, height, sigma * scaleX, horizontal: true);
            Convolve(scratch, mask, width, height, sigma * scaleY, horizontal: false);
            var pixels = new byte[checked(length * 4)];
            for (var index = 0; index < length; index++)
            {
                var alpha = (byte)Math.Clamp((int)Math.Round(mask[index] * color.A), 0, color.A);
                var pixel = index * 4;
                // Keep RGB even at zero alpha so bilinear sampling cannot introduce
                // black texels around a colored shadow's transparent fringe.
                pixels[pixel] = color.R;
                pixels[pixel + 1] = color.G;
                pixels[pixel + 2] = color.B;
                pixels[pixel + 3] = alpha;
            }
            return pixels;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(mask);
            ArrayPool<float>.Shared.Return(scratch);
        }
    }

    private static void Convolve(float[] source, float[] target, int width, int height, double sigma, bool horizontal)
    {
        if (sigma < 0.05)
        {
            Array.Copy(source, target, width * height);
            return;
        }
        var radius = (int)Math.Ceiling(sigma * 3);
        var kernel = new float[radius * 2 + 1];
        double sum = 0;
        for (var index = -radius; index <= radius; index++)
        {
            var weight = Math.Exp(-index * (double)index / (2 * sigma * sigma));
            kernel[index + radius] = (float)weight;
            sum += weight;
        }
        for (var index = 0; index < kernel.Length; index++) kernel[index] /= (float)sum;
        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                var position = horizontal ? x : y;
                var limit = horizontal ? width : height;
                var first = Math.Max(-radius, -position);
                var last = Math.Min(radius, limit - 1 - position);
                var sourceIndex = row + x + first * (horizontal ? 1 : width);
                var step = horizontal ? 1 : width;
                float value = 0;
                for (var offset = first; offset <= last; offset++, sourceIndex += step)
                    value += source[sourceIndex] * kernel[offset + radius];
                target[row + x] = value;
            }
        }
    }

    public void Dispose()
    {
        foreach (var entry in _recent) entry.Image.Dispose();
        _recent.Clear();
        _entries.Clear();
        _bytes = 0;
    }
}

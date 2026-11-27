namespace BubuUI.Rendering;

/// <summary>
/// CPU 渲染目标：RGBA8 直通 alpha 的紧密缓冲区。
/// G1 用它做 PNG 输出与像素探针；GPU surface 是后续的第二类目标。
/// </summary>
public sealed class RenderTarget
{
    public RenderTarget(int width, int height)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        Width = width;
        Height = height;
        Pixels = new byte[width * height * 4];
    }

    public int Width { get; }

    public int Height { get; }

    public int Stride => Width * 4;

    public byte[] Pixels { get; }

    public Size Size => new(Width, Height);

    public Span<byte> Row(int y) => Pixels.AsSpan(y * Stride, Stride);

    public Color GetPixel(int x, int y)
    {
        var index = y * Stride + x * 4;
        return new Color(Pixels[index], Pixels[index + 1], Pixels[index + 2], Pixels[index + 3]);
    }

    public void SetPixel(int x, int y, Color color)
    {
        var index = y * Stride + x * 4;
        Pixels[index] = color.R;
        Pixels[index + 1] = color.G;
        Pixels[index + 2] = color.B;
        Pixels[index + 3] = color.A;
    }

    public void Clear(Color color)
    {
        for (int i = 0; i < Pixels.Length; i += 4)
        {
            Pixels[i] = color.R;
            Pixels[i + 1] = color.G;
            Pixels[i + 2] = color.B;
            Pixels[i + 3] = color.A;
        }
    }

    /// <summary>统计与给定颜色“明显不同”的像素数（用于非空断言）。</summary>
    public int CountPixelsDifferentFrom(Color color, int tolerance = 8)
    {
        int count = 0;
        for (int i = 0; i < Pixels.Length; i += 4)
        {
            if (Math.Abs(Pixels[i] - color.R) > tolerance ||
                Math.Abs(Pixels[i + 1] - color.G) > tolerance ||
                Math.Abs(Pixels[i + 2] - color.B) > tolerance ||
                Math.Abs(Pixels[i + 3] - color.A) > tolerance)
                count++;
        }
        return count;
    }

    /// <summary>非透明像素包围盒（半开区间）；全透明返回空矩形。</summary>
    public Rect OpaqueBounds()
    {
        int minX = Width, minY = Height, maxX = -1, maxY = -1;
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                if (Pixels[y * Stride + x * 4 + 3] == 0) continue;
                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
            }
        }
        return maxX < 0 ? new Rect(0, 0, 0, 0) : new Rect(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    /// <summary>FNV-1a 64 位像素哈希；用于确定性断言。</summary>
    public ulong ContentHash()
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong hash = offset;
        foreach (var b in Pixels)
        {
            hash ^= b;
            hash *= prime;
        }
        return hash;
    }
}

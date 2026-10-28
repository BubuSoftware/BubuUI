namespace EclipseUI.Rendering;

public enum PixelFormat
{
    /// <summary>8 位/通道、直通 alpha、sRGB。</summary>
    Rgba8,
}

/// <summary>
/// 已解码的图像资源（CPU 侧 RGBA，直通 alpha）。
/// 后端按引用缓存原生图像；3D 视口的 readback 同样以该类型进入 display list。
/// </summary>
public sealed class ImageHandle
{
    public ImageHandle(int width, int height, ReadOnlyMemory<byte> rgba, PixelFormat format = PixelFormat.Rgba8)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        if (rgba.Length < width * height * 4)
            throw new ArgumentException("pixel buffer is smaller than width*height*4", nameof(rgba));

        Width = width;
        Height = height;
        Pixels = rgba;
        Format = format;
    }

    public int Width { get; }

    public int Height { get; }

    public int Stride => Width * 4;

    public ReadOnlyMemory<byte> Pixels { get; }

    public PixelFormat Format { get; }

    public Size Size => new(Width, Height);

    public static ImageHandle Solid(int width, int height, Color color)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = color.R;
            pixels[i + 1] = color.G;
            pixels[i + 2] = color.B;
            pixels[i + 3] = color.A;
        }
        return new ImageHandle(width, height, pixels);
    }

    /// <summary>四象限/棋盘测试图：用于验证缩放与过滤质量。</summary>
    public static ImageHandle Checkerboard(int width, int height, int cell, Color a, Color b)
    {
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var color = ((x / cell) + (y / cell)) % 2 == 0 ? a : b;
                int i = (y * width + x) * 4;
                pixels[i] = color.R;
                pixels[i + 1] = color.G;
                pixels[i + 2] = color.B;
                pixels[i + 3] = color.A;
            }
        }
        return new ImageHandle(width, height, pixels);
    }
}

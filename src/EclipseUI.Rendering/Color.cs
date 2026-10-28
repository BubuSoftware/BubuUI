namespace EclipseUI.Rendering;

/// <summary>
/// 渲染用 RGBA 颜色（非预乘，sRGB 字节分量）。
/// 与 <c>VelloSharp.RgbaColor</c> 等后端类型解耦，后端负责转换。
/// </summary>
public readonly record struct Color(byte R, byte G, byte B, byte A = 255)
{
    public static readonly Color Transparent = new(0, 0, 0, 0);
    public static readonly Color Black = new(0, 0, 0);
    public static readonly Color White = new(255, 255, 255);

    public static Color FromRgb(byte r, byte g, byte b) => new(r, g, b, 255);

    /// <summary>按 0–1 的 alpha 生成颜色（用于图层透明度、测试半透明叠加）。</summary>
    public static Color FromRgba(byte r, byte g, byte b, double alpha)
        => new(r, g, b, ToByte(alpha));

    public Color WithAlpha(byte alpha) => this with { A = alpha };

    public Color WithAlpha(double alpha) => this with { A = ToByte(alpha) };

    /// <summary>线性插值（仅用于测试与主题过渡，不做色彩空间转换）。</summary>
    public static Color Lerp(Color from, Color to, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return new Color(
            ToByte(from.R + (to.R - from.R) * t),
            ToByte(from.G + (to.G - from.G) * t),
            ToByte(from.B + (to.B - from.B) * t),
            ToByte(from.A + (to.A - from.A) * t));
    }

    private static byte ToByte(double value)
        => (byte)Math.Clamp(Math.Round(value * 255), 0, 255);

    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}{A:X2}";
}

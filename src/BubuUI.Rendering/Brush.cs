namespace BubuUI.Rendering;

public enum GradientExtend
{
    Pad,
    Repeat,
    Reflect,
}

public readonly record struct GradientStop(double Offset, Color Color)
{
    public static GradientStop At(double offset, Color color) => new(offset, color);
}

/// <summary>
/// 填充画刷。G1 只覆盖实色与三种渐变；图像填充走 <see cref="DrawImage"/>。
/// </summary>
public abstract record Brush
{
    public static implicit operator Brush(Color color) => new SolidColorBrush(color);
}

public sealed record SolidColorBrush(Color Color) : Brush;

public sealed record LinearGradient(
    Point Start,
    Point End,
    IReadOnlyList<GradientStop> Stops,
    GradientExtend Extend = GradientExtend.Pad) : Brush;

/// <summary>同心圆径向渐变（<paramref name="Center"/> 为圆心，<paramref name="Radius"/> 为外半径）。</summary>
public sealed record RadialGradient(
    Point Center,
    double Radius,
    IReadOnlyList<GradientStop> Stops,
    GradientExtend Extend = GradientExtend.Pad) : Brush;

/// <summary>锥形/扫描渐变，角度为弧度。</summary>
public sealed record SweepGradient(
    Point Center,
    double StartAngle,
    double EndAngle,
    IReadOnlyList<GradientStop> Stops,
    GradientExtend Extend = GradientExtend.Pad) : Brush;

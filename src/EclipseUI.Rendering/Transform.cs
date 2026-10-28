namespace EclipseUI.Rendering;

/// <summary>
/// 2D 仿射变换（行向量约定：<c>[x y 1] * M</c>）。
/// <see cref="operator *"/> 语义为“先应用左操作数，再应用右操作数”。
/// 与 <c>System.Numerics.Matrix3x2</c> 字段顺序一致，后端可直接转换。
/// </summary>
public readonly record struct Transform(
    float M11, float M12,
    float M21, float M22,
    float M31, float M32)
{
    public static readonly Transform Identity = new(1, 0, 0, 1, 0, 0);

    public static Transform Translation(double x, double y)
        => new(1, 0, 0, 1, (float)x, (float)y);

    public static Transform Scale(double x, double y)
        => new((float)x, 0, 0, (float)y, 0, 0);

    public static Transform Scale(double uniform) => Scale(uniform, uniform);

    public static Transform Rotation(double radians)
    {
        float cos = (float)Math.Cos(radians);
        float sin = (float)Math.Sin(radians);
        return new Transform(cos, sin, -sin, cos, 0, 0);
    }

    /// <summary>绕 <paramref name="center"/> 旋转（等价于先平移到原点、旋转、再平移回去）。</summary>
    public static Transform RotationAround(double radians, Point center)
        => Translation(-center.X, -center.Y)
           * Rotation(radians)
           * Translation(center.X, center.Y);

    /// <summary>以 <paramref name="center"/> 为不动点缩放。</summary>
    public static Transform ScaleAround(double x, double y, Point center)
        => Translation(-center.X, -center.Y)
           * Scale(x, y)
           * Translation(center.X, center.Y);

    /// <summary>先应用 <paramref name="left"/>，再应用 <paramref name="right"/>。</summary>
    public static Transform operator *(Transform left, Transform right) => new(
        left.M11 * right.M11 + left.M12 * right.M21,
        left.M11 * right.M12 + left.M12 * right.M22,
        left.M21 * right.M11 + left.M22 * right.M21,
        left.M21 * right.M12 + left.M22 * right.M22,
        left.M31 * right.M11 + left.M32 * right.M21 + right.M31,
        left.M31 * right.M12 + left.M32 * right.M22 + right.M32);

    public (double X, double Y) Apply(double x, double y)
        => (x * M11 + y * M21 + M31, x * M12 + y * M22 + M32);
}

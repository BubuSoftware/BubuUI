namespace EclipseUI;

public readonly record struct Point(double X, double Y)
{
    public static readonly Point Zero = default;

    public static Point operator +(Point p, Size s) => new(p.X + s.Width, p.Y + s.Height);
}

public readonly record struct Size(double Width, double Height)
{
    public static readonly Size Empty = default;
    public static readonly Size Infinity = new(double.PositiveInfinity, double.PositiveInfinity);

    public bool IsEmpty => Width <= 0 || Height <= 0;
}

public readonly record struct Rect(Point Location, Size Size)
{
    public Rect(double x, double y, double width, double height)
        : this(new Point(x, y), new Size(width, height)) { }

    public double X => Location.X;
    public double Y => Location.Y;
    public double Width => Size.Width;
    public double Height => Size.Height;
    public double Left => Location.X;
    public double Top => Location.Y;
    public double Right => Location.X + Size.Width;
    public double Bottom => Location.Y + Size.Height;

    /// <summary>半开区间判定（左/上含，右/下不含）：相邻元素不双重命中。</summary>
    public bool Contains(Point p) =>
        p.X >= Location.X && p.X < Right && p.Y >= Location.Y && p.Y < Bottom;
}

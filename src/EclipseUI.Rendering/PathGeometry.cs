namespace EclipseUI.Rendering;

public enum PathVerb
{
    MoveTo,
    LineTo,
    QuadraticTo,
    CubicTo,
    Close,
}

/// <summary>路径片段的控制点（未使用的分量保持 0）。</summary>
public readonly record struct PathSegment(
    PathVerb Verb,
    double X0 = 0, double Y0 = 0,
    double X1 = 0, double Y1 = 0,
    double X2 = 0, double Y2 = 0);

/// <summary>与后端无关的 2D 路径。由 <see cref="PathGeometryBuilder"/> 或静态工厂构造。</summary>
public sealed class PathGeometry
{
    public static readonly PathGeometry Empty = new(Array.Empty<PathSegment>(), FillRule.NonZero);

    public PathGeometry(IReadOnlyList<PathSegment> segments, FillRule fillRule = FillRule.NonZero)
    {
        Segments = segments;
        FillRule = fillRule;
    }

    public IReadOnlyList<PathSegment> Segments { get; }

    public FillRule FillRule { get; }

    /// <summary>路径覆盖的原始包围盒（不含描边宽度）。空路径返回零矩形。</summary>
    public Rect Bounds
    {
        get
        {
            if (Segments.Count == 0) return new Rect(0, 0, 0, 0);
            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;
            foreach (var s in Segments)
            {
                Accumulate(s.X0, s.Y0);
                if (s.Verb is PathVerb.QuadraticTo or PathVerb.CubicTo) Accumulate(s.X1, s.Y1);
                if (s.Verb == PathVerb.CubicTo) Accumulate(s.X2, s.Y2);
            }
            return new Rect(minX, minY, maxX - minX, maxY - minY);

            void Accumulate(double x, double y)
            {
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
        }
    }

    public static PathGeometry Rectangle(Rect rect) => new PathGeometryBuilder()
        .MoveTo(rect.Left, rect.Top)
        .LineTo(rect.Right, rect.Top)
        .LineTo(rect.Right, rect.Bottom)
        .LineTo(rect.Left, rect.Bottom)
        .Close()
        .Build();

    public static PathGeometry RoundedRectangle(Rect rect, double radius)
    {
        double maxRadius = Math.Min(rect.Width, rect.Height) / 2;
        radius = Math.Clamp(radius, 0, maxRadius);
        if (radius <= 0) return Rectangle(rect);

        double k = radius * 0.5522847498307936; // 圆弧的 cubic 近似常数
        double left = rect.Left, top = rect.Top, right = rect.Right, bottom = rect.Bottom;

        return new PathGeometryBuilder()
            .MoveTo(left + radius, top)
            .LineTo(right - radius, top)
            .CubicTo(right - radius + k, top, right, top + radius - k, right, top + radius)
            .LineTo(right, bottom - radius)
            .CubicTo(right, bottom - radius + k, right - radius + k, bottom, right - radius, bottom)
            .LineTo(left + radius, bottom)
            .CubicTo(left + radius - k, bottom, left, bottom - radius + k, left, bottom - radius)
            .LineTo(left, top + radius)
            .CubicTo(left, top + radius - k, left + radius - k, top, left + radius, top)
            .Close()
            .Build();
    }

    public static PathGeometry Ellipse(Rect bounds)
    {
        double rx = bounds.Width / 2, ry = bounds.Height / 2;
        double cx = bounds.X + rx, cy = bounds.Y + ry;
        double kx = rx * 0.5522847498307936, ky = ry * 0.5522847498307936;

        return new PathGeometryBuilder()
            .MoveTo(cx - rx, cy)
            .CubicTo(cx - rx, cy - ky, cx - kx, cy - ry, cx, cy - ry)
            .CubicTo(cx + kx, cy - ry, cx + rx, cy - ky, cx + rx, cy)
            .CubicTo(cx + rx, cy + ky, cx + kx, cy + ry, cx, cy + ry)
            .CubicTo(cx - kx, cy + ry, cx - rx, cy + ky, cx - rx, cy)
            .Close()
            .Build();
    }

    public static PathGeometry Line(Point from, Point to) => new PathGeometryBuilder()
        .MoveTo(from.X, from.Y)
        .LineTo(to.X, to.Y)
        .Build();

    public override string ToString() => $"Path({Segments.Count} segments, {FillRule})";
}

public enum FillRule
{
    NonZero,
    EvenOdd,
}

/// <summary>可变的路径构建器；<see cref="Build"/> 后不再修改。</summary>
public sealed class PathGeometryBuilder
{
    private readonly List<PathSegment> _segments = new();
    private FillRule _fillRule = FillRule.NonZero;

    public PathGeometryBuilder WithFillRule(FillRule rule)
    {
        _fillRule = rule;
        return this;
    }

    public PathGeometryBuilder MoveTo(double x, double y)
    {
        _segments.Add(new PathSegment(PathVerb.MoveTo, x, y));
        return this;
    }

    public PathGeometryBuilder LineTo(double x, double y)
    {
        _segments.Add(new PathSegment(PathVerb.LineTo, x, y));
        return this;
    }

    public PathGeometryBuilder QuadraticTo(double cx, double cy, double x, double y)
    {
        _segments.Add(new PathSegment(PathVerb.QuadraticTo, x, y, cx, cy));
        return this;
    }

    public PathGeometryBuilder CubicTo(double c1x, double c1y, double c2x, double c2y, double x, double y)
    {
        _segments.Add(new PathSegment(PathVerb.CubicTo, x, y, c1x, c1y, c2x, c2y));
        return this;
    }

    public PathGeometryBuilder Close()
    {
        _segments.Add(new PathSegment(PathVerb.Close));
        return this;
    }

    public PathGeometry Build() => new(_segments.ToArray(), _fillRule);
}

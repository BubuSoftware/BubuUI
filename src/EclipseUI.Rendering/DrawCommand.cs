namespace EclipseUI.Rendering;

/// <summary>
/// 图层混合模式（映射到后端 mix/compose）。
/// G1 只需要证明 clip + 透明度 + 常见混合可用。
/// </summary>
public enum LayerBlend
{
    Normal,
    Multiply,
    Screen,
    Overlay,
    Darken,
    Lighten,
    Difference,
    Plus,
}

/// <summary>display list 指令。渲染器按顺序执行；栈类指令必须成对出现。</summary>
public abstract record DrawCommand;

public sealed record FillRect(Rect Rect, Brush Brush) : DrawCommand
{
    public FillRect(Rect rect, Color color) : this(rect, new SolidColorBrush(color)) { }
}

public sealed record FillRoundedRect(Rect Rect, double Radius, Brush Brush) : DrawCommand
{
    public FillRoundedRect(Rect rect, double radius, Color color)
        : this(rect, radius, new SolidColorBrush(color)) { }
}

public sealed record FillEllipse(Rect Bounds, Brush Brush) : DrawCommand
{
    public FillEllipse(Rect bounds, Color color) : this(bounds, new SolidColorBrush(color)) { }
}

public sealed record StrokeRect(Rect Rect, Brush Brush, double Width) : DrawCommand
{
    public StrokeRect(Rect rect, Color color, double width) : this(rect, new SolidColorBrush(color), width) { }
}

public sealed record FillPath(PathGeometry Path, Brush Brush) : DrawCommand
{
    public FillPath(PathGeometry path, Color color) : this(path, new SolidColorBrush(color)) { }
}

public sealed record StrokePath(
    PathGeometry Path,
    Brush Brush,
    double Width,
    IReadOnlyList<double>? DashPattern = null,
    double DashPhase = 0) : DrawCommand
{
    public StrokePath(PathGeometry path, Color color, double width, IReadOnlyList<double>? dashPattern = null, double dashPhase = 0)
        : this(path, new SolidColorBrush(color), width, dashPattern, dashPhase) { }
}

/// <summary>
/// 绘制 glyph run。Glyph run 直接复用 shaping 结果，保证“布局所见即绘制所得”。
/// <paramref name="Origin"/> 是基线起点（像素坐标），变换围绕该原点。
/// </summary>
public sealed record DrawGlyphs(
    FontFace Font,
    EclipseUI.Text.ShapedBuffer Glyphs,
    Point Origin,
    Brush Brush,
    Transform? Transform = null,
    double Opacity = 1) : DrawCommand
{
    public DrawGlyphs(FontFace font, EclipseUI.Text.ShapedBuffer glyphs, Point origin, Color color,
        Transform? transform = null, double opacity = 1)
        : this(font, glyphs, origin, new SolidColorBrush(color), transform, opacity) { }
}

public sealed record DrawImage(
    ImageHandle Image,
    Rect Destination,
    double Opacity = 1) : DrawCommand;

/// <summary>
/// 高斯阴影/模糊圆角矩形。<paramref name="BlurRadius"/> 按 CSS 语义（≈ 2σ），
/// 后端换算为 <c>stdDev = BlurRadius / 2</c>。
/// </summary>
public sealed record DropShadow(
    Rect Rect,
    double CornerRadius,
    double BlurRadius,
    Point Offset,
    Color Color) : DrawCommand;

/// <summary>矩形裁剪，压栈。</summary>
public sealed record PushClip(Rect Rect) : DrawCommand;

/// <summary>路径裁剪，压栈。</summary>
public sealed record PushClipPath(PathGeometry Path) : DrawCommand;

public sealed record PopClip : DrawCommand;

/// <summary>变换压栈；影响后续指令，直到匹配的 <see cref="PopTransform"/>。</summary>
public sealed record PushTransform(Transform Transform) : DrawCommand;

public sealed record PopTransform : DrawCommand;

/// <summary>
/// 图层压栈：透明度 + 混合模式。图层不建立坐标变换（用 <see cref="PushTransform"/>），
/// 带透明度/混合的图层可能触发离屏合成，性能上要单独计量。
/// </summary>
public sealed record PushLayer(double Opacity, LayerBlend Blend = LayerBlend.Normal) : DrawCommand;

public sealed record PopLayer : DrawCommand;

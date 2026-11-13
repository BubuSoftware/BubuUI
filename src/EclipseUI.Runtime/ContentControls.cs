namespace EclipseUI.Elements;

/// <summary>A single real child; replacement detaches, rather than disposes, caller-owned content.</summary>
[UiContent(UiContentKind.Children, Target = nameof(Content))]
public class ContentControl : Panel
{
    public Element? Content
    {
        get => Children.Count == 0 ? null : Children[0];
        set
        {
            if (ReferenceEquals(Content, value)) return;
            Children.ReplaceAll(value is null ? [] : [value]);
        }
    }
    protected internal override Size MeasureCore(Size available)
    {
        var inner = new Size(Math.Max(0, available.Width - Padding * 2), Math.Max(0, available.Height - Padding * 2));
        Content?.Measure(inner);
        return new((Content?.DesiredSize.Width ?? 0) + Padding * 2, (Content?.DesiredSize.Height ?? 0) + Padding * 2);
    }
    protected internal override void ArrangeCore(Rect final) => Content?.Arrange(new(Padding, Padding,
        Math.Max(0, final.Width - Padding * 2), Math.Max(0, final.Height - Padding * 2)));
}

public class Border : ContentControl
{
    public static readonly StyledProperty<UiColor> BackgroundProperty = StyledProperty<UiColor>.Register<Border>("Background", UiColor.Transparent, new() { Invalidate = InvalidateKind.Render });
    public static readonly StyledProperty<UiColor> BorderColorProperty = StyledProperty<UiColor>.Register<Border>("BorderColor", new(100, 110, 125), new() { Invalidate = InvalidateKind.Render });
    public static readonly StyledProperty<double> BorderThicknessProperty = StyledProperty<double>.Register<Border>("BorderThickness", 1, new() { Invalidate = InvalidateKind.Measure });
    public static readonly StyledProperty<double> CornerRadiusProperty = StyledProperty<double>.Register<Border>("CornerRadius", 4, new() { Invalidate = InvalidateKind.Render });
    public UiColor Background { get => GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }
    public UiColor BorderColor { get => GetValue(BorderColorProperty); set => SetValue(BorderColorProperty, value); }
    public double BorderThickness { get => GetValue(BorderThicknessProperty); set => SetValue(BorderThicknessProperty, Math.Max(0, value)); }
    public double CornerRadius { get => GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, Math.Max(0, value)); }
    protected internal override Size MeasureCore(Size available)
    {
        var t = BorderThickness * 2;
        var size = base.MeasureCore(new(Math.Max(0, available.Width - t), Math.Max(0, available.Height - t)));
        return new(size.Width + t, size.Height + t);
    }
    protected internal override void ArrangeCore(Rect final) => Content?.Arrange(new(Padding + BorderThickness, Padding + BorderThickness,
        Math.Max(0, final.Width - 2 * (Padding + BorderThickness)), Math.Max(0, final.Height - 2 * (Padding + BorderThickness))));
    public override void Render(IElementDrawingContext c)
    {
        c.FillRectangle(new(0, 0, FinalRect.Width, FinalRect.Height), Background, CornerRadius);
        c.StrokeRectangle(new(BorderThickness / 2, BorderThickness / 2, Math.Max(0, FinalRect.Width - BorderThickness), Math.Max(0, FinalRect.Height - BorderThickness)), BorderColor, BorderThickness, CornerRadius);
    }
}

public enum Stretch { None, Fill, Uniform, UniformToFill }
public class Image : Element
{
    private IRasterImage? _source;
    public IRasterImage? Source { get => _source; set { if (ReferenceEquals(_source, value)) return; _source = value; InvalidateMeasure(); InvalidateRender(); } }
    public static readonly StyledProperty<Stretch> StretchProperty = StyledProperty<Stretch>.Register<Image>("Stretch", Stretch.Uniform, new() { Invalidate = InvalidateKind.Measure });
    public static readonly StyledProperty<double> OpacityProperty = StyledProperty<double>.Register<Image>("Opacity", 1, new() { Invalidate = InvalidateKind.Render });
    public Stretch Stretch { get => GetValue(StretchProperty); set => SetValue(StretchProperty, value); }
    public double Opacity { get => GetValue(OpacityProperty); set => SetValue(OpacityProperty, Math.Clamp(value, 0, 1)); }
    protected internal override Size MeasureCore(Size available) => Source is null ? Size.Empty : Fit(new(Source.Width, Source.Height), available, Stretch);
    protected internal override void ArrangeCore(Rect final) { }
    internal static Size Fit(Size source, Size available, Stretch stretch)
    {
        if (source.IsEmpty) return Size.Empty;
        var sx = double.IsFinite(available.Width) ? available.Width / source.Width : double.PositiveInfinity;
        var sy = double.IsFinite(available.Height) ? available.Height / source.Height : double.PositiveInfinity;
        if (stretch == Stretch.None || double.IsPositiveInfinity(sx) && double.IsPositiveInfinity(sy)) return source;
        if (stretch == Stretch.Fill) return new(double.IsFinite(available.Width) ? available.Width : source.Width, double.IsFinite(available.Height) ? available.Height : source.Height);
        var scale = stretch == Stretch.UniformToFill && double.IsFinite(sx) && double.IsFinite(sy) ? Math.Max(sx, sy) : Math.Min(sx, sy);
        return new(source.Width * scale, source.Height * scale);
    }
    public override void Render(IElementDrawingContext c)
    {
        if (Source is null) return;
        var size = Fit(new(Source.Width, Source.Height), FinalRect.Size, Stretch);
        c.PushClip(new(0, 0, FinalRect.Width, FinalRect.Height));
        c.Image(Source, new((FinalRect.Width - size.Width) / 2, (FinalRect.Height - size.Height) / 2, size.Width, size.Height), Opacity);
        c.PopClip();
    }
}

public abstract class Shape : Element
{
    public static readonly StyledProperty<UiColor> FillProperty = StyledProperty<UiColor>.Register<Shape>("Fill", new(70, 130, 220), new() { Invalidate = InvalidateKind.Render });
    public static readonly StyledProperty<UiColor> StrokeProperty = StyledProperty<UiColor>.Register<Shape>("Stroke", UiColor.Transparent, new() { Invalidate = InvalidateKind.Render });
    public static readonly StyledProperty<double> StrokeThicknessProperty = StyledProperty<double>.Register<Shape>("StrokeThickness", 1, new() { Invalidate = InvalidateKind.Render });
    public UiColor Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public UiColor Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public double StrokeThickness { get => GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, Math.Max(0, value)); }
    protected internal override Size MeasureCore(Size available) => new(32, 32);
    protected internal override void ArrangeCore(Rect final) { }
}
public class Rectangle : Shape
{
    public static readonly StyledProperty<double> RadiusProperty = StyledProperty<double>.Register<Rectangle>("Radius", 0, new() { Invalidate = InvalidateKind.Render });
    public double Radius { get => GetValue(RadiusProperty); set => SetValue(RadiusProperty, Math.Max(0, value)); }
    public override void Render(IElementDrawingContext c)
    {
        var t = StrokeThickness;
        var r = new Rect(t / 2, t / 2, Math.Max(0, FinalRect.Width - t), Math.Max(0, FinalRect.Height - t));
        c.FillRectangle(r, Fill, Radius); c.StrokeRectangle(r, Stroke, t, Radius);
    }
}
public class Ellipse : Shape
{
    public override void Render(IElementDrawingContext c) => c.Ellipse(new(StrokeThickness / 2, StrokeThickness / 2,
        Math.Max(0, FinalRect.Width - StrokeThickness), Math.Max(0, FinalRect.Height - StrokeThickness)), Fill, Stroke, StrokeThickness);
}
public class Line : Shape
{
    public static readonly StyledProperty<double> X1Property = StyledProperty<double>.Register<Line>("X1", 0, new() { Invalidate = InvalidateKind.Measure });
    public static readonly StyledProperty<double> Y1Property = StyledProperty<double>.Register<Line>("Y1", 0, new() { Invalidate = InvalidateKind.Measure });
    public static readonly StyledProperty<double> X2Property = StyledProperty<double>.Register<Line>("X2", 32, new() { Invalidate = InvalidateKind.Measure });
    public static readonly StyledProperty<double> Y2Property = StyledProperty<double>.Register<Line>("Y2", 32, new() { Invalidate = InvalidateKind.Measure });
    public double X1 { get => GetValue(X1Property); set => SetValue(X1Property, value); }
    public double Y1 { get => GetValue(Y1Property); set => SetValue(Y1Property, value); }
    public double X2 { get => GetValue(X2Property); set => SetValue(X2Property, value); }
    public double Y2 { get => GetValue(Y2Property); set => SetValue(Y2Property, value); }
    protected internal override Size MeasureCore(Size available) => new(Math.Max(0, Math.Max(X1, X2)) + StrokeThickness, Math.Max(0, Math.Max(Y1, Y2)) + StrokeThickness);
    public override void Render(IElementDrawingContext c)
    {
        c.PushClip(new(0, 0, FinalRect.Width, FinalRect.Height)); c.Line(new(X1, Y1), new(X2, Y2), Stroke.A == 0 ? Fill : Stroke, StrokeThickness); c.PopClip();
    }
}
public class Path : Shape
{
    public static readonly StyledProperty<string> DataProperty = StyledProperty<string>.Register<Path>("Data", "", new() { Invalidate = InvalidateKind.Render });
    public string Data { get => GetValue(DataProperty); set => SetValue(DataProperty, value); }
    public override void Render(IElementDrawingContext c)
    {
        c.PushClip(new(0, 0, FinalRect.Width, FinalRect.Height)); c.Path(Data, Fill, Stroke, StrokeThickness); c.PopClip();
    }
}

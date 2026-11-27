using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace BubuUI.Rendering;

internal sealed class ElementDrawingContext : IElementDrawingContext
{
    private readonly DisplayList _list;
    private readonly Point _offset;
    private readonly ITextPainter? _text;
    private int _clipDepth;
    private static readonly ConditionalWeakTable<IRasterImage, ImageHandle> Images = new();
    public UiColor Foreground { get; }
    public UiColor Background { get; }
    public UiColor Border { get; }
    public double CornerRadius { get; }
    internal ElementDrawingContext(DisplayList list, Point offset, ITextPainter? text, Color foreground, Color background, Element element)
    {
        _list = list; _offset = offset; _text = text;
        Foreground = Convert(foreground); Background = Convert(background);
        Border = element.BorderBrush;
        CornerRadius = Visual.GetCornerRadius(element) ?? element.CornerRadius;
    }
    private static UiColor Convert(Color c) => new(c.R, c.G, c.B, c.A);
    private static Color Convert(UiColor c) => new(c.R, c.G, c.B, c.A);
    private Point At(Point p) => new(p.X + _offset.X, p.Y + _offset.Y);
    private Rect At(Rect r) => new(At(r.Location), r.Size);
    public void FillRectangle(Rect rect, UiColor color, double radius = 0)
    {
        if (rect.Size.IsEmpty || color.A == 0) return;
        _list.Add(new FillRoundedRect(At(rect), Math.Max(0, radius), Convert(color)));
    }
    public void DrawShadow(Rect rect, UiColor color, double blurRadius, double radius = 0, Point offset = default)
    {
        if (rect.Size.IsEmpty || color.A == 0) return;
        _list.Add(new DropShadow(At(rect), Math.Max(0, radius), Math.Max(0, blurRadius), offset, Convert(color)));
    }
    public void StrokeRectangle(Rect rect, UiColor color, double width = 1, double radius = 0)
    {
        if (rect.Size.IsEmpty || width <= 0 || color.A == 0) return;
        _list.Add(new StrokePath(PathGeometry.RoundedRectangle(At(rect), Math.Max(0, radius)), Convert(color), width));
    }
    public void StrokeRectangle(Rect rect, UiLinearGradient gradient, double width = 1, double radius = 0)
    {
        if (rect.Size.IsEmpty || width <= 0) return;
        Point Resolve(Point relative, Point offset) => At(new Point(rect.X + rect.Width * relative.X + offset.X, rect.Y + rect.Height * relative.Y + offset.Y));
        var brush = new LinearGradient(Resolve(gradient.Start, gradient.StartOffset), Resolve(gradient.End, gradient.EndOffset),
            [new(Math.Clamp(gradient.StartStop, 0, 1), Convert(gradient.StartColor)), new(1, Convert(gradient.EndColor))]);
        _list.Add(new StrokePath(PathGeometry.RoundedRectangle(At(rect), Math.Max(0, radius)), brush, width));
    }
    public void Ellipse(Rect rect, UiColor fill, UiColor stroke, double strokeWidth = 0)
    {
        if (rect.Size.IsEmpty) return;
        if (fill.A > 0) _list.Add(new FillEllipse(At(rect), Convert(fill)));
        if (strokeWidth > 0 && stroke.A > 0) _list.Add(new StrokePath(PathGeometry.Ellipse(At(rect)), Convert(stroke), strokeWidth));
    }
    public void Line(Point start, Point end, UiColor color, double width = 1)
    {
        if (width > 0 && color.A > 0) _list.Add(new StrokePath(new PathGeometryBuilder().MoveTo(At(start).X, At(start).Y).LineTo(At(end).X, At(end).Y).Build(), Convert(color), width));
    }
    public void Text(string text, Point origin, double fontSize, UiColor color) => _text?.Paint(_list, text, fontSize, At(origin), Convert(color));
    public void Image(IRasterImage image, Rect destination, double opacity = 1)
    {
        var handle = image as ImageHandle ?? Images.GetValue(image, static i => new ImageHandle(i.Width, i.Height, i.Pixels));
        _list.Add(new DrawImage(handle, At(destination), Math.Clamp(opacity, 0, 1)));
    }
    public void PushClip(Rect rect) { _clipDepth++; _list.Add(new PushClip(At(rect))); }
    public void PushRoundedClip(Rect rect, double radius) { _clipDepth++; _list.Add(new PushClipPath(PathGeometry.RoundedRectangle(At(rect), radius))); }
    public void PopClip()
    {
        if (_clipDepth == 0) throw new InvalidOperationException("Custom drawing clip stack is empty.");
        _clipDepth--; _list.Add(new PopClip());
    }
    internal void Complete()
    {
        if (_clipDepth != 0) throw new InvalidOperationException("Custom drawing must balance PushClip and PopClip.");
    }
    public void Path(string data, UiColor fill, UiColor stroke, double strokeWidth = 1)
    {
        if (string.IsNullOrWhiteSpace(data)) return;
        var matches = Regex.Matches(data, @"[MmLlHhVvCcQqZz]|[-+]?(?:\d*\.\d+|\d+\.?\d*)(?:[eE][-+]?\d+)?", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        // Reject unsupported commands and malformed input instead of silently drawing a different path.
        var remainder = Regex.Replace(data, @"[MmLlHhVvCcQqZz]|[-+]?(?:\d*\.\d+|\d+\.?\d*)(?:[eE][-+]?\d+)?|[\s,]+", "", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        if (remainder.Length != 0) throw new FormatException("Path supports M, L, H, V, C, Q and Z commands.");
        int i = 0; char command = ' '; double x = 0, y = 0, startX = 0, startY = 0;
        var builder = new PathGeometryBuilder();
        double Number() => i < matches.Count && double.TryParse(matches[i++].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) ? n : throw new FormatException("Incomplete path command.");
        Point Pair(bool relative) { var a = Number(); var b = Number(); return new(a + (relative ? x : 0), b + (relative ? y : 0)); }
        while (i < matches.Count)
        {
            if (char.IsLetter(matches[i].Value[0])) command = matches[i++].Value[0];
            var relative = char.IsLower(command);
            switch (char.ToUpperInvariant(command))
            {
                case 'M':
                    var m = Pair(relative); x = startX = m.X; y = startY = m.Y; builder.MoveTo(x + _offset.X, y + _offset.Y); command = relative ? 'l' : 'L'; break;
                case 'L': var l = Pair(relative); x = l.X; y = l.Y; builder.LineTo(x + _offset.X, y + _offset.Y); break;
                case 'H': x = Number() + (relative ? x : 0); builder.LineTo(x + _offset.X, y + _offset.Y); break;
                case 'V': y = Number() + (relative ? y : 0); builder.LineTo(x + _offset.X, y + _offset.Y); break;
                case 'C':
                    var c1 = Pair(relative); var c2 = Pair(relative); var c3 = Pair(relative);
                    builder.CubicTo(c1.X + _offset.X, c1.Y + _offset.Y, c2.X + _offset.X, c2.Y + _offset.Y, c3.X + _offset.X, c3.Y + _offset.Y); x = c3.X; y = c3.Y; break;
                case 'Q':
                    var q1 = Pair(relative); var q2 = Pair(relative);
                    builder.QuadraticTo(q1.X + _offset.X, q1.Y + _offset.Y, q2.X + _offset.X, q2.Y + _offset.Y); x = q2.X; y = q2.Y; break;
                case 'Z': builder.Close(); x = startX; y = startY; command = ' '; break;
                default: throw new FormatException("Path must begin with a command.");
            }
        }
        var path = builder.Build();
        if (fill.A > 0) _list.Add(new FillPath(path, Convert(fill)));
        if (strokeWidth > 0 && stroke.A > 0) _list.Add(new StrokePath(path, Convert(stroke), strokeWidth));
    }
}

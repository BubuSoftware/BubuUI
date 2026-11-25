using CoreGraphics;
using CoreText;
using EclipseUI.Elements;
using EclipseUI.Rendering;
using Foundation;
using NFloat = System.Runtime.InteropServices.NFloat;

namespace EclipseUI.Platform.iOS;

/// <summary>Shares the system-shaped line, including fallback font runs, between layout and drawing.</summary>
internal sealed class CoreTextPainter : ITextPainter, ITextMeasurer
{
    private sealed record Layout(CTLine Line, TextLineMetrics Metrics);
    private readonly Dictionary<(string Text, double Size), Layout> _layouts = [];

    private Layout Shape(string text, double fontSize)
    {
        if (_layouts.TryGetValue((text, fontSize), out var layout)) return layout;

        using var font = new CTFont(CTFontUIFontType.System, (NFloat)fontSize, NSLocale.PreferredLanguages[0]);
        var attributes = new CTStringAttributes { Font = font, ForegroundColorFromContext = true };
        using var value = new NSAttributedString(text, attributes);
        using var dictionary = attributes.Dictionary;
        var line = new CTLine(value);
        var width = line.GetTypographicBounds(out var ascent, out var descent, out _);
        var metrics = new TextLineMetrics(Math.Max(font.AscentMetric, ascent), Math.Max(font.DescentMetric, descent), width);
        layout = new Layout(line, metrics);
        // Retained display lists can still reference an evicted line. Its wrapper releases
        // the native CTLine once both the layout cache and the last drawing command let go.
        if (_layouts.Count >= 512) _layouts.Clear();
        _layouts.Add((text, fontSize), layout);
        return layout;
    }

    public TextLineMetrics Measure(string text, double fontSize) => Shape(text, fontSize).Metrics;

    TextMetrics ITextMeasurer.Measure(string text, double fontSize)
    {
        var metrics = Measure(text, fontSize);
        return new(metrics.Width, metrics.Height);
    }

    public void Paint(DisplayList list, string text, double fontSize, Point topLeft, Color color)
    {
        if (text.Length == 0) return;
        var layout = Shape(text, fontSize);
        list.Add(new CoreTextLine(layout.Line, new Point(topLeft.X, topLeft.Y + layout.Metrics.Ascent), color));
    }
}

internal sealed record CoreTextLine(CTLine Line, Point Baseline, Color Color) : DrawCommand;

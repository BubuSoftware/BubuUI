using System;
using System.Collections.Generic;
using EclipseUI.Elements;
using EclipseUI.Rendering;
using EclipseUI.Text;

namespace EclipseUI.Text.Shaping;

/// <summary>
/// HarfBuzz → <see cref="DisplayList"/> 的文本适配器，同时实现布局侧的
/// <see cref="ITextMeasurer"/>。布局测量与绘制共用同一 shaping 缓存，
/// 因此断行/对齐用的是真实字形进距，屏幕上看到的也就是布局算出来的。
/// </summary>
public sealed class HarfBuzzTextPainter : ITextPainter, ITextMeasurer, IDisposable
{
    /// <summary>缓存上限：文本是动态的（计数器、列表标题），超过后整体清空避免无界增长。</summary>
    private const int MaxCachedRuns = 512;

    private readonly HarfBuzzTextShaper _shaper;
    private readonly Dictionary<(string Text, double Size), ShapedBuffer> _cache = new();

    public HarfBuzzTextPainter(HarfBuzzTextShaper shaper)
    {
        _shaper = shaper ?? throw new ArgumentNullException(nameof(shaper));
    }

    public HarfBuzzTextShaper Shaper => _shaper;

    public TextLineMetrics Measure(string text, double fontSize)
    {
        if (string.IsNullOrEmpty(text)) return _shaper.LineMetrics(fontSize);
        var shaped = ShapeCached(text, fontSize);
        return _shaper.LineMetrics(fontSize) with { Width = shaped.Width };
    }

    TextMetrics ITextMeasurer.Measure(string text, double fontSize)
    {
        var metrics = Measure(text, fontSize);
        return new TextMetrics(metrics.Width, metrics.Height);
    }

    public void Paint(DisplayList list, string text, double fontSize, Point topLeft, Color color)
    {
        if (string.IsNullOrEmpty(text)) return;

        var shaped = ShapeCached(text, fontSize);
        if (shaped.Glyphs.Count == 0) return;

        var ascent = _shaper.LineMetrics(fontSize).Ascent;
        list.Add(new DrawGlyphs(
            _shaper.Face,
            shaped,
            new Point(topLeft.X, topLeft.Y + ascent),
            color));
    }

    private ShapedBuffer ShapeCached(string text, double fontSize)
    {
        var key = (text, fontSize);
        if (_cache.TryGetValue(key, out var shaped)) return shaped;

        if (_cache.Count >= MaxCachedRuns) _cache.Clear();

        shaped = _shaper.Shape(text, fontSize);
        _cache[key] = shaped;
        return shaped;
    }

    public void Dispose() => _cache.Clear();
}

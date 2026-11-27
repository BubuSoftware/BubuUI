using Path = System.IO.Path;
using System.Globalization;
using System.Text;
using BubuUI.Elements;
using BubuUI.Rendering;
using BubuUI.Text;

namespace BubuUI.Text.Shaping;

/// <summary>
/// Shapes mixed text into font runs. Layout and painting share the same cached runs,
/// advances and baseline; a grapheme (including an emoji sequence) never crosses fonts.
/// </summary>
public sealed class HarfBuzzTextPainter : ITextPainter, ITextMeasurer, IDisposable
{
    private const int MaxCachedRuns = 512;
    private readonly HarfBuzzTextShaper _shaper;
    private readonly Fallback[] _fallbacks;
    private readonly Dictionary<(string Text, double Size), TextLayout> _cache = new();
    private readonly Dictionary<string, HarfBuzzTextShaper> _fontSelections = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private bool _disposed;
    private sealed record FontRun(HarfBuzzTextShaper Font, ShapedBuffer Glyphs);
    private sealed record TextLayout(IReadOnlyList<FontRun> Runs, TextLineMetrics Metrics);

    private sealed class Fallback(string path)
    {
        internal readonly string Path = path;
        internal bool Attempted;
        internal HarfBuzzTextShaper? Font;
        internal bool IsEmoji => System.IO.Path.GetFileName(Path).Contains("emoji", StringComparison.OrdinalIgnoreCase) ||
            System.IO.Path.GetFileName(Path).Contains("seguiemj", StringComparison.OrdinalIgnoreCase);
        internal HarfBuzzTextShaper? Load()
        {
            if (Attempted) return Font;
            Attempted = true;
            if (!File.Exists(Path)) return null;
            try { return Font = new HarfBuzzTextShaper(Path); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Console.WriteLine($"fallback font failed: {Path}: {ex.Message}");
                return null;
            }
        }
    }

    /// <summary>Installed fonts are opened lazily, only when the primary face cannot display the text.</summary>
    public static IReadOnlyList<string> DefaultFallbackFontPaths
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                var directory = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
                return new[] { "msyh.ttc", "seguisym.ttf", "simsun.ttc", "simsunb.ttf", "seguiemj.ttf" }
                    .Select(name => Path.Combine(directory, name)).ToArray();
            }
            if (OperatingSystem.IsMacOS()) return [
                "/System/Library/Fonts/Apple Color Emoji.ttc",
                "/System/Library/Fonts/PingFang.ttc",
                "/System/Library/Fonts/STHeiti Medium.ttc",
                "/System/Library/Fonts/STHeiti Light.ttc",
                "/System/Library/Fonts/Supplemental/Songti.ttc"];
            return ["/usr/share/fonts/truetype/noto/NotoColorEmoji.ttf",
                "/usr/share/fonts/noto/NotoColorEmoji.ttf",
                "/usr/share/fonts/google-noto-emoji/NotoColorEmoji.ttf",
                "/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc",
                "/usr/share/fonts/noto-cjk/NotoSansCJK-Regular.ttc",
                "/usr/share/fonts/google-noto-sans-cjk-fonts/NotoSansCJK-Regular.ttc"];
        }
    }

    /// <param name="shaper">Caller-owned primary font; the painter does not dispose it.</param>
    /// <param name="fallbackFontPaths">Fallback fonts owned by the painter. Null uses installed system defaults; an empty list disables fallback.</param>
    public HarfBuzzTextPainter(HarfBuzzTextShaper shaper, IEnumerable<string>? fallbackFontPaths = null)
    {
        _shaper = shaper ?? throw new ArgumentNullException(nameof(shaper));
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        _fallbacks = (fallbackFontPaths ?? DefaultFallbackFontPaths).Select(Path.GetFullPath)
            .Where(path => !comparer.Equals(path, shaper.FontPath)).Distinct(comparer).Select(path => new Fallback(path)).ToArray();
    }

    public HarfBuzzTextShaper Shaper => _shaper;

    public TextLineMetrics Measure(string text, double fontSize) => ShapeCached(text, fontSize).Metrics;

    TextMetrics ITextMeasurer.Measure(string text, double fontSize)
    {
        var metrics = Measure(text, fontSize);
        return new(metrics.Width, metrics.Height);
    }

    public void Paint(DisplayList list, string text, double fontSize, Point topLeft, Color color)
    {
        var layout = ShapeCached(text, fontSize);
        var x = topLeft.X;
        foreach (var run in layout.Runs)
        {
            if (run.Glyphs.Glyphs.Count > 0)
                list.Add(new DrawGlyphs(run.Font.Face, run.Glyphs, new(x, topLeft.Y + layout.Metrics.Ascent), color));
            x += run.Glyphs.Width;
        }
    }

    private TextLayout ShapeCached(string text, double fontSize)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_cache.TryGetValue((text, fontSize), out var layout)) return layout;
            if (_cache.Count >= MaxCachedRuns) _cache.Clear();
            layout = Shape(text, fontSize);
            _cache[(text, fontSize)] = layout;
            return layout;
        }
    }

    private TextLayout Shape(string text, double fontSize)
    {
        var primary = _shaper.Shape(text, fontSize);
        var metrics = _shaper.LineMetrics(fontSize);
        if (text.Length == 0) return new([], metrics);
        // Preserve kerning, ligatures and script context when no fallback is required.
        if (_fallbacks.Length == 0 || primary.Glyphs.All(g => g.GlyphId != 0) && !MayUseEmoji(text))
            return new([new(_shaper, primary)], metrics with { Width = primary.Width });

        var runs = new List<FontRun>();
        var elements = StringInfo.GetTextElementEnumerator(text);
        HarfBuzzTextShaper? current = null;
        var segment = new StringBuilder();
        void Flush()
        {
            if (current is null || segment.Length == 0) return;
            runs.Add(new(current, current.Shape(segment.ToString(), fontSize)));
            var line = current.LineMetrics(fontSize);
            metrics = new(Math.Max(metrics.Ascent, line.Ascent), Math.Max(metrics.Descent, line.Descent));
            segment.Clear();
        }
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            var font = SelectFont(element);
            if (!ReferenceEquals(font, current)) { Flush(); current = font; }
            segment.Append(element);
        }
        Flush();
        return new(runs, metrics with { Width = runs.Sum(r => r.Glyphs.Width) });
    }

    private HarfBuzzTextShaper SelectFont(string element)
    {
        if (_fontSelections.TryGetValue(element, out var selected)) return selected;
        HarfBuzzTextShaper? font = null;
        if (PrefersEmoji(element))
            foreach (var candidate in _fallbacks)
                if (candidate.IsEmoji && candidate.Load() is { } emoji && emoji.SupportsTextElement(element)) { font = emoji; break; }
        if (font is null && _shaper.SupportsTextElement(element)) font = _shaper;
        if (font is null && element.Contains('\uFE0E'))
            foreach (var candidate in _fallbacks)
                if (!candidate.IsEmoji && candidate.Load() is { } text && text.SupportsTextElement(element)) { font = text; break; }
        if (font is null)
            foreach (var candidate in _fallbacks)
                if (candidate.Load() is { } fallback && fallback.SupportsTextElement(element)) { font = fallback; break; }
        font ??= _shaper; // No font supports this cluster: retain .notdef and its advance.
        if (_fontSelections.Count >= 4096) _fontSelections.Clear();
        _fontSelections[element] = font;
        return font;
    }

    private static bool PrefersEmoji(string text) => !text.Contains('\uFE0E') && MayUseEmoji(text);

    private static bool MayUseEmoji(string text)
    {
        foreach (var rune in text.EnumerateRunes())
            if (rune.Value is 0xFE0F or 0x20E3 or >= 0x1F000 and <= 0x1FAFF or >= 0x2600 and <= 0x27BF)
                return true;
        return false;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _cache.Clear(); _fontSelections.Clear();
            foreach (var fallback in _fallbacks) fallback.Font?.Dispose();
        }
    }
}

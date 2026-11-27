using System.Globalization;

namespace BubuUI;

/// <summary>Backend independent sRGB color. Template literals use #RRGGBB or #RRGGBBAA.</summary>
[UiLiteralParser(Pattern = @"^#[0-9a-fA-F]{6}([0-9a-fA-F]{2})?$")]
public readonly record struct UiColor(byte R, byte G, byte B, byte A = 255)
{
    public static UiColor Transparent => default;
    public static UiColor Parse(string value)
    {
        if (value.Length is not (7 or 9) || value[0] != '#') throw new FormatException("Expected #RRGGBB or #RRGGBBAA.");
        byte Part(int i) => byte.Parse(value.AsSpan(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return new(Part(1), Part(3), Part(5), value.Length == 9 ? Part(7) : (byte)255);
    }
}

public interface IRasterImage
{
    int Width { get; }
    int Height { get; }
    ReadOnlyMemory<byte> Pixels { get; }
}

/// <summary>Local coordinates; implementations participate in retained painting and damage tracking.</summary>
public interface IElementDrawingContext
{
    void StrokeRectangle(Rect rect, UiLinearGradient gradient, double width = 1, double radius = 0) =>
        StrokeRectangle(rect, gradient.StartColor, width, radius);
    UiColor Foreground { get; }
    UiColor Background { get; }
    UiColor Border { get; }
    double CornerRadius => 0;
    void FillRectangle(Rect rect, UiColor color, double radius = 0);
    /// <summary>Draw a Gaussian shadow behind a rounded rectangle. Blur radius follows CSS semantics (twice sigma).</summary>
    void DrawShadow(Rect rect, UiColor color, double blurRadius, double radius = 0, Point offset = default) { }
    void StrokeRectangle(Rect rect, UiColor color, double width = 1, double radius = 0);
    void Ellipse(Rect rect, UiColor fill, UiColor stroke, double strokeWidth = 0);
    void Line(Point start, Point end, UiColor color, double width = 1);
    void Path(string data, UiColor fill, UiColor stroke, double strokeWidth = 1);
    void Text(string text, Point origin, double fontSize, UiColor color);
    void Image(IRasterImage image, Rect destination, double opacity = 1);
    void PushClip(Rect rect);
    void PushRoundedClip(Rect rect, double radius) => PushClip(rect);
    void PopClip();
}

public interface IClipboard
{
    string GetText();
    void SetText(string text);
}

/// <summary>Hosts install their clipboard; headless tests can use an isolated instance.</summary>
public sealed class MemoryClipboard : IClipboard
{
    private string _text = "";
    public string GetText() => _text;
    public void SetText(string text) => _text = text ?? "";
}

public static class Clipboard
{
    public static IClipboard Current { get; set; } = new MemoryClipboard();
}

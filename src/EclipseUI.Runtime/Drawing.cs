using System.Globalization;

namespace EclipseUI;

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
    UiColor Foreground { get; }
    UiColor Background { get; }
    UiColor Border { get; }
    UiColor Accent { get; }
    UiColor OnAccent { get; }
    UiColor SelectionBackground => Accent;
    UiColor SelectionForeground => OnAccent;
    UiColor MutedForeground => Foreground with { A = 160 };
    double CornerRadius => 4;
    /// <summary>Opt-in layered control chrome; flat/custom drawing contexts retain their defaults.</summary>
    bool HasLayeredChrome => false;
    UiColor StrongBorder => Border;
    UiColor AltBackground => Background;
    UiColor ThumbBackground => OnAccent;
    void ControlSurface(Rect rect, double radius, bool input = false, bool focused = false, bool selected = false)
    {
        FillRectangle(rect, selected ? Accent : Background, radius);
        StrokeRectangle(new(rect.X + .5, rect.Y + .5, Math.Max(0, rect.Width - 1), Math.Max(0, rect.Height - 1)), Border, 1, radius);
    }
    void FillRectangle(Rect rect, UiColor color, double radius = 0);
    void StrokeRectangle(Rect rect, UiColor color, double width = 1, double radius = 0);
    void Ellipse(Rect rect, UiColor fill, UiColor stroke, double strokeWidth = 0);
    void Line(Point start, Point end, UiColor color, double width = 1);
    void Path(string data, UiColor fill, UiColor stroke, double strokeWidth = 1);
    void Text(string text, Point origin, double fontSize, UiColor color);
    void Image(IRasterImage image, Rect destination, double opacity = 1);
    void PushClip(Rect rect);
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

namespace BubuUI;

/// <summary>Two-stop gradient. Points are relative to the painted bounds, offsets are logical pixels.</summary>
public readonly record struct UiLinearGradient(UiColor StartColor, UiColor EndColor,
    Point Start, Point End, Point StartOffset = default, Point EndOffset = default, double StartStop = 0);

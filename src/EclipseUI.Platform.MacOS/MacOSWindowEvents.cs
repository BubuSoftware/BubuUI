using EclipseUI.Rendering;

namespace EclipseUI.Platform.MacOS;

public sealed class MacOSResizeEventArgs(int width, int height, double scale) : EventArgs
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public double Scale { get; } = scale;
}

public sealed class MacOSPointerEventArgs(Point logicalPosition, KeyModifiers modifiers = KeyModifiers.None,
    PointerButton button = PointerButton.Left) : EventArgs
{
    public Point LogicalPosition { get; } = logicalPosition;
    public KeyModifiers Modifiers { get; } = modifiers;
    public PointerButton Button { get; } = button;
}

public sealed class MacOSWheelEventArgs(Point logicalPosition, double delta, KeyModifiers modifiers = KeyModifiers.None) : EventArgs
{
    public Point LogicalPosition { get; } = logicalPosition;
    public double Delta { get; } = delta;
    public KeyModifiers Modifiers { get; } = modifiers;
}

public sealed class MacOSKeyEventArgs(Key key, KeyModifiers modifiers = KeyModifiers.None, bool isRepeat = false) : EventArgs
{
    public Key Key { get; } = key;
    public KeyModifiers Modifiers { get; } = modifiers;
    public bool IsRepeat { get; } = isRepeat;
}

public sealed class MacOSTextEventArgs(string text) : EventArgs
{
    public string Text { get; } = text;
}

/// <summary>The native window contract consumed by the macOS frame loop. All calls use the UI thread.</summary>
public interface IMacOSWindow : IDisposable, IWindowLiveUpdater
{
    NativeWindowHandle SurfaceHandle { get; }
    int ClientWidth { get; }
    int ClientHeight { get; }
    double ScaleFactor { get; }
    Size LogicalClientSize { get; }
    bool IsClosed { get; }
    IClipboard Clipboard { get; }
    /// <summary>Match native window chrome to the resolved content palette.</summary>
    void SetDarkTitleBar(bool enabled) { }
    void Show();
    void Close();
    bool PumpMessages();
    void SetImeCaret(Rect logicalCaret);
    void SetTextInputEnabled(bool enabled);
    event EventHandler<MacOSResizeEventArgs>? Resized;
    event EventHandler? PaintRequested;
    event EventHandler<MacOSPointerEventArgs>? PointerMoved;
    event EventHandler<MacOSPointerEventArgs>? PointerPressed;
    event EventHandler<MacOSPointerEventArgs>? PointerReleased;
    event EventHandler? PointerCaptureLost;
    event EventHandler<MacOSWheelEventArgs>? PointerWheel;
    event EventHandler<MacOSKeyEventArgs>? KeyDown;
    event EventHandler<MacOSKeyEventArgs>? KeyUp;
    event EventHandler<MacOSTextEventArgs>? CharacterTyped;
    event EventHandler<TextCompositionEventArgs>? TextComposition;
    event EventHandler? FocusLost;
    event EventHandler? Closing;
}

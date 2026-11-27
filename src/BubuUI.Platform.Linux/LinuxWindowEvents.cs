using BubuUI.Rendering;

namespace BubuUI.Platform.Linux;

public sealed class LinuxResizeEventArgs(int width, int height, double scale) : EventArgs
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public double Scale { get; } = scale;
}

public sealed class LinuxPointerEventArgs(Point logicalPosition, KeyModifiers modifiers = KeyModifiers.None,
    PointerButton button = PointerButton.Left) : EventArgs
{
    public Point LogicalPosition { get; } = logicalPosition;
    public KeyModifiers Modifiers { get; } = modifiers;
    public PointerButton Button { get; } = button;
}

public sealed class LinuxWheelEventArgs(Point logicalPosition, double delta, KeyModifiers modifiers = KeyModifiers.None) : EventArgs
{
    public Point LogicalPosition { get; } = logicalPosition;
    public double Delta { get; } = delta;
    public KeyModifiers Modifiers { get; } = modifiers;
}

public sealed class LinuxKeyEventArgs(Key key, KeyModifiers modifiers = KeyModifiers.None, bool isRepeat = false) : EventArgs
{
    public Key Key { get; } = key;
    public KeyModifiers Modifiers { get; } = modifiers;
    public bool IsRepeat { get; } = isRepeat;
}

public sealed class LinuxTextEventArgs(string text) : EventArgs
{
    public string Text { get; } = text;
}

/// <summary>The native window contract consumed by the Linux frame loop. All calls use the UI thread.</summary>
public interface ILinuxWindow : IDisposable, IWindowLiveUpdater
{
    NativeWindowHandle SurfaceHandle { get; }
    int ClientWidth { get; }
    int ClientHeight { get; }
    double ScaleFactor { get; }
    Size LogicalClientSize { get; }
    bool IsClosed { get; }
    IClipboard Clipboard { get; }
    void Show();
    void Close();
    bool PumpMessages();
    void SetImeCaret(Rect logicalCaret);
    void SetTextInputEnabled(bool enabled);
    event EventHandler<LinuxResizeEventArgs>? Resized;
    event EventHandler? PaintRequested;
    event EventHandler<LinuxPointerEventArgs>? PointerMoved;
    event EventHandler<LinuxPointerEventArgs>? PointerPressed;
    event EventHandler<LinuxPointerEventArgs>? PointerReleased;
    event EventHandler? PointerCaptureLost;
    event EventHandler<LinuxWheelEventArgs>? PointerWheel;
    event EventHandler<LinuxKeyEventArgs>? KeyDown;
    event EventHandler<LinuxKeyEventArgs>? KeyUp;
    event EventHandler<LinuxTextEventArgs>? CharacterTyped;
    event EventHandler<TextCompositionEventArgs>? TextComposition;
    event EventHandler? FocusLost;
    event EventHandler? Closing;
}

namespace BubuUI.Rendering;

/// <summary>Creates owned popup surfaces. Coordinates and sizes use the owner's logical client units.</summary>
public interface IPopupWindowFactory
{
    Size AvailableSize { get; }
    bool IsOwnerFocused { get; }
    IPopupWindow Create(bool interactive, IPopupWindow? parent = null);
    /// <summary>Whether the factory can create a genuine modal child window.</summary>
    bool SupportsModal => false;
    IPopupWindow Create(bool interactive, bool modal, IPopupWindow? parent = null)
        => modal ? throw new NotSupportedException("This popup factory does not support modal child windows.")
            : Create(interactive, parent);
}

/// <summary>A native owned popup; input positions are local logical client coordinates.</summary>
public interface IPopupWindow : IDisposable
{
    NativeWindowHandle SurfaceHandle { get; }
    int ClientWidth { get; }
    int ClientHeight { get; }
    double ScaleFactor { get; }
    bool IsClosed { get; }
    bool IsFocused { get; }
    bool IsModal => false;
    /// <summary>Whether the native surface supports per-pixel transparency, including exterior shadows.</summary>
    bool IsTransparent => false;
    /// <summary>Positions the popup, constraining it to the display work area, and returns its actual owner-relative bounds.</summary>
    Rect SetBounds(Rect bounds);
    /// <summary>Updates the input/visible shape. The content rectangle is in local logical client coordinates.</summary>
    void SetShape(Rect contentBounds, double cornerRadius) { }
    void Show();
    /// <summary>Returns native focus to a modal child without activating another application.</summary>
    void Activate() { }
    void SetTextInputEnabled(bool enabled);
    void SetImeCaret(Rect caret);
    event Action<Point>? PointerMoved;
    event Action<Point, KeyModifiers, PointerButton>? PointerPressed;
    event Action<Point>? PointerReleased;
    event Action? PointerCaptureLost;
    event Action<Point, double, KeyModifiers>? PointerWheel;
    event Action<Key, KeyModifiers, bool>? KeyDown;
    event Action<Key, KeyModifiers>? KeyUp;
    event Action<string>? TextInput;
    event Action<string, CompositionStage>? TextComposition;
    event Action? FocusLost;
    event Action? PaintRequested;
    event Action? Closed;
}

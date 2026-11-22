namespace EclipseUI;

public enum PointerCursor { Arrow, ResizeHorizontal }

public enum Key
{
    None = 0,
    Backspace = 8,
    Tab = 9,
    Enter = 13,
    Escape = 27,
    Space = 32,
    Left = 37,
    Up = 38,
    Right = 39,
    Down = 40,
    Delete = 46,
    Home = 36, End = 35, PageUp = 33, PageDown = 34,
    F2 = 113,
    A = 65, B, C, D, E, F, G, H, I, J, K, L, M, N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
}

/// <summary>指针事件。Position 为根坐标系位置（无头宿主即窗口客户区坐标）。</summary>
public sealed class PointerEventArgs : RoutedEventArgs
{
    public PointerEventArgs(RoutedEvent routedEvent, Point position, KeyModifiers modifiers = KeyModifiers.None, PointerButton button = PointerButton.Left)
        : base(routedEvent) { Position = position; Modifiers = modifiers; Button = button; }

    public KeyModifiers Modifiers { get; }
    public PointerButton Button { get; }

    public Point Position { get; }
}

public sealed class KeyEventArgs : RoutedEventArgs
{
    public KeyEventArgs(RoutedEvent routedEvent, Key key, KeyModifiers modifiers = KeyModifiers.None, bool isRepeat = false)
        : base(routedEvent) { Key = key; Modifiers = modifiers; IsRepeat = isRepeat; }

    public KeyModifiers Modifiers { get; }
    public bool IsRepeat { get; }

    public Key Key { get; }
}

/// <summary>
/// 滚轮事件。Position 为根坐标系位置；Delta 为原始齿轮刻度（Win32 约定每格 120，
/// 正值 = 向上滚/远离用户）。消费方负责把刻度换算为滚动像素。
/// </summary>
public sealed class WheelEventArgs : RoutedEventArgs
{
    public WheelEventArgs(RoutedEvent routedEvent, Point position, double delta, KeyModifiers modifiers = KeyModifiers.None)
        : base(routedEvent)
    {
        Position = position;
        Delta = delta;
        Modifiers = modifiers;
    }

    public Point Position { get; }

    public double Delta { get; }
    public KeyModifiers Modifiers { get; }
}

[Flags]
public enum KeyModifiers { None = 0, Shift = 1, Control = 2, Alt = 4, Meta = 8 }

internal static class InputGestures
{
    // Keep the existing Control shortcuts available; on macOS Command is also
    // a command modifier without changing the native modifier reported to apps.
    internal static bool HasCommand(KeyModifiers modifiers)
        => modifiers.HasFlag(KeyModifiers.Control)
           || OperatingSystem.IsMacOS() && modifiers.HasFlag(KeyModifiers.Meta);
}
public enum PointerButton { Left, Right, Middle }
public enum CompositionStage { Started, Updated, Committed, Cancelled }
public sealed class TextInputEventArgs : RoutedEventArgs
{
    public TextInputEventArgs(string text) : base(Element.TextInputEvent) => Text = text;
    public string Text { get; }
}
public sealed class TextCompositionEventArgs : RoutedEventArgs
{
    public TextCompositionEventArgs(string text, CompositionStage stage) : base(Element.TextCompositionEvent)
    { Text = text; Stage = stage; }
    public string Text { get; }
    public CompositionStage Stage { get; }
}

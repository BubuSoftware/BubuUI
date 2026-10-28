namespace EclipseUI;

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
    A = 65, B, C, D, E, F, G, H, I, J, K, L, M, N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
}

/// <summary>输入与焦点路由事件（tunnel/bubble 语义见 RoutedEvent）。</summary>
public static class InputEvents
{
    public static readonly RoutedEvent PointerPressedEvent = new RoutedEvent("PointerPressed", RoutingStrategy.Bubble);
    public static readonly RoutedEvent PointerReleasedEvent = new RoutedEvent("PointerReleased", RoutingStrategy.Bubble);
    public static readonly RoutedEvent PointerMovedEvent = new RoutedEvent("PointerMoved", RoutingStrategy.Bubble);
    public static readonly RoutedEvent KeyDownEvent = new RoutedEvent("KeyDown", RoutingStrategy.Bubble);
    public static readonly RoutedEvent KeyUpEvent = new RoutedEvent("KeyUp", RoutingStrategy.Bubble);
    public static readonly RoutedEvent GotFocusEvent = new RoutedEvent("GotFocus", RoutingStrategy.Bubble);
    public static readonly RoutedEvent LostFocusEvent = new RoutedEvent("LostFocus", RoutingStrategy.Bubble);
}

/// <summary>指针事件。Position 为根坐标系位置（无头宿主即窗口客户区坐标）。</summary>
public sealed class PointerEventArgs : RoutedEventArgs
{
    public PointerEventArgs(RoutedEvent routedEvent, Point position)
        : base(routedEvent) => Position = position;

    public Point Position { get; }
}

public sealed class KeyEventArgs : RoutedEventArgs
{
    public KeyEventArgs(RoutedEvent routedEvent, Key key)
        : base(routedEvent) => Key = key;

    public Key Key { get; }
}

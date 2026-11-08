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

/// <summary>
/// 滚轮事件。Position 为根坐标系位置；Delta 为原始齿轮刻度（Win32 约定每格 120，
/// 正值 = 向上滚/远离用户）。消费方负责把刻度换算为滚动像素。
/// </summary>
public sealed class WheelEventArgs : RoutedEventArgs
{
    public WheelEventArgs(RoutedEvent routedEvent, Point position, double delta)
        : base(routedEvent)
    {
        Position = position;
        Delta = delta;
    }

    public Point Position { get; }

    public double Delta { get; }
}

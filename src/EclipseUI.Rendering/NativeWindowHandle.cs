namespace EclipseUI.Rendering;

public enum NativeWindowKind { Unknown, Win32, Xlib, Wayland }

/// <summary>Borrowed native window and display handles. The platform owns their lifetime.</summary>
public readonly record struct NativeWindowHandle
{
    private NativeWindowHandle(NativeWindowKind kind, nint handle, nint display, int screen)
    {
        if (handle == 0) throw new ArgumentException("Window handle must not be zero.", nameof(handle));
        if (kind != NativeWindowKind.Win32 && display == 0)
            throw new ArgumentException("Display handle must not be zero.", nameof(display));
        Kind = kind;
        Handle = handle;
        Display = display;
        Screen = screen;
    }

    public NativeWindowKind Kind { get; }
    public nint Handle { get; }
    public nint Display { get; }
    public int Screen { get; }

    public static NativeWindowHandle FromWin32(nint hwnd) => new(NativeWindowKind.Win32, hwnd, 0, 0);
    public static NativeWindowHandle FromXlib(nint window, nint display, int screen = 0)
        => new(NativeWindowKind.Xlib, window, display, screen);
    public static NativeWindowHandle FromWayland(nint surface, nint display)
        => new(NativeWindowKind.Wayland, surface, display, 0);
}

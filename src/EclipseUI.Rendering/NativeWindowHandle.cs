namespace EclipseUI.Rendering;

public enum NativeWindowKind { Unknown, Win32, Xlib, Wayland, AppKit }

/// <summary>Borrowed native window and display handles. The platform owns their lifetime.</summary>
public readonly record struct NativeWindowHandle
{
    private NativeWindowHandle(NativeWindowKind kind, nint handle, nint display, int screen)
    {
        if (handle == 0) throw new ArgumentException("Window handle must not be zero.", nameof(handle));
        if (kind is NativeWindowKind.Xlib or NativeWindowKind.Wayland && display == 0)
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
    /// <summary>The native window supports compositing the surface's per-pixel alpha.</summary>
    public bool IsTransparent { get; init; }

    public static NativeWindowHandle FromWin32(nint hwnd) => new(NativeWindowKind.Win32, hwnd, 0, 0);
    public static NativeWindowHandle FromXlib(nint window, nint display, int screen = 0)
        => new(NativeWindowKind.Xlib, window, display, screen);
    public static NativeWindowHandle FromWayland(nint surface, nint display)
        => new(NativeWindowKind.Wayland, surface, display, 0);

    /// <summary>A borrowed NSView, retained by the platform until the rendering surface is disposed.</summary>
    public static NativeWindowHandle FromAppKit(nint nsView) => new(NativeWindowKind.AppKit, nsView, 0, 0);
}

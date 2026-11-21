using System.Runtime.InteropServices;

namespace EclipseUI.Platform.Windows;

public sealed partial class Win32Window
{
    private bool? _requestedDarkTitleBar;

    // The DWM frame is independent of the client renderer. Apply the resolved
    // palette before showing the window and whenever its light/dark variant changes.
    internal void SetDarkTitleBar(bool enabled)
    {
        if (_disposed || IsClosed || _requestedDarkTitleBar == enabled) return;
        _requestedDarkTitleBar = enabled;
        var value = enabled ? 1 : 0;
        if (AppearanceNative.DwmSetWindowAttribute(_hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */,
                ref value, sizeof(int)) >= 0)
        {
            // Schedule a non-client repaint without resizing, activating, or
            // synchronously entering another frame. Older Windows builds can reject
            // this optional attribute and retain their default caption appearance.
            AppearanceNative.RedrawWindow(_hwnd, 0, 0, 0x0401 /* RDW_FRAME | RDW_INVALIDATE */);
        }
    }

    private static class AppearanceNative
    {
        [DllImport("dwmapi.dll")]
        internal static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool RedrawWindow(nint hwnd, nint updateRect, nint updateRegion, uint flags);
    }
}

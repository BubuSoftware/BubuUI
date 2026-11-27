using System.Runtime.Versioning;

namespace BubuUI.Platform.MacOS;

/// <summary>Joins a prepared Cocoa window to the standard application window lifetime.</summary>
[SupportedOSPlatform("macos")]
public static class MacOSWindowLifetimeExtensions
{
    /// <summary>
    /// Runs a prepared native window with the framework's normal host, window registry,
    /// theme and shutdown behavior. The caller retains ownership of the native window;
    /// this method owns the UI window, host and rendering surface until it returns.
    /// </summary>
    public static int RunWindow(this MacOSWindow nativeWindow, Application application, Window window)
    {
        ArgumentNullException.ThrowIfNull(nativeWindow);
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(window);
        return MacOSDesktopLifetime.RunPreparedWindow(application, window, nativeWindow, modal: false);
    }
}

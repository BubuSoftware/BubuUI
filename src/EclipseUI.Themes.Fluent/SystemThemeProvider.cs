using System.Security;
using Microsoft.Win32;

namespace EclipseUI.Themes.Fluent;

/// <summary>Windows app preference and headless fallback. Linux window hosts supply SDL's preference.</summary>
public sealed class SystemThemeProvider : ISystemThemeProvider
{
    public static SystemThemeProvider Instance { get; } = new();
    private long _nextRead;
    private ThemeVariant _variant = ThemeVariant.Light;
    public ThemeVariant Variant
    {
        get
        {
            // Called on the UI thread. Avoid reading the registry on every frame.
            var now = Environment.TickCount64;
            if (now < _nextRead) return _variant;
            _nextRead = now + 1000;
            _variant = Read();
            return _variant;
        }
    }
    private static ThemeVariant Read()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("AppsUseLightTheme") is int value && value == 0 ? ThemeVariant.Dark : ThemeVariant.Light;
            }
            catch (Exception error) when (error is SecurityException or UnauthorizedAccessException or IOException) { }
        }
        var gtk = Environment.GetEnvironmentVariable("GTK_THEME");
        return gtk?.Contains("dark", StringComparison.OrdinalIgnoreCase) == true ? ThemeVariant.Dark : ThemeVariant.Light;
    }
}

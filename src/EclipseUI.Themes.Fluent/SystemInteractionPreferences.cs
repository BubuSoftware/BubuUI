using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;

namespace EclipseUI.Themes.Fluent;

public readonly record struct FluentInteractionSettings(bool AutoHideScrollBars, bool AnimationsEnabled);

public interface IFluentInteractionPreferences
{
    FluentInteractionSettings Current { get; }
}

/// <summary>Read-only desktop preferences, cached across windows and polled for live changes.</summary>
public sealed class SystemInteractionPreferences : IFluentInteractionPreferences
{
    public static SystemInteractionPreferences Instance { get; } = new();
    private readonly object _gate = new();
    private long _nextRead;
    private FluentInteractionSettings _current = new(true, true);
    public FluentInteractionSettings Current
    {
        get
        {
            lock (_gate)
            {
                var now = Environment.TickCount64;
                if (now < _nextRead) return _current;
                _nextRead = now + 1000;
                var autoHide = true;
                var animations = true;
                if (OperatingSystem.IsWindows())
                {
                    try
                    {
                        // Same preference used by Microsoft's Accessibility DSC resource.
                        using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Accessibility");
                        autoHide = key?.GetValue("DynamicScrollbars") is not int value || value != 0;
                    }
                    catch (Exception error) when (error is SecurityException or UnauthorizedAccessException or IOException) { }
                    if (SystemParametersInfoW(0x1042, 0, out var enabled, 0)) animations = enabled != 0;
                }
                return _current = new(autoHide, animations);
            }
        }
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoW(uint action, uint parameter, out int value, uint flags);
}

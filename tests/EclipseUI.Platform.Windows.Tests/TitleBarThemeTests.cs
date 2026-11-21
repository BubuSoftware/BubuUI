using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using EclipseUI.Elements;
using EclipseUI.Rendering;
using Xunit;

namespace EclipseUI.Platform.Windows.Tests;

public sealed class DwmThemeFactAttribute : FactAttribute
{
    public DwmThemeFactAttribute()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            Skip = "Requires documented DWM dark-title-bar support (Windows 11).";
    }
}

[SupportedOSPlatform("windows")]
public sealed class TitleBarThemeTests : IDisposable
{
    private readonly IClipboard _clipboard = Clipboard.Current;

    public void Dispose()
    {
        Clipboard.Current = _clipboard;
        while (Native.PeekMessageW(out _, 0, 0x0012, 0x0012, 1)) { }
    }

    [DwmThemeFact]
    public void InitialAndExplicitThemesUpdateTheNativeFrameWithoutReplacingItsChrome()
    {
        using var window = new Win32Window("EclipseUI title bar theme", 320, 180);
        using var root = new Grid();
        using var presenter = new Presenter();
        using var host = new WindowHost(window, presenter, root, new() { Theme = ElementTheme.Dark });
        Assert.True(ReadDarkFrame(window)); // Applied while the native window is still hidden.
        var style = GetWindowLongW(window.Handle, -16);
        Assert.Equal(0x00C00000, style & 0x00C00000); // Caption and border remain native.
        ((IWindowHost)host).SetTheme(ElementTheme.Light);
        Assert.False(ReadDarkFrame(window));
        host.SetTheme(ElementTheme.Dark);
        Assert.True(ReadDarkFrame(window));
        host.SetTheme(ElementTheme.Dark);
        Assert.Equal(style, GetWindowLongW(window.Handle, -16));
    }

    [DwmThemeFact]
    public void FollowSystemChangesUpdateTheNativeFrameOnTheNextUiFrame()
    {
        var system = new SystemPreference();
        using var window = new Win32Window("EclipseUI follow-system title bar", 320, 180);
        using var root = new Grid();
        using var presenter = new Presenter();
        using var host = new WindowHost(window, presenter, root, new()
        {
            Theme = new FollowSystemTheme(), SystemTheme = system,
        });
        var observed = new List<bool>();
        host.FrameTicked += (_, e) =>
        {
            observed.Add(ReadDarkFrame(window));
            system.Variant = e.Iteration == 1 ? ThemeVariant.Dark : ThemeVariant.Light;
        };
        host.Run(3);
        Assert.Equal(new[] { false, true, false }, observed);
    }

    private static bool ReadDarkFrame(Win32Window window)
    {
        Assert.Equal(0, DwmGetWindowAttribute(window.Handle, 20, out var value, sizeof(int)));
        return value != 0;
    }

    [DwmThemeFact]
    public void OwnerRepaintsItsThemeWhilePostedWorkRunsAChildWindowLoop()
    {
        using var owner = new Win32Window("Theme owner", 320, 180);
        using var root = new Grid();
        using var presenter = new Presenter();
        using var host = new WindowHost(owner, presenter, root, new() { Theme = ElementTheme.Light });
        var verified = false;
        host.FrameTicked += (_, e) =>
        {
            if (e.Iteration != 1) return;
            var ownerContext = SynchronizationContext.Current!;
            var ownerClipboard = Clipboard.Current;
            ownerContext.Post(_ =>
            {
                using var child = new Win32Window("Theme child", 240, 140);
                using var childRoot = new Grid();
                using var childPresenter = new Presenter();
                using var childHost = new WindowHost(child, childPresenter, childRoot);
                childHost.FrameTicked += (_, childFrame) =>
                {
                    if (childFrame.Iteration != 1) return;
                    var childContext = SynchronizationContext.Current;
                    var presents = presenter.Presents;
                    var ownerTicks = host.Ticks;
                    presenter.OnRender = () =>
                    {
                        Assert.Same(ownerContext, SynchronizationContext.Current);
                        Assert.Same(ownerClipboard, Clipboard.Current);
                    };
                    ((IWindowHost)host).SetTheme(ElementTheme.Dark);
                    Native.SendMessageW(owner.Handle, 0x000F, 0, 0); // WM_PAINT, inside the nested loop.
                    Assert.True(presenter.Presents > presents);
                    Assert.False(host.Painter.Theme.IsLight);
                    Assert.True(ReadDarkFrame(owner));
                    Assert.Equal(ownerTicks, host.Ticks);
                    Assert.Same(childContext, SynchronizationContext.Current);
                    verified = true;
                };
                childHost.Run(2);
            }, null);
        };
        host.Run(3);
        Assert.True(verified);
    }

    private sealed class SystemPreference : ISystemThemeProvider
    {
        public ThemeVariant Variant { get; set; } = ThemeVariant.Light;
    }
    private sealed class FollowSystemTheme : IHostThemeSource
    {
        public IHostTheme Resolve(ISystemThemeProvider? systemTheme = null) =>
            systemTheme?.Variant == ThemeVariant.Dark ? ElementTheme.Dark : ElementTheme.Light;
    }
    private sealed class Presenter : ISurfacePresenter
    {
        public string Name => "title-bar-theme-test";
        public string Description => Name;
        public RenderCapabilities Capabilities => RenderCapabilities.All;
        public int Width { get; private set; }
        public int Height { get; private set; }
        public bool IsVsync => false;
        public int Presents { get; private set; }
        public Action? OnRender { get; set; }
        public void Resize(int width, int height) { Width = width; Height = height; }
        public void Render(DisplayList list) { Presents++; OnRender?.Invoke(); }
        public void Dispose() { }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);
    [DllImport("user32.dll")]
    private static extern int GetWindowLongW(nint hwnd, int index);
}

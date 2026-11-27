using BubuUI.Elements;
using BubuUI.Themes.Fluent;
using Xunit;

namespace BubuUI.Platform.MacOS.Tests;

internal static partial class Program
{
    private sealed class NativeThemePreference : ISystemThemeProvider
    {
        public ThemeVariant Variant { get; set; } = ThemeVariant.Dark;
    }

    private static void WindowThemeScenario()
    {
        using var owner = new MacOSWindow("Theme owner", 320, 200);
        using var presenter = new TestPresenter();
        using var root = new Grid();
        var preference = new NativeThemePreference();
        using var host = new WindowHost(owner, presenter, root,
            new() { Theme = FluentTheme.System, SystemTheme = preference });
        CocoaPopupAssertions.Appearance(owner.NativeHandle, true); // Applied before the first Show.

        using var independent = new MacOSWindow("Independent light window", 260, 180);
        using var independentPresenter = new TestPresenter();
        using var independentRoot = new Grid();
        using var independentHost = new WindowHost(independent, independentPresenter, independentRoot,
            new() { Theme = FluentTheme.Light });
        independent.Show();

        var factory = new MacOSPopupWindowFactory(owner);
        using var popup = (MacOSPopupWindow)factory.Create(true, true);
        using var nested = (MacOSPopupWindow)factory.Create(true, false, popup);
        Check(true);
        host.FrameTicked += (_, frame) =>
        {
            switch (frame.Iteration)
            {
                case 1:
                    Check(true);
                    preference.Variant = ThemeVariant.Light;
                    break;
                case 2:
                    Check(false);
                    var focus = SdlNative.SDL_GetKeyboardFocus();
                    host.SetTheme(FluentTheme.Dark);
                    Check(true);
                    Assert.Equal(focus, SdlNative.SDL_GetKeyboardFocus());
                    break;
                case 3:
                    Check(true); // Explicit dark ignores the light system preference.
                    host.SetTheme(FluentTheme.System);
                    Check(false);
                    preference.Variant = ThemeVariant.Dark;
                    break;
                case 4:
                    Check(true); // Returning to System resumes live updates.
                    break;
            }
        };
        host.Run(4);

        void Check(bool dark)
        {
            CocoaPopupAssertions.Appearance(owner.NativeHandle, dark);
            CocoaPopupAssertions.Appearance(popup.Window.NativeHandle, dark);
            CocoaPopupAssertions.Appearance(nested.Window.NativeHandle, dark);
            CocoaPopupAssertions.Appearance(independent.NativeHandle, false);
            Assert.Equal(!dark, host.Painter.Theme.IsLight);
        }
    }
}

using EclipseUI;
using Microsoft.Extensions.DependencyInjection;

namespace Gallery;

/// <summary>Fluent gallery application; the desktop shell supplies platform and launch options.</summary>
public sealed class GalleryApp : Application
{
    public override void OnFrameworkInitializationCompleted()
    {
        var options = Services.GetRequiredService<GalleryLaunchOptions>();

        var window = new MainWindow
        {
            W = options.Width,
            H = options.Height,
            InitialTheme = options.Theme,
            ApplyTheme = SetTheme,
        };
        window.EnsureInitialized();

        // CLI 帧循环/调试旋钮：在平台创建窗口之前设置。
        window.MaxFramesPerSecond = options.MaxFps;
        window.AlwaysRender = options.AlwaysRender;
        window.MaxTicks = options.Frames;

        MainWindow = window;
    }
}

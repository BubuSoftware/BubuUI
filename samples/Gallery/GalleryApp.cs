using EclipseUI;
using EclipseUI.Elements;
using EclipseUI.Rendering;
using Microsoft.Extensions.DependencyInjection;

namespace Gallery;

/// <summary>
/// 画廊应用（D101，Avalonia 式应用定义）：平台壳只做 <c>AppBuilder.Configure&lt;GalleryApp&gt;()...Start(args)</c>，
/// 应用侧在 <see cref="Application.OnFrameworkInitializationCompleted"/> 构建场景并声明主窗口；
/// CLI 选项经 <see cref="Application.Services"/>（依赖注入）消费。
/// 主题切换经 <see cref="Application.SetTheme"/>——宿主运行期由桌面生命周期注入 <see cref="Application.Host"/>。
/// </summary>
public sealed class GalleryApp : Application
{
    public override void OnFrameworkInitializationCompleted()
    {
        var options = Services.GetRequiredService<GalleryLaunchOptions>();

        var scene = GalleryScene.Build();
        var root = new GalleryRoot(scene.Counter, scene.Tasks, scene.RootViewer);
        root.Mount();

        // S-A 亮暗切换：换 token 表 → 段缓存失效 → 整帧重绘。初值主题由 AppBuilder.UseTheme 提供。
        var dark = true;
        scene.ThemeButton.AddHandler(Button.ClickEvent, () =>
        {
            dark = !dark;
            SetTheme(dark ? ElementTheme.Dark : ElementTheme.Light);
        });

        MainWindow = new Window
        {
            Title = "EclipseUI Gallery",
            Width = options.Width,
            Height = options.Height,
            Content = root.Root,
            RootComponent = root,
            MaxFramesPerSecond = options.MaxFps,
            AlwaysRender = options.AlwaysRender,
            MaxTicks = options.Frames,
        };
    }
}

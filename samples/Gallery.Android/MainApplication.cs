using Android.App;
using Android.Runtime;
using EclipseUI;
using EclipseUI.Platform.Android;
using EclipseUI.Text.Shaping;
using EclipseUI.Themes.Fluent;
using Gallery.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Gallery.Android;

[Application]
public sealed class MainApplication(nint handle, JniHandleOwnership ownership)
    : EclipseAndroidApplication<GalleryApp>(handle, ownership)
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
        => builder
            .UseHarfBuzzText(options =>
            {
                options.FontPaths = ["/system/fonts/Roboto-Regular.ttf"];
                options.FallbackFontPaths = ["/system/fonts/NotoSansCJK-Regular.ttc", "/system/fonts/NotoColorEmoji.ttf"];
            })
            .UseFluentTheme()
            // K37 @inject 示例：DependencyInjectionExample.ui 经 @inject 解析的单例状态服务。
            .ConfigureServices(s => s.AddSingleton<IGalleryDemoState, GalleryDemoState>());
}

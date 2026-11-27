using Android.App;
using Android.Runtime;
using BubuUI;
using BubuUI.Platform.Android;
using BubuUI.Text.Shaping;
using BubuUI.Themes.Fluent;
using Gallery.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Gallery.Android;

[Application]
public sealed class MainApplication(nint handle, JniHandleOwnership ownership)
    : BubuAndroidApplication<GalleryApp>(handle, ownership)
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
        => builder
            .UseHarfBuzzText(options =>
            {
                options.FontPaths = ["/system/fonts/Roboto-Regular.ttf"];
                options.FallbackFontPaths = ["/system/fonts/NotoSansCJK-Regular.ttc", "/system/fonts/NotoColorEmoji.ttf"];
            })
            .UseFluentTheme()
            // K37 @inject 示例：DependencyInjectionExample.bui 经 @inject 解析的单例状态服务。
            .ConfigureServices(s => s.AddSingleton<IGalleryDemoState, GalleryDemoState>());
}

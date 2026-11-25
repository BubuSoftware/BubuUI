using Android.App;
using Android.Runtime;
using EclipseUI;
using EclipseUI.Platform.Android;
using EclipseUI.Text.Shaping;
using EclipseUI.Themes.Fluent;

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
            .UseFluentTheme();
}

using EclipseUI;
using EclipseUI.Platform.iOS;
using EclipseUI.Themes.Fluent;
using Foundation;
using UIKit;

namespace Gallery.iOS;

internal static class Program
{
    private static void Main(string[] args) => UIApplication.Main(args, null, typeof(AppDelegate));
}

[Register("AppDelegate")]
public sealed class AppDelegate : EclipseAppDelegate
{
    protected override AppBuilder CreateAppBuilder()
        => AppBuilder.Configure<GalleryApp>().UseiOS().UseFluentTheme();
}

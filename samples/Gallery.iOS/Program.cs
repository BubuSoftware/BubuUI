using BubuUI;
using BubuUI.Platform.iOS;
using BubuUI.Themes.Fluent;
using Foundation;
using UIKit;

namespace Gallery.iOS;

internal static class Program
{
    private static void Main(string[] args) => UIApplication.Main(args, null, typeof(AppDelegate));
}

[Register("AppDelegate")]
public sealed class AppDelegate : BubuAppDelegate
{
    protected override AppBuilder CreateAppBuilder()
        => AppBuilder.Configure<GalleryApp>().UseiOS().UseFluentTheme();
}

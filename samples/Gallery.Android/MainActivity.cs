using Android.App;
using Android.Content.PM;
using EclipseUI.Platform.Android;

namespace Gallery.Android;

[Activity(Label = "EclipseUI Gallery", MainLauncher = true, Theme = "@android:style/Theme.Material.Light.NoActionBar",
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public sealed class MainActivity : EclipseActivity
{
}

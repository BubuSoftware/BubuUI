using Android.App;
using Android.Content.PM;
using Android.OS;
using EclipseUI.Platform.Android;
using EclipseUI.Themes.Fluent;

namespace Gallery.Android;

[Activity(Label = "EclipseUI Gallery", MainLauncher = true, Theme = "@android:style/Theme.Material.Light.NoActionBar",
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public sealed class MainActivity : Activity
{
    private EclipseView? _view;
    private FluentGalleryPage? _page;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        _page = new FluentGalleryPage { ApplyTheme = theme => _view?.SetTheme(theme) };
        _view = new EclipseView(this, _page, FluentTheme.System);
        SetContentView(_view);
    }

    protected override void OnPause()
    {
        _view?.SetActive(false);
        base.OnPause();
    }

    protected override void OnResume()
    {
        base.OnResume();
        _view?.SetActive(true);
    }

    protected override void OnDestroy()
    {
        _view?.Close();
        _page?.Dispose();
        base.OnDestroy();
    }
}

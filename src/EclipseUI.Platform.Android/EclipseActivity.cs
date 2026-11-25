using Android.OS;
using EclipseUI.Rendering;
using Microsoft.Extensions.DependencyInjection;

namespace EclipseUI.Platform.Android;

/// <summary>Owns one UI tree and forwards the native activity lifecycle to its view.</summary>
public class EclipseActivity : global::Android.App.Activity
{
    private EclipseAndroidApplication? _application;
    private EclipseView? _view;
    private Element? _content;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        _application = Application as EclipseAndroidApplication
            ?? throw new InvalidOperationException("Register an EclipseAndroidApplication with [Application].");
        var app = _application.EclipseApplication;
        var factory = app.Services.GetRequiredService<IActivityApplicationLifetime>().MainViewFactory
            ?? throw new InvalidOperationException("Set IActivityApplicationLifetime.MainViewFactory during application initialization.");
        try
        {
            _content = factory() ?? throw new InvalidOperationException("MainViewFactory returned null.");
            _view = new EclipseView(this, _content, app.DefaultTheme ?? ElementTheme.Light,
                app.Services.GetService<ITextPainter>() ?? new MeasuringTextPainter());
            _application.Attach(_view);
            SetContentView(_view);
        }
        catch
        {
            ReleaseView();
            throw;
        }
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
        try { ReleaseView(); }
        finally { base.OnDestroy(); }
    }

    private void ReleaseView()
    {
        var view = _view;
        var content = _content;
        _view = null;
        _content = null;
        if (view is not null) _application?.Detach(view);
        try { view?.Close(); }
        finally
        {
            try { content?.Dispose(); }
            finally { view?.Dispose(); }
        }
    }
}

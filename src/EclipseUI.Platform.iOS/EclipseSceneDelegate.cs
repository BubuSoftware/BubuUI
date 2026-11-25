using EclipseUI.Rendering;
using Foundation;
using Microsoft.Extensions.DependencyInjection;
using UIKit;

namespace EclipseUI.Platform.iOS;

/// <summary>Owns one window and one EclipseUI tree for the lifetime of a connected scene.</summary>
[Register("EclipseSceneDelegate")]
public sealed class EclipseSceneDelegate : UIWindowSceneDelegate
{
    private EclipseAppDelegate? _application;
    private Element? _content;
    private EclipseView? _view;
    private UIViewController? _controller;
    private UiSynchronizationContext.UiThreadScope? _uiScope;

    public override UIWindow? Window { get; set; }

    public override void WillConnect(UIScene scene, UISceneSession session, UISceneConnectionOptions connectionOptions)
    {
        if (scene is not UIWindowScene windowScene) return;

        _application = UIApplication.SharedApplication.Delegate as EclipseAppDelegate
            ?? throw new InvalidOperationException("Start UIApplication with an EclipseAppDelegate.");
        var app = _application.EclipseApplication;
        var factory = app.Services.GetRequiredService<IActivityApplicationLifetime>().MainViewFactory
            ?? throw new InvalidOperationException("Set IActivityApplicationLifetime.MainViewFactory during application initialization.");
        var previousContext = SynchronizationContext.Current;
        _uiScope = UiSynchronizationContext.Install();
        try
        {
            _content = factory() ?? throw new InvalidOperationException("MainViewFactory returned null.");
            _uiScope.Context.AttachTo(_content);
            _view = new EclipseView(_content, app.DefaultTheme ?? ElementTheme.Light,
                app.Services.GetRequiredService<ITextPainter>(), _uiScope.Context) { TranslatesAutoresizingMaskIntoConstraints = false };
            _controller = new UIViewController();
            var container = _controller.View!;
            container.BackgroundColor = UIColor.SystemBackground;
            container.AddSubview(_view);
            var safe = container.SafeAreaLayoutGuide;
            NSLayoutConstraint.ActivateConstraints([
                _view.TopAnchor.ConstraintEqualTo(safe.TopAnchor),
                _view.LeadingAnchor.ConstraintEqualTo(safe.LeadingAnchor),
                _view.TrailingAnchor.ConstraintEqualTo(safe.TrailingAnchor),
                _view.BottomAnchor.ConstraintEqualTo(container.KeyboardLayoutGuide.TopAnchor)
            ]);

            Window = new UIWindow(windowScene) { RootViewController = _controller };
            _application.Attach(_view);
            Window.MakeKeyAndVisible();
        }
        catch
        {
            ReleaseWindow();
            throw;
        }
        finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
    }

    public override void DidBecomeActive(UIScene scene) => _view?.SetActive(true);
    public override void WillResignActive(UIScene scene) => _view?.SetActive(false);
    public override void DidDisconnect(UIScene scene) => ReleaseWindow();

    private void ReleaseWindow()
    {
        try
        {
            if (_view != null)
            {
                _application?.Detach(_view);
                _view.Close();
            }
        }
        finally
        {
            try { _content?.Dispose(); }
            finally
            {
                if (Window is { } window)
                {
                    window.Hidden = true;
                    window.RootViewController = null;
                    Window = null;
                    window.Dispose();
                }
                _view?.RemoveFromSuperview();
                _view?.Dispose();
                _controller?.Dispose();
                _view = null;
                _content = null;
                _controller = null;
                _application = null;
                _uiScope?.Dispose();
                _uiScope = null;
            }
        }
    }
}

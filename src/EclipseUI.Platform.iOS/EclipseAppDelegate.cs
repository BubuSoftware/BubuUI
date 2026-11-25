using EclipseUI.Elements;
using EclipseUI.Rendering;
using Foundation;
using Microsoft.Extensions.DependencyInjection;
using UIKit;

namespace EclipseUI.Platform.iOS;

/// <summary>Builds the EclipseUI application once and shares its services across UIKit scenes.</summary>
public abstract class EclipseAppDelegate : UIApplicationDelegate, IWindowHost
{
    private readonly List<EclipseView> _views = [];

    public Application EclipseApplication { get; private set; } = null!;
    protected abstract AppBuilder CreateAppBuilder();

    public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions)
    {
        EclipseApplication = CreateAppBuilder().Build();
        EclipseApplication.Host = this;
        if (EclipseApplication.Services.GetRequiredService<ITextPainter>() is ITextMeasurer measurer)
            TextMeasurer.Current = measurer;
        Clipboard.Current = new IosClipboard();
        return true;
    }

    public override UISceneConfiguration GetConfiguration(UIApplication application, UISceneSession connectingSceneSession,
        UISceneConnectionOptions options)
        => new("Default Configuration", connectingSceneSession.Role) { DelegateType = typeof(EclipseSceneDelegate) };

    internal void Attach(EclipseView view) => _views.Add(view);
    internal void Detach(EclipseView view) => _views.Remove(view);

    void IWindowHost.SetTheme(IHostTheme theme)
    {
        foreach (var view in _views.ToArray()) view.SetTheme(theme);
    }

    private sealed class IosClipboard : IClipboard
    {
        public string GetText() => UIPasteboard.General.String ?? string.Empty;
        public void SetText(string text) => UIPasteboard.General.String = text;
    }
}

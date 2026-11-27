using BubuUI.Elements;
using BubuUI.Rendering;
using Foundation;
using Microsoft.Extensions.DependencyInjection;
using UIKit;

namespace BubuUI.Platform.iOS;

/// <summary>Builds the BubuUI application once and shares its services across UIKit scenes.</summary>
public abstract class BubuAppDelegate : UIApplicationDelegate, IWindowHost
{
    private readonly List<BubuView> _views = [];

    public Application BubuApplication { get; private set; } = null!;
    protected abstract AppBuilder CreateAppBuilder();

    public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions)
    {
        BubuApplication = CreateAppBuilder().Build();
        BubuApplication.Host = this;
        if (BubuApplication.Services.GetRequiredService<ITextPainter>() is ITextMeasurer measurer)
            TextMeasurer.Current = measurer;
        Clipboard.Current = new IosClipboard();
        return true;
    }

    public override UISceneConfiguration GetConfiguration(UIApplication application, UISceneSession connectingSceneSession,
        UISceneConnectionOptions options)
        => new("Default Configuration", connectingSceneSession.Role) { DelegateType = typeof(BubuSceneDelegate) };

    internal void Attach(BubuView view) => _views.Add(view);
    internal void Detach(BubuView view) => _views.Remove(view);

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

using System.Runtime.Versioning;
using EclipseUI.Rendering;
using EclipseUI.Rendering.Vello;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EclipseUI.Platform.Windows.Tests;

[SupportedOSPlatform("windows")]
public sealed class SharedSurfaceDeviceTests
{
    [WindowsFact]
    public void SoftwarePopupsShareDeviceAndSurviveOwnerPresenterAndFactoryDisposal() => VerifyLifetime(true);

    [WindowsFact]
    public void GpuPopupsShareDeviceAndSurviveOwnerPresenterAndFactoryDisposal() => VerifyLifetime(false);

    private static void VerifyLifetime(bool software)
    {
        var app = AppBuilder.Configure<ProbeApp>()
            .UseVello(new VelloSurfaceOptions { UseSoftwareRasterizer = software }).Build();
        using var services = (IDisposable)app.Services;
        var factory = app.Services.GetRequiredService<ISurfaceFactory>();
        using var owner = new Win32Window("Shared device test", 240, 180, quitOnDestroy: false);
        owner.Show();
        using var main = (VelloSurfaceBackend)factory.Create(owner.Handle, owner.ClientWidth, owner.ClientHeight);
        Present(main);
        var context = main.RetainDeviceContext();
        context.Release(); // Observe lifetime without keeping the device alive ourselves.
        var windows = new Win32PopupWindowFactory(owner);

        for (var i = 0; i < 3; i++)
        {
            using var popup = windows.Create(true);
            popup.SetBounds(new(20, 20, 160, 100));
            popup.Show();
            Assert.Throws<ArgumentOutOfRangeException>(() => factory.Create(popup.SurfaceHandle, 0, 100));
            using var surface = (VelloSurfaceBackend)factory.Create(popup.SurfaceHandle, popup.ClientWidth, popup.ClientHeight);
            var popupContext = surface.RetainDeviceContext();
            try { Assert.Same(context, popupContext); }
            finally { popupContext.Release(); }
            Present(surface);
            Present(main);
        }

        using var lastPopup = windows.Create(true);
        lastPopup.SetBounds(new(20, 20, 160, 100));
        lastPopup.Show();
        using var lastSurface = (VelloSurfaceBackend)factory.Create(lastPopup.SurfaceHandle, lastPopup.ClientWidth, lastPopup.ClientHeight);
        main.Dispose();
        services.Dispose();
        Assert.Throws<ObjectDisposedException>(() => factory.Create(owner.Handle, 240, 180));
        Present(lastSurface);
        lastPopup.SetBounds(new(20, 20, 200, 120));
        lastSurface.Resize(lastPopup.ClientWidth, lastPopup.ClientHeight);
        Present(lastSurface);
        lastSurface.Dispose();
        lastSurface.Dispose();
        Assert.Throws<ObjectDisposedException>(() => context.Retain());
        Assert.True(owner.PumpMessages());
    }

    private static void Present(VelloSurfaceBackend surface)
    {
        var before = surface.PresentedFrames;
        var list = new DisplayList();
        list.Add(new FillRect(new Rect(0, 0, surface.Width, surface.Height), new Color(240, 244, 249)));
        list.Add(new FillRect(new Rect(8, 8, 64, 24), new Color(0, 120, 215)));
        surface.Render(list);
        Assert.Equal(before + 1, surface.PresentedFrames);
    }

    public sealed class ProbeApp : Application { }
}

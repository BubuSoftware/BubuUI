using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using BubuUI.Elements;
using BubuUI.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BubuUI.Platform.Windows.Tests;

[SupportedOSPlatform("windows")]
public sealed class ManagedModalWindowTests
{
    [WindowsFact]
    public void ShowDialogBlocksNativeFocusAcrossNestedWindowsAndRestoresEachOwner()
    {
        var factory = new Surfaces();
        var app = AppBuilder.Configure<ProbeApp>().UseWindows()
            .ConfigureServices(services => services.AddSingleton<ISurfaceFactory>(factory)).Build();
        using var services = (IDisposable)app.Services;
        var originalClipboard = Clipboard.Current;
        app.MainWindow = Window("main", 100);
        var verified = false;
        factory.FirstFrame = (index, handle) => Post(() =>
        {
            if (index == 0)
            {
                Assert.True(Native.IsWindowEnabled(handle));
                app.ShowDialog(Window("child"));
                Assert.True(Native.IsWindowEnabled(handle));
                Assert.Equal(handle, Native.GetFocus());
                // A pre-existing native disabled state must survive another dialog.
                Native.EnableWindow(handle, false);
                app.ShowDialog(Window("already disabled owner"));
                Assert.False(Native.IsWindowEnabled(handle));
                Native.EnableWindow(handle, true);
                verified = true;
                app.Shutdown();
            }
            else if (index == 1)
            {
                AssertCannotFocus(factory.Handles[0]);
                Native.SetFocus(handle);
                app.ShowDialog(Window("grandchild"));
                AssertCannotFocus(factory.Handles[0]);
                Assert.True(Native.IsWindowEnabled(handle));
                Assert.Equal(handle, Native.GetFocus());
            }
            else
            {
                AssertCannotFocus(factory.Handles[0]);
                if (index == 2) AssertCannotFocus(factory.Handles[1]);
                Native.SetFocus(handle);
                Assert.Equal(handle, Native.GetFocus());
            }
        });
        app.Services.GetRequiredService<IApplicationLifetime>().Run(app, []);
        Assert.True(verified);
        Assert.Empty(app.Windows);
        Assert.Same(originalClipboard, Clipboard.Current);
    }

    [WindowsFact]
    public void FailingDialogReleasesItsNativeOwnerBlock()
    {
        var factory = new Surfaces { FailOnIndex = 1 };
        var app = AppBuilder.Configure<ProbeApp>().UseWindows()
            .ConfigureServices(services => services.AddSingleton<ISurfaceFactory>(factory)).Build();
        using var services = (IDisposable)app.Services;
        app.MainWindow = Window("failure owner", 100);
        var verified = false;
        factory.FirstFrame = (_, handle) => Post(() =>
        {
            Assert.Throws<InvalidOperationException>(() => app.ShowDialog(Window("failing child")));
            Assert.True(Native.IsWindowEnabled(handle));
            Assert.True(app.MainWindow.Content!.IsEnabled);
            Assert.Equal(handle, Native.GetFocus());
            verified = true;
            app.Shutdown();
        });
        app.Services.GetRequiredService<IApplicationLifetime>().Run(app, []);
        Assert.True(verified);
    }

    private static void AssertCannotFocus(nint handle)
    {
        Assert.False(Native.IsWindowEnabled(handle));
        Native.SetFocus(handle);
        Assert.NotEqual(handle, Native.GetFocus());
    }
    private static Window Window(string title, int ticks = 5) => new() { Title = title, Width = 240, Height = 160, Content = new Grid(), MaxTicks = ticks };
    private static void Post(Action action) => SynchronizationContext.Current!.Post(_ => action(), null);
    public sealed class ProbeApp : Application { }

    private sealed class Surfaces : ISurfaceFactory
    {
        public List<nint> Handles { get; } = [];
        public Action<int, nint>? FirstFrame;
        public int FailOnIndex = -1;
        public ISurfacePresenter Create(nint handle, int width, int height)
        {
            var index = Handles.Count; Handles.Add(handle);
            return new Presenter(width, height, () =>
            {
                if (index == FailOnIndex) throw new InvalidOperationException("render failure");
                FirstFrame?.Invoke(index, handle);
            });
        }
    }
    private sealed class Presenter(int width, int height, Action first) : ISurfacePresenter
    {
        private bool _presented;
        public string Name => "modal test";
        public string Description => Name;
        public RenderCapabilities Capabilities => RenderCapabilities.All;
        public int Width { get; private set; } = width;
        public int Height { get; private set; } = height;
        public bool IsVsync => false;
        public void Resize(int w, int h) { Width = w; Height = h; }
        public void Render(DisplayList list) { if (!_presented) { _presented = true; first(); } }
        public void Dispose() { }
    }
    private static class Native
    {
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsWindowEnabled(nint handle);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool EnableWindow(nint handle, [MarshalAs(UnmanagedType.Bool)] bool enabled);
        [DllImport("user32.dll")] public static extern nint SetFocus(nint handle);
        [DllImport("user32.dll")] public static extern nint GetFocus();
    }
}

using EclipseUI.Elements;
using EclipseUI.Rendering.Vello;
using EclipseUI.Text.Shaping;
using EclipseUI.Themes.Fluent;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EclipseUI.Platform.MacOS.Tests;

internal static partial class Program
{
    private static void ManagedWindowLifetimeScenario(string mode)
    {
        var previousMeasurer = TextMeasurer.Current;
        var previousClipboard = Clipboard.Current;
        var app = (ManagedWindowApp)AppBuilder.Configure<ManagedWindowApp>().UseMacOS()
            .UseVello(new VelloSurfaceOptions { UseSoftwareRasterizer = true, ValidateDisplayList = true })
            .UseHarfBuzzText().UseFluentTheme(ThemeVariant.Light).Build();
        using var services = (IDisposable)app.Services;
        app.Mode = mode;
        try
        {
            Assert.Same(app.Services.GetRequiredService<IApplicationLifetime>(),
                app.Services.GetRequiredService<IWindowLifetime>());
            Assert.Equal(0, app.Services.GetRequiredService<IWindowLifetime>().RunWindow(app, app.MainWindow!));
            Assert.True(app.Finished, "The managed window scenario did not reach its intended close path.");
            Assert.Empty(app.Windows);
            Assert.Null(app.Host);
            Assert.Same(previousClipboard, Clipboard.Current);
            Assert.All(app.Hosts, host =>
            {
                Assert.True(host.IsClosed);
                Assert.Equal(nint.Zero, ((MacOSWindow)host.Window).NativeHandle);
            });
            var windows = SdlNative.SDL_GetWindows(out var count);
            SdlNative.SDL_free(windows);
            Assert.Equal(0, count);
        }
        finally
        {
            app.MainWindow?.Dispose();
            TextMeasurer.Current = previousMeasurer;
        }
    }

    public sealed class ManagedWindowApp : Application
    {
        public string Mode { get; set; } = "independent";
        public bool Finished { get; private set; }
        public List<WindowHost> Hosts { get; } = [];
        private ManagedProbeWindow _root = null!;
        private WindowHost _rootHost = null!;

        public override void OnFrameworkInitializationCompleted()
        {
            _root = CreateWindow("Managed main", host =>
            {
                _rootHost = host;
                host.FrameTicked += (_, frame) =>
                {
                    if (frame.Iteration != 2) return;
                    Post(() =>
                    {
                        if (Mode == "three-level") OpenThreeLevels();
                        else OpenIndependentDialog();
                    });
                };
            });
            MainWindow = _root;
        }

        private ManagedProbeWindow CreateWindow(string title, Action<WindowHost> started)
            => new(this)
            {
                Title = title, Width = 340, Height = 220, MaxTicks = 120, CloseOnEscape = false,
                Content = new TextBox { Text = title },
                Started = host => { Hosts.Add(host); started(host); },
            };

        private static void Post(Action action)
        {
            var context = Assert.IsType<UiSynchronizationContext>(SynchronizationContext.Current);
            context.Post(_ => action(), null);
        }

        private void OpenIndependentDialog()
        {
            Assert.Same(_rootHost, Host);
            Assert.Equal(new[] { _root }, Windows);
            var ticksWithoutMain = 0;
            var child = CreateWindow("Managed independent dialog", host =>
            {
                host.FrameTicked += (_, frame) =>
                {
                    Assert.Same(host, Host);
                    Assert.False(_root.Content!.IsEnabled);
                    if (Mode == "shutdown")
                    {
                        if (frame.Iteration != 3) return;
                        Assert.Equal(2, Windows.Count);
                        Assert.False(_rootHost.Window.IsClosed);
                        Finished = true;
                        Shutdown();
                        Assert.True(_rootHost.Window.IsClosed);
                        Assert.True(host.Window.IsClosed);
                        Assert.Empty(Windows);
                        return;
                    }
                    if (frame.Iteration == 2)
                    {
                        Assert.Equal(2, Windows.Count);
                        _rootHost.Window.Close();
                        return;
                    }
                    if (frame.Iteration < 3) return;
                    ticksWithoutMain++;
                    Assert.True(_rootHost.IsClosed);
                    Assert.False(host.IsClosed);
                    Assert.Single(Windows);
                    Assert.DoesNotContain(_root, Windows);
                    if (frame.Iteration == 4)
                    {
                        SetTheme(FluentTheme.Dark);
                        Assert.Same(FluentTheme.Dark, DefaultTheme);
                        Assert.False(host.Painter.Theme.IsLight);
                    }
                    if (frame.Iteration != 7) return;
                    Assert.True(ticksWithoutMain >= 4);
                    Finished = true;
                    host.Window.Close();
                };
            });
            ShowDialog(child);
            Assert.True(child.IsDisposed);
            Assert.True(_rootHost.IsClosed);
            Assert.Empty(Windows);
            Assert.Null(Host);
        }

        private void OpenThreeLevels()
        {
            ManagedProbeWindow? middle = null;
            ManagedProbeWindow? leaf = null;
            WindowHost? middleHost = null;
            var leafReturned = false;
            middle = CreateWindow("Managed middle", host =>
            {
                middleHost = host;
                host.FrameTicked += (_, frame) =>
                {
                    if (frame.Iteration != 2) return;
                    Post(() =>
                    {
                        var middleTheme = middle!.Theme;
                        leaf = CreateWindow("Managed leaf", childHost =>
                        {
                            childHost.FrameTicked += (_, childFrame) =>
                            {
                                Assert.Same(childHost, Host);
                                if (childFrame.Iteration == 2)
                                {
                                    Assert.Equal(3, Windows.Count);
                                    Assert.False(_root.Content!.IsEnabled);
                                    Assert.False(middle.Content!.IsEnabled);
                                    middleHost!.Window.Close();
                                    return;
                                }
                                if (childFrame.Iteration < 3) return;
                                Assert.True(middleHost!.IsClosed);
                                Assert.False(childHost.IsClosed);
                                Assert.Equal(2, Windows.Count);
                                Assert.DoesNotContain(middle, Windows);
                                if (childFrame.Iteration == 4)
                                {
                                    SetTheme(FluentTheme.Dark);
                                    Assert.False(_rootHost.Painter.Theme.IsLight);
                                    Assert.False(childHost.Painter.Theme.IsLight);
                                    Assert.Same(middleTheme, middle.Theme);
                                }
                                if (childFrame.Iteration == 6) childHost.Window.Close();
                            };
                        });
                        ShowDialog(leaf);
                        leafReturned = true;
                        Assert.Same(_rootHost, Host);
                        Assert.Equal(new[] { _root }, Windows);
                        Assert.Same(_rootHost.Window.Clipboard, Clipboard.Current);
                    });
                };
            });
            ShowDialog(middle);
            Assert.True(leafReturned);
            Assert.True(middle.IsDisposed);
            Assert.True(leaf!.IsDisposed);
            Assert.Same(_rootHost, Host);
            Assert.True(_root.Content!.IsEnabled);
            Assert.Equal(new[] { _root }, Windows);
            var inheritedTheme = false;
            var future = CreateWindow("Managed future window", host =>
            {
                Assert.False(host.Painter.Theme.IsLight);
                inheritedTheme = true;
                host.FrameTicked += (_, frame) =>
                {
                    if (frame.Iteration == 2) host.Window.Close();
                };
            });
            ShowDialog(future);
            Assert.True(inheritedTheme);
            Assert.True(_root.Content.IsEnabled);
            Assert.Same(_rootHost, Host);
            Finished = true;
            _rootHost.Window.Close();
        }
    }

    private sealed class ManagedProbeWindow(ManagedWindowApp app) : Window
    {
        public Action<WindowHost>? Started { get; init; }
        private bool _hooked;
        protected override void OnInitialized()
        {
            base.OnInitialized();
            Invalidate();
        }
        protected override void OnUpdating()
        {
            base.OnUpdating();
            if (_hooked) return;
            _hooked = true;
            Started?.Invoke(Assert.IsType<WindowHost>(app.Host));
        }
    }
}

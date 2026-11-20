using System.Runtime.InteropServices;
using EclipseUI.Elements;
using EclipseUI.Rendering;
using EclipseUI.Rendering.Vello;
using EclipseUI.Text.Shaping;
using EclipseUI.Themes.Fluent;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EclipseUI.Platform.MacOS.Tests;

internal static class Program
{
    // Deliberately synchronous: awaiting before AppKit initialization may change the OS thread.
    public static int Main(string[] args)
    {
        if (!OperatingSystem.IsMacOS() || args is not ["--native-smoke", var scenario]) return 2;
        try
        {
            switch (scenario)
            {
                case "window": WindowScenario(); break;
                case "live-resize": LiveResizeScenario(); break;
                case "lifetime": LifetimeScenario(); break;
                case "thread": ThreadScenario(); break;
                case "partial-raster": PartialRasterScenario(); break;
                case "software-raster": SoftwareRasterScenario(); break;
                case "popup": PopupScenario(); break;
                case "popup-host": PopupHostScenario(); break;
                case "popup-alpha": PopupAlphaScenario(); break;
                case "popup-shadow": PopupShadowScenario(); break;
                case "popup-shadow-software": PopupShadowScenario(true); break;
                default: throw new ArgumentException("Unknown native scenario.");
            }
            Console.WriteLine($"macOS native {scenario}: passed");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void PopupShadowScenario(bool software = false)
    {
        var previousMeasurer = TextMeasurer.Current;
        var app = AppBuilder.Configure<NativeApp>().UseMacOS().UseVello(new() { ValidateDisplayList = true, UseSoftwareRasterizer = software }).UseHarfBuzzText().Build();
        using var services = (IDisposable)app.Services;
        try
        {
            foreach (var dark in new[] { false, true })
            {
                using var owner = new MacOSWindow("Native popup shadow " + (dark ? "dark" : "light"), 460, 360);
                var surfaces = new ObservedSurfaceFactory(app.Services.GetRequiredService<ISurfaceFactory>());
                using var presenter = surfaces.Inner.Create(owner.SurfaceHandle, owner.ClientWidth, owner.ClientHeight);
                var windows = new ObservedPopupFactory(new MacOSPopupWindowFactory(owner));
                using var root = new Grid();
                var combo = new ComboBox { Width = 200, Height = 32, Margin = 80, HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top, ItemsSource = new[] { "First", "Second", "Third" } };
                root.Children.Add(combo);
                using var host = new WindowHost(owner, presenter, root, new()
                {
                    Theme = dark ? FluentTheme.Dark : FluentTheme.Light, TextPainter = app.Services.GetRequiredService<ITextPainter>(),
                    SurfaceFactory = surfaces, PopupWindowFactory = windows,
                });
                host.FrameTicked += (sender, frame) =>
                {
                    if (frame.Iteration == 1) { host.Input.Focus.Focus(combo); combo.IsDropDownOpen = true; }
                    if (frame.Iteration != 3) return;
                    var popup = Assert.Single(windows.Created).Window;
                    CocoaPopupAssertions.Transparent(popup.SurfaceHandle);
                    if (CocoaPopupAssertions.Capture(popup.SurfaceHandle) is not { } shot) return;
                    var shape = SdlNative.SDL_GetPointerProperty(SdlNative.SDL_GetWindowProperties(popup.Window.NativeHandle), "SDL.window.shape", 0);
                    var bottom = popup.ClientHeight;
                    while (bottom > 0)
                    {
                        SdlNative.Check(SdlNative.SDL_ReadSurfacePixel(shape, popup.ClientWidth / 2, bottom - 1, out _, out _, out _, out var a));
                        if (a == 255) break;
                        bottom--;
                    }
                    var start = (int)Math.Ceiling(bottom * shot.Height / (double)popup.ClientHeight);
                    var profile = Enumerable.Range(start, shot.Height - start).Select(y => shot.GetPixel(shot.Width / 2, y).A).ToArray();
                    Assert.True(profile.Where(a => a > 0).Distinct().Count() >= 7, "A blurred native shadow should have a continuous alpha falloff.");
                    Assert.True(profile[^1] <= 1, "The native frame must include the shadow's fade to transparent.");
                    for (var i = 1; i < profile.Length; i++) Assert.True(profile[i] <= profile[i - 1] + 1);
                    var output = Environment.GetEnvironmentVariable("ECLIPSEUI_POPUP_CAPTURE");
                    if (string.IsNullOrEmpty(output)) return;
                    var label = (software ? "software-" : "") + (dark ? "dark" : "light");
                    PngEncoder.Write(output + "-shadow-" + label + "-alpha.png", shot);
                    SdlNative.Check(SdlNative.SDL_GetWindowPosition(owner.NativeHandle, out var x, out var y));
                    SdlNative.Check(SdlNative.SDL_GetWindowSize(owner.NativeHandle, out var width, out var height));
                    if (CocoaPopupAssertions.CaptureComposite(owner.SurfaceHandle, popup.SurfaceHandle, new(x, y, width, height)) is { } composite)
                        PngEncoder.Write(output + "-shadow-" + label + ".png", composite);
                    Console.WriteLine(label + " shadow alpha: " + string.Join(",", profile));
                };
                host.Run(4);
            }
        }
        finally
        {
            app.MainWindow?.Dispose();
            TextMeasurer.Current = previousMeasurer;
        }
    }

    private static void PopupAlphaScenario()
    {
        using var owner = new MacOSWindow("Transparent popup integration", 360, 240);
        owner.Show();
        var factory = new MacOSPopupWindowFactory(owner);
        foreach (var software in new[] { false, true })
        {
            using var popup = (MacOSPopupWindow)factory.Create(true);
            Assert.True(popup.IsTransparent);
            popup.SetBounds(new(20, 20, 220, 140));
            popup.SetShape(new(20, 20, 180, 100), 24);
            popup.Show();
            CocoaPopupAssertions.Transparent(popup.SurfaceHandle);
            using var presenter = VelloSurfaceBackend.Create(popup.SurfaceHandle, popup.ClientWidth, popup.ClientHeight,
                new() { ValidateDisplayList = true, UseSoftwareRasterizer = software });
            CocoaPopupAssertions.Transparent(popup.SurfaceHandle);
            foreach (var size in new[] { new Size(220, 140), new Size(260, 160) })
            {
                popup.SetBounds(new(new Point(20, 20), size));
                var content = new Rect(20, 20, size.Width - 40, size.Height - 40);
                popup.SetShape(content, 24);
                presenter.Resize(popup.ClientWidth, popup.ClientHeight);
                var list = new DisplayList(size)
                    .Add(new PushTransform(Transform.Scale(popup.ScaleFactor)))
                    .Add(new FillRoundedRect(content, 24, new(40, 120, 220)))
                    .Add(new PopTransform());
                presenter.Render(list);
                CocoaPopupAssertions.Transparent(popup.SurfaceHandle);
                var shape = SdlNative.SDL_GetPointerProperty(SdlNative.SDL_GetWindowProperties(popup.Window.NativeHandle), "SDL.window.shape", 0);
                Assert.NotEqual(0, shape);
                byte Alpha(double x, double y)
                {
                    SdlNative.Check(SdlNative.SDL_ReadSurfacePixel(shape, (int)(x * popup.ScaleFactor), (int)(y * popup.ScaleFactor), out _, out _, out _, out var alpha));
                    return alpha;
                }
                Assert.Equal(0, Alpha(1, 1)); // Exterior shadow margin passes clicks through.
                Assert.Equal(0, Alpha(21, 21)); // Rounded corner passes clicks through.
                Assert.Equal(255, Alpha(size.Width / 2, size.Height / 2));
                for (var i = 0; i < 3; i++) { owner.PumpMessages(); Thread.Sleep(16); }
                if (CocoaPopupAssertions.Capture(popup.SurfaceHandle) is { } shot)
                {
                    Assert.Equal(0, shot.GetPixel(1, 1).A);
                    Assert.Equal(255, shot.GetPixel(shot.Width / 2, shot.Height / 2).A);
                    var output = Environment.GetEnvironmentVariable("ECLIPSEUI_POPUP_CAPTURE");
                    if (!string.IsNullOrEmpty(output)) PngEncoder.Write(output + (software ? "-software" : "-gpu") + $"-{(int)size.Width}.png", shot);
                }
            }
        }
    }

    private static void PopupHostScenario()
    {
        var previousMeasurer = TextMeasurer.Current;
        var app = AppBuilder.Configure<NativeApp>().UseMacOS().UseVello(new() { ValidateDisplayList = true }).UseHarfBuzzText().Build();
        using var services = (IDisposable)app.Services;
        try
        {
            using var owner = new MacOSWindow("Native popup host integration", 360, 240);
            var surfaces = new ObservedSurfaceFactory(app.Services.GetRequiredService<ISurfaceFactory>());
            using var presenter = surfaces.Inner.Create(owner.SurfaceHandle, owner.ClientWidth, owner.ClientHeight);
            var windows = new ObservedPopupFactory(new MacOSPopupWindowFactory(owner));
            using var root = new Grid();
            var button = new Button { Label = "Open", Width = 100, Height = 32, HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top };
            var editor = new TextBox { Width = 180, Height = 40 };
            var popup = new Popup { Content = editor, PlacementTarget = button, HorizontalOffset = 300, VerticalOffset = 200 };
            var ownerEditor = new TextBox { Width = 180, Height = 40, HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center };
            var combo = new ComboBox { Width = 180, Height = 32, HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom, ItemsSource = new[] { "First", "Second", "Third" } };
            var tooltipText = new TextBlock();
            tooltipText.AddRun("Persistent native tooltip");
            var tooltip = new ToolTip { Content = tooltipText, IsOpen = true,
                PlacementTarget = button, VerticalOffset = 40 };
            root.Children.Add(button);
            root.Children.Add(popup);
            root.Children.Add(ownerEditor);
            root.Children.Add(combo);
            root.Children.Add(tooltip);
            using var host = new WindowHost(owner, presenter, root, new()
            {
                Theme = FluentTheme.Light, TextPainter = app.Services.GetRequiredService<ITextPainter>(),
                SurfaceFactory = surfaces, PopupWindowFactory = windows,
            });
            host.FrameTicked += (_, frame) =>
            {
                Assert.True(tooltip.IsOpen);
                Assert.False(owner.IsClosed);
                switch (frame.Iteration)
                {
                    case 1:
                        Assert.True(tooltip.IsWindowHosted);
                        Assert.Single(windows.Created);
                        Assert.False(windows.Created[0].Interactive);
                        Assert.False(windows.Created[0].Window.IsFocused);
                        Assert.True(host.Input.Focus.Focus(button));
                        popup.Open();
                        break;
                    case 2:
                        Assert.True(popup.IsWindowHosted);
                        Assert.True(popup.IsOpen);
                        var nativeEditor = windows.Created.Last(x => x.Interactive).Window;
                        Assert.True(nativeEditor.IsFocused);
                        Assert.True(host.Input.Focus.Focus(editor));
                        var origin = editor.PointFromRoot(Point.Zero);
                        Assert.True(editor.FinalRect.Width - origin.X > owner.LogicalClientSize.Width);
                        PushComposition(nativeEditor.Window, "zhong", "中文 😀");
                        Assert.Equal("中文 😀", editor.Text);
                        QueueKey(nativeEditor.Window, 9); // Tab follows the shared logical tree into the owner.
                        break;
                    case 3:
                        // SDL transferred native focus to the popup, preserving the shared logical focus.
                        Assert.True(popup.IsOpen);
                        Assert.Same(ownerEditor, host.Input.Focus.Focused);
                        Assert.Equal("中文 😀", editor.Text);
                        var nativeTab = windows.Created.Last(x => x.Interactive).Window;
                        Assert.True(nativeTab.IsFocused);
                        PushComposition(nativeTab.Window, "wai", "窗外输入 😀");
                        Assert.Equal("窗外输入 😀", ownerEditor.Text);
                        QueueKey(nativeTab.Window, 9, 1); // Shift+Tab returns to the popup editor.
                        break;
                    case 4:
                        Assert.True(popup.IsOpen);
                        Assert.Same(editor, host.Input.Focus.Focused);
                        QueueKey(windows.Created.Last(x => x.Interactive).Window.Window, 27);
                        break;
                    case 5:
                        Assert.False(popup.IsOpen);
                        Assert.Same(button, host.Input.Focus.Focused);
                        Assert.True(host.Input.Focus.Focus(combo));
                        combo.IsDropDownOpen = true;
                        break;
                    case 6:
                        var dropdown = Assert.Single(combo.Descendants().OfType<Popup>());
                        Assert.True(dropdown.IsWindowHosted);
                        Assert.True(dropdown.IsOpen);
                        Assert.IsType<ListBox>(host.Input.Focus.Focused);
                        Assert.True(windows.Created.Last(x => x.Interactive).Window.IsFocused);
                        var nativeDropdown = windows.Created.Last(x => x.Interactive).Window;
                        CocoaPopupAssertions.Transparent(nativeDropdown.SurfaceHandle);
                        break;
                    case 7:
                        Assert.True(combo.IsDropDownOpen);
                        Assert.IsType<ListBox>(host.Input.Focus.Focused);
                        if (CocoaPopupAssertions.Capture(windows.Created.Last(x => x.Interactive).Window.SurfaceHandle) is { } comboShot)
                        {
                            Assert.Equal(0, comboShot.GetPixel(0, 0).A);
                            Assert.Equal(255, comboShot.GetPixel(comboShot.Width / 2, comboShot.Height / 2).A);
                            var output = Environment.GetEnvironmentVariable("ECLIPSEUI_POPUP_CAPTURE");
                            if (!string.IsNullOrEmpty(output)) PngEncoder.Write(output + "-combo.png", comboShot);
                        }
                        var nativeCombo = windows.Created.Last(x => x.Interactive).Window.Window;
                        QueueKey(nativeCombo, 0x40000051); // Down selects the first item.
                        QueueKey(nativeCombo, 13); // Enter accepts it and restores ComboBox focus.
                        break;
                    case 8:
                        Assert.False(combo.IsDropDownOpen);
                        Assert.Equal(0, combo.SelectedIndex);
                        Assert.Same(combo, host.Input.Focus.Focused);
                        break;
                }
            };
            host.Run(9);
            Assert.Equal(3, windows.Created.Count);
            Assert.All(windows.Created, item => Assert.True(item.Window.IsClosed));
            Assert.Equal(3, surfaces.Created.Count);
            Assert.All(surfaces.Created, item => { Assert.True(item.Frames > 0); Assert.True(item.IsDisposed); });
            Assert.True(owner.PumpMessages());
        }
        finally
        {
            app.MainWindow?.Dispose();
            TextMeasurer.Current = previousMeasurer;
        }
    }

    private static void QueueKey(MacOSWindow window, uint key, ushort modifiers = 0)
    {
        var message = new SdlNative.Event { Key = new() { Type = SdlNative.KeyDown, WindowId = window.Id, Key = key, Mod = modifiers, Down = 1 } };
        SdlNative.Check(SdlNative.SDL_PushEvent(ref message));
    }

    private sealed class ObservedPopupFactory(IPopupWindowFactory inner) : IPopupWindowFactory
    {
        public List<(bool Interactive, MacOSPopupWindow Window)> Created { get; } = [];
        public Size AvailableSize => inner.AvailableSize;
        public bool IsOwnerFocused => inner.IsOwnerFocused;
        public IPopupWindow Create(bool interactive, IPopupWindow? parent = null)
        {
            var popup = (MacOSPopupWindow)inner.Create(interactive, parent);
            Created.Add((interactive, popup));
            return popup;
        }
    }

    private sealed class ObservedSurfaceFactory(ISurfaceFactory inner) : ISurfaceFactory
    {
        public ISurfaceFactory Inner => inner;
        public List<ObservedPresenter> Created { get; } = [];
        public ISurfacePresenter Create(nint handle, int width, int height) => Create(NativeWindowHandle.FromWin32(handle), width, height);
        public ISurfacePresenter Create(NativeWindowHandle handle, int width, int height)
        {
            var presenter = new ObservedPresenter(inner.Create(handle, width, height));
            if (handle.IsTransparent) CocoaPopupAssertions.Transparent(handle);
            Created.Add(presenter);
            return presenter;
        }
    }

    private sealed class ObservedPresenter(ISurfacePresenter inner) : ISurfacePresenter
    {
        public string Name => inner.Name;
        public string Description => inner.Description;
        public RenderCapabilities Capabilities => inner.Capabilities;
        public bool SupportsTransparency => inner.SupportsTransparency;
        public int Width => inner.Width;
        public int Height => inner.Height;
        public bool IsVsync => inner.IsVsync;
        public int Frames { get; private set; }
        public bool IsDisposed { get; private set; }
        public void Resize(int width, int height) => inner.Resize(width, height);
        public void Render(DisplayList list) { inner.Render(list); Frames++; }
        public void Render(PaintedFrame frame, double scale = 1) { inner.Render(frame, scale); Frames++; }
        public void Dispose() { IsDisposed = true; inner.Dispose(); }
    }

    private static void PopupScenario()
    {
        using var owner = new MacOSWindow("Popup ownership smoke", 240, 160);
        owner.Show();
        var factory = new MacOSPopupWindowFactory(owner);
        Assert.True(factory.AvailableSize.Width > owner.LogicalClientSize.Width);
        using (var popup = (MacOSPopupWindow)factory.Create(true))
        {
            var bounds = popup.SetBounds(new(250, 170, 180, 100));
            popup.Show();
            owner.PumpMessages();
            Assert.True(bounds.Right > owner.LogicalClientSize.Width);
            Assert.True(bounds.Bottom > owner.LogicalClientSize.Height);
            Assert.Equal(NativeWindowKind.AppKit, popup.SurfaceHandle.Kind);
            Assert.NotEqual(owner.SurfaceHandle.Handle, popup.SurfaceHandle.Handle);
            using var presenter = VelloSurfaceBackend.Create(popup.SurfaceHandle, popup.ClientWidth, popup.ClientHeight,
                new() { ValidateDisplayList = true });
            presenter.Render(new DisplayList(new(popup.ClientWidth, popup.ClientHeight))
                .Add(new FillRect(new(0, 0, popup.ClientWidth, popup.ClientHeight), Color.White)));
            Assert.Equal(1, presenter.PresentedFrames);

            var pointer = Point.Zero;
            popup.PointerPressed += (point, _, _) => pointer = point;
            var press = new SdlNative.Event
            {
                Button = new() { Type = SdlNative.MouseButtonDown, WindowId = popup.Window.Id, Button = 1, Down = 1, X = 12, Y = 14 },
            };
            SdlNative.Check(SdlNative.SDL_PushEvent(ref press));
            owner.PumpMessages();
            Assert.Equal(new Point(12, 14), pointer);

            var committed = "";
            popup.TextComposition += (text, stage) => { if (stage == CompositionStage.Committed) committed = text; };
            popup.SetTextInputEnabled(true);
            PushComposition(popup.Window, "zhong", "中文 😀");
            Assert.Equal("中文 😀", committed);
            popup.SetTextInputEnabled(false);

            using (var child = (MacOSPopupWindow)factory.Create(true, popup))
            {
                var nested = child.SetBounds(new(bounds.Right, bounds.Y + 10, 120, 70));
                child.Show();
                owner.PumpMessages();
                Assert.NotEqual(popup.SurfaceHandle.Handle, child.SurfaceHandle.Handle);
                SdlNative.Check(SdlNative.SDL_GetWindowPosition(child.Window.NativeHandle, out var x, out var y));
                Assert.Equal(nested.X - bounds.X, x / owner.NativeUnitsPerLogicalX, 1);
                Assert.Equal(nested.Y - bounds.Y, y / owner.NativeUnitsPerLogicalY, 1);
                child.SetBounds(new(-100000, -100000, 120, 70));
            }
        }
        Assert.True(owner.PumpMessages());
        Assert.False(owner.IsClosed);
        using var tooltip = factory.Create(false);
        tooltip.SetBounds(new(10, 10, 100, 40));
        tooltip.Show();
        owner.PumpMessages();
        Assert.False(tooltip.IsFocused);
    }

    private static void SoftwareRasterScenario()
    {
        using var window = new MacOSWindow("Software raster with Metal presentation", 400, 300);
        window.Show();
        using var presenter = VelloSurfaceBackend.Create(window.SurfaceHandle, window.ClientWidth, window.ClientHeight,
            new() { UseSoftwareRasterizer = true, ValidateDisplayList = true });
        void Draw(Color color)
        {
            var width = presenter.Width; var height = presenter.Height;
            var list = new DisplayList(new(width, height))
                .Add(new FillRect(new(0, 0, width, height), Color.White))
                .Add(new PushClip(new(10, 10, width - 20, height - 20)))
                .Add(new FillRoundedRect(new(20, 20, 70, 50), 8, color))
                .Add(new PopClip());
            presenter.Render(list);
            Assert.Equal((long)width * height, presenter.LastRasterizedPixels);
        }
        Draw(Color.Black);
        Draw(new(30, 80, 220));
        presenter.Resize(presenter.Width + 20, presenter.Height + 20);
        Draw(new(50, 160, 60));
        Assert.Equal(3, presenter.PresentedFrames);
        Assert.Contains("CPU raster", presenter.Name);
    }

    private static void PartialRasterScenario()
    {
        using var window = new MacOSWindow("Retained Metal raster", 400, 300);
        using var presenter = VelloSurfaceBackend.Create(window.SurfaceHandle, window.ClientWidth, window.ClientHeight,
            new() { ValidateDisplayList = true });
        var width = presenter.Width; var height = presenter.Height;
        DisplayList List(Color color) => new DisplayList(new(width, height))
            .Add(new FillRect(new(0, 0, width, height), Color.White))
            .Add(new FillRoundedRect(new(20, 20, 30, 30), 4, color));
        var full = new FrameDamage(); full.AddFull();
        presenter.Render(new PaintedFrame(List(Color.Black), full));
        Assert.Equal((long)width * height, presenter.LastRasterizedPixels);
        var damage = new FrameDamage(); damage.Add(new(20, 20, 30, 30));
        presenter.Render(new PaintedFrame(List(new(30, 80, 220)), damage));
        Assert.Equal(900, presenter.LastRasterizedPixels);
        Assert.True(presenter.LastReusedSceneFragments > 0);
        presenter.Render(new PaintedFrame(List(Color.Black), new FrameDamage()));
        Assert.Equal(0, presenter.LastRasterizedPixels);
        Assert.Equal(0, presenter.LastBuiltSceneFragments);
        Assert.Equal(0, presenter.LastReusedSceneFragments);
        Assert.Equal(3, presenter.PresentedFrames);
        presenter.Resize(width + 20, height + 20);
        presenter.Render(new PaintedFrame(List(Color.Black), new FrameDamage()));
        Assert.Equal((long)(width + 20) * (height + 20), presenter.LastRasterizedPixels);
    }

    private static void LiveResizeScenario()
    {
        using var window = new MacOSWindow("Cocoa live resize", 500, 360);
        using var other = new MacOSWindow("Other window", 200, 100);
        using var presenter = VelloSurfaceBackend.Create(window.SurfaceHandle,
            Math.Max(1, window.ClientWidth), Math.Max(1, window.ClientHeight));
        using var root = new StackPanel();
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap };
        text.AddRun(string.Join(" ", Enumerable.Repeat("live resize content", 30)));
        root.Children.Add(text);
        using var host = new WindowHost(window, presenter, root);
        host.FrameTicked += (_, e) =>
        {
            if (e.Iteration != 1) return;
            var lines = text.TextLines.Count;
            foreach (var width in new[] { 240, 420, 300 })
            {
                window.SetClientSize(width, 360);
                var before = presenter.PresentedFrames;
                // SDL's Cocoa tracking timer emits this event inside its nested loop.
                // Do not poll: the watcher must finish the frame before PushEvent returns.
                var expose = new SdlNative.Event { Type = SdlNative.WindowExposed, WindowId = window.Id };
                SdlNative.Check(SdlNative.SDL_PushEvent(ref expose));
                Assert.True(presenter.PresentedFrames > before);
                Assert.Equal(window.LogicalClientSize, root.FinalRect.Size);
                Assert.Equal((window.ClientWidth, window.ClientHeight), (presenter.Width, presenter.Height));
                Assert.Equal(1, host.Ticks);
                if (width == 240) Assert.True(text.TextLines.Count > lines);
            }
            var previous = presenter.PresentedFrames;
            var unrelated = new SdlNative.Event { Type = SdlNative.WindowExposed, WindowId = other.Id };
            SdlNative.Check(SdlNative.SDL_PushEvent(ref unrelated));
            Assert.Equal(previous, presenter.PresentedFrames);
            Exception? failure = null;
            var worker = new Thread(() =>
            {
                try
                {
                    var expose = new SdlNative.Event { Type = SdlNative.WindowExposed, WindowId = window.Id };
                    SdlNative.Check(SdlNative.SDL_PushEvent(ref expose));
                }
                catch (Exception error) { failure = error; }
            });
            worker.Start();
            Assert.True(worker.Join(TimeSpan.FromSeconds(5)));
            Assert.Null(failure);
            Assert.Equal(previous, presenter.PresentedFrames);
        };
        host.Run(2);
    }

    private static void WindowScenario()
    {
        using var window = new MacOSWindow("EclipseUI macOS smoke", 400, 300);
        window.Show();
        Assert.Equal("cocoa", window.VideoDriver);
        Assert.Equal(NativeWindowKind.AppKit, window.SurfaceHandle.Kind);
        Assert.NotEqual((nint)0, window.SurfaceHandle.Handle);
        var previousClipboard = window.Clipboard.GetText();
        try
        {
            window.Clipboard.SetText("macOS 剪贴板 😀");
            Assert.Equal("macOS 剪贴板 😀", window.Clipboard.GetText());
            using var presenter = VelloSurfaceBackend.Create(window.SurfaceHandle,
                Math.Max(1, window.ClientWidth), Math.Max(1, window.ClientHeight));
            Assert.Equal(VelloSharp.WgpuBackend.Metal, presenter.BackendType);
            using var editor = new TextBox();
            using var host = new WindowHost(window, presenter, editor, new() { AlwaysRender = true });
            host.FrameTicked += (_, e) =>
            {
                if (e.Iteration == 1)
                {
                    host.Input.Focus.Focus(editor);
                    window.SetTextInputEnabled(true);
                    PushText(window, SdlNative.TextInput, "A ");
                    var compositions = new List<CompositionStage>();
                    var typed = new List<string>();
                    window.TextComposition += (_, message) => compositions.Add(message.Stage);
                    window.CharacterTyped += (_, message) => typed.Add(message.Text);
                    // Cocoa clears marked text immediately before committing the same text.
                    PushComposition(window, "zhong", "中文 😀");
                    Assert.Equal(new[] { CompositionStage.Started, CompositionStage.Updated, CompositionStage.Committed }, compositions);
                    Assert.Empty(typed);
                    PushText(window, SdlNative.TextEditing, "cancelled");
                    PushText(window, SdlNative.TextEditing, "");
                    Assert.Equal(CompositionStage.Cancelled, compositions[^1]);
                    Assert.Equal("A 中文 😀", editor.Text);
                    window.SetTitle("EclipseUI macOS 已更新");
                    window.SetClientSize(500, 360);
                }
                if (e.Iteration == 2)
                {
                    // Preserve the native Command modifier through SDL, host routing and TextBox.
                    var selectAll = new SdlNative.Event
                    {
                        Key = new() { Type = SdlNative.KeyDown, WindowId = window.Id, Key = 'a', Mod = 0x0400, Down = 1 },
                    };
                    SdlNative.Check(SdlNative.SDL_PushEvent(ref selectAll));
                    window.PumpMessages();
                    PushText(window, SdlNative.TextInput, "替换 ✅");
                }
            };
            Assert.True(host.Run(4) >= 4); // Native callbacks can present between outer ticks.
            Assert.Equal("替换 ✅", editor.Text);
            Assert.True(presenter.PresentedFrames >= 4);
            Assert.True(window.ScaleFactor >= 1);
            Assert.Equal((window.ClientWidth, window.ClientHeight), (presenter.Width, presenter.Height));
            Assert.InRange(window.LogicalClientSize.Width, 499, 501);
            Assert.InRange(window.LogicalClientSize.Height, 359, 361);
        }
        finally { window.Clipboard.SetText(previousClipboard); }
    }

    private static void PushText(MacOSWindow window, uint type, string text)
    {
        var pointer = Marshal.StringToCoTaskMemUTF8(text);
        try
        {
            var message = new SdlNative.Event { Text = new() { Type = type, WindowId = window.Id, Text = pointer } };
            SdlNative.Check(SdlNative.SDL_PushEvent(ref message));
            window.PumpMessages();
        }
        finally { Marshal.FreeCoTaskMem(pointer); }
    }

    private static void PushComposition(MacOSWindow window, string preedit, string committed)
    {
        var pointers = new[] { preedit, "", committed }.Select(Marshal.StringToCoTaskMemUTF8).ToArray();
        try
        {
            for (var index = 0; index < pointers.Length; index++)
            {
                var message = new SdlNative.Event
                {
                    Text = new() { Type = index == 2 ? SdlNative.TextInput : SdlNative.TextEditing,
                        WindowId = window.Id, Text = pointers[index] },
                };
                SdlNative.Check(SdlNative.SDL_PushEvent(ref message));
            }
            window.PumpMessages();
        }
        finally { foreach (var pointer in pointers) Marshal.FreeCoTaskMem(pointer); }
    }

    private static void LifetimeScenario()
    {
        var previousMeasurer = TextMeasurer.Current;
        var app = AppBuilder.Configure<NativeApp>().UseMacOS().UseVello().UseHarfBuzzText().Build();
        using var services = (IDisposable)app.Services;
        try
        {
            var painter = app.Services.GetRequiredService<ITextPainter>();
            var list = new DisplayList(new(500, 90));
            painter.Paint(list, "Hello 中文 😀", 28, new(8, 8), Color.Black);
            var glyphs = list.Commands.OfType<DrawGlyphs>().SelectMany(run => run.Glyphs.Glyphs).ToArray();
            Assert.NotEmpty(glyphs);
            Assert.All(glyphs, glyph => Assert.NotEqual(0, glyph.GlyphId));
            using var cpu = new VelloCpuBackend();
            var target = new RenderTarget(500, 90);
            cpu.Render(list, target);
            Assert.True(target.CountPixelsDifferentFrom(Color.Transparent) > 100);
            Assert.Equal(0, app.Services.GetRequiredService<IApplicationLifetime>().Run(app, []));
            Assert.True(app.MainWindow!.IsDisposed);
            Assert.Null(app.Host);
        }
        finally
        {
            app.MainWindow?.Dispose();
            TextMeasurer.Current = previousMeasurer;
        }
    }

    private static void ThreadScenario()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { using var invalid = new MacOSWindow("Invalid worker window", 200, 100); }
            catch (Exception error) { failure = error; }
        });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        Assert.IsType<InvalidOperationException>(failure);

        using var window = new MacOSWindow("Main-thread ownership", 200, 100);
        failure = null;
        thread = new Thread(() =>
        {
            try { window.PumpMessages(); }
            catch (Exception error) { failure = error; }
        });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        Assert.IsType<InvalidOperationException>(failure);
    }

    public sealed class NativeApp : Application
    {
        public override void OnFrameworkInitializationCompleted()
            => MainWindow = new Window { Title = "macOS lifetime smoke", Width = 320, Height = 240,
                Content = new TextBox { Text = "macOS 中文 😀" }, MaxTicks = 3 };
    }
}

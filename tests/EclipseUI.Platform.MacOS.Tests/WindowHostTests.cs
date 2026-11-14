using EclipseUI.Elements;
using EclipseUI.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)] // Clipboard and UI globals

namespace EclipseUI.Platform.MacOS.Tests;

public class WindowHostTests
{
    private sealed class ThemePreference : ISystemThemeProvider { public ThemeVariant Variant { get; set; } }

    [Fact]
    public void SystemThemeSourceFollowsPreferenceAndExplicitModeStopsFollowing()
    {
        using var window = new TestWindow(); using var presenter = new TestPresenter();
        using var root = new StackPanel();
        var preference = new ThemePreference { Variant = ThemeVariant.Light };
        using var host = new WindowHost(window, presenter, root, new WindowHostOptions { SystemTheme = preference });
        Assert.Same(EclipseUI.Themes.Fluent.FluentTheme.Light, host.Painter.Theme);
        host.FrameTicked += (_, e) =>
        {
            if (e.Iteration == 1) preference.Variant = ThemeVariant.Dark;
            if (e.Iteration == 2) { Assert.Same(EclipseUI.Themes.Fluent.FluentTheme.Dark, host.Painter.Theme); host.SetTheme(EclipseUI.Themes.Fluent.FluentTheme.Light); }
            if (e.Iteration == 3) Assert.Same(EclipseUI.Themes.Fluent.FluentTheme.Light, host.Painter.Theme);
        };
        Assert.Equal(3, host.Run(4));
        Assert.Equal(1, host.SkippedFrames);
    }
    [Fact]
    public void MacOSSetupCanBeBuiltWithoutLoadingSdl()
    {
        var app = AppBuilder.Configure<TestApp>().UseMacOS().Build();
        using var services = (IDisposable)app.Services;
        Assert.IsType<MacOSDesktopLifetime>(app.Services.GetRequiredService<IApplicationLifetime>());
        app.MainWindow?.Dispose();
    }

    [Fact]
    public void UnchangedFramesSkipAndExposeOrThemeForcesPresentation()
    {
        using var window = new TestWindow();
        using var presenter = new TestPresenter();
        using var root = new StackPanel();
        using var host = new WindowHost(window, presenter, root);
        host.FrameTicked += (_, e) =>
        {
            if (e.Iteration == 2) window.Expose();
            if (e.Iteration == 3) host.SetTheme(ElementTheme.Light);
        };
        Assert.Equal(3, host.Run(4));
        Assert.Equal(1, host.SkippedFrames);
        Assert.Equal(800, presenter.Width);
        Assert.Equal(new Size(400, 300), root.FinalRect.Size);
        Assert.False(root.IsDisposed);
    }

    [Fact]
    public void ResizeReconfiguresSurfaceAndLogicalLayout()
    {
        using var window = new TestWindow();
        using var presenter = new TestPresenter();
        using var root = new StackPanel();
        using var host = new WindowHost(window, presenter, root);
        host.FrameTicked += (_, e) => { if (e.Iteration == 1) window.Resize(1200, 900, 1.5); };
        host.Run(2);
        Assert.Equal((1200, 900), (presenter.Width, presenter.Height));
        Assert.Equal(new Size(800, 600), root.FinalRect.Size);
        Assert.Equal(2, host.RenderedFrames);
    }

    [Fact]
    public void UnicodeCompositionPointerAndTabReachRealControls()
    {
        using var window = new TestWindow();
        using var presenter = new TestPresenter();
        using var root = new StackPanel();
        var editor = new TextBox { Width = 180, Height = 40 };
        var button = new Button { Label = "next", Width = 180, Height = 40 };
        root.Children.Add(editor);
        root.Children.Add(button);
        using var host = new WindowHost(window, presenter, root, new() { CloseOnEscape = false });
        host.FrameTicked += (_, e) =>
        {
            if (e.Iteration == 1)
            {
                var zero = editor.PointFromRoot(Point.Zero);
                var point = new Point(10 - zero.X, 10 - zero.Y);
                window.Press(point);
                window.Release(point);
                window.Type("Hi 😀 ");
                window.Compose("zhong", CompositionStage.Updated);
                window.Compose("中文", CompositionStage.Committed);
            }
            if (e.Iteration == 2)
            {
                Assert.True(window.TextInputEnabled);
                Assert.NotNull(window.Caret);
                Assert.Equal("Hi 😀 中文", editor.Text);
                window.Key(Key.Tab);
                Assert.Same(button, host.Input.Focus.Focused);
            }
        };
        host.Run(3);
        Assert.False(window.TextInputEnabled);
    }

    [Fact]
    public void FocusLossCancelsCaptureAndEscapeDismissesPopupBeforeClosing()
    {
        using var window = new TestWindow();
        using var presenter = new TestPresenter();
        using var root = new StackPanel();
        var button = new Button { Width = 100, Height = 40 };
        var popup = new Popup { Content = new TextBlock(), IsOpen = true };
        root.Children.Add(button);
        root.Children.Add(popup);
        using var host = new WindowHost(window, presenter, root);
        host.FrameTicked += (_, _) =>
        {
            window.Key(Key.Escape);
            Assert.False(popup.IsOpen);
            Assert.False(window.IsClosed);
            window.Press(new(10, 10));
            Assert.NotNull(host.Input.Captured);
            window.LoseFocus();
            Assert.Null(host.Input.Captured);
            Assert.Null(host.Input.Focus.Focused);
            window.Key(Key.Escape);
        };
        host.Run(10);
        Assert.True(window.IsClosed);
        Assert.Equal(1, host.Ticks);
    }

    [Fact]
    public void PostedWorkRunsOnUiThreadAndCleanupRestoresClipboardAndContextAfterFailure()
    {
        using var window = new TestWindow();
        using var presenter = new TestPresenter();
        using var root = new StackPanel();
        var context = SynchronizationContext.Current;
        var clipboard = Clipboard.Current;
        var thread = Environment.CurrentManagedThreadId;
        var ran = false;
        using (var host = new WindowHost(window, presenter, root))
        {
            host.FrameTicked += (_, e) =>
            {
                Assert.Same(window.Clipboard, Clipboard.Current);
                if (e.Iteration == 1)
                    SynchronizationContext.Current!.Post(_ => { Assert.Equal(thread, Environment.CurrentManagedThreadId); ran = true; }, null);
                else throw new InvalidOperationException("test failure");
            };
            Assert.Throws<InvalidOperationException>(() => host.Run(3));
        }
        Assert.True(ran);
        Assert.Same(context, SynchronizationContext.Current);
        Assert.Same(clipboard, Clipboard.Current);
        // Attachment was released, including on the exceptional path.
        using var nextHost = new HeadlessRoot(root);
    }

    [Fact]
    public void WindowContentReplacementResetsInputAndPaintsNewTree()
    {
        using var window = new TestWindow();
        using var presenter = new TestPresenter();
        using var ui = new Window { Content = new Button { Width = 100, Height = 40 } };
        var previous = ui.Content;
        using var host = new WindowHost(window, presenter, ui.Content, uiRoot: ui);
        var next = new TextBox();
        host.FrameTicked += (_, e) =>
        {
            if (e.Iteration != 1) return;
            window.Press(new(10, 10));
            ui.Content = next;
        };
        host.Run(2);
        Assert.Same(next, host.Root);
        Assert.Null(host.Input.Captured);
        Assert.Equal(new Size(400, 300), next.FinalRect.Size);
        previous?.Dispose();
    }

    public sealed class TestApp : Application
    {
        public override void OnFrameworkInitializationCompleted() => MainWindow = new Window { Content = new StackPanel() };
    }
}

#pragma warning disable CS0067 // The fake exposes the complete native event contract.
internal sealed class TestWindow : IMacOSWindow
{
    public NativeWindowHandle SurfaceHandle => NativeWindowHandle.FromAppKit(1);
    public int ClientWidth { get; private set; } = 800;
    public int ClientHeight { get; private set; } = 600;
    public double ScaleFactor { get; private set; } = 2;
    public Size LogicalClientSize => new(ClientWidth / ScaleFactor, ClientHeight / ScaleFactor);
    public bool IsClosed { get; private set; }
    public IClipboard Clipboard { get; } = new MemoryClipboard();
    public bool TextInputEnabled { get; private set; }
    public Rect? Caret { get; private set; }
    public void Show() { }
    public void Close() { IsClosed = true; Closing?.Invoke(this, EventArgs.Empty); }
    public bool PumpMessages() => !IsClosed;
    public void SetTitle(string title) { }
    public void SetClientSize(double width, double height) { }
    public void SetImeCaret(Rect logicalCaret) => Caret = logicalCaret;
    public void SetTextInputEnabled(bool enabled) => TextInputEnabled = enabled;
    public void Dispose() { }
    public void Expose() => PaintRequested?.Invoke(this, EventArgs.Empty);
    public void Resize(int width, int height, double scale)
    {
        ClientWidth = width; ClientHeight = height; ScaleFactor = scale;
        Resized?.Invoke(this, new(width, height, scale));
    }
    public void Press(Point point) => PointerPressed?.Invoke(this, new(point));
    public void Release(Point point) => PointerReleased?.Invoke(this, new(point));
    public void Type(string text) => CharacterTyped?.Invoke(this, new(text));
    public void Compose(string text, CompositionStage stage) => TextComposition?.Invoke(this, new(text, stage));
    public void Key(Key key) => KeyDown?.Invoke(this, new(key));
    public void LoseFocus() => FocusLost?.Invoke(this, EventArgs.Empty);
    public event EventHandler<MacOSResizeEventArgs>? Resized;
    public event EventHandler? PaintRequested;
    public event EventHandler<MacOSPointerEventArgs>? PointerMoved;
    public event EventHandler<MacOSPointerEventArgs>? PointerPressed;
    public event EventHandler<MacOSPointerEventArgs>? PointerReleased;
    public event EventHandler? PointerCaptureLost;
    public event EventHandler<MacOSWheelEventArgs>? PointerWheel;
    public event EventHandler<MacOSKeyEventArgs>? KeyDown;
    public event EventHandler<MacOSKeyEventArgs>? KeyUp;
    public event EventHandler<MacOSTextEventArgs>? CharacterTyped;
    public event EventHandler<TextCompositionEventArgs>? TextComposition;
    public event EventHandler? FocusLost;
    public event EventHandler? Closing;
}

internal sealed class TestPresenter : ISurfacePresenter
{
    public string Name => "test";
    public string Description => Name;
    public RenderCapabilities Capabilities => RenderCapabilities.All;
    public int Width { get; private set; }
    public int Height { get; private set; }
    public bool IsVsync => false;
    public void Resize(int width, int height) { Width = width; Height = height; }
    public void Render(DisplayList list) { }
    public void Dispose() { }
}

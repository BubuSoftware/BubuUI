using EclipseUI.Elements;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EclipseUI.WindowLifetime.Tests;

[CollectionDefinition("ApplicationWindowLifetime", DisableParallelization = true)]
public sealed class ApplicationWindowLifetimeCollection { }

[Collection("ApplicationWindowLifetime")]
public sealed class ApplicationWindowLifetimeTests
{
    [Fact]
    public void ClosingTheMainWindowLeavesItsDialogRunningUntilTheDialogCloses()
    {
        using var fixture = new Fixture();
        var main = fixture.Open("main");
        var dialog = fixture.Open("dialog", modal: true);
        Assert.False(main.Window.Content!.IsEnabled);

        main.RequestClose();

        Assert.Equal(new[] { dialog.Window }, fixture.App.Windows);
        Assert.False(dialog.Host.IsClosed);
        Assert.Equal(0, dialog.CloseCount);
        Assert.Same(dialog.Host, fixture.App.Host);
        var theme = new TestTheme();
        fixture.App.SetTheme(theme);
        Assert.Empty(main.Host.Themes);
        Assert.Equal(new[] { theme }, dialog.Host.Themes);
        dialog.End();
        Assert.Empty(fixture.App.Windows);
        Assert.Null(fixture.App.Host);
        Assert.Same(fixture.InitialClipboard, Clipboard.Current);
        main.End();
        Assert.Empty(fixture.App.Windows);
    }

    [Fact]
    public void ClosingAnIntermediateWindowRestoresTheNearestLiveHostAndClipboard()
    {
        using var fixture = new Fixture();
        var main = fixture.Open("main");
        var middle = fixture.Open("middle", modal: true);
        var inner = fixture.Open("inner", modal: true);
        Assert.False(main.Window.Content!.IsEnabled);
        Assert.False(middle.Window.Content!.IsEnabled);

        middle.RequestClose();
        var theme = new TestTheme();
        fixture.App.SetTheme(theme);
        Assert.Equal(new[] { main.Window, inner.Window }, fixture.App.Windows);
        Assert.Empty(middle.Host.Themes);
        Assert.Same(theme, main.Window.Theme);
        Assert.Same(theme, inner.Window.Theme);
        inner.End();

        Assert.Same(main.Host, fixture.App.Host);
        Assert.Same(main.Clipboard, Clipboard.Current);
        Assert.False(main.Window.Content.IsEnabled);
        middle.End();
        Assert.True(main.Window.Content.IsEnabled);
        Assert.Same(main.Host, fixture.App.Host);
    }

    [Fact]
    public void ModalWindowRestoresItsOwnerEnabledStateAndKeyboardFocus()
    {
        using var fixture = new Fixture();
        var content = new Grid();
        var button = new Button { Label = "Open", Width = 80, Height = 30 };
        content.Children.Add(button);
        var main = fixture.Open("main", content: content);
        using var headless = new HeadlessRoot(main.Window) { AvailableSize = new(300, 200) };
        headless.UpdateLayout();
        Assert.True(headless.Focus.Focus(button));
        var dialog = fixture.Open("dialog", modal: true);
        Assert.False(content.IsEnabled);
        Assert.Null(headless.Focus.Focused);

        dialog.End();

        Assert.True(content.IsEnabled);
        Assert.Same(button, headless.Focus.Focused);
        Assert.Equal(FocusOrigin.Restore, headless.Focus.LastFocusOrigin);
    }

    [Fact]
    public void ModalWindowDoesNotEnableAnOwnerThatWasAlreadyDisabled()
    {
        using var fixture = new Fixture();
        var main = fixture.Open("main", content: new Grid { IsEnabled = false });
        fixture.Open("dialog", modal: true).End();
        Assert.False(main.Window.Content!.IsEnabled);
    }

    [Fact]
    public void ApplicationThemeChangesUpdateTheDefaultForFutureWindowsAndSkipDisposedUi()
    {
        using var fixture = new Fixture();
        var main = fixture.Open("main");
        var dialog = fixture.Open("dialog");
        main.Window.Dispose();
        var theme = new TestTheme();

        fixture.App.SetTheme(theme);

        Assert.Same(theme, fixture.App.DefaultTheme);
        Assert.Empty(main.Host.Themes);
        Assert.Same(theme, dialog.Window.Theme);
        Assert.Equal(new[] { theme }, dialog.Host.Themes);
        using var future = new Window();
        Assert.Same(theme, future.Theme ?? fixture.App.DefaultTheme);
    }

    [Fact]
    public void AThemeFailureDoesNotPreventOtherOpenWindowsFromUpdating()
    {
        using var fixture = new Fixture();
        var main = fixture.Open("main");
        main.Host.ThemeFailure = new InvalidOperationException("theme failure");
        var dialog = fixture.Open("dialog");
        var theme = new TestTheme();

        var error = Assert.Throws<AggregateException>(() => fixture.App.SetTheme(theme));

        Assert.Equal("theme failure", Assert.Single(error.InnerExceptions).Message);
        Assert.Equal(new[] { theme }, dialog.Host.Themes);
        Assert.Same(theme, fixture.App.DefaultTheme);
    }

    [Fact]
    public void ShutdownClosesAllWindowsInReverseOrderAndPreventsNewDialogs()
    {
        using var fixture = new Fixture();
        var main = fixture.Open("main");
        var dialog = fixture.Open("dialog", modal: true);
        var inner = fixture.Open("inner", modal: true);

        fixture.App.Shutdown();
        fixture.App.Shutdown();

        Assert.Equal(new[] { "inner", "dialog", "main" }, fixture.CloseOrder);
        Assert.True(main.Host.IsClosed);
        Assert.True(dialog.Host.IsClosed);
        Assert.True(inner.Host.IsClosed);
        Assert.Empty(fixture.App.Windows);
        using var rejected = new Window();
        Assert.Throws<InvalidOperationException>(() => fixture.App.ShowDialog(rejected));
        Assert.False(rejected.IsDisposed);
    }

    [Fact]
    public void ShutdownAggregatesFailuresAndStillClosesTheOtherWindows()
    {
        using var fixture = new Fixture();
        var main = fixture.Open("main");
        main.CloseFailure = new InvalidOperationException("main close");
        var dialog = fixture.Open("dialog");
        var inner = fixture.Open("inner");
        inner.CloseFailure = new InvalidOperationException("inner close");

        var error = Assert.Throws<AggregateException>(fixture.App.Shutdown);

        Assert.Equal(new[] { "inner close", "main close" }, error.InnerExceptions.Select(item => item.Message));
        Assert.Equal(new[] { "inner", "dialog", "main" }, fixture.CloseOrder);
        Assert.True(dialog.Host.IsClosed);
        Assert.False(main.Host.IsClosed);
        Assert.False(inner.Host.IsClosed);
        main.CloseFailure = null;
        inner.CloseFailure = null;
        fixture.App.Shutdown();
        Assert.Empty(fixture.App.Windows);
    }

    [Fact]
    public void DuplicateShowDialogIsRejectedBeforeThePlatformTakesWindowOwnership()
    {
        using var fixture = new Fixture();
        var lifetime = new RecordingLifetime();
        using var services = new ServiceCollection().AddSingleton<IWindowLifetime>(lifetime).BuildServiceProvider();
        fixture.App.Services = services;
        var main = fixture.Open("main");

        Assert.Throws<InvalidOperationException>(() => fixture.App.ShowDialog(main.Window));

        Assert.Equal(0, lifetime.Calls);
        Assert.False(main.Window.IsDisposed);
        Assert.False(main.Host.IsClosed);
        Assert.Same(main.Host, fixture.App.Host);
        Assert.Single(fixture.App.Windows);
    }

    [Fact]
    public void ShowDialogDelegatesToTheInstalledPlatformWithModalSemantics()
    {
        using var fixture = new Fixture();
        var lifetime = new RecordingLifetime();
        using var services = new ServiceCollection().AddSingleton<IWindowLifetime>(lifetime).BuildServiceProvider();
        fixture.App.Services = services;
        using var dialog = new Window();

        fixture.App.ShowDialog(dialog);

        Assert.Equal(1, lifetime.Calls);
        Assert.Same(fixture.App, lifetime.Application);
        Assert.Same(dialog, lifetime.Window);
        Assert.True(lifetime.Modal);
    }

    [Fact]
    public void RegistrationRejectsDisposedWindowsAndDuplicateHostsWithoutChangingTheOwner()
    {
        using var fixture = new Fixture();
        var main = fixture.Open("main");
        using var duplicate = new Window();
        using var disposed = new Window();
        disposed.Dispose();
        Assert.Throws<InvalidOperationException>(() => fixture.App.AttachWindow(duplicate, main.Host, () => { }, new MemoryClipboard(), true));
        Assert.Throws<ObjectDisposedException>(() => fixture.App.AttachWindow(disposed, new FakeHost(), () => { }, new MemoryClipboard(), true));
        Assert.True(main.Window.Content!.IsEnabled);
        Assert.Same(main.Host, fixture.App.Host);
        Assert.Single(fixture.App.Windows);
    }

    [Fact]
    public void AFailedModalRegistrationRestoresTheOriginalHostClipboardAndOwnerState()
    {
        using var fixture = new Fixture();
        var main = fixture.Open("main", content: new ThrowOnDisableGrid());
        using var rejected = new Window { Content = new Grid() };

        var error = Assert.Throws<InvalidOperationException>(() =>
            fixture.App.AttachWindow(rejected, new FakeHost(), () => { }, new MemoryClipboard(), true));

        Assert.Equal("disable failure", error.Message);
        Assert.True(main.Window.Content!.IsEnabled);
        Assert.Same(main.Host, fixture.App.Host);
        Assert.Same(main.Clipboard, Clipboard.Current);
        Assert.Equal(new[] { main.Window }, fixture.App.Windows);
        Assert.False(rejected.IsDisposed);
    }

    [Fact]
    public void SessionDisposalIsIdempotentAndDoesNotRestoreAClosedLegacyHost()
    {
        using var fixture = new Fixture();
        var legacy = new FakeHost();
        fixture.App.Host = legacy;
        var dialog = fixture.Open("dialog");
        legacy.IsClosed = true;

        dialog.End();
        dialog.Session!.Dispose();

        Assert.Null(fixture.App.Host);
        Assert.Same(fixture.InitialClipboard, Clipboard.Current);
        Assert.Empty(fixture.App.Windows);
    }

    [Fact]
    public void SessionDisposalRestoresAnOpenLegacyHostAndItsClipboard()
    {
        using var fixture = new Fixture();
        var legacy = new FakeHost();
        fixture.App.Host = legacy;
        fixture.Open("dialog").End();
        Assert.Same(legacy, fixture.App.Host);
        Assert.Same(fixture.InitialClipboard, Clipboard.Current);
    }

    [Fact]
    public void WindowOperationsRejectAnotherThreadWithoutChangingLiveState()
    {
        using var fixture = new Fixture();
        var main = fixture.Open("main");
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { fixture.App.SetTheme(new TestTheme()); }
            catch (Exception failure) { error = failure; }
        });
        thread.Start();
        thread.Join();
        Assert.IsType<InvalidOperationException>(error);
        Assert.Null(fixture.App.DefaultTheme);
        Assert.Empty(main.Host.Themes);
        Assert.Same(main.Host, fixture.App.Host);
    }

    private sealed class TestApp : Application { }
    private sealed class TestTheme : IHostTheme { }

    private sealed class FakeHost : IWindowHost
    {
        public bool IsClosed { get; set; }
        public List<IHostTheme> Themes { get; } = [];
        public Exception? ThemeFailure;
        public void SetTheme(IHostTheme theme)
        {
            ObjectDisposedException.ThrowIf(IsClosed, this);
            if (ThemeFailure is { } error) throw error;
            Themes.Add(theme);
        }
    }

    private sealed class RecordingLifetime : IWindowLifetime
    {
        public int Calls;
        public Application? Application;
        public Window? Window;
        public bool Modal;
        public int RunWindow(Application app, Window window, bool modal = false)
        {
            Calls++;
            Application = app;
            Window = window;
            Modal = modal;
            return 0;
        }
    }

    private sealed class ThrowOnDisableGrid : Grid
    {
        protected override void OnStylesChanged()
        {
            if (!IsEnabled) throw new InvalidOperationException("disable failure");
            base.OnStylesChanged();
        }
    }

    private sealed class OpenWindow(Fixture fixture, string title, Element content)
    {
        public Window Window { get; } = new() { Title = title, Content = content };
        public FakeHost Host { get; } = new();
        public MemoryClipboard Clipboard { get; } = new();
        public ApplicationWindowSession? Session;
        public Exception? CloseFailure;
        public int CloseCount;
        public void RequestClose()
        {
            CloseCount++;
            fixture.CloseOrder.Add(title);
            if (CloseFailure is { } error) throw error;
            Host.IsClosed = true;
        }
        public void End()
        {
            Window.Dispose();
            Host.IsClosed = true;
            Session?.Dispose();
        }
    }

    private sealed class Fixture : IDisposable
    {
        public TestApp App { get; } = new();
        public IClipboard InitialClipboard { get; } = Clipboard.Current;
        public List<string> CloseOrder { get; } = [];
        private readonly List<OpenWindow> _windows = [];
        public OpenWindow Open(string title, bool modal = false, Element? content = null)
        {
            var window = new OpenWindow(this, title, content ?? new Grid());
            _windows.Add(window);
            window.Session = App.AttachWindow(window.Window, window.Host, window.RequestClose, window.Clipboard, modal);
            return window;
        }
        public void Dispose()
        {
            try
            {
                foreach (var window in _windows.AsEnumerable().Reverse()) window.End();
            }
            finally { Clipboard.Current = InitialClipboard; }
        }
    }
}

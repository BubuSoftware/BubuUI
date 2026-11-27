using System.Runtime.InteropServices;
using BubuUI.Elements;
using BubuUI.Rendering.Vello;
using BubuUI.Text.Shaping;
using BubuUI.Themes.Fluent;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BubuUI.Platform.MacOS.Tests;

internal static partial class Program
{
    // Values from the SDL 3.2 SDL_video.h flags, not framework implementation details.
    private const ulong ModalWindowFlag = 0x1000, PopupMenuWindowFlag = 0x80000, TooltipWindowFlag = 0x40000;

    private static void ModalPopupScenario(string mode)
    {
        var previousMeasurer = TextMeasurer.Current;
        var clipboard = Clipboard.Current;
        var app = (ModalPopupApp)AppBuilder.Configure<ModalPopupApp>().UseMacOS()
            .UseVello(new VelloSurfaceOptions { UseSoftwareRasterizer = true, ValidateDisplayList = true })
            .UseHarfBuzzText().UseFluentTheme(ThemeVariant.Light).Build();
        using var services = (IDisposable)app.Services;
        app.Mode = mode;
        try
        {
            Assert.Equal(0, app.Services.GetRequiredService<IApplicationLifetime>().Run(app, []));
            Assert.True(app.Finished, "The native modal popup scenario did not reach its close path.");
            Assert.Empty(app.Windows);
            Assert.Null(app.Host);
            Assert.Same(clipboard, Clipboard.Current);
            Assert.Empty(SdlWindows());
            CocoaPopupAssertions.NoModalSession();
        }
        finally
        {
            app.MainWindow?.Dispose();
            TextMeasurer.Current = previousMeasurer;
        }
    }

    public sealed class ModalPopupApp : Application
    {
        public string Mode { get; set; } = "basic";
        public bool Finished { get; private set; }
        private WindowHost _mainHost = null!;
        private Popup _modal = null!, _inner = null!;
        private TextBox _outside = null!, _first = null!, _second = null!, _innerEditor = null!;
        private Button _outsideButton = null!, _closeButton = null!;
        private ComboBox _combo = null!;
        private int _outsideClicks, _outsideKeys, _modalKeys, _ownerRootClicks;
        private uint _firstId, _secondId;
        private bool _ownerPathCompleted;

        public override void OnFrameworkInitializationCompleted()
        {
            MainWindow = new ModalProbeWindow(this)
            {
                Title = "Modal popup main", Width = 360, Height = 240, MaxTicks = 100, CloseOnEscape = false,
                Content = new TextBox { Text = "Parent of the settings-style child window" },
                Started = host =>
                {
                    _mainHost = host;
                    // Activate only this test application's own window before simulating the
                    // user's action that opens a child window and its modal popup.
                    ((MacOSWindow)host.Window).Activate();
                    host.FrameTicked += (_, frame) =>
                    {
                        if (frame.Iteration != 2) return;
                        var context = Assert.IsType<UiSynchronizationContext>(SynchronizationContext.Current);
                        context.Post(_ => OpenOwnerWindow(), null);
                    };
                },
            };
        }

        private void OpenOwnerWindow()
        {
            _outside = new TextBox { Text = "Owner editor", Width = 220, Height = 32,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = 16 };
            _outsideButton = new Button { Label = "Owner action", Width = 130, Height = 32,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = 16 };
            _outsideButton.AddHandler(Button.ClickEvent, _ => _outsideClicks++);
            _first = new TextBox { Text = "First modal editor", Height = 32 };
            _second = new TextBox { Text = "Second modal editor", Height = 32 };
            _innerEditor = new TextBox { Text = "Nested modal editor", Width = 190, Height = 36 };
            _combo = new ComboBox { Height = 32, ItemsSource = new[] { "First", "Second", "Third" } };
            _closeButton = new Button { Label = "Close modal", Height = 32 };
            var body = new StackPanel { Width = 300, Padding = 12, Gap = 8 };
            body.Children.Add(_first); body.Children.Add(_second); body.Children.Add(_combo); body.Children.Add(_closeButton);
            body.AddHandler(Element.KeyDownEvent, _ => _modalKeys++);
            _inner = new Popup { Content = _innerEditor, IsModal = true, HostMode = PopupHostMode.Window,
                PlacementTarget = _first, HorizontalOffset = 24, VerticalOffset = 16 };
            body.Children.Add(_inner);
            _modal = new Popup { Content = body, IsModal = Mode != "toggle", HostMode = PopupHostMode.Window,
                PlacementTarget = _outside, HorizontalOffset = 10, VerticalOffset = 12 };
            _closeButton.AddHandler(Button.ClickEvent, _ => _modal.Close());
            var root = new Grid();
            root.Children.Add(_outside); root.Children.Add(_outsideButton); root.Children.Add(_modal);
            root.AddHandler(Element.PreviewKeyDownEvent, _ => _outsideKeys++);
            root.AddHandler(Element.KeyDownEvent, _ => _outsideKeys++);
            root.AddHandler(Button.ClickEvent, _ => _ownerRootClicks++);
            var owner = new ModalProbeWindow(this)
            {
                Title = "Child window owning a modal popup", Width = 560, Height = 420,
                MaxTicks = 100, CloseOnEscape = false, Content = root,
                Started = host => host.FrameTicked += (_, frame) => TickOwner(host, frame.Iteration),
            };
            ShowDialog(owner);
            Assert.True(_ownerPathCompleted, "The child window did not complete its modal popup assertions.");
            Assert.True(owner.IsDisposed);
            Assert.True(_modal.IsDisposed);
            Finished = true;
            if (!_mainHost.Window.IsClosed)
            {
                Assert.Same(_mainHost, Host);
                Assert.True(MainWindow!.Content!.IsEnabled);
                _mainHost.Window.Close();
            }
        }

        private void TickOwner(WindowHost host, int frame)
        {
            Assert.Equal(2, Windows.Count);
            Assert.False(_mainHost.Window.IsClosed);
            if (frame == 1)
            {
                var window = (MacOSWindow)host.Window;
                window.Activate();
                for (var attempt = 0; attempt < 60 && SdlNative.SDL_GetKeyboardFocus() != window.NativeHandle; attempt++)
                {
                    window.PumpMessages();
                    Thread.Sleep(16);
                }
                Assert.Equal(window.NativeHandle, SdlNative.SDL_GetKeyboardFocus());
                Assert.True(host.Input.Focus.Focus(_outside));
                _modal.Open();
                return;
            }
            if (Mode == "nested") TickNested(host, frame);
            else if (Mode == "toggle") TickToggle(host, frame);
            else TickBasic(host, frame);
        }

        private void TickBasic(WindowHost host, int frame)
        {
            if (frame == 2)
            {
                var native = AssertModalNative(host);
                _firstId = SdlNative.SDL_GetWindowID(native);
                Assert.True(host.Input.Focus.Focus(_first));
                Assert.False(host.Input.Focus.Focus(_outside));
                for (var i = 0; i < 12; i++)
                {
                    host.Input.KeyPress(Key.Tab, i % 2 == 0 ? KeyModifiers.None : KeyModifiers.Shift);
                    host.Input.KeyRelease(Key.Tab, i % 2 == 0 ? KeyModifiers.None : KeyModifiers.Shift);
                    AssertInside(_modal, host.Input.Focus.Focused);
                }
                QueueModalClick(((MacOSWindow)host.Window).NativeHandle, _outsideButton, (MacOSWindow)host.Window);
                QueueModalKey(native, 0x4000003b); // F2 is unhandled by the editor and must not reach owner shortcuts.
            }
            if (frame == 3)
            {
                Assert.True(_modal.IsOpen);
                Assert.Equal(0, _outsideClicks);
                Assert.Equal(0, _outsideKeys);
                Assert.True(_modalKeys > 0);
                var native = AssertModalNative(host);
                QueueModalClick(native, _closeButton, (MacOSWindow)host.Window);
            }
            if (frame == 5)
            {
                Assert.False(_modal.IsOpen);
                Assert.Equal(0, _ownerRootClicks); // Closing during Click must not lift its route fence mid-dispatch.
                Assert.DoesNotContain(SdlWindows(), native => SdlNative.SDL_GetWindowID(native) == _firstId);
                CocoaPopupAssertions.NoModalSession();
                Assert.Same(_outside, host.Input.Focus.Focused);
                Assert.Equal(((MacOSWindow)host.Window).NativeHandle, SdlNative.SDL_GetKeyboardFocus());
                _ownerPathCompleted = true;
                host.Window.Close();
            }
        }

        private void TickNested(WindowHost host, int frame)
        {
            if (frame == 2)
            {
                _firstId = SdlNative.SDL_GetWindowID(AssertModalNative(host));
                Assert.True(host.Input.Focus.Focus(_first));
                _inner.Open();
            }
            if (frame == 3)
            {
                var modals = ModalNatives();
                Assert.Equal(2, modals.Length);
                var outer = Assert.Single(modals, native => SdlNative.SDL_GetWindowID(native) == _firstId);
                var inner = Assert.Single(modals, native => SdlNative.SDL_GetWindowID(native) != _firstId);
                Assert.Equal(outer, SdlNative.SDL_GetWindowParent(inner));
                Assert.Equal(inner, SdlNative.SDL_GetKeyboardFocus());
                CocoaPopupAssertions.ModalSession(inner);
                Assert.True(host.Input.Focus.Focus(_innerEditor));
                Assert.False(host.Input.Focus.Focus(_first));
                _secondId = SdlNative.SDL_GetWindowID(inner);
                _modalKeys = 0;
                QueueModalKey(inner, 0x4000003b);
            }
            if (frame == 4)
            {
                Assert.Equal(0, _outsideKeys);
                Assert.Equal(0, _modalKeys); // The outer dialog's shortcut handler is outside the current fence too.
                QueueModalKey(Assert.Single(ModalNatives(), native => SdlNative.SDL_GetWindowID(native) == _secondId), 27);
            }
            if (frame == 6)
            {
                Assert.False(_inner.IsOpen);
                Assert.True(_modal.IsOpen);
                Assert.Single(ModalNatives());
                AssertInside(_modal, host.Input.Focus.Focused);
                Assert.Equal(AssertModalNative(host), SdlNative.SDL_GetKeyboardFocus());
                _combo.IsDropDownOpen = true;
            }
            if (frame == 7)
            {
                var modal = AssertModalNative(host, focused: false);
                var dropdown = Assert.Single(SdlWindows(), native => (SdlNative.SDL_GetWindowFlags(native) & PopupMenuWindowFlag) != 0);
                Assert.Equal(modal, SdlNative.SDL_GetWindowParent(dropdown));
                Assert.Equal(dropdown, SdlNative.SDL_GetKeyboardFocus());
                QueueModalKey(dropdown, 0x40000051); // Down
                QueueModalKey(dropdown, 13); // Enter
            }
            if (frame == 9)
            {
                Assert.False(_combo.IsDropDownOpen);
                Assert.Equal(0, _combo.SelectedIndex);
                Assert.True(_modal.IsOpen);
                QueueModalKey(AssertModalNative(host), 27);
            }
            if (frame == 11)
            {
                Assert.False(_modal.IsOpen);
                Assert.Empty(ModalNatives());
                CocoaPopupAssertions.NoModalSession();
                Assert.Same(_outside, host.Input.Focus.Focused);
                _ownerPathCompleted = true;
                host.Window.Close();
            }
        }

        private void TickToggle(WindowHost host, int frame)
        {
            if (frame == 2)
            {
                var popup = Assert.Single(SdlWindows(), native => (SdlNative.SDL_GetWindowFlags(native) & PopupMenuWindowFlag) != 0);
                _firstId = SdlNative.SDL_GetWindowID(popup);
                Assert.Empty(ModalNatives());
                _modal.IsModal = true;
            }
            if (frame == 3)
            {
                var modal = AssertModalNative(host);
                _secondId = SdlNative.SDL_GetWindowID(modal);
                Assert.NotEqual(_firstId, _secondId);
                _modal.IsModal = false;
            }
            if (frame == 4)
            {
                Assert.True(_modal.IsOpen);
                Assert.Empty(ModalNatives());
                CocoaPopupAssertions.NoModalSession();
                var popup = Assert.Single(SdlWindows(), native => (SdlNative.SDL_GetWindowFlags(native) & PopupMenuWindowFlag) != 0);
                Assert.NotEqual(_secondId, SdlNative.SDL_GetWindowID(popup));
                _modal.HostMode = PopupHostMode.Overlay;
                _modal.IsModal = true;
            }
            if (frame == 5)
            {
                Assert.False(_modal.IsWindowHosted);
                Assert.Empty(ModalNatives());
                CocoaPopupAssertions.NoModalSession();
                Assert.False(host.Input.Focus.Focus(_outside));
                var point = PointIn(_outsideButton, _outsideButton.FinalRect.Width / 2, _outsideButton.FinalRect.Height / 2);
                host.Input.PointerPress(point); host.Input.PointerRelease(point);
                Assert.True(_modal.IsOpen);
                Assert.Equal(0, _outsideClicks);
                _modal.HostMode = PopupHostMode.Window;
            }
            if (frame == 6)
            {
                AssertModalNative(host);
                SdlNative.Check(SdlNative.SDL_HideWindow(((MacOSWindow)host.Window).NativeHandle));
            }
            if (frame == 8)
            {
                // Simulate native focus loss using only this test process's own windows.
                Assert.True(_modal.IsOpen);
                Assert.True(_modal.IsModal);
                CocoaPopupAssertions.NoModalSession();
                host.Window.Show();
            }
            if (frame == 10)
            {
                Assert.True(_modal.IsOpen);
                Assert.Single(ModalNatives());
                _ownerPathCompleted = true;
                Shutdown(); // Explicit exit must also end the active native modal session.
            }
        }

        private nint AssertModalNative(WindowHost host, bool focused = true)
        {
            Assert.True(_modal.IsOpen);
            Assert.True(_modal.IsModal);
            Assert.True(_modal.IsWindowHosted);
            var native = Assert.Single(ModalNatives());
            Assert.Equal(((MacOSWindow)host.Window).NativeHandle, SdlNative.SDL_GetWindowParent(native));
            Assert.Equal(0UL, SdlNative.SDL_GetWindowFlags(native) & (PopupMenuWindowFlag | TooltipWindowFlag));
            if (focused) Assert.Equal(native, SdlNative.SDL_GetKeyboardFocus());
            CocoaPopupAssertions.ModalSession(native);
            return native;
        }
    }

    private sealed class ModalProbeWindow(ModalPopupApp app) : Window
    {
        public Action<WindowHost>? Started { get; init; }
        private bool _started;
        protected override void OnInitialized() { base.OnInitialized(); Invalidate(); }
        protected override void OnUpdating()
        {
            base.OnUpdating();
            if (_started) return;
            _started = true;
            Started?.Invoke(Assert.IsType<WindowHost>(app.Host));
        }
    }

    private static nint[] ModalNatives()
        => SdlWindows().Where(native => (SdlNative.SDL_GetWindowFlags(native) & ModalWindowFlag) != 0).ToArray();

    private static Point PointIn(Element element, double x, double y)
    {
        var zero = element.PointFromRoot(Point.Zero);
        return new(x - zero.X, y - zero.Y);
    }

    private static void AssertInside(Popup popup, Element? element)
    {
        Assert.NotNull(element);
        Assert.True(ReferenceEquals(popup, element) || popup.Descendants().Contains(element),
            "Focus must remain in the active modal popup.");
    }

    private static nint[] SdlWindows()
    {
        var array = SdlNative.SDL_GetWindows(out var count);
        try { return Enumerable.Range(0, count).Select(index => Marshal.ReadIntPtr(array, index * nint.Size)).ToArray(); }
        finally { SdlNative.SDL_free(array); }
    }

    private static void QueueModalKey(nint window, uint key)
    {
        foreach (var type in new[] { SdlNative.KeyDown, SdlNative.KeyUp })
        {
            var message = new SdlNative.Event { Key = new() { Type = type,
                WindowId = SdlNative.SDL_GetWindowID(window), Key = key,
                Down = type == SdlNative.KeyDown ? (byte)1 : (byte)0 } };
            SdlNative.Check(SdlNative.SDL_PushEvent(ref message));
        }
    }

    private static void QueueModalClick(nint native, Element element, MacOSWindow owner)
    {
        var point = PointIn(element, element.FinalRect.Width / 2, element.FinalRect.Height / 2);
        var ownerOrigin = NativePosition(owner.NativeHandle);
        var origin = NativePosition(native);
        var x = (float)(ownerOrigin.X + point.X * owner.NativeUnitsPerLogicalX - origin.X);
        var y = (float)(ownerOrigin.Y + point.Y * owner.NativeUnitsPerLogicalY - origin.Y);
        foreach (var type in new[] { SdlNative.MouseButtonDown, SdlNative.MouseButtonUp })
        {
            var message = new SdlNative.Event { Button = new() { Type = type,
                WindowId = SdlNative.SDL_GetWindowID(native), Button = 1,
                Down = type == SdlNative.MouseButtonDown ? (byte)1 : (byte)0, X = x, Y = y } };
            SdlNative.Check(SdlNative.SDL_PushEvent(ref message));
        }
    }

    private static Point NativePosition(nint window)
    {
        SdlNative.Check(SdlNative.SDL_GetWindowPosition(window, out var x, out var y));
        if ((SdlNative.SDL_GetWindowFlags(window) & (PopupMenuWindowFlag | TooltipWindowFlag)) == 0) return new(x, y);
        var parent = SdlNative.SDL_GetWindowParent(window);
        var origin = parent == 0 ? Point.Zero : NativePosition(parent);
        return new(origin.X + x, origin.Y + y);
    }
}

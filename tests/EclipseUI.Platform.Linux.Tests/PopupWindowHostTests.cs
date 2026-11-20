using EclipseUI.Elements;
using EclipseUI.Rendering;
using Xunit;

namespace EclipseUI.Platform.Linux.Tests;

public sealed class PopupWindowHostTests
{
    [Fact]
    public void NativePopupOverflowsOwnerRoutesInputAndReleasesSurfaceWhenClosed()
    {
        using var window = new TestWindow();
        using var presenter = new TestPresenter();
        using var root = new Grid();
        var button = new Button { Width = 120, Height = 40 };
        var popup = new Popup { Content = button, PlacementTarget = root, IsOpen = true };
        root.Children.Add(popup);
        var factory = new PopupFactory();
        var surfaces = new SurfaceFactory();
        var clicks = 0;
        button.AddHandler(Button.ClickEvent, _ => clicks++);
        using var host = new WindowHost(window, presenter, root, new()
        {
            SurfaceFactory = surfaces,
            PopupWindowFactory = factory,
        });
        host.FrameTicked += (_, e) =>
        {
            if (e.Iteration == 1)
            {
                Assert.Same(popup, button.Parent);
                Assert.Equal(root.FinalRect.Height, factory.Window!.Bounds.Y);
                Assert.True(factory.Window.Shown);
                factory.Window.Press(new(10, 10));
                factory.Window.Release(new(10, 10));
                Assert.Equal(1, clicks);
                Assert.False(window.IsClosed);
                factory.Window.Key(Key.Escape);
            }
            else
            {
                Assert.False(popup.IsOpen);
                Assert.True(factory.Window!.Disposed);
                Assert.True(surfaces.Surface!.Disposed);
            }
        };
        host.Run(2);
    }

    [Fact]
    public void NativePopupRetainsInputWhenOwnerFocusMovesIntoPopup()
    {
        using var window = new TestWindow();
        using var presenter = new TestPresenter();
        using var root = new Grid();
        var editor = new TextBox { Width = 120, Height = 40 };
        var popup = new Popup { Content = editor, IsOpen = true };
        root.Children.Add(popup);
        var factory = new PopupFactory();
        using var host = new WindowHost(window, presenter, root, new()
        {
            SurfaceFactory = new SurfaceFactory(),
            PopupWindowFactory = factory,
        });
        host.FrameTicked += (_, e) =>
        {
            if (e.Iteration == 1)
            {
                factory.Window!.Focused = true;
                factory.Window.Press(new(10, 10));
                factory.Window.Release(new(10, 10));
                window.LoseFocus();
                Assert.Same(editor, host.Input.Focus.Focused);
                factory.Window.Type("弹出窗口");
            }
            else
            {
                Assert.True(popup.IsOpen);
                Assert.Equal("弹出窗口", editor.Text);
                Assert.False(window.TextInputEnabled);
                Assert.True(factory.Window!.TextEnabled);
            }
        };
        host.Run(2);
        Assert.True(factory.Window!.Disposed);
        Assert.False(editor.IsDisposed);
    }

    private sealed class PopupFactory : IPopupWindowFactory
    {
        public Size AvailableSize => new(1200, 900);
        public bool IsOwnerFocused => false;
        public PopupWindow? Window { get; private set; }
        public IPopupWindow Create(bool interactive, IPopupWindow? parent = null) => Window = new();
    }

    private sealed class SurfaceFactory : ISurfaceFactory
    {
        public PopupPresenter? Surface { get; private set; }
        public ISurfacePresenter Create(nint handle, int width, int height)
        {
            Surface = new();
            Surface.Resize(width, height);
            return Surface;
        }
        public ISurfacePresenter Create(NativeWindowHandle handle, int width, int height) => Create(handle.Handle, width, height);
    }

    private sealed class PopupPresenter : ISurfacePresenter
    {
        public string Name => "popup";
        public string Description => Name;
        public RenderCapabilities Capabilities => RenderCapabilities.All;
        public int Width { get; private set; }
        public int Height { get; private set; }
        public bool IsVsync => false;
        public bool Disposed { get; private set; }
        public void Resize(int width, int height) { Width = width; Height = height; }
        public void Render(DisplayList list) { }
        public void Dispose() => Disposed = true;
    }

#pragma warning disable CS0067
    private sealed class PopupWindow : IPopupWindow
    {
        public NativeWindowHandle SurfaceHandle => NativeWindowHandle.FromXlib(3, 2);
        public int ClientWidth => (int)Bounds.Width;
        public int ClientHeight => (int)Bounds.Height;
        public double ScaleFactor => 1;
        public bool IsClosed => Disposed;
        public bool IsFocused => Focused;
        public bool Focused { get; set; }
        public bool Disposed { get; private set; }
        public bool Shown { get; private set; }
        public bool TextEnabled { get; private set; }
        public Rect Bounds { get; private set; }
        public Rect SetBounds(Rect bounds) => Bounds = bounds;
        public void Show() => Shown = true;
        public void SetTextInputEnabled(bool enabled) => TextEnabled = enabled;
        public void SetImeCaret(Rect caret) { }
        public void Dispose() => Disposed = true;
        public void Press(Point point) => PointerPressed?.Invoke(point, KeyModifiers.None, PointerButton.Left);
        public void Release(Point point) => PointerReleased?.Invoke(point);
        public void Key(Key key) => KeyDown?.Invoke(key, KeyModifiers.None, false);
        public void Type(string text) => TextInput?.Invoke(text);
        public event Action<Point>? PointerMoved;
        public event Action<Point, KeyModifiers, PointerButton>? PointerPressed;
        public event Action<Point>? PointerReleased;
        public event Action? PointerCaptureLost;
        public event Action<Point, double, KeyModifiers>? PointerWheel;
        public event Action<Key, KeyModifiers, bool>? KeyDown;
        public event Action<Key, KeyModifiers>? KeyUp;
        public event Action<string>? TextInput;
        public event Action<string, CompositionStage>? TextComposition;
        public event Action? FocusLost;
        public event Action? PaintRequested;
        public event Action? Closed;
    }
}

using BubuUI.Elements;
using Xunit;

namespace BubuUI.Rendering.Tests;

public class PopupWindowTests
{
    private static readonly Size OwnerSize = new(160, 100);

    private static void Layout(Canvas root, HeadlessRoot host, PopupWindowManager manager, ElementTheme? theme = null)
    {
        manager.PrepareLayout(root, host.Input);
        host.UpdateLayout();
        manager.Render(root, host.Input, theme ?? ElementTheme.Light);
    }

    [Fact]
    public void PopupEscapesOwnerAndUsesSeparatePaintingAndInputWithoutReparenting()
    {
        using var root = new Canvas { Width = OwnerSize.Width, Height = OwnerSize.Height };
        var target = new Button { Width = 30, Height = 20 };
        Canvas.SetLeft(target, 140); Canvas.SetTop(target, 80);
        var body = new Border { Width = 240, Height = 80, Background = new(233, 17, 91), Content = new Button { Label = "Choose" } };
        var popup = new Popup { Content = body, PlacementTarget = target, IsOpen = true };
        root.Children.Add(target); root.Children.Add(popup);
        using var host = new HeadlessRoot(root) { AvailableSize = OwnerSize };
        var windows = new FakeWindows(); var surfaces = new FakeSurfaces();
        using var manager = new PopupWindowManager(windows, surfaces);
        Layout(root, host, manager);
        var native = Assert.Single(windows.Created);
        Assert.True(popup.IsWindowHosted);
        Assert.Equal(new Rect(140, 100, 240, 80), native.Bounds);
        Assert.Same(popup, body.Parent);
        Assert.Equal(240, body.FinalRect.Width);
        Assert.Null(host.HitTest(new(150, 110)));
        var ownerFrame = new ElementPainter().Build(root, OwnerSize);
        Assert.DoesNotContain(ownerFrame.Commands.OfType<FillRoundedRect>(), c => c.Brush == new SolidColorBrush(new Color(233, 17, 91)));
        var popupFrame = Assert.Single(surfaces.Created).Last!;
        Assert.Empty(popupFrame.Validate());
        Assert.Contains(popupFrame.Commands.OfType<FillRoundedRect>(), c => c.Brush == new SolidColorBrush(new Color(233, 17, 91)) && c.Rect.Location == Point.Zero);
        var clicks = 0;
        body.Content!.AddHandler(Button.ClickEvent, _ => clicks++);
        native.Press(new(10, 10)); native.Release(new(10, 10));
        Assert.Equal(1, clicks);
        Assert.True(popup.IsOpen);
    }

    [Fact]
    public void PopupEditsTextAndEscapeRestoresOwnerFocusAndDisposesSurfaceFirst()
    {
        using var root = new Canvas { Width = 160, Height = 100 };
        var button = new Button { Width = 40, Height = 20 };
        var ownerEditor = new TextBox { Width = 100, Height = 30 };
        Canvas.SetTop(ownerEditor, 60);
        var editor = new TextBox { Width = 200, Height = 40 };
        var popup = new Popup { Content = editor, PlacementTarget = button };
        root.Children.Add(button); root.Children.Add(popup); root.Children.Add(ownerEditor);
        using var host = new HeadlessRoot(root) { AvailableSize = OwnerSize };
        var windows = new FakeWindows(); var surfaces = new FakeSurfaces(windows.Log);
        using var manager = new PopupWindowManager(windows, surfaces);
        host.Focus.Focus(button); popup.Open();
        Layout(root, host, manager);
        var native = Assert.Single(windows.Created);
        native.IsFocused = true;
        native.Press(new(10, 10)); native.Release(new(10, 10));
        native.Type("中文 😀");
        Layout(root, host, manager);
        Assert.Equal("中文 😀", editor.Text);
        Assert.Same(editor, host.Focus.Focused);
        Assert.True(native.TextInputEnabled);
        Assert.True(manager.OwnsEditor(editor));
        Assert.NotNull(native.Caret);
        native.Key(Key.Tab);
        Assert.Same(ownerEditor, host.Focus.Focused);
        Layout(root, host, manager);
        Assert.True(native.TextInputEnabled);
        Assert.True(manager.OwnsEditor(ownerEditor));
        native.Type("owner"); Assert.Equal("owner", ownerEditor.Text);
        host.Focus.Focus(editor);
        native.Press(new(10, 10));
        Assert.Same(editor, host.Input.Captured);
        native.Key(Key.Escape);
        Assert.False(popup.IsOpen);
        Assert.Same(button, host.Focus.Focused);
        Layout(root, host, manager);
        Assert.True(native.Disposed);
        Assert.Null(host.Input.Captured);
        Assert.False(editor.IsPressed);
        Assert.Equal(new[] { "surface", "window" }, windows.Log);
        Assert.False(editor.IsDisposed);
    }

    [Fact]
    public void OwnerClicksDismissAndFocusTransferWithinWindowGroupKeepsPopupOpen()
    {
        using var root = new Canvas { Width = 160, Height = 100 };
        var popup = new Popup { Content = new Button { Width = 100, Height = 30 }, IsOpen = true };
        root.Children.Add(popup);
        using var host = new HeadlessRoot(root) { AvailableSize = OwnerSize };
        var windows = new FakeWindows(); var surfaces = new FakeSurfaces();
        using var manager = new PopupWindowManager(windows, surfaces);
        Layout(root, host, manager);
        var native = Assert.Single(windows.Created);
        windows.IsOwnerFocused = false; native.IsFocused = true;
        Assert.True(manager.HandleOwnerFocusLost(host.Input));
        Layout(root, host, manager);
        Assert.True(popup.IsOpen);
        native.IsFocused = false; native.LoseFocus();
        Layout(root, host, manager);
        Assert.False(popup.IsOpen);
        Assert.True(native.Disposed);
        popup.Open(); windows.IsOwnerFocused = true;
        Layout(root, host, manager);
        host.PointerPress(new(150, 50));
        Assert.False(popup.IsOpen);
    }

    [Fact]
    public void NestedPopupUsesItsParentWindowAndClosingAncestorReleasesBothWindows()
    {
        using var root = new Canvas { Width = 160, Height = 100 };
        var panel = new StackPanel { Width = 120 };
        var target = new Button { Width = 120, Height = 30 };
        var inner = new Popup { Content = new Button { Width = 80, Height = 30 }, PlacementTarget = target, Placement = Placement.Right, IsOpen = true };
        panel.Children.Add(target); panel.Children.Add(inner);
        var outer = new Popup { Content = panel, IsOpen = true };
        root.Children.Add(outer);
        using var host = new HeadlessRoot(root) { AvailableSize = OwnerSize };
        var windows = new FakeWindows(); var surfaces = new FakeSurfaces();
        using var manager = new PopupWindowManager(windows, surfaces);
        Layout(root, host, manager);
        Assert.Equal(2, windows.Created.Count);
        Assert.Same(windows.Created[0], windows.Created[1].Parent);
        windows.Created[1].Press(new(10, 10)); windows.Created[1].Release(new(10, 10));
        Assert.True(outer.IsOpen); Assert.True(inner.IsOpen);
        outer.HostMode = PopupHostMode.Overlay; Layout(root, host, manager);
        Assert.True(windows.Created[0].Disposed); Assert.True(windows.Created[1].Disposed);
        Assert.Null(windows.Created[2].Parent);
        outer.HostMode = PopupHostMode.Window; Layout(root, host, manager);
        Assert.True(windows.Created[2].Disposed);
        Assert.Same(windows.Created[3], windows.Created[4].Parent);
        outer.Close(); Layout(root, host, manager);
        Assert.All(windows.Created, window => Assert.True(window.Disposed));
    }

    [Fact]
    public void PopupDamageTracksContentAndThemeWhileIdleFramesSkip()
    {
        using var root = new Canvas { Width = 160, Height = 100 };
        var body = new Border { Width = 100, Height = 40 };
        var popup = new Popup { Content = body, IsOpen = true };
        root.Children.Add(popup);
        using var host = new HeadlessRoot(root) { AvailableSize = OwnerSize };
        var windows = new FakeWindows(); var surfaces = new FakeSurfaces();
        using var manager = new PopupWindowManager(windows, surfaces);
        Layout(root, host, manager);
        var surface = Assert.Single(surfaces.Created);
        Layout(root, host, manager); Assert.Equal(1, surface.Frames);
        body.Background = new(80, 120, 180);
        Layout(root, host, manager); Assert.Equal(2, surface.Frames);
        Layout(root, host, manager, ElementTheme.Dark); Assert.Equal(3, surface.Frames);
        windows.Created[0].Expose(); Layout(root, host, manager, ElementTheme.Dark);
        Assert.Equal(4, surface.Frames);
        popup.HostMode = PopupHostMode.Overlay; Layout(root, host, manager);
        Assert.False(popup.IsWindowHosted);
        Assert.True(windows.Created[0].Disposed);
        Assert.True(popup.IsOpen);
        Assert.NotNull(host.HitTest(new(10, 90)));
    }

    [Fact]
    public void RemovingPopupAndDisposingManagerLeavesContentOwnedByOriginalTree()
    {
        using var root = new Canvas { Width = 160, Height = 100 };
        var body = new Border { Width = 100, Height = 40 };
        var popup = new Popup { Content = body, IsOpen = true };
        root.Children.Add(popup);
        using var host = new HeadlessRoot(root) { AvailableSize = OwnerSize };
        var windows = new FakeWindows(); var surfaces = new FakeSurfaces();
        using var manager = new PopupWindowManager(windows, surfaces);
        Layout(root, host, manager);
        root.Children.Remove(popup); Layout(root, host, manager);
        Assert.True(windows.Created[0].Disposed);
        Assert.False(popup.IsWindowHosted);
        Assert.Same(popup, body.Parent);
        Assert.False(body.IsDisposed);
        popup.Dispose();
        Assert.True(body.IsDisposed);
    }

    [Fact]
    public void NativeCloseCanReopenPopupWithoutRetainingClosedWindow()
    {
        using var root = new Canvas { Width = 160, Height = 100 };
        var popup = new Popup { Content = new Border { Width = 100, Height = 40 }, IsOpen = true };
        root.Children.Add(popup);
        using var host = new HeadlessRoot(root) { AvailableSize = OwnerSize };
        var windows = new FakeWindows(); var surfaces = new FakeSurfaces();
        using var manager = new PopupWindowManager(windows, surfaces);
        popup.AddHandler(Popup.ClosedEvent, _ => popup.Open());
        Layout(root, host, manager);
        windows.Created[0].CloseNative();
        Assert.True(popup.IsOpen);
        Layout(root, host, manager);
        Assert.Equal(2, windows.Created.Count);
        Assert.True(windows.Created[0].Disposed);
        Assert.False(windows.Created[1].IsClosed);
        Assert.Equal(1, surfaces.Created[1].Frames);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ComboBoxNativePopupHasTransparentCornersAndUnclippedShadow(bool dark)
    {
        var theme = dark ? FluentTheme.Dark : FluentTheme.Light;
        using var root = new Canvas { Width = 160, Height = 100 };
        var combo = new ComboBox { Width = 120, Height = 32, ItemsSource = new[] { "Alpha", "Beta" }, IsDropDownOpen = true };
        root.Children.Add(combo);
        new ElementPainter(theme).Prepare(root);
        using var host = new HeadlessRoot(root) { AvailableSize = OwnerSize };
        var windows = new FakeWindows(transparent: true); var surfaces = new FakeSurfaces();
        using var manager = new PopupWindowManager(windows, surfaces);
        Layout(root, host, manager, theme);
        var native = Assert.Single(windows.Created);
        var popup = Assert.Single(combo.Descendants().OfType<Popup>());
        var content = native.Shape;
        Assert.True(content.X > 0 && content.Y > 0);
        Assert.True(native.Bounds.Width > content.Width && native.Bounds.Height > content.Height);
        Assert.Equal(8, native.ShapeRadius);
        var pixels = Render.Frame(Assert.Single(surfaces.Created).Last!, native.ClientWidth, native.ClientHeight);
        Assert.Equal(0, pixels.GetPixel(0, 0).A);
        Assert.InRange(pixels.GetPixel((int)(content.X * 2), (int)(content.Y * 2)).A, 0, 127);
        Assert.Equal(255, pixels.GetPixel((int)((content.X + content.Width / 2) * 2), (int)((content.Y + 2) * 2)).A);
        Assert.InRange(pixels.GetPixel((int)((content.X + content.Width / 2) * 2), (int)((content.Bottom + 2) * 2)).A, 1, 254);
        Assert.False(popup.ContainsRootPoint(new(native.Bounds.X + content.X + .01, native.Bounds.Y + content.Y + .01)));
        Assert.True(popup.ContainsRootPoint(new(native.Bounds.X + content.X + content.Width / 2, native.Bounds.Y + content.Y + 2)));
        Visual.SetCornerRadius(popup, 12);
        Layout(root, host, manager, theme);
        Assert.Equal(12, native.ShapeRadius);
        Assert.Single(windows.Created);
    }

    [Fact]
    public void SoftwarePopupCoverageRetainsPremultipliedAlphaAndClearsPreviousPixels()
    {
        using var backend = new BubuUI.Rendering.Vello.VelloSparseBackend();
        var target = new RenderTarget(4, 4);
        backend.Render(new DisplayList(new(4, 4)).Add(new FillRect(new(0, 0, 4, 4), new Color(200, 80, 40, 128))), target);
        Render.AssertColor(new(100, 40, 20, 128), target.GetPixel(1, 1), tolerance: 1);
        backend.Render(new DisplayList(new(4, 4)), target);
        Assert.Equal(0, target.GetPixel(1, 1).A);
    }

    [Fact]
    public void DisplayClampDropsShadowMarginsBeforeReducingComboBoxContent()
    {
        using var root = new Canvas { Width = 160, Height = 100 };
        var combo = new ComboBox { Width = 160, ItemsSource = new[] { "Alpha", "Beta" } };
        root.Children.Add(combo);
        new ElementPainter(FluentTheme.Light).Prepare(root);
        using var host = new HeadlessRoot(root) { AvailableSize = OwnerSize };
        var windows = new FakeWindows(transparent: true, clampToDisplay: true) { AvailableSize = new(160, 120) };
        using var manager = new PopupWindowManager(windows, new FakeSurfaces());
        Layout(root, host, manager, FluentTheme.Light);
        combo.IsDropDownOpen = true;
        Layout(root, host, manager, FluentTheme.Light);
        var native = Assert.Single(windows.Created);
        var popup = Assert.Single(combo.Descendants().OfType<Popup>());
        Assert.Equal(160, native.Shape.Width);
        Assert.Equal(160, popup.TemplateRoot!.FinalRect.Width);
        var first = (native.Bounds, native.Shape);
        Layout(root, host, manager, FluentTheme.Light);
        Assert.Equal(first, (native.Bounds, native.Shape));
    }

    [Fact]
    public void OpaqueDriverFallsBackToOverlayWithoutRecreatingNativeWindowEveryFrame()
    {
        using var root = new Canvas { Width = 160, Height = 100 };
        var popup = new Popup { Content = new Border { Width = 100, Height = 40 }, IsOpen = true };
        root.Children.Add(popup);
        using var host = new HeadlessRoot(root) { AvailableSize = OwnerSize };
        var windows = new FakeWindows(transparent: true);
        using var manager = new PopupWindowManager(windows, new FakeSurfaces(supportsTransparency: false));
        Layout(root, host, manager);
        Assert.False(popup.IsWindowHosted);
        Assert.True(Assert.Single(windows.Created).Disposed);
        Assert.Same(popup.Content, host.HitTest(new(10, 70)));
        Layout(root, host, manager);
        Assert.Single(windows.Created);
        Assert.True(popup.IsOpen);
    }

    [Fact]
    public void OpaquePopupSurfaceKeepsThemeBackdropBehindTransparentContent()
    {
        using var root = new Canvas { Width = 160, Height = 100 };
        root.Children.Add(new Popup { Content = new TextBlock { Width = 100, Height = 40 }, IsOpen = true });
        using var host = new HeadlessRoot(root) { AvailableSize = OwnerSize };
        var windows = new FakeWindows(); var surfaces = new FakeSurfaces();
        using var manager = new PopupWindowManager(windows, surfaces);
        Layout(root, host, manager);
        var native = Assert.Single(windows.Created);
        var pixels = Render.Frame(Assert.Single(surfaces.Created).Last!, native.ClientWidth, native.ClientHeight);
        Assert.Equal(ElementTheme.Light.Surface, pixels.GetPixel(0, 0));
    }

    private sealed class FakeWindows(bool transparent = false, bool clampToDisplay = false) : IPopupWindowFactory
    {
        public Size AvailableSize { get; init; } = new(1920, 1080);
        public bool IsOwnerFocused { get; set; } = true;
        public List<FakeWindow> Created { get; } = new();
        public List<string> Log { get; } = new();
        public IPopupWindow Create(bool interactive, IPopupWindow? parent = null)
        {
            var window = new FakeWindow(parent, Log, transparent, clampToDisplay ? AvailableSize : null);
            Created.Add(window);
            return window;
        }
    }

    #pragma warning disable CS0067
    private sealed class FakeWindow(IPopupWindow? parent, List<string> log, bool transparent = false, Size? limit = null) : IPopupWindow
    {
        public IPopupWindow? Parent => parent;
        public Rect Bounds { get; private set; }
        public NativeWindowHandle SurfaceHandle => NativeWindowHandle.FromWin32(1);
        public int ClientWidth => (int)(Bounds.Width * ScaleFactor);
        public int ClientHeight => (int)(Bounds.Height * ScaleFactor);
        public double ScaleFactor => 2;
        private bool _closed;
        public bool IsClosed => Disposed || _closed;
        public bool IsFocused { get; set; }
        public bool IsTransparent => transparent;
        public Rect Shape { get; private set; }
        public double ShapeRadius { get; private set; }
        public void SetShape(Rect contentBounds, double cornerRadius) { Shape = contentBounds; ShapeRadius = cornerRadius; }
        public bool Disposed { get; private set; }
        public bool TextInputEnabled { get; private set; }
        public Rect? Caret { get; private set; }
        public Rect SetBounds(Rect bounds)
        {
            if (limit is { } display)
            {
                var width = Math.Min(bounds.Width, display.Width); var height = Math.Min(bounds.Height, display.Height);
                bounds = new(Math.Clamp(bounds.X, 0, display.Width - width), Math.Clamp(bounds.Y, 0, display.Height - height), width, height);
            }
            Bounds = bounds; return bounds;
        }
        public void Show() { }
        public void SetTextInputEnabled(bool enabled) => TextInputEnabled = enabled;
        public void SetImeCaret(Rect caret) => Caret = caret;
        public void Dispose() { Disposed = true; log.Add("window"); }
        public void Press(Point point) => PointerPressed?.Invoke(point, KeyModifiers.None, PointerButton.Left);
        public void Release(Point point) => PointerReleased?.Invoke(point);
        public void Type(string text) => TextInput?.Invoke(text);
        public void Key(Key key) => KeyDown?.Invoke(key, KeyModifiers.None, false);
        public void LoseFocus() => FocusLost?.Invoke();
        public void Expose() => PaintRequested?.Invoke();
        public void CloseNative() { _closed = true; Closed?.Invoke(); }
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
    #pragma warning restore CS0067

    private sealed class FakeSurfaces(List<string>? log = null, bool supportsTransparency = true) : ISurfaceFactory
    {
        public List<FakeSurface> Created { get; } = new();
        public ISurfacePresenter Create(nint handle, int width, int height)
        {
            var surface = new FakeSurface(log, supportsTransparency); surface.Resize(width, height); Created.Add(surface); return surface;
        }
    }
    private sealed class FakeSurface(List<string>? log, bool supportsTransparency) : ISurfacePresenter
    {
        public string Name => "test";
        public string Description => Name;
        public RenderCapabilities Capabilities => RenderCapabilities.All;
        public int Width { get; private set; }
        public int Height { get; private set; }
        public bool IsVsync => false;
        public bool SupportsTransparency => supportsTransparency;
        public int Frames { get; private set; }
        public DisplayList? Last { get; private set; }
        public void Resize(int width, int height) { Width = width; Height = height; }
        public void Render(DisplayList list) { Last = list; Frames++; }
        public void Dispose() => log?.Add("surface");
    }
}

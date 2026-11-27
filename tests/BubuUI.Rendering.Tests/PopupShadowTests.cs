using System.Globalization;
using BubuUI.Elements;
using BubuUI.Themes.Fluent;
using Xunit;

namespace BubuUI.Rendering.Tests;

public sealed class PopupShadowTests
{
    [Theory]
    [InlineData(false, 1.0)]
    [InlineData(false, 1.5)]
    [InlineData(false, 2.0)]
    [InlineData(true, 1.0)]
    [InlineData(true, 1.5)]
    [InlineData(true, 2.0)]
    public void NativeDropdownShadowHasSmoothFalloffAndTransparentExteriorAtEveryScale(bool dark, double scale)
    {
        using var scene = new DropdownScene(dark, scale);
        scene.Layout();
        var window = Assert.Single(scene.Windows.Created);
        var surface = Assert.Single(scene.Surfaces.Created);
        var frame = surface.Last!.Value.List;
        var pixels = Render.Frame(frame, window.ClientWidth, window.ClientHeight);
        var body = window.Shape;
        var lowerMargin = window.Bounds.Height - body.Bottom;
        Assert.True(lowerMargin > body.Top, "A downward shadow needs more room below the body than above it.");
        Assert.True(body.X > 0 && body.Y > 0);
        Assert.True(window.Bounds.Width > body.Width);

        var centerX = (int)Math.Floor((body.X + body.Width / 2) * scale);
        var firstShadowRow = (int)Math.Ceiling(body.Bottom * scale);
        var profile = Enumerable.Range(firstShadowRow, pixels.Height - firstShadowRow)
            .Select(y => pixels.GetPixel(centerX, y).A).ToArray();
        Assert.True(profile[0] > 2, $"Expected a visible shadow at the body edge: [{string.Join(",", profile)}].");
        Assert.True(profile.Distinct().Count() >= 6,
            $"Expected gradual coverage rather than stacked flat bands: [{string.Join(",", profile)}].");
        for (var i = 1; i < profile.Length; i++)
            Assert.True(profile[i] <= profile[i - 1] + 1,
                $"Shadow coverage should decrease away from the body: [{string.Join(",", profile)}].");
        Assert.InRange(profile[^1], 0, 1);
        Assert.True(profile[profile.Length / 2] < profile[0]);

        // The native allocation must include the entire visible fringe. Cutting a
        // nonzero alpha row or column at the window edge creates a rectangular halo.
        Assert.All(Enumerable.Range(0, pixels.Width), x =>
        {
            Assert.InRange(pixels.GetPixel(x, 0).A, 0, 1);
            Assert.InRange(pixels.GetPixel(x, pixels.Height - 1).A, 0, 1);
        });
        Assert.All(Enumerable.Range(0, pixels.Height), y =>
        {
            Assert.InRange(pixels.GetPixel(0, y).A, 0, 1);
            Assert.InRange(pixels.GetPixel(pixels.Width - 1, y).A, 0, 1);
        });
        Assert.Equal(255, pixels.GetPixel(centerX, (int)Math.Floor((body.Y + body.Height / 2) * scale)).A);
        Assert.False(scene.Popup.ContainsRootPoint(new(window.Bounds.X + body.X + body.Width / 2,
            window.Bounds.Y + body.Bottom + 2)));
        ExportPreview(scene, window, frame);
        // ComboBox creates its popup template during the initial measure. Let the
        // owner's next layout settle that new native constraint before checking idle.
        scene.Layout();
        var settledPresents = surface.Presents;
        scene.Layout();
        Assert.Equal(settledPresents, surface.Presents);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OpaqueNativePopupKeepsContentSizedWindowForItsNativeOutline(bool dark)
    {
        using var scene = new DropdownScene(dark, 2, transparent: false);
        scene.Layout();
        var window = Assert.Single(scene.Windows.Created);
        Assert.Equal(Point.Zero, window.Shape.Location);
        Assert.Equal(window.Bounds.Size, window.Shape.Size);
        var pixels = Render.Frame(Assert.Single(scene.Surfaces.Created).Last!.Value.List,
            window.ClientWidth, window.ClientHeight);
        Assert.Equal(255, pixels.GetPixel(pixels.Width / 2, pixels.Height / 2).A);
        Assert.Equal(255, pixels.GetPixel(pixels.Width / 2, pixels.Height - 1).A);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClosingRetainedNativePopupDamagesAndClearsItsEntireShadow(bool dark)
    {
        using var scene = new DropdownScene(dark, 1.5);
        scene.Layout();
        var window = Assert.Single(scene.Windows.Created);
        var painter = new ElementPainter(scene.Theme);
        var first = painter.BuildPopupFrame(scene.Popup, window.Bounds, window.ScaleFactor);
        var shown = Render.Frame(first.List, window.ClientWidth, window.ClientHeight);
        Assert.True(painter.BuildPopupFrame(scene.Popup, window.Bounds, window.ScaleFactor).Damage.IsEmpty);
        scene.Popup.Close();
        var closed = painter.BuildPopupFrame(scene.Popup, window.Bounds, window.ScaleFactor);
        var cleared = Render.Frame(closed.List, window.ClientWidth, window.ClientHeight);
        Assert.False(closed.Damage.IsEmpty);
        Assert.Equal(Size.Empty, cleared.OpaqueBounds().Size);
        AssertDamageCoversChanges(shown, cleared, closed.Damage, window.ScaleFactor);
        scene.Layout();
        Assert.True(window.Disposed);
        Assert.True(Assert.Single(scene.Surfaces.Created).Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OverlayShadowEscapesBodyClipAndItsFringeIsDamagedWhenClosed(bool dark)
    {
        var theme = dark ? FluentTheme.Dark : FluentTheme.Light;
        using var root = new Canvas { Width = 400, Height = 260 };
        var target = new Button { Width = 80, Height = 32 };
        Canvas.SetLeft(target, 70); Canvas.SetTop(target, 50);
        var popup = new Popup
        {
            HostMode = PopupHostMode.Overlay, PlacementTarget = target, IsOpen = true,
            Content = new Border { Width = 140, Height = 60 },
        };
        root.Children.Add(target); root.Children.Add(popup);
        var painter = new ElementPainter(theme);
        painter.Prepare(root);
        using var host = new HeadlessRoot(root) { AvailableSize = new(400, 260) };
        host.UpdateLayout();
        var body = popup.TemplateRoot ?? popup.Content!;
        var zero = body.PointFromRoot(Point.Zero);
        var below = new Point(-zero.X + body.FinalRect.Width / 2, -zero.Y + body.FinalRect.Height + 4);
        var first = painter.BuildFrame(root, root.FinalRect.Size);
        var shown = Render.Frame(first.List);
        Assert.True(shown.GetPixel((int)below.X, (int)below.Y).R < theme.Surface.R);
        Assert.True(painter.BuildFrame(root, root.FinalRect.Size).Damage.IsEmpty);
        popup.Close(); host.UpdateLayout();
        var closed = painter.BuildFrame(root, root.FinalRect.Size);
        var cleared = Render.Frame(closed.List);
        Render.AssertColor(theme.Surface, cleared.GetPixel((int)below.X, (int)below.Y));
        Assert.Contains(closed.Damage.Rects, r => r.Contains(below));
        AssertDamageCoversChanges(shown, cleared, closed.Damage, 1);
    }

    private static void AssertDamageCoversChanges(RenderTarget previous, RenderTarget next, FrameDamage damage, double scale)
    {
        for (var y = 0; y < previous.Height; y++)
        for (var x = 0; x < previous.Width; x++)
            if (previous.GetPixel(x, y) != next.GetPixel(x, y))
                Assert.True(damage.IsFull || damage.Rects.Any(r => r.Contains(new((x + .5) / scale, (y + .5) / scale))),
                    $"Changed pixel ({x},{y}) lies outside the reported damage.");
    }

    private static void ExportPreview(DropdownScene scene, NativeWindow window, DisplayList popupFrame)
    {
        var folder = Environment.GetEnvironmentVariable("BUBUUI_POPUP_SHADOW_PREVIEW_DIR");
        if (string.IsNullOrEmpty(folder)) return;
        Directory.CreateDirectory(folder);
        const int padding = 24;
        var width = window.ClientWidth + padding * 2;
        var height = window.ClientHeight + padding * 2;
        var list = new DisplayList(new(width, height))
            .Add(new FillRect(new(0, 0, width, height), scene.Theme.Surface))
            .Add(new PushTransform(Transform.Translation(padding, padding)))
            .AddRange(popupFrame.Commands).Add(new PopTransform());
        var label = scene.Theme.IsLight ? "light" : "dark";
        var percent = (window.ScaleFactor * 100).ToString("0", CultureInfo.InvariantCulture);
        PngEncoder.Write(System.IO.Path.Combine(folder, $"popup-shadow-{label}-{percent}pct.png"), Render.Frame(list));
    }

    private sealed class DropdownScene : IDisposable
    {
        public Canvas Root { get; } = new() { Width = 400, Height = 260 };
        public HeadlessRoot Host { get; }
        public PopupWindowManager Manager { get; }
        public ElementTheme Theme { get; }
        public WindowFactory Windows { get; }
        public SurfaceFactory Surfaces { get; } = new();
        public Popup Popup => Assert.Single(Root.Descendants().OfType<Popup>());
        public DropdownScene(bool dark, double scale, bool transparent = true)
        {
            Theme = dark ? FluentTheme.Dark : FluentTheme.Light;
            var combo = new ComboBox { Width = 140, Height = 32, ItemsSource = new[] { "Alpha", "Beta", "Gamma" } };
            Canvas.SetLeft(combo, 70); Canvas.SetTop(combo, 50);
            Root.Children.Add(combo);
            new ElementPainter(Theme).Prepare(Root);
            Host = new HeadlessRoot(Root) { AvailableSize = new(400, 260) };
            Windows = new(scale, transparent);
            Manager = new(Windows, Surfaces);
            // Open after the owner has its real size, like a user opening the dropdown.
            Manager.PrepareLayout(Root, Host.Input);
            Host.UpdateLayout();
            combo.IsDropDownOpen = true;
        }
        public void Layout()
        {
            Manager.PrepareLayout(Root, Host.Input);
            Host.UpdateLayout();
            Manager.Render(Root, Host.Input, Theme);
        }
        public void Dispose() { Manager.Dispose(); Host.Dispose(); Root.Dispose(); }
    }

    private sealed class WindowFactory(double scale, bool transparent) : IPopupWindowFactory
    {
        public Size AvailableSize => new(1200, 800);
        public bool IsOwnerFocused => true;
        public List<NativeWindow> Created { get; } = [];
        public IPopupWindow Create(bool interactive, IPopupWindow? parent = null)
        {
            var window = new NativeWindow(scale, transparent);
            Created.Add(window);
            return window;
        }
    }

    #pragma warning disable CS0067
    private sealed class NativeWindow(double scale, bool transparent) : IPopupWindow
    {
        public NativeWindowHandle SurfaceHandle => NativeWindowHandle.FromWin32(1);
        public int ClientWidth { get; private set; }
        public int ClientHeight { get; private set; }
        public double ScaleFactor => scale;
        public bool IsTransparent => transparent;
        public bool IsClosed => Disposed;
        public bool IsFocused => false;
        public bool Disposed { get; private set; }
        public Rect Bounds { get; private set; }
        public Rect Shape { get; private set; }
        public Rect SetBounds(Rect bounds)
        {
            ClientWidth = Math.Max(1, (int)Math.Ceiling(bounds.Width * scale));
            ClientHeight = Math.Max(1, (int)Math.Ceiling(bounds.Height * scale));
            return Bounds = new(bounds.Location, new(ClientWidth / scale, ClientHeight / scale));
        }
        public void SetShape(Rect contentBounds, double cornerRadius) => Shape = contentBounds;
        public void Show() { }
        public void SetTextInputEnabled(bool enabled) { }
        public void SetImeCaret(Rect caret) { }
        public void Dispose() => Disposed = true;
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

    private sealed class SurfaceFactory : ISurfaceFactory
    {
        public List<Surface> Created { get; } = [];
        public ISurfacePresenter Create(nint handle, int width, int height)
        {
            var surface = new Surface(); surface.Resize(width, height); Created.Add(surface); return surface;
        }
    }
    private sealed class Surface : ISurfacePresenter
    {
        public string Name => "popup-shadow-test";
        public string Description => Name;
        public RenderCapabilities Capabilities => RenderCapabilities.All;
        public int Width { get; private set; }
        public int Height { get; private set; }
        public bool IsVsync => false;
        public bool SupportsTransparency => true;
        public int Presents { get; private set; }
        public PaintedFrame? Last { get; private set; }
        public bool Disposed { get; private set; }
        public void Resize(int width, int height) { Width = width; Height = height; }
        public void Render(DisplayList list)
        {
            var damage = new FrameDamage(); damage.AddFull(); Render(new PaintedFrame(list, damage));
        }
        public void Render(PaintedFrame frame, double scale = 1) { Last = frame; Presents++; }
        public void Dispose() => Disposed = true;
    }
}

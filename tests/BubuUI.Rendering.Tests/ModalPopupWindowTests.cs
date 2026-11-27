using BubuUI.Elements;
using Xunit;

namespace BubuUI.Rendering.Tests;

public sealed class ModalPopupWindowTests
{
    [Fact]
    public void ModalNativeWindowAcceptsItsInputAndBlocksTheOwner()
    {
        using var fixture = new Fixture();
        var owner = new TextBox { Width = 120, Height = 28 };
        fixture.Root.Children.Add(owner);
        var editor = new TextBox { Width = 160, Height = 32 };
        var modal = fixture.AddPopup(editor, modal: true);
        fixture.Layout();

        var native = Assert.Single(fixture.Windows.Created);
        Assert.True(native.IsModal);
        Assert.True(native.Interactive);
        Assert.True(modal.IsWindowHosted);
        Assert.Equal(1, fixture.Windows.ModalLocks);
        Assert.False(fixture.Host.Focus.Focus(owner));
        native.Press(new(10, 10));
        native.Release(new(10, 10));
        native.Type("modal 中文");

        Assert.Equal("modal 中文", editor.Text);
        Assert.Equal("", owner.Text);
        Assert.Same(editor, fixture.Host.Focus.Focused);
        Assert.True(modal.IsOpen);
    }

    [Fact]
    public void AnOrdinaryDropdownCanOpenInsideTheModalNativeWindow()
    {
        using var fixture = new Fixture();
        var panel = new StackPanel { Width = 180, Height = 120 };
        panel.Children.Add(new Button { Label = "Modal action", Height = 28 });
        var modal = fixture.AddPopup(panel, modal: true);
        var choice = new Button { Width = 100, Height = 28, Label = "Choose" };
        var dropdown = new Popup { Content = choice, Placement = Placement.Pointer, PointerPosition = new(210, 90) };
        panel.Children.Add(dropdown);
        fixture.Layout();
        dropdown.Open();
        fixture.Layout();

        Assert.Equal(2, fixture.Windows.Created.Count);
        var native = fixture.Windows.Created[1];
        Assert.Same(fixture.Windows.Created[0], native.Parent);
        Assert.False(native.IsModal);
        var chosen = 0;
        choice.AddHandler(Button.ClickEvent, _ => chosen++);
        native.Press(new(8, 8));
        native.Release(new(8, 8));
        Assert.Equal(1, chosen);
        Assert.True(modal.IsOpen);
    }

    [Fact]
    public void ChangingModalityRecreatesTheNativeWindowWithoutClosingThePopup()
    {
        using var fixture = new Fixture();
        var popup = fixture.AddPopup(new Button { Width = 100, Height = 32 }, modal: false);
        fixture.Layout();
        var ordinary = Assert.Single(fixture.Windows.Created);

        popup.IsModal = true;
        fixture.Layout();
        Assert.True(ordinary.Disposed);
        Assert.True(popup.IsOpen);
        var modal = fixture.Windows.Created[1];
        Assert.True(modal.IsModal);
        Assert.Equal(1, fixture.Windows.ModalLocks);

        popup.IsModal = false;
        fixture.Layout();
        Assert.True(modal.Disposed);
        Assert.True(popup.IsOpen);
        Assert.False(fixture.Windows.Created[2].IsModal);
        Assert.Equal(0, fixture.Windows.ModalLocks);
    }

    [Fact]
    public void NativeParentsAreCreatedFirstEvenWhenTheirModalDescendantOpenedEarlier()
    {
        using var fixture = new Fixture();
        var inner = new Popup
        {
            IsModal = true, Content = new Button { Width = 80, Height = 28 },
            Placement = Placement.Pointer, PointerPosition = new(220, 110), IsOpen = true,
        };
        var panel = new StackPanel { Width = 180, Height = 120 };
        panel.Children.Add(inner);
        var outer = fixture.AddPopup(panel, modal: true);
        fixture.Layout();

        Assert.Equal(2, fixture.Windows.Created.Count);
        Assert.Null(fixture.Windows.Created[0].Parent);
        Assert.Same(fixture.Windows.Created[0], fixture.Windows.Created[1].Parent);
        Assert.All(fixture.Windows.Created, window => Assert.True(window.IsModal));
        Assert.Equal(new[] { 1, 2 }, fixture.Surfaces.Drawn);
        Assert.Equal(2, fixture.Windows.ModalLocks);
        outer.Close();
        fixture.Layout();
        Assert.All(fixture.Windows.Created, window => Assert.True(window.Disposed));
        Assert.Equal(0, fixture.Windows.ModalLocks);
    }

    [Fact]
    public void ModalWithAnOrdinaryPopupAncestorKeepsTheLogicalCleanupHierarchy()
    {
        using var fixture = new Fixture();
        var panel = new StackPanel { Width = 180, Height = 120 };
        var ordinary = fixture.AddPopup(panel, modal: false);
        var modal = new Popup
        {
            IsModal = true, Content = new Button { Width = 90, Height = 32 },
            Placement = Placement.Pointer, PointerPosition = new(230, 110), IsOpen = true,
        };
        panel.Children.Add(modal);
        fixture.Layout();

        Assert.Equal(2, fixture.Windows.Created.Count);
        Assert.False(fixture.Windows.Created[0].IsModal);
        Assert.True(fixture.Windows.Created[1].IsModal);
        Assert.Same(fixture.Windows.Created[0], fixture.Windows.Created[1].Parent);
        ordinary.Close();
        fixture.Layout();

        Assert.All(fixture.Windows.Created, window => Assert.True(window.Disposed));
        Assert.Equal(0, fixture.Windows.ModalLocks);
        Assert.Equal(new[] { "surface:2", "window:2", "surface:1", "window:1" }, fixture.Windows.Log);
    }

    [Fact]
    public void LosingApplicationFocusKeepsTheModalButDoesNotStealFocusBack()
    {
        using var fixture = new Fixture();
        var panel = new StackPanel { Width = 180, Height = 120 };
        var ordinary = fixture.AddPopup(panel, modal: false);
        var fields = new StackPanel { Width = 160, Height = 90 };
        fields.Children.Add(new TextBox { Width = 140, Height = 28 });
        var focused = new TextBox { Width = 140, Height = 28 };
        fields.Children.Add(focused);
        var modal = new Popup
        {
            IsModal = true, Content = fields,
            Placement = Placement.Pointer, PointerPosition = new(230, 110), IsOpen = true,
        };
        panel.Children.Add(modal);
        fixture.Layout();
        var child = fixture.Windows.Created[1];
        Assert.True(fixture.Host.Focus.Focus(focused));
        child.IsFocused = true;
        child.LoseFocus();
        Assert.True(fixture.Manager.HandleOwnerFocusLost(fixture.Host.Input));
        fixture.Layout();

        Assert.True(modal.IsOpen);
        Assert.True(ordinary.IsOpen);
        Assert.Same(focused, fixture.Host.Focus.Focused);
        Assert.Equal(0, child.Activations);
        Assert.Equal(1, fixture.Windows.ModalLocks);
    }

    [Fact]
    public void FocusingTheBlockedOwnerReactivatesOnlyTheModalChild()
    {
        using var fixture = new Fixture();
        fixture.AddPopup(new Button { Width = 120, Height = 32 }, modal: true);
        fixture.Layout();
        var modal = Assert.Single(fixture.Windows.Created);
        Assert.Equal(0, modal.Activations);

        fixture.Windows.IsOwnerFocused = true;
        fixture.Layout();
        fixture.Layout();

        Assert.Equal(1, modal.Activations);
        Assert.True(modal.IsFocused);
    }

    [Fact]
    public void ModalInputIsPreservedWhenAutomaticHostingFallsBackToAnOverlay()
    {
        using var fixture = new Fixture();
        fixture.Windows.SupportsModal = false;
        var outside = new Button { Width = 80, Height = 30 };
        var clicks = 0;
        outside.AddHandler(Button.ClickEvent, _ => clicks++);
        fixture.Root.Children.Add(outside);
        var popup = fixture.AddPopup(new Button { Width = 100, Height = 32 }, modal: true);
        fixture.Layout();

        Assert.Empty(fixture.Windows.Created);
        Assert.False(popup.IsWindowHosted);
        fixture.Host.PointerPress(new(10, 10));
        fixture.Host.PointerRelease(new(10, 10));
        Assert.Equal(0, clicks);
        Assert.True(popup.IsOpen);
        popup.Close();
        fixture.Layout();
        fixture.Host.PointerPress(new(10, 10));
        fixture.Host.PointerRelease(new(10, 10));
        Assert.Equal(1, clicks);
    }

    [Fact]
    public void ForcingAnUnsupportedNativeModalFailsBeforeCreatingAnyWindow()
    {
        using var fixture = new Fixture();
        fixture.Windows.SupportsModal = false;
        var modal = fixture.AddPopup(new Button { Width = 100, Height = 32 }, modal: true);
        modal.HostMode = PopupHostMode.Window;

        Assert.Throws<NotSupportedException>(fixture.Layout);
        Assert.Empty(fixture.Windows.Created);
        Assert.Empty(fixture.Surfaces.Created);
    }

    [Fact]
    public void UnsupportedSurfaceTransparencyReleasesNativeModalityAndKeepsTheOverlayModal()
    {
        using var fixture = new Fixture();
        fixture.Surfaces.SupportsTransparency = false;
        var outside = new TextBox { Width = 100, Height = 28 };
        fixture.Root.Children.Add(outside);
        var modal = fixture.AddPopup(new TextBox { Width = 120, Height = 32 }, modal: true);
        fixture.Layout();

        Assert.True(Assert.Single(fixture.Windows.Created).Disposed);
        Assert.True(Assert.Single(fixture.Surfaces.Created).Disposed);
        Assert.Equal(0, fixture.Windows.ModalLocks);
        Assert.False(modal.IsWindowHosted);
        Assert.True(modal.IsOpen);
        Assert.False(fixture.Host.Focus.Focus(outside));
        Assert.Equal(new[] { "surface:1", "window:1" }, fixture.Windows.Log);
    }

    [Fact]
    public void ForcingNativeModalityAfterAnAutomaticFallbackDoesNotReuseThatFallback()
    {
        using var fixture = new Fixture();
        fixture.Surfaces.SupportsTransparency = false;
        var popup = fixture.AddPopup(new Button { Width = 120, Height = 32 }, modal: false);
        fixture.Layout();
        Assert.False(popup.IsWindowHosted);
        Assert.True(Assert.Single(fixture.Windows.Created).Disposed);

        popup.IsModal = true;
        popup.HostMode = PopupHostMode.Window;

        Assert.Throws<NotSupportedException>(fixture.Layout);
        Assert.Equal(2, fixture.Windows.Created.Count);
        Assert.True(fixture.Windows.Created[1].IsModal);
        Assert.All(fixture.Windows.Created, window => Assert.True(window.Disposed));
        Assert.Equal(0, fixture.Windows.ModalLocks);
    }

    [Fact]
    public void ForcedNativeModalityDoesNotSilentlyDowngradeWhenTransparencyIsUnsupported()
    {
        using var fixture = new Fixture();
        fixture.Surfaces.SupportsTransparency = false;
        var modal = fixture.AddPopup(new TextBox { Width = 120, Height = 32 }, modal: true);
        modal.HostMode = PopupHostMode.Window;

        Assert.Throws<NotSupportedException>(fixture.Layout);

        Assert.True(Assert.Single(fixture.Windows.Created).Disposed);
        Assert.True(Assert.Single(fixture.Surfaces.Created).Disposed);
        Assert.Equal(0, fixture.Windows.ModalLocks);
        Assert.True(modal.IsOpen);
    }

    [Fact]
    public void AModalHidesUnrelatedNativePopupsAndRestoresThemAfterItCloses()
    {
        using var fixture = new Fixture();
        var ordinary = fixture.AddPopup(new Button { Width = 100, Height = 32 }, modal: false);
        fixture.Layout();
        var original = Assert.Single(fixture.Windows.Created);
        var modal = fixture.AddPopup(new Button { Width = 120, Height = 32 }, modal: true);
        fixture.Layout();

        Assert.True(original.Disposed);
        Assert.True(ordinary.IsOpen);
        Assert.True(fixture.Windows.Created[1].IsModal);
        modal.Close();
        fixture.Layout();

        Assert.True(ordinary.IsOpen);
        Assert.True(fixture.Windows.Created[1].Disposed);
        Assert.False(fixture.Windows.Created[2].IsModal);
        Assert.Equal(0, fixture.Windows.ModalLocks);
    }

    [Fact]
    public void OnlyTheMostRecentlyOpenedSiblingModalReceivesInputAndRenders()
    {
        using var fixture = new Fixture();
        var firstAction = new Button { Width = 100, Height = 32 };
        var secondAction = new Button { Width = 100, Height = 32 };
        var first = fixture.AddPopup(firstAction, modal: true);
        var second = fixture.AddPopup(secondAction, modal: true);
        var firstClicks = 0;
        var secondClicks = 0;
        firstAction.AddHandler(Button.ClickEvent, _ => firstClicks++);
        secondAction.AddHandler(Button.ClickEvent, _ => secondClicks++);
        fixture.Layout();

        var top = Assert.Single(fixture.Windows.Created);
        top.Press(new(8, 8));
        top.Release(new(8, 8));
        Assert.Equal(0, firstClicks);
        Assert.Equal(1, secondClicks);
        Assert.False(fixture.Host.Focus.Focus(firstAction));
        Assert.True(first.IsOpen);
        second.Close();
        fixture.Layout();

        Assert.True(top.Disposed);
        Assert.Equal(2, fixture.Windows.Created.Count);
        fixture.Windows.Created[1].Press(new(8, 8));
        fixture.Windows.Created[1].Release(new(8, 8));
        Assert.Equal(1, firstClicks);
        Assert.True(first.IsOpen);
    }

    [Fact]
    public void RemovingAModalAndDisposingTheManagerReleaseNativeResourcesButNotItsContent()
    {
        using var fixture = new Fixture();
        var content = new Button { Width = 100, Height = 32 };
        var popup = fixture.AddPopup(content, modal: true);
        fixture.Layout();
        fixture.Root.Children.Remove(popup);
        fixture.Layout();

        Assert.True(Assert.Single(fixture.Windows.Created).Disposed);
        Assert.False(popup.IsWindowHosted);
        Assert.Same(popup, content.Parent);
        Assert.False(content.IsDisposed);
        Assert.Equal(0, fixture.Windows.ModalLocks);
        fixture.Manager.Dispose();
        popup.Dispose();
        Assert.True(content.IsDisposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedNativeCreationReleasesModalStateAndCanBeRetried(bool geometryFailure)
    {
        using var fixture = new Fixture();
        var modal = fixture.AddPopup(new Button { Width = 100, Height = 32 }, modal: true);
        fixture.Windows.BoundsFailure = geometryFailure;
        fixture.Surfaces.CreateFailure = !geometryFailure;

        Assert.Throws<InvalidOperationException>(fixture.Layout);

        Assert.True(Assert.Single(fixture.Windows.Created).Disposed);
        Assert.Equal(0, fixture.Windows.ModalLocks);
        Assert.True(modal.IsOpen);
        Assert.False(modal.Content!.IsDisposed);
        fixture.Windows.BoundsFailure = false;
        fixture.Surfaces.CreateFailure = false;
        fixture.Layout();
        Assert.True(fixture.Windows.Created[1].Shown);
        Assert.Equal(1, fixture.Windows.ModalLocks);
    }

    private sealed class Fixture : IDisposable
    {
        public Canvas Root { get; } = new() { Width = 500, Height = 300 };
        public HeadlessRoot Host { get; }
        public FakeWindows Windows { get; } = new();
        public FakeSurfaces Surfaces { get; }
        public PopupWindowManager Manager { get; }
        public Fixture()
        {
            Host = new HeadlessRoot(Root) { AvailableSize = new(500, 300) };
            Surfaces = new FakeSurfaces(Windows.Log);
            Manager = new PopupWindowManager(Windows, Surfaces);
        }
        public Popup AddPopup(Element content, bool modal)
        {
            var popup = new Popup
            {
                Content = content, IsModal = modal, Placement = Placement.Pointer,
                PointerPosition = new(180, 60), IsOpen = true,
            };
            Root.Children.Add(popup);
            return popup;
        }
        public void Layout()
        {
            Manager.PrepareLayout(Root, Host.Input);
            Host.UpdateLayout();
            Manager.Render(Root, Host.Input, ElementTheme.Light);
        }
        public void Dispose()
        {
            Manager.Dispose();
            Host.Dispose();
            Root.Dispose();
        }
    }

    private sealed class FakeWindows : IPopupWindowFactory
    {
        public bool SupportsModal { get; set; } = true;
        public bool IsOwnerFocused { get; set; }
        public bool BoundsFailure { get; set; }
        public int ModalLocks { get; private set; }
        public Size AvailableSize => new(1920, 1080);
        public List<FakeWindow> Created { get; } = [];
        public List<string> Log { get; } = [];
        public IPopupWindow Create(bool interactive, IPopupWindow? parent = null) => Create(interactive, false, parent);
        public IPopupWindow Create(bool interactive, bool modal, IPopupWindow? parent = null)
        {
            var window = new FakeWindow(this, Created.Count + 1, parent, interactive, modal);
            Created.Add(window);
            return window;
        }
        public void Lock() => ModalLocks++;
        public void Unlock() => ModalLocks--;
    }

#pragma warning disable CS0067
    private sealed class FakeWindow(FakeWindows owner, int id, IPopupWindow? parent, bool interactive, bool modal) : IPopupWindow
    {
        public IPopupWindow? Parent => parent;
        public bool Interactive => interactive;
        public bool IsModal => modal;
        public bool IsTransparent => true;
        public bool IsClosed => Disposed;
        public bool IsFocused { get; set; }
        public bool Disposed { get; private set; }
        public bool Shown { get; private set; }
        public int Activations { get; private set; }
        private Rect _bounds;
        public NativeWindowHandle SurfaceHandle => NativeWindowHandle.FromWin32(id);
        public int ClientWidth => Math.Max(1, (int)_bounds.Width);
        public int ClientHeight => Math.Max(1, (int)_bounds.Height);
        public double ScaleFactor => 1;
        public Rect SetBounds(Rect bounds)
        {
            if (owner.BoundsFailure) throw new InvalidOperationException("geometry failure");
            return _bounds = bounds;
        }
        public void Show()
        {
            if (Shown) return;
            Shown = true;
            if (IsModal) owner.Lock();
        }
        public void Activate() { Activations++; IsFocused = true; }
        public void SetTextInputEnabled(bool enabled) { }
        public void SetImeCaret(Rect caret) { }
        public void Press(Point point) => PointerPressed?.Invoke(point, KeyModifiers.None, PointerButton.Left);
        public void Release(Point point) => PointerReleased?.Invoke(point);
        public void Type(string text) => TextInput?.Invoke(text);
        public void LoseFocus() { IsFocused = false; FocusLost?.Invoke(); }
        public void Dispose()
        {
            if (Disposed) return;
            Disposed = true;
            if (Shown && IsModal) owner.Unlock();
            owner.Log.Add("window:" + id);
        }
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

    private sealed class FakeSurfaces(List<string> log) : ISurfaceFactory
    {
        public bool SupportsTransparency { get; set; } = true;
        public bool CreateFailure { get; set; }
        public List<FakeSurface> Created { get; } = [];
        public List<int> Drawn { get; } = [];
        public ISurfacePresenter Create(nint handle, int width, int height)
        {
            if (CreateFailure) throw new InvalidOperationException("surface failure");
            var surface = new FakeSurface(log, (int)handle, SupportsTransparency, () => Drawn.Add((int)handle));
            surface.Resize(width, height);
            Created.Add(surface);
            return surface;
        }
    }

    private sealed class FakeSurface(List<string> log, int id, bool transparent, Action rendered) : ISurfacePresenter
    {
        public string Name => "modal test";
        public string Description => Name;
        public RenderCapabilities Capabilities => RenderCapabilities.All;
        public int Width { get; private set; }
        public int Height { get; private set; }
        public bool IsVsync => false;
        public bool SupportsTransparency => transparent;
        public bool Disposed { get; private set; }
        public void Resize(int width, int height) { Width = width; Height = height; }
        public void Render(DisplayList list) => rendered();
        public void Dispose()
        {
            if (Disposed) return;
            Disposed = true;
            log.Add("surface:" + id);
        }
    }
}

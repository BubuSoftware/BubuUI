using BubuUI.Elements;
using BubuUI.Rendering.Vello;
using Xunit;

namespace BubuUI.Rendering.Tests;

public class FluentScrollBarStateTests
{
    private sealed class Preferences : IFluentInteractionPreferences
    {
        public FluentInteractionSettings Current { get; set; } = new(true, true);
    }

    private static (ScrollViewer Viewer, HeadlessRoot Host, ElementPainter Painter, Preferences Preferences, FluentTheme Theme) Create(bool horizontal, bool dark)
    {
        var preferences = new Preferences();
        var theme = new FluentTheme(!dark, preferences);
        var viewer = new ScrollViewer { Width = 160, Height = 160,
            HorizontalScrollBarVisibility = horizontal ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = horizontal ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto };
        viewer.Children.Add(new Border { Width = horizontal ? 1600 : 160, Height = horizontal ? 160 : 1600 });
        viewer.SetThemeDefaults(theme);
        var host = new HeadlessRoot(viewer) { AvailableSize = new(160, 160) }; host.UpdateLayout();
        var painter = new ElementPainter(theme);
        painter.Build(viewer, new(160, 160));
        return (viewer, host, painter, preferences, theme);
    }

    private static FillRoundedRect Thumb(DisplayList list, bool horizontal) => Assert.Single(list.Commands.OfType<FillRoundedRect>(),
        fill => horizontal ? fill.Rect.Height <= 6 && fill.Rect.Width >= 24 : fill.Rect.Width <= 6 && fill.Rect.Height >= 24);
    private static void Advance(ScrollViewer viewer, double milliseconds)
    {
        while (milliseconds > 0)
        {
            var step = Math.Min(16, milliseconds);
            FrameAnimations.Advance(viewer, TimeSpan.FromMilliseconds(step));
            milliseconds -= step;
        }
    }
    private static Point Center(Rect rect) => new(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void IndicatorExpandsWithoutChangingHitGeometryAndStaysVisibleDuringDrag(bool horizontal, bool dark)
    {
        var (viewer, host, painter, _, theme) = Create(horizontal, dark); using var scope = host;
        DisplayList Paint() => painter.Build(viewer, new(160, 160));
        var initial = Thumb(Paint(), horizontal);
        Assert.Equal(2, horizontal ? initial.Rect.Height : initial.Rect.Width);
        Assert.Equal(12, viewer.ScrollBarHitThickness);
        Assert.Equal(12, viewer.ScrollBarButtonLength);
        Assert.True(viewer.TryGetScrollBarGeometry(horizontal, out var geometry));
        Assert.True((horizontal ? geometry.ThumbHit.Width : geometry.ThumbHit.Height) >= 30);
        Assert.Equal(6, (horizontal ? geometry.ThumbHit.Width - geometry.Thumb.Width : geometry.ThumbHit.Height - geometry.Thumb.Height));
        Assert.Equal(157, horizontal ? initial.Rect.Bottom : initial.Rect.Right);
        var point = Center(geometry.Thumb);
        host.Input.PointerMove(point); Advance(viewer, 300);
        Assert.Equal(2, horizontal ? Thumb(Paint(), horizontal).Rect.Height : Thumb(Paint(), horizontal).Rect.Width);
        Advance(viewer, 180);
        var halfway = Thumb(Paint(), horizontal);
        Assert.InRange(horizontal ? halfway.Rect.Height : halfway.Rect.Width, 3, 5);
        Advance(viewer, 150);
        var expanded = Thumb(Paint(), horizontal);
        Assert.Equal(6, horizontal ? expanded.Rect.Height : expanded.Rect.Width);
        Assert.Equal(theme.ScrollBarThumb, Assert.IsType<SolidColorBrush>(expanded.Brush).Color);
        viewer.TryGetScrollBarGeometry(horizontal, out var hoveredGeometry); Assert.Equal(geometry, hoveredGeometry);
        host.Input.PointerPress(point); Advance(viewer, 16);
        Assert.Equal(theme.ScrollBarThumb, Assert.IsType<SolidColorBrush>(Thumb(Paint(), horizontal).Brush).Color);
        host.Input.PointerLeave(); Advance(viewer, 5000);
        Assert.Same(viewer, host.Input.Captured);
        Assert.Equal(6, horizontal ? Thumb(Paint(), horizontal).Rect.Height : Thumb(Paint(), horizontal).Rect.Width);
        host.Input.PointerMove(horizontal ? new(250, point.Y) : new(point.X, 250)); host.UpdateLayout();
        Assert.Equal(horizontal ? viewer.ScrollableWidth : viewer.ScrollableHeight, horizontal ? viewer.HorizontalOffset : viewer.VerticalOffset);
        host.Input.PointerRelease(new(-10, -10));
        Advance(viewer, 16); Advance(viewer, 2300);
        Assert.Empty(Paint().Commands.OfType<FillRoundedRect>());
        // A transparent scrollbar retains its large hit target and reveals on hover.
        viewer.TryGetScrollBarGeometry(horizontal, out var end);
        var hit = Center(end.Thumb); Assert.Same(viewer, host.HitTest(hit));
        host.Input.PointerMove(hit); Advance(viewer, 200);
        Assert.Equal(theme.ScrollBarThumb.A, Assert.IsType<SolidColorBrush>(Thumb(Paint(), horizontal).Brush).Color.A);
    }

    [Fact]
    public void SystemPreferencesChangeLiveAndReducedMotionSnapsWithoutTransitions()
    {
        var (viewer, host, painter, preferences, _) = Create(false, false); using var scope = host;
        DisplayList Paint() => painter.Build(viewer, new(160, 160));
        Advance(viewer, 16); Advance(viewer, 2300);
        Assert.Empty(Paint().Commands.OfType<FillRoundedRect>());
        preferences.Current = new(false, false); Advance(viewer, 1);
        Assert.Equal(6, Thumb(Paint(), false).Rect.Width);
        Advance(viewer, 10000); Assert.Equal(6, Thumb(Paint(), false).Rect.Width);
        preferences.Current = new(true, false); Advance(viewer, 1);
        Assert.Equal(2, Thumb(Paint(), false).Rect.Width);
        viewer.TryGetScrollBarGeometry(false, out var bar);
        host.Input.PointerMove(Center(bar.Thumb)); Advance(viewer, 1);
        Assert.Equal(6, Thumb(Paint(), false).Rect.Width);
        viewer.IsEnabled = false; Advance(viewer, 1);
        Assert.DoesNotContain(Paint().Commands.OfType<FillRoundedRect>(), fill => fill.Rect.Width <= 6 && fill.Rect.Height >= 24);
        host.Input.PointerLeave(); Advance(viewer, 2300);
        Assert.Empty(Paint().Commands.OfType<FillRoundedRect>());
        viewer.IsEnabled = true; viewer.VerticalScrollBarVisibility = ScrollBarVisibility.Visible; host.UpdateLayout(); Advance(viewer, 1);
        Assert.Equal(6, Thumb(Paint(), false).Rect.Width);
    }

    [Fact]
    public void AnimatedThumbAndFadeClearTheirOldPixelsInRetainedFrames()
    {
        var (viewer, host, painter, _, theme) = Create(false, false); using var scope = host;
        viewer.Background = new(243, 243, 243);
        using var backend = new VelloSparseBackend();
        using var referenceBackend = new VelloSparseBackend();
        var compositor = new RetainedFrameCompositor(backend, 160, 160);
        void Check()
        {
            var frame = painter.BuildFrame(viewer, new(160, 160));
            var retained = compositor.Composite(frame.List, frame.Damage);
            var full = new RenderTarget(160, 160);
            referenceBackend.Render(new ElementPainter(theme).Build(viewer, new(160, 160)), full);
            Assert.Equal(full.ContentHash(), retained.ContentHash());
        }
        Check();
        viewer.TryGetScrollBarGeometry(false, out var bar);
        host.Input.PointerMove(Center(bar.Thumb)); Advance(viewer, 480); Check();
        Advance(viewer, 150); Check();
        host.Input.PointerLeave(); Advance(viewer, 500); Check();
        Advance(viewer, 167); Check();
        Advance(viewer, 1300); Check();
        Advance(viewer, 100); Check();
        Advance(viewer, 100); Check();
        Assert.Empty(painter.Build(viewer, new(160, 160)).Commands.OfType<FillRoundedRect>().Where(fill => fill.Rect.Width <= 16));
    }

    [Fact]
    public void ProgrammaticScrollingRevealsIndicatorAndSettledAnimationStopsDamage()
    {
        var (viewer, host, painter, _, _) = Create(false, true); using var scope = host;
        Advance(viewer, 16); Advance(viewer, 2300);
        painter.BuildFrame(viewer, new(160, 160));
        Advance(viewer, 1000);
        Assert.True(painter.BuildFrame(viewer, new(160, 160)).Damage.IsEmpty);
        viewer.ScrollToVerticalOffset(100); host.UpdateLayout(); Advance(viewer, 200);
        Assert.Equal(2, Thumb(painter.Build(viewer, new(160, 160)), false).Rect.Width);
    }
}

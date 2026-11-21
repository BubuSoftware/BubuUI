using EclipseUI.Elements;
using Xunit;

namespace EclipseUI.Headless.Tests;

public class ScrollBarInteractionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ArrowButtonsStepRepeatAndStopOutsideTheirOwnButton(bool horizontal)
    {
        var (host, viewer) = Create(horizontal); using var scope = host;
        viewer.ScrollBarButtonLength = 16; viewer.ScrollBarMinimumThumbLength = 24; host.UpdateLayout();
        viewer.TryGetScrollBarGeometry(horizontal, out var bar);
        Assert.True((horizontal ? bar.Thumb.Width : bar.Thumb.Height) >= 24);
        var rect = bar.IncreaseButton;
        var press = At(viewer, rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
        host.Input.PointerPress(press);
        Assert.Equal(16, horizontal ? viewer.HorizontalOffset : viewer.VerticalOffset);
        FrameAnimations.Advance(viewer, TimeSpan.FromMilliseconds(500));
        var offset = horizontal ? viewer.HorizontalOffset : viewer.VerticalOffset;
        Assert.True(offset > 16);
        host.Input.PointerMove(At(viewer, 40, 40)); FrameAnimations.Advance(viewer, TimeSpan.FromSeconds(1));
        Assert.Equal(offset, horizontal ? viewer.HorizontalOffset : viewer.VerticalOffset);
        host.Input.PointerRelease(press);
        FrameAnimations.Advance(viewer, TimeSpan.FromSeconds(1));
        Assert.Equal(offset, horizontal ? viewer.HorizontalOffset : viewer.VerticalOffset);
    }

    [Fact]
    public void TwoAxesLeaveAnEmptyCornerAndRespectHiddenAndDisabledModes()
    {
        var (host, viewer) = Create(); using var scope = host;
        viewer.Children[0].Width = 1200;
        viewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        viewer.ScrollBarButtonLength = 16; host.UpdateLayout();
        Assert.False(viewer.TryGetScrollBar(new(118, 118), out _, out _));
        Assert.True(viewer.TryGetScrollBarGeometry(true, out _));
        viewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden; host.UpdateLayout();
        Assert.False(viewer.TryGetScrollBarGeometry(true, out _));
        Assert.True(viewer.ScrollableWidth > 0);
        viewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled; host.UpdateLayout();
        Assert.Equal(0, viewer.ScrollableWidth);
    }

    private static (HeadlessRoot Host, ScrollViewer Viewer) Create(bool horizontal = false)
    {
        var viewer = new ScrollViewer { Width = 120, Height = 120, Padding = 8, ScrollBarInset = 4, ScrollBarThickness = 6, ScrollBarHitThickness = 14,
            HorizontalScrollBarVisibility = horizontal ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = horizontal ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto };
        viewer.Children.Add(new Border { Width = horizontal ? 1200 : 104, Height = horizontal ? 104 : 1200 });
        var root = new Border { Width = 220, Height = 200, Padding = 12, Content = viewer };
        var host = new HeadlessRoot(root); host.UpdateLayout();
        return (host, viewer);
    }
    private static Point At(ScrollViewer viewer, double x, double y)
    {
        var p = viewer.PointFromRoot(Point.Zero); return new(x - p.X, y - p.Y);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ThumbAcceptsWholeTrackWidthAndCapturesDragOutsideViewer(bool horizontal)
    {
        var (host, viewer) = Create(horizontal); using var scope = host;
        // Cross-axis position is outside the painted 6px thumb but inside its hit strip.
        var press = horizontal ? At(viewer, 8, 118) : At(viewer, 118, 8);
        Assert.Same(viewer, host.HitTest(press)); host.Input.PointerPress(press);
        Assert.Same(viewer, host.Input.Captured);
        Assert.Equal(0, horizontal ? viewer.HorizontalOffset : viewer.VerticalOffset);
        var outside = horizontal ? At(viewer, 250, 118) : At(viewer, 118, 250);
        host.Input.PointerMove(outside); host.UpdateLayout();
        Assert.Equal(horizontal ? viewer.ScrollableWidth : viewer.ScrollableHeight, horizontal ? viewer.HorizontalOffset : viewer.VerticalOffset);
        host.Input.PointerRelease(outside);
        Assert.Null(host.Input.Captured); Assert.False(viewer.IsPressed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HoldingTrackRepeatsAfterDelayAndStopsAtPointerAndOnRelease(bool horizontal)
    {
        var (host, viewer) = Create(horizontal); using var scope = host;
        var press = horizontal ? At(viewer, 95, 118) : At(viewer, 118, 95);
        host.Input.PointerPress(press); var first = horizontal ? viewer.HorizontalOffset : viewer.VerticalOffset;
        Assert.InRange(first, 1, 200);
        FrameAnimations.Advance(host.Content, TimeSpan.FromMilliseconds(300));
        Assert.Equal(first, horizontal ? viewer.HorizontalOffset : viewer.VerticalOffset);
        FrameAnimations.Advance(host.Content, TimeSpan.FromMilliseconds(200));
        Assert.True((horizontal ? viewer.HorizontalOffset : viewer.VerticalOffset) > first);
        for (var i = 0; i < 50; i++) FrameAnimations.Advance(host.Content, TimeSpan.FromMilliseconds(100));
        var stopped = horizontal ? viewer.HorizontalOffset : viewer.VerticalOffset;
        Assert.True(stopped < (horizontal ? viewer.ScrollableWidth : viewer.ScrollableHeight));
        FrameAnimations.Advance(host.Content, TimeSpan.FromSeconds(1));
        Assert.Equal(stopped, horizontal ? viewer.HorizontalOffset : viewer.VerticalOffset);
        host.Input.PointerRelease(press); FrameAnimations.Advance(host.Content, TimeSpan.FromSeconds(1));
        Assert.Equal(stopped, horizontal ? viewer.HorizontalOffset : viewer.VerticalOffset);
    }

    [Fact]
    public void HoldingTrackPausesOutsideAndResumesWhenPointerReturns()
    {
        var (host, viewer) = Create(); using var scope = host;
        var press = At(viewer, 118, 100); host.Input.PointerPress(press); var first = viewer.VerticalOffset;
        host.Input.PointerMove(At(viewer, 50, 100)); FrameAnimations.Advance(host.Content, TimeSpan.FromSeconds(1));
        Assert.Equal(first, viewer.VerticalOffset);
        host.Input.PointerMove(press); FrameAnimations.Advance(host.Content, TimeSpan.FromMilliseconds(100));
        Assert.True(viewer.VerticalOffset > first);
        host.Input.PointerRelease(press); var released = viewer.VerticalOffset;
        FrameAnimations.Advance(host.Content, TimeSpan.FromSeconds(1)); Assert.Equal(released, viewer.VerticalOffset);
    }

    [Theory]
    [InlineData("capture")]
    [InlineData("disabled")]
    [InlineData("hidden")]
    [InlineData("bar-hidden")]
    [InlineData("detach")]
    [InlineData("dispose")]
    public void InterruptedHoldCannotContinueAfterBecomingInteractiveAgain(string interruption)
    {
        var (host, viewer) = Create(); using var scope = host;
        var parent = (Border)viewer.Parent!;
        var press = At(viewer, 118, 100); host.Input.PointerPress(press);
        switch (interruption)
        {
            case "capture": host.Input.CancelPointerCapture(); break;
            case "disabled": viewer.IsEnabled = false; break;
            case "hidden": viewer.IsVisible = false; break;
            case "bar-hidden": viewer.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden; break;
            case "detach": parent.Content = null; break;
            case "dispose": viewer.Dispose(); break;
        }
        FrameAnimations.Advance(host.Content, TimeSpan.FromSeconds(1)); host.Input.PointerMove(press);
        var stopped = viewer.VerticalOffset;
        if (!viewer.IsDisposed)
        {
            viewer.IsEnabled = true; viewer.IsVisible = true; viewer.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            if (viewer.Parent is null) parent.Content = viewer;
        }
        host.Input.PointerMove(press); FrameAnimations.Advance(host.Content, TimeSpan.FromSeconds(1));
        Assert.Equal(stopped, viewer.VerticalOffset); host.Input.PointerRelease(press);
        Assert.Null(host.Input.Captured); Assert.False(viewer.IsPressed);
    }

    [Fact]
    public void HoldingAboveThumbRepeatsTowardStartAndIgnoresRightButton()
    {
        var (host, viewer) = Create(); using var scope = host; viewer.ScrollToEnd(); host.UpdateLayout();
        var press = At(viewer, 118, 20); var end = viewer.VerticalOffset;
        host.Input.PointerPress(press, button: PointerButton.Right); FrameAnimations.Advance(host.Content, TimeSpan.FromSeconds(1));
        Assert.Equal(end, viewer.VerticalOffset); host.Input.PointerRelease(press);
        host.Input.PointerPress(press); var first = viewer.VerticalOffset;
        FrameAnimations.Advance(host.Content, TimeSpan.FromSeconds(1)); Assert.True(viewer.VerticalOffset < first);
        for (var i = 0; i < 50; i++) FrameAnimations.Advance(host.Content, TimeSpan.FromMilliseconds(100));
        var stopped = viewer.VerticalOffset; Assert.True(stopped > 0);
        FrameAnimations.Advance(host.Content, TimeSpan.FromSeconds(1)); Assert.Equal(stopped, viewer.VerticalOffset);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ScrollBarReleaseDoesNotClickContainingButton(bool overflow)
    {
        var viewer = new ScrollViewer { Width = 120, Height = 120, ScrollBarInset = 4, ScrollBarThickness = 6, ScrollBarHitThickness = 14, VerticalScrollBarVisibility = ScrollBarVisibility.Visible };
        viewer.Children.Add(new Border { Height = overflow ? 1200 : 80 });
        var button = new Button { Content = viewer }; var clicks = 0; button.AddHandler(Button.ClickEvent, _ => clicks++);
        using var host = new HeadlessRoot(button); host.UpdateLayout(); var press = At(viewer, 118, 8);
        host.Input.PointerPress(press); FrameAnimations.Advance(host.Content, TimeSpan.FromSeconds(1));
        host.Input.PointerMove(At(viewer, 118, 50)); host.Input.PointerRelease(At(viewer, 118, 50));
        Assert.Equal(overflow, viewer.VerticalOffset > 0); Assert.Equal(0, clicks); Assert.False(button.IsPressed);
    }
}

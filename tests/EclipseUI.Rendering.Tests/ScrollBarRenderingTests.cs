using EclipseUI.Elements;
using EclipseUI.Rendering.Vello;
using Xunit;

namespace EclipseUI.Rendering.Tests;

public class ScrollBarRenderingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PaintedThumbIsHitAndDraggedUsingTheSameGeometry(bool horizontal)
    {
        var viewer = new ScrollViewer { Width = 120, Height = 120, Padding = 8,
            HorizontalScrollBarVisibility = horizontal ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = horizontal ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto };
        viewer.Children.Add(new Border { Width = horizontal ? 1200 : 104, Height = horizontal ? 104 : 1200 });
        var root = new Border { Content = viewer, Padding = 20 };
        using var host = new HeadlessRoot(root); host.UpdateLayout();
        if (horizontal) viewer.ScrollToHorizontalOffset(100); else viewer.ScrollToVerticalOffset(100);
        host.UpdateLayout();
        var frame = new ElementPainter().Build(root, new(220, 220));
        var painted = Assert.Single(frame.Commands.OfType<FillRoundedRect>(), command => command.Brush is SolidColorBrush { Color: var c } && c == ElementTheme.Dark.ScrollBarThumb);
        var center = new Point(painted.Rect.X + painted.Rect.Width / 2, painted.Rect.Y + painted.Rect.Height / 2);
        Assert.Same(viewer, host.HitTest(center)); host.Input.PointerPress(center);
        Assert.Equal(100, horizontal ? viewer.HorizontalOffset : viewer.VerticalOffset);
        host.Input.PointerMove(horizontal ? new(center.X + 20, center.Y) : new(center.X, center.Y + 20));
        Assert.True((horizontal ? viewer.HorizontalOffset : viewer.VerticalOffset) > 100);
    }

    [Fact]
    public void DragDamageAndRetainedPixelsMatchFullRedraw()
    {
        var content = new StackPanel();
        for (var i = 0; i < 30; i++) content.Children.Add(new Border { Height = 28, Background = i % 2 == 0 ? new(200, 50, 50) : new(50, 80, 200) });
        var viewer = new ScrollViewer { Width = 180, Height = 120 }; viewer.Children.Add(content);
        var root = new Border { Content = viewer, Padding = 10 };
        var surface = new Size(240, 160); using var host = new HeadlessRoot(root) { AvailableSize = surface }; host.UpdateLayout();
        var painter = new ElementPainter();
        using var backend = new VelloCpuBackend(new VelloBackendOptions { UseCpu = true, ValidateDisplayList = true });
        var compositor = new RetainedFrameCompositor(backend, 240, 160);
        var first = painter.BuildFrame(root, surface); compositor.Composite(first.List, first.Damage);
        var origin = viewer.PointFromRoot(Point.Zero);
        host.Input.PointerPress(new(-origin.X + 178, -origin.Y + 8));
        host.Input.PointerMove(new(-origin.X + 178, -origin.Y + 60)); host.UpdateLayout();
        var frame = painter.BuildFrame(root, surface); Assert.False(frame.Damage.IsEmpty);
        var retained = compositor.Composite(frame.List, frame.Damage);
        var reference = Render.Frame(new ElementPainter().Build(root, surface));
        Assert.Equal(reference.ContentHash(), retained.ContentHash()); Assert.Empty(frame.List.Validate());
    }
}

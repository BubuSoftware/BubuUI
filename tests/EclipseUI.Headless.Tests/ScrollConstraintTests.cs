using System;
using EclipseUI.Elements;
using Xunit;

namespace EclipseUI.Headless.Tests;

public class ScrollConstraintTests
{
    [Theory]
    [InlineData(ScrollBarVisibility.Disabled, 300, 0)]
    [InlineData(ScrollBarVisibility.Auto, 800, 500)]
    [InlineData(ScrollBarVisibility.Hidden, 800, 500)]
    [InlineData(ScrollBarVisibility.Visible, 800, 500)]
    public void HorizontalMode_ControlsMeasurementAndFullExtent(ScrollBarVisibility mode, double extent, double scrollable)
    {
        var content = new Probe(new Size(800, 40));
        var viewer = new ScrollViewer { HorizontalScrollBarVisibility = mode, Padding = 10 };
        viewer.Children.Add(content);
        Layout(viewer, 320, 120);
        Assert.Equal(mode == ScrollBarVisibility.Disabled ? 300 : double.PositiveInfinity, content.Constraint.Width);
        Assert.Equal(double.PositiveInfinity, content.Constraint.Height);
        Assert.Equal(extent, viewer.ExtentWidth);
        Assert.Equal(300, viewer.ViewportWidth);
        Assert.Equal(scrollable, viewer.ScrollableWidth);
        Assert.True(viewer.DesiredSize.Width <= 320);
        viewer.HorizontalOffset = 1000;
        viewer.Arrange(new Rect(0, 0, 320, 120));
        Assert.Equal(scrollable, viewer.HorizontalOffset);
        Assert.Equal(10 - scrollable, content.FinalRect.X);
        Assert.Equal(extent, content.FinalRect.Width);
    }

    [Theory]
    [InlineData(ScrollBarVisibility.Disabled, 100, 0)]
    [InlineData(ScrollBarVisibility.Auto, 600, 500)]
    [InlineData(ScrollBarVisibility.Hidden, 600, 500)]
    [InlineData(ScrollBarVisibility.Visible, 600, 500)]
    public void VerticalMode_ControlsMeasurementAndEffectiveOffset(ScrollBarVisibility mode, double extent, double scrollable)
    {
        var content = new Probe(new Size(800, 600));
        var viewer = new ScrollViewer { VerticalScrollBarVisibility = mode };
        viewer.Children.Add(content);
        Layout(viewer, 300, 100);
        Assert.Equal(300, content.Constraint.Width);
        Assert.Equal(mode == ScrollBarVisibility.Disabled ? 100 : double.PositiveInfinity, content.Constraint.Height);
        Assert.Equal(extent, viewer.ExtentHeight);
        Assert.Equal(scrollable, viewer.ScrollableHeight);
        viewer.VerticalOffset = 1000;
        viewer.Arrange(new Rect(0, 0, 300, 100));
        Assert.Equal(scrollable, viewer.VerticalOffset);
        Assert.Equal(-scrollable, content.FinalRect.Y);
    }

    [Fact]
    public void HorizontalAuto_UnboundsWrapping_AndModeChangeRemeasures()
    {
        var content = new TextBlock { TextWrapping = TextWrapping.Wrap };
        content.AddRun(new string('a', 100));
        var viewer = new ScrollViewer();
        viewer.Children.Add(content);
        Layout(viewer, 300, 100);
        Assert.Equal(3, content.TextLines.Count);
        Assert.Equal(0, viewer.ScrollableWidth);

        viewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        Layout(viewer, 300, 100);
        Assert.Empty(content.TextLines); // 无界宽退化自然单行。
        Assert.Equal(840, viewer.ExtentWidth, 5);
        Assert.Equal(540, viewer.ScrollableWidth, 5);
        viewer.ScrollToHorizontalOffset(500);

        viewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Layout(viewer, 300, 100);
        Assert.Equal(3, content.TextLines.Count);
        Assert.Equal(0, viewer.HorizontalOffset);
        Assert.Equal(0, content.FinalRect.X);
    }

    [Fact]
    public void ScrollRange_ShrinksAndResizesWithoutStaleOffsetsOrRepeatedMeasure()
    {
        var content = new StackPanel();
        var box = new Probe(new Size(100, 1000));
        content.Children.Add(box);
        var viewer = new ScrollViewer();
        viewer.Children.Add(content);
        Layout(viewer, 300, 100);
        viewer.ScrollToEnd();
        viewer.Arrange(new Rect(0, 0, 300, 100));
        Assert.Equal(900, viewer.VerticalOffset);
        var measures = box.Measures;
        viewer.ScrollToHome();
        viewer.Arrange(new Rect(0, 0, 300, 100));
        Assert.Equal(measures, box.Measures);

        viewer.ScrollToEnd();
        box.Height = 200;
        Layout(viewer, 300, 100);
        Assert.Equal(200, viewer.ExtentHeight);
        Assert.Equal(100, viewer.VerticalOffset);
        Assert.Equal(-100, content.FinalRect.Y);

        Layout(viewer, 300, 300);
        Assert.Equal(0, viewer.VerticalOffset);
        Assert.Equal(0, content.FinalRect.Y);
        measures = box.Measures;
        Layout(viewer, 300, 300);
        Assert.Equal(measures, box.Measures);
    }

    [Fact]
    public void FinalViewportWidth_RemeasuresWrappingAndExtent()
    {
        var content = new TextBlock { TextWrapping = TextWrapping.Wrap };
        content.AddRun(new string('a', 100));
        var viewer = new ScrollViewer();
        viewer.Children.Add(content);
        viewer.Measure(new Size(600, 100));
        viewer.Arrange(new Rect(0, 0, 100, 100));
        Assert.Equal(10, content.TextLines.Count);
        Assert.Equal(196, viewer.ExtentHeight, 5);
        Assert.Equal(96, viewer.ScrollableHeight, 5);
        viewer.Arrange(new Rect(0, 0, 600, 100));
        Assert.Equal(2, content.TextLines.Count);
        Assert.Equal(0, viewer.ScrollableHeight);
    }

    [Fact]
    public void HorizontalAuto_UnboundsWrapPanelFlow()
    {
        var content = new WrapPanel();
        for (var i = 0; i < 4; i++) content.Children.Add(new Probe(new Size(200, 40)));
        var viewer = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
        viewer.Children.Add(content);
        Layout(viewer, 300, 100);
        Assert.Equal(800, viewer.ExtentWidth);
        Assert.Equal(40, viewer.ExtentHeight);
        Assert.All(content.Children, child => Assert.Equal(0, child.FinalRect.Y));
        viewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Layout(viewer, 300, 100);
        Assert.Equal(160, viewer.ExtentHeight);
        Assert.Equal(0, viewer.ScrollableWidth);
    }

    [Fact]
    public void UnboundedViewer_AndEmptyViewer_ProduceFiniteSizesAndNoFalseScrollRange()
    {
        foreach (var empty in new[] { false, true })
        {
            var viewer = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = 10 };
            if (!empty) viewer.Children.Add(new Probe(new Size(800, 600)));
            viewer.Measure(Size.Infinity);
            viewer.Arrange(new Rect(Point.Zero, viewer.DesiredSize));
            Assert.True(double.IsFinite(viewer.ViewportWidth));
            Assert.True(double.IsFinite(viewer.ViewportHeight));
            Assert.Equal(0, viewer.ScrollableWidth);
            Assert.Equal(0, viewer.ScrollableHeight);
        }
    }

    [Fact]
    public void ScrolledContent_CannotBeHitInsideViewportPadding()
    {
        var content = new StackPanel();
        for (var i = 0; i < 10; i++) content.Children.Add(new Button { Height = 40 });
        var viewer = new ScrollViewer { Padding = 10 };
        viewer.Children.Add(content);
        Layout(viewer, 300, 100);
        viewer.ScrollToVerticalOffset(25);
        viewer.Arrange(new Rect(0, 0, 300, 100));
        var input = new InputRouter(viewer);
        Assert.Same(viewer, input.HitTest(new Point(30, 5)));
        Assert.Same(viewer, input.HitTest(new Point(30, 95)));
        Assert.IsType<Button>(input.HitTest(new Point(30, 20)));
    }

    [Fact]
    public void VirtualizedExtent_StaysIndependentOfRealizedItems_AfterNarrowResize()
    {
        var panel = new VirtualizingStackPanel
        {
            ItemCount = 10000,
            ItemFactory = _ =>
            {
                var text = new TextBlock { TextWrapping = TextWrapping.Wrap };
                text.AddRun(new string('a', 40));
                return text;
            },
        };
        var viewer = new ScrollViewer();
        viewer.Children.Add(panel);
        Layout(viewer, 400, 100);
        Assert.Equal(196000, viewer.ExtentHeight, 4);
        viewer.ScrollToEnd();
        var offset = viewer.VerticalOffset;
        Layout(viewer, 100, 100);
        Assert.Equal(784000, viewer.ExtentHeight, 4);
        Assert.InRange(panel.RealizedCount, 1, 6);
        Assert.Equal(offset, viewer.VerticalOffset, 4); // 范围增长时保留仍有效的像素偏移。
        viewer.ScrollToEnd();
        viewer.Arrange(new Rect(0, 0, 100, 100));
        Assert.Equal(viewer.ExtentHeight - 100, viewer.VerticalOffset, 4);
    }

    private static void Layout(ScrollViewer viewer, double width, double height)
    {
        viewer.Measure(new Size(width, height));
        viewer.Arrange(new Rect(0, 0, width, height));
    }

    private sealed class Probe(Size size) : Panel
    {
        public Size Constraint { get; private set; }
        public int Measures { get; private set; }
        protected override Size MeasureCore(Size available)
        {
            Constraint = available;
            Measures++;
            return size;
        }
        protected override void ArrangeCore(Rect final) { }
    }
}

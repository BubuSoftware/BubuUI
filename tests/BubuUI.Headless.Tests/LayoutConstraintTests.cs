using System;
using System.Linq;
using BubuUI.Elements;
using Gallery;
using Xunit;

namespace BubuUI.Headless.Tests;

public class LayoutConstraintTests
{
    [Fact]
    public void NaturalContent_IsBoundedOnEveryFiniteAxis()
    {
        var box = new Probe(new Size(800, 600)) { Margin = 10 };
        box.Measure(new Size(300, 100));
        Assert.Equal(new Size(280, 80), box.Constraint);
        Assert.Equal(new Size(300, 100), box.DesiredSize);

        box.Measure(Size.Infinity);
        Assert.Equal(new Size(820, 620), box.DesiredSize);
    }

    [Theory]
    [InlineData(double.NaN, 20)]
    [InlineData(20, double.NaN)]
    [InlineData(-1, 20)]
    [InlineData(20, -1)]
    [InlineData(double.NegativeInfinity, 20)]
    public void InvalidMeasureConstraints_AreRejected(double width, double height)
    {
        var box = new Probe(Size.Empty);
        Assert.Throws<ArgumentOutOfRangeException>(() => box.Measure(new Size(width, height)));
    }

    [Theory]
    [InlineData(double.NaN, 20)]
    [InlineData(20, double.PositiveInfinity)]
    [InlineData(-1, 20)]
    public void InvalidMeasureResults_AreRejected(double width, double height)
    {
        var box = new Probe(new Size(width, height));
        Assert.Throws<InvalidOperationException>(() => box.Measure(Size.Infinity));
    }

    [Fact]
    public void Cache_TracksConstraintsAndPropertyInvalidation()
    {
        var box = new Probe(new Size(800, 40));
        box.Measure(new Size(300, 100));
        box.Measure(new Size(300, 100));
        Assert.Equal(1, box.Measures);
        box.Measure(new Size(200, 100));
        Assert.Equal(2, box.Measures);
        box.MaxWidth = 150;
        box.Measure(new Size(200, 100));
        Assert.Equal(3, box.Measures);
        Assert.Equal(150, box.Constraint.Width);
        Assert.Equal(150, box.DesiredSize.Width);
    }

    [Fact]
    public void NaturalArrangeWidth_DoesNotInvalidateTheOriginalMeasureCache()
    {
        var panel = new StackPanel();
        var box = new Probe(new Size(40, 20));
        panel.Children.Add(box);
        using var root = new HeadlessRoot(panel) { AvailableSize = new Size(300, 100) };
        root.UpdateLayout();
        var measures = box.Measures;
        root.UpdateLayout();
        Assert.Equal(measures, box.Measures);
    }

    [Fact]
    public void ExplicitSizeAndMin_CanOverflowLocally_WhileDesiredIncludesMarginOnlyOnce()
    {
        var box = new Probe(new Size(20, 10)) { Width = 800, MinHeight = 200, Margin = 10 };
        box.Measure(new Size(300, 100));
        Assert.Equal(new Size(800, 200), box.Constraint);
        Assert.Equal(new Size(300, 100), box.DesiredSize);
        box.Arrange(new Rect(0, 0, 300, 100));
        Assert.Equal(new Rect(-250, -50, 800, 200), box.FinalRect);
        Assert.Equal(new Size(300, 100), box.DesiredSize);
    }

    [Fact]
    public void Stretch_AppliesMaxAndExplicitSize_AndCentersInsideMargin()
    {
        var box = new Probe(new Size(800, 600))
        {
            Width = 500,
            MaxWidth = 120,
            MaxHeight = 40,
            Margin = 10,
        };
        box.Measure(new Size(300, 200));
        Assert.Equal(new Size(120, 40), box.Constraint);
        box.Arrange(new Rect(20, 30, 300, 200));
        Assert.Equal(new Rect(110, 110, 120, 40), box.FinalRect);
    }

    [Fact]
    public void MinWinsOverMax_DuringMeasureAndArrange()
    {
        var box = new Probe(new Size(10, 10)) { Width = 100, MinWidth = 200, MaxWidth = 50 };
        box.Measure(new Size(150, 100));
        Assert.Equal(200, box.Constraint.Width);
        Assert.Equal(150, box.DesiredSize.Width);
        box.Arrange(new Rect(0, 0, 150, 100));
        Assert.Equal(-25, box.FinalRect.X);
        Assert.Equal(200, box.FinalRect.Width);
    }

    [Fact]
    public void NonStretch_UsesConstrainedDesiredAndSmallerSlot()
    {
        var box = new Probe(new Size(800, 600))
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = 10,
        };
        box.Measure(new Size(300, 100));
        box.Arrange(new Rect(0, 0, 200, 80));
        Assert.Equal(new Rect(10, 10, 180, 60), box.FinalRect);
    }

    [Fact]
    public void NestedStacksAndTransparentBlocks_DoNotGrowForLongLabels()
    {
        var outer = new StackPanel { Padding = 12 };
        var block = new TransparentStack();
        var card = new StackPanel { Padding = 16 };
        card.Children.Add(new Button { Label = new string('x', 200) });
        card.Children.Add(Text(new string('x', 200)));
        block.Children.Add(card);
        outer.Children.Add(block);
        outer.Measure(new Size(300, 1000));
        outer.Arrange(new Rect(0, 0, 300, 1000));
        Assert.Equal(300, outer.DesiredSize.Width);
        Assert.Equal(276, block.FinalRect.Width);
        Assert.Equal(276, card.FinalRect.Width);
        Assert.All(card.Children, child => Assert.Equal(244, child.FinalRect.Width));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Wrap_UsesExplicitOrMaxWidthBeforeComputingHeight(bool explicitWidth)
    {
        var text = Text(new string('a', 20), wrap: true);
        text.Margin = 10;
        if (explicitWidth) text.Width = 50;
        else text.MaxWidth = 50;
        text.Measure(new Size(300, double.PositiveInfinity));
        Assert.Equal(4, text.TextLines.Count);
        Assert.Equal(4 * 19.6 + 20, text.DesiredSize.Height, 5);
        Assert.True(text.DesiredSize.Width <= 70);
    }

    [Fact]
    public void ArrangeAtDifferentWidth_RemeasuresTextBeforePositioningFollowingSibling()
    {
        var stack = new StackPanel();
        var text = Text(new string('a', 20), wrap: true);
        var sibling = new Probe(new Size(10, 10));
        stack.Children.Add(text);
        stack.Children.Add(sibling);
        stack.Measure(new Size(300, double.PositiveInfinity));
        Assert.Single(text.TextLines);
        stack.Arrange(new Rect(0, 0, 50, 400));
        Assert.Equal(4, text.TextLines.Count);
        Assert.Equal(4 * 19.6, sibling.FinalRect.Y, 5);
        stack.Arrange(new Rect(0, 0, 300, 400));
        Assert.Single(text.TextLines);
        Assert.Equal(19.6, sibling.FinalRect.Y, 5);
    }

    [Fact]
    public void FontAndContentChanges_InvalidateWrappingAtTheSameConstraint()
    {
        var panel = new StackPanel();
        panel.SetValue(TextBlock.FontSizeProperty, 10);
        var text = Text(new string('a', 20), wrap: true);
        panel.Children.Add(text);
        panel.Measure(new Size(100, double.PositiveInfinity));
        Assert.Equal(2, text.TextLines.Count);
        Assert.Equal(28, panel.DesiredSize.Height, 5);

        panel.SetValue(TextBlock.FontSizeProperty, 20);
        panel.Measure(new Size(100, double.PositiveInfinity));
        Assert.Equal(3, text.TextLines.Count);
        Assert.Equal(84, panel.DesiredSize.Height, 5);

        text.Runs[0].Text = "aaaa";
        panel.Measure(new Size(100, double.PositiveInfinity));
        Assert.Single(text.TextLines);
        Assert.Equal(28, panel.DesiredSize.Height, 5);
    }

    [Fact]
    public void NoWrap_PreservesNaturalRunPositionsInsideBoundedBlock()
    {
        var block = Text(new string('a', 100));
        var second = block.AddRun("bb");
        block.Measure(new Size(100, 100));
        block.Arrange(new Rect(0, 0, 100, 100));
        Assert.Equal(100, block.DesiredSize.Width);
        Assert.Equal(840, second.FinalRect.X, 5);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OverflowHitTest_UsesTheSameClipBoundaryAsPainting(bool clip)
    {
        var root = new Grid();
        var parent = new StackPanel { Width = 100, ClipToBounds = clip, HorizontalAlignment = HorizontalAlignment.Left };
        var child = new Button { Width = 200, Label = "wide", HorizontalAlignment = HorizontalAlignment.Left };
        parent.Children.Add(child);
        root.Children.Add(parent);
        root.Measure(new Size(300, 100));
        root.Arrange(new Rect(0, 0, 300, 100));
        var hit = new InputRouter(root).HitTest(new Point(150, 10));
        if (clip) Assert.Same(root, hit);
        else Assert.Same(child, hit);
    }

    [Fact]
    public void GallerySections_FollowNarrowViewport_AndRecoverOnResize()
    {
        using var window = new LegacyWindow();
        window.EnsureInitialized();
        GalleryFixtureInteractions.Wire(window);
        var root = window.Content!;
        foreach (var width in new[] { 1000d, 360d, 700d, 360d })
        {
            root.Measure(new Size(width, 760));
            root.Arrange(new Rect(0, 0, width, 760));
            var sections = root.Descendants().OfType<Section>().ToArray();
            Assert.Equal(6, sections.Length);
            Assert.All(sections, section =>
            {
                Assert.Equal(width - 48, section.FinalRect.Width, 5);
                Assert.Equal(width - 80, section.BodySlot!.FinalRect.Width, 5);
                Assert.True(section.DesiredSize.Width <= width - 48);
            });
            Assert.Equal(0, window.RootViewer.ScrollableWidth);
            Assert.True(window.RootViewer.ScrollableHeight > 0);
        }
    }

    private static TextBlock Text(string text, bool wrap = false)
    {
        var block = new TextBlock { TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap };
        block.AddRun(text);
        return block;
    }

    private sealed class TransparentStack : StackPanel
    {
        public override bool IsLayoutTransparent => true;
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

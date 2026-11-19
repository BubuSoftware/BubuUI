using EclipseUI.Elements;
using EclipseUI.Rendering;
using EclipseUI.Themes.Fluent;
using Xunit;

namespace EclipseUI.Headless.Tests;

public class AnimationAuditTests
{
    [Fact]
    public void EasingCallbackRetargetingDoesNotLetTheOldFrameFinishTheNewTransition()
    {
        using var panel = new StackPanel();
        using var host = new HeadlessRoot(panel);
        var redirected = false;
        Transitions.Set(panel, Element.CornerRadiusProperty, new TransitionSpec(TimeSpan.FromMilliseconds(100), t =>
        {
            if (!redirected && t > 0)
            {
                redirected = true;
                panel.CornerRadius = 20;
            }
            return t;
        }));
        panel.CornerRadius = 10;
        FrameAnimations.Advance(panel, TimeSpan.FromMilliseconds(100));

        Assert.Equal(20, panel.GetBaseValue(Element.CornerRadiusProperty));
        Assert.Equal(0, panel.CornerRadius);
        FrameAnimations.Advance(panel, TimeSpan.FromMilliseconds(50));
        Assert.Equal(10, panel.CornerRadius);
    }

    [Fact]
    public void ThemeDefaultChangeRetargetsTheInProgressTransition()
    {
        using var panel = new StackPanel();
        panel.SetThemeDefaults(new RadiusTheme(0));
        using var host = new HeadlessRoot(panel);
        Transitions.Set(panel, Element.CornerRadiusProperty, new TransitionSpec(TimeSpan.FromMilliseconds(100)));
        panel.SetThemeDefaults(new RadiusTheme(10));
        FrameAnimations.Advance(panel, TimeSpan.FromMilliseconds(50));
        Assert.Equal(5, panel.CornerRadius);

        panel.SetThemeDefaults(new RadiusTheme(20));
        Assert.Equal(5, panel.CornerRadius);
        FrameAnimations.Advance(panel, TimeSpan.FromMilliseconds(50));
        Assert.Equal(12.5, panel.CornerRadius);
        FrameAnimations.Advance(panel, TimeSpan.FromMilliseconds(50));
        Assert.Equal(20, panel.CornerRadius);
    }

    [Fact]
    public void TwoWayBindingKeepsSourceAtTargetWhileFramesInterpolate()
    {
        using var panel = new StackPanel();
        using var host = new HeadlessRoot(panel);
        var source = 0d;
        var writes = 0;
        using var binding = UiBinding.TwoWay(panel, Element.CornerRadiusProperty,
            () => panel.CornerRadius, value => panel.CornerRadius = value,
            () => source, value => { writes++; source = value; }, () => { });
        Transitions.Set(panel, Element.CornerRadiusProperty, new TransitionSpec(TimeSpan.FromMilliseconds(100)));

        panel.CornerRadius = 10;
        Assert.Equal(10, source);
        Assert.Equal(1, writes);
        FrameAnimations.Advance(panel, TimeSpan.FromMilliseconds(50));
        Assert.Equal(5, panel.CornerRadius);
        Assert.Equal(10, source);
        Assert.Equal(1, writes);

        source = 20;
        binding.Refresh();
        Assert.Equal(5, panel.CornerRadius);
        Assert.Equal(20, panel.GetBaseValue(Element.CornerRadiusProperty));
        FrameAnimations.Advance(panel, TimeSpan.FromMilliseconds(50));
        Assert.Equal(12.5, panel.CornerRadius);
        Assert.Equal(20, source);
        Assert.Equal(1, writes);
    }

    [Fact]
    public void RemovingLastStyleTransitionInBatchCancelsTheRunningAnimation()
    {
        using var panel = new StackPanel();
        using var host = new HeadlessRoot(panel);
        panel.SetStyleTransition(Element.CornerRadiusProperty, new TransitionSpec(TimeSpan.FromMilliseconds(100)));
        panel.CornerRadius = 10;
        FrameAnimations.Advance(panel, TimeSpan.FromMilliseconds(50));
        Assert.Equal(5, panel.CornerRadius);

        using (panel.BeginValueBatch()) panel.ClearStyleTransitions();
        Assert.Equal(10, panel.CornerRadius);
        FrameAnimations.Advance(panel, TimeSpan.FromMilliseconds(50));
        Assert.Equal(10, panel.CornerRadius);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompiledThemeStyleAnimatesThePaintedBackground(bool light)
    {
        var previous = Styling.Provider;
        var registry = new StyleRegistry();
        StyleBootstrap.Apply(registry);
        Styling.Provider = registry;
        try
        {
            using var block = new TextBlock { Classes = ["legacy-theme-anim"], Width = 100, Height = 40 };
            using var host = new HeadlessRoot(block);
            host.UpdateLayout();
            var painter = new ElementPainter(light ? FluentTheme.Light : FluentTheme.Dark);
            Color Background() => Assert.IsType<SolidColorBrush>(
                Assert.Single(painter.BuildFrame(block, new Size(200, 100)).List.Commands.OfType<FillRoundedRect>()).Brush).Color;

            Assert.Equal(new Color(light ? (byte)100 : (byte)0, 0, 0), Background());
            host.PointerMove(new Point(10, 10));
            host.AdvanceAnimations(TimeSpan.FromMilliseconds(50));
            Assert.Equal(new Color(light ? (byte)150 : (byte)40, 0, 0), Background());
        }
        finally { Styling.Provider = previous; }
    }

    [Fact]
    public void CompiledRadiusTransitionChangesPaintedGeometry()
    {
        var previous = Styling.Provider;
        var registry = new StyleRegistry();
        StyleBootstrap.Apply(registry);
        Styling.Provider = registry;
        try
        {
            using var block = new TextBlock { Classes = ["legacy-radius-anim"], Width = 100, Height = 40 };
            using var host = new HeadlessRoot(block);
            host.UpdateLayout();
            var painter = new ElementPainter(FluentTheme.Light);
            double Radius() => Assert.Single(
                painter.BuildFrame(block, new Size(200, 100)).List.Commands.OfType<FillRoundedRect>()).Radius;

            Assert.Equal(2, Radius());
            host.PointerMove(new Point(10, 10));
            host.AdvanceAnimations(TimeSpan.FromMilliseconds(50));
            Assert.Equal(6, Radius());
            host.PointerMove(new Point(150, 80));
            host.AdvanceAnimations(TimeSpan.FromMilliseconds(50));
            Assert.Equal(4, Radius());
        }
        finally { Styling.Provider = previous; }
    }

    private sealed class RadiusTheme(double value) : IElementThemeDefaults
    {
        public T GetDefault<T>(Element element, StyledProperty<T> property) =>
            ReferenceEquals(property, Element.CornerRadiusProperty) ? (T)(object)value : property.DefaultValue;
    }
}

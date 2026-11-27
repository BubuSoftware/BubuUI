using BubuUI.Elements;
using Xunit;

namespace BubuUI.Headless.Tests;

public class TransitionTests
{
    private sealed class Probe : Border
    {
        public static readonly StyledProperty<double> AmountProperty = StyledProperty<double>.Register<Probe>(
            nameof(Amount), 0, new() { Animatable = true, Invalidate = InvalidateKind.Render });
        public double Amount { get => GetValue(AmountProperty); set => SetValue(AmountProperty, value); }
    }

    private static readonly TransitionSpec Linear = new(TimeSpan.FromSeconds(1), Easings.Linear);

    private sealed class Defaults(double amount) : IElementThemeDefaults
    {
        public T GetDefault<T>(Element element, StyledProperty<T> property) =>
            ReferenceEquals(property, Probe.AmountProperty) ? (T)(object)amount : property.DefaultValue;
    }

    [Fact]
    public void TransitionPreservesBaseValueAndRedirectsFromCurrentDisplay()
    {
        var probe = new Probe(); using var host = new HeadlessRoot(probe);
        Transitions.Set(probe, Probe.AmountProperty, Linear);
        probe.Amount = 10;
        Assert.Equal(10, probe.GetBaseValue(Probe.AmountProperty));
        Assert.Equal(0, probe.Amount);
        FrameAnimations.Advance(probe, TimeSpan.FromMilliseconds(400));
        Assert.Equal(4, probe.Amount, 8);
        probe.Amount = 0;
        Assert.Equal(4, probe.Amount, 8);
        FrameAnimations.Advance(probe, TimeSpan.FromMilliseconds(500));
        Assert.Equal(2, probe.Amount, 8);
        FrameAnimations.Advance(probe, TimeSpan.FromSeconds(1));
        Assert.Equal(0, probe.Amount);
    }

    [Fact]
    public void BindingNeverWritesAnimatedValuesToItsSource()
    {
        var probe = new Probe(); using var host = new HeadlessRoot(probe);
        var source = 0d; var writes = new List<double>();
        using var binding = UiBinding.TwoWay(probe, Probe.AmountProperty, () => probe.Amount,
            value => probe.Amount = value, () => source, value => { source = value; writes.Add(value); }, () => { });
        Transitions.Set(probe, Probe.AmountProperty, Linear);
        probe.Amount = 10;
        Assert.Equal(10, source);
        FrameAnimations.Advance(probe, TimeSpan.FromMilliseconds(300));
        binding.Refresh();
        Assert.Equal(10, source);
        Assert.Equal(new[] { 10d }, writes);
        source = 5; binding.Refresh();
        Assert.Equal(3, probe.Amount, 8);
        FrameAnimations.Advance(probe, TimeSpan.FromMilliseconds(500));
        Assert.Equal(4, probe.Amount, 8);
        Assert.Single(writes);
    }

    [Fact]
    public void BindingPreservesCustomGetterConversionWithAndWithoutAnimation()
    {
        var probe = new Probe(); using var host = new HeadlessRoot(probe);
        var source = 0d; var writes = new List<double>();
        using var binding = UiBinding.TwoWay(probe, Probe.AmountProperty, () => probe.Amount * 2,
            value => probe.Amount = value / 2, () => source,
            value => { source = value; writes.Add(value); }, () => { });

        probe.Amount = 5;
        Assert.Equal(10, source);
        Transitions.Set(probe, Probe.AmountProperty, Linear);
        probe.Amount = 10;
        Assert.Equal(20, source);
        Assert.Equal(5, probe.Amount);
        FrameAnimations.Advance(probe, TimeSpan.FromMilliseconds(500));
        Assert.Equal(7.5, probe.Amount, 8);
        binding.Refresh();
        Assert.Equal(new[] { 10d, 20d }, writes);
        source = 30;
        binding.Refresh();
        Assert.Equal(15, probe.GetBaseValue(Probe.AmountProperty));
        Assert.Equal(7.5, probe.Amount, 8);
        FrameAnimations.Advance(probe, TimeSpan.FromMilliseconds(500));
        Assert.Equal(11.25, probe.Amount, 8);
    }

    [Fact]
    public void BatchIgnoresTemporaryStyleValuesAndTransitionRemovalCancels()
    {
        var probe = new Probe(); using var host = new HeadlessRoot(probe);
        probe.SetStyleValue(Probe.AmountProperty, 10);
        probe.SetStyleTransition(Probe.AmountProperty, Linear);
        using (probe.BeginValueBatch())
        {
            probe.ClearStyleTransitions();
            probe.ClearStyleValues();
            probe.SetStyleTransition(Probe.AmountProperty, Linear);
            probe.SetStyleValue(Probe.AmountProperty, 20);
        }
        Assert.Equal(10, probe.Amount);
        FrameAnimations.Advance(probe, TimeSpan.FromMilliseconds(500));
        Assert.Equal(15, probe.Amount, 8);
        using (probe.BeginValueBatch()) probe.ClearStyleTransitions();
        Assert.Equal(20, probe.Amount);
    }

    [Fact]
    public void BatchCapturesOldValueEvenWhenTransitionIsDeclaredAfterValue()
    {
        var probe = new Probe(); using var host = new HeadlessRoot(probe);
        probe.SetStyleValue(Probe.AmountProperty, 5);
        using (probe.BeginValueBatch())
        {
            probe.SetStyleValue(Probe.AmountProperty, 10);
            probe.SetStyleTransition(Probe.AmountProperty, Linear);
        }
        Assert.Equal(5, probe.Amount);
        FrameAnimations.Advance(probe, TimeSpan.FromMilliseconds(500));
        Assert.Equal(7.5, probe.Amount, 8);
    }

    [Fact]
    public void HiddenAndDetachedElementsDoNotKeepStaleTargets()
    {
        var probe = new Probe(); var host = new HeadlessRoot(probe);
        Transitions.Set(probe, Probe.AmountProperty, Linear);
        probe.Amount = 10;
        FrameAnimations.Advance(probe, TimeSpan.FromMilliseconds(400));
        probe.IsVisible = false;
        FrameAnimations.Advance(probe, TimeSpan.FromSeconds(1));
        Assert.Equal(4, probe.Amount, 8);
        probe.Amount = 5;
        Assert.Equal(5, probe.Amount);
        probe.IsVisible = true;
        probe.Amount = 10;
        FrameAnimations.Advance(probe, TimeSpan.FromMilliseconds(500));
        Assert.Equal(7.5, probe.Amount, 8);
        host.Dispose();
        Assert.Equal(10, probe.Amount);
    }

    [Fact]
    public void EasingCallbackCanReplaceAnimationWithoutOldFrameOverwritingIt()
    {
        var probe = new Probe(); using var host = new HeadlessRoot(probe);
        var replaced = false;
        Transitions.Set(probe, Probe.AmountProperty, new(TimeSpan.FromSeconds(1), t =>
        {
            if (!replaced) { replaced = true; probe.Amount = 20; }
            return t;
        }));
        probe.Amount = 10;
        FrameAnimations.Advance(probe, TimeSpan.FromMilliseconds(500));
        Assert.Equal(0, probe.Amount);
        FrameAnimations.Advance(probe, TimeSpan.FromMilliseconds(500));
        Assert.Equal(10, probe.Amount, 8);
    }

    [Fact]
    public void ThemeChangeRedirectsTheCurrentDisplayToTheNewDefault()
    {
        var probe = new Probe(); probe.SetThemeDefaults(new Defaults(0));
        using var host = new HeadlessRoot(probe);
        Transitions.Set(probe, Probe.AmountProperty, Linear);
        probe.SetThemeDefaults(new Defaults(10));
        FrameAnimations.Advance(probe, TimeSpan.FromMilliseconds(400));
        Assert.Equal(4, probe.Amount, 8);
        probe.SetThemeDefaults(new Defaults(20));
        Assert.Equal(4, probe.Amount, 8);
        FrameAnimations.Advance(probe, TimeSpan.FromMilliseconds(500));
        Assert.Equal(12, probe.Amount, 8);
    }

    [Fact]
    public void ClearingAnUnbatchedStyleTransitionRestoresBaseValue()
    {
        var probe = new Probe(); using var host = new HeadlessRoot(probe);
        probe.SetStyleTransition(Probe.AmountProperty, Linear);
        probe.Amount = 10;
        FrameAnimations.Advance(probe, TimeSpan.FromMilliseconds(400));
        Assert.Equal(4, probe.Amount, 8);
        probe.ClearStyleTransitions();
        Assert.Equal(10, probe.Amount);
    }

    [Fact]
    public void ReplacingControlThemeTransitionsDirectlyBetweenThemeValues()
    {
        var template = new FuncControlTemplate<Probe>(_ => new TemplateInstance(new Border()));
        ControlTheme Theme(double value) => new(typeof(Probe), template,
            new ThemeRule(PseudoStates.None, null, new[]
            {
                ThemeSetter.Value(Probe.AmountProperty, value),
                ThemeSetter.Transition(Probe.AmountProperty, Linear)
            }));
        var probe = new Probe { Theme = Theme(10) };
        using var host = new HeadlessRoot(probe);
        host.UpdateLayout();
        Assert.Equal(10, probe.Amount);
        probe.Theme = Theme(20);
        Assert.Equal(10, probe.Amount);
        FrameAnimations.Advance(probe, TimeSpan.FromMilliseconds(500));
        Assert.Equal(15, probe.Amount, 8);
    }

    [Fact]
    public void AdvancingSubtreeDoesNotAdvanceSiblingsOrAnotherHost()
    {
        var root = new StackPanel(); var first = new Probe(); var second = new Probe();
        root.Children.Add(first); root.Children.Add(second);
        var other = new Probe();
        using var host = new HeadlessRoot(root); using var otherHost = new HeadlessRoot(other);
        foreach (var probe in new[] { first, second, other })
        {
            Transitions.Set(probe, Probe.AmountProperty, Linear);
            probe.Amount = 10;
        }
        FrameAnimations.Advance(first, TimeSpan.FromMilliseconds(400));
        Assert.Equal(4, first.Amount, 8);
        Assert.Equal(0, second.Amount);
        Assert.Equal(0, other.Amount);
        FrameAnimations.Advance(root, TimeSpan.FromMilliseconds(500));
        Assert.Equal(9, first.Amount, 8);
        Assert.Equal(5, second.Amount, 8);
        Assert.Equal(0, other.Amount);
    }

    [Fact]
    public void RemovingAnAnimatedSubtreeReleasesItFromTheOldRoot()
    {
        var root = new StackPanel(); var child = new Probe(); root.Children.Add(child);
        using var host = new HeadlessRoot(root);
        Transitions.Set(child, Probe.AmountProperty, Linear);
        child.Amount = 10;
        FrameAnimations.Advance(root, TimeSpan.FromMilliseconds(400));
        root.Children.Remove(child);
        Assert.Equal(10, child.Amount);
        using var otherHost = new HeadlessRoot(child);
        child.Amount = 20;
        FrameAnimations.Advance(root, TimeSpan.FromSeconds(1));
        Assert.Equal(10, child.Amount);
        FrameAnimations.Advance(child, TimeSpan.FromMilliseconds(500));
        Assert.Equal(15, child.Amount, 8);
    }
}

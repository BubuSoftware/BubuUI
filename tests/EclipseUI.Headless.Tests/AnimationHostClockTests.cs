using EclipseUI.Elements;
using Xunit;

namespace EclipseUI.Headless.Tests;

public class AnimationHostClockTests
{
    private sealed class ClockProbe : Border, IFrameAnimation
    {
        public static readonly StyledProperty<double> PositionProperty = StyledProperty<double>.Register<ClockProbe>(
            nameof(Position), 0, new() { Invalidate = InvalidateKind.Render, Animatable = true });
        public double Position { get => GetValue(PositionProperty); set => SetValue(PositionProperty, value); }
        public int Ticks { get; private set; }
        public void Advance(TimeSpan elapsed) => Ticks++;
    }

    [Fact]
    public void HeadlessAdvanceDrivesBothClocksOnceWithoutLayout()
    {
        using var element = new ClockProbe();
        using var host = new HeadlessRoot(element);
        Transitions.Set(element, ClockProbe.PositionProperty, new TransitionSpec(TimeSpan.FromSeconds(1), Easings.Linear));
        element.Position = 10;

        host.AdvanceAnimations(TimeSpan.FromMilliseconds(250));

        Assert.Equal(2.5, element.Position, 8);
        Assert.Equal(1, element.Ticks);
        Assert.Equal(Size.Empty, element.FinalRect.Size);
        Assert.Throws<ArgumentOutOfRangeException>(() => host.AdvanceAnimations(TimeSpan.FromTicks(-1)));
    }

    [Fact]
    public void PausedHostLeavesLegacyClockAvailableAndRetargetsDirectly()
    {
        using var element = new ClockProbe();
        using var host = new HeadlessRoot(element);
        Transitions.Set(element, ClockProbe.PositionProperty, new TransitionSpec(TimeSpan.FromSeconds(1), Easings.Linear));
        element.Position = 10;
        host.AdvanceAnimations(TimeSpan.FromMilliseconds(250));

        FrameAnimations.SetPaused(element, true);
        FrameAnimations.Advance(element, TimeSpan.FromSeconds(4), advanceTransitions: false);
        Assert.Equal(2.5, element.Position, 8);
        Assert.Equal(2, element.Ticks);
        element.Position = 20;
        Assert.Equal(20, element.Position, 8);

        FrameAnimations.SetPaused(element, false);
        host.AdvanceAnimations(TimeSpan.FromMilliseconds(250));
        Assert.Equal(20, element.Position, 8);
    }
}

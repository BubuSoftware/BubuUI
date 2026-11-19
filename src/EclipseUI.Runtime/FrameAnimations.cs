namespace EclipseUI;

public interface IFrameAnimation
{
    void Advance(TimeSpan elapsed);
}

/// <summary>UI-thread animation clock shared by desktop hosts and deterministic headless checks.</summary>
public static class FrameAnimations
{
    public static void Advance(Element root, TimeSpan elapsed) => Advance(root, elapsed, true);

    public static void Advance(Element root, TimeSpan elapsed, bool advanceTransitions)
    {
        if (elapsed < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(elapsed));
        if (advanceTransitions) Element.AdvanceRegisteredTransitions(root, elapsed);
        AdvanceLegacy(root, elapsed);
    }
    private static void AdvanceLegacy(Element root, TimeSpan elapsed)
    {
        if (!root.IsEffectivelyVisible) return;
        if (root is IFrameAnimation animation) animation.Advance(elapsed);
        foreach (var child in root.VisualChildren.ToArray()) AdvanceLegacy(child, elapsed);
    }

    public static void SetPaused(Element root, bool paused) => root.SetAnimationPaused(paused);
    public static void SetEnabled(Element root, bool enabled) => root.AnimationsEnabled = enabled;
}

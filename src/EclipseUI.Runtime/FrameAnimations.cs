namespace EclipseUI;

public interface IFrameAnimation
{
    void Advance(TimeSpan elapsed);
}

/// <summary>UI-thread animation clock shared by desktop hosts and deterministic headless checks.</summary>
public static class FrameAnimations
{
    public static void Advance(Element root, TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(elapsed));
        if (!root.IsEffectivelyVisible) return;
        if (root is IFrameAnimation animation) animation.Advance(elapsed);
        foreach (var child in root.VisualChildren.ToArray()) Advance(child, elapsed);
    }
}

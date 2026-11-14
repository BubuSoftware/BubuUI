using EclipseUI.Elements;

namespace EclipseUI.Rendering;

/// <summary>The same resolved geometry drives commands, retained keys and damage.</summary>
internal readonly record struct FocusVisualDescriptor(
    Element? Owner, Element? Source, Rect Bounds, double Radius = 0, double Width = 0,
    double Offset = 0, Color Color = default, Rect? Clip = null, bool Control = false)
{
    public static FocusVisualDescriptor Resolve(Element root, ElementTheme theme)
    {
        var source = FocusManager.GetFor(root)?.Focused;
        if (source is null || !source.IsFocusVisible) return default;
        var owner = source.FocusVisualTarget;
        if (!IsAncestor(owner, source) || owner.IsDisposed || !owner.IsEffectivelyVisible ||
            owner.EffectiveFocusVisualMode != FocusVisualMode.Outline) return default;
        var geometry = owner.GetFocusVisualGeometry();
        var target = geometry.Target;
        if (target is null || !IsAncestor(owner, target) || target.IsDisposed || !target.IsEffectivelyVisible) return default;
        var width = Visual.GetFocusRingWidth(owner) ?? theme.FocusRingWidth;
        var offset = Visual.GetFocusRingOffset(owner) ?? theme.FocusRingOffset;
        if (!double.IsFinite(width) || width <= 0 || !double.IsFinite(offset)) return default;
        var origin = target.PointFromRoot(Point.Zero);
        var bounds = new Rect(geometry.Bounds.X - origin.X, geometry.Bounds.Y - origin.Y, geometry.Bounds.Width, geometry.Bounds.Height);
        if (bounds.Width <= 0 || bounds.Height <= 0) return default;
        Rect? clip = null;
        // The owner is painted after its own clip. Preserve any viewport clips between
        // a navigation target and its owner, without escaping ancestor clips.
        for (var node = ReferenceEquals(target, owner) ? null : target.Parent; node is not null; node = node.Parent)
        {
            if (node.GetContentClip() is { } localClip)
            {
                var p = node.PointFromRoot(Point.Zero);
                var r = new Rect(localClip.X - p.X, localClip.Y - p.Y, localClip.Width, localClip.Height);
                clip = clip is { } previous ? Intersect(previous, r) : r;
            }
            if (ReferenceEquals(node, owner)) break;
        }
        var radius = Visual.GetCornerRadius(owner) ?? Visual.GetCornerRadius(target) ?? geometry.CornerRadius ?? (target switch
        {
            Button => theme.ButtonRadius, CheckBox => theme.CheckBoxRadius,
            ListBoxItem => theme.ItemRadius, _ => theme.PanelRadius,
        });
        return new(owner, source, bounds, radius, width, offset,
            Visual.GetFocusRingColorResource(owner)?.Resolve(theme) ?? Visual.GetFocusRingColor(owner) ?? theme.FocusRing, clip);
    }

    private static bool IsAncestor(Element ancestor, Element node)
    {
        for (Element? p = node; p is not null; p = p.Parent) if (ReferenceEquals(p, ancestor)) return true;
        return false;
    }

    private static Rect Intersect(Rect a, Rect b) => new(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y),
        Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X)),
        Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Y, b.Y)));
}

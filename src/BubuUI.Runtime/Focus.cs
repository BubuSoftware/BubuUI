namespace BubuUI;

public enum FocusOrigin { Auto, Pointer, Keyboard, Programmatic, Restore }
public enum FocusVisibility { Auto, Show }
public enum FocusVisualMode { Auto, Outline, Control, None }

public sealed class FocusEventArgs : RoutedEventArgs
{
    public FocusOrigin Origin { get; }
    public KeyModifiers Modifiers { get; }
    public FocusEventArgs(RoutedEvent routedEvent, FocusOrigin origin, KeyModifiers modifiers) : base(routedEvent)
    { Origin = origin; Modifiers = modifiers; }
}

/// <summary>Bounds are local to Target, which must be the drawing owner or its descendant;
/// supplying geometry does not move keyboard focus.</summary>
public readonly record struct FocusVisualGeometry(Element Target, Rect Bounds, double? CornerRadius = null);

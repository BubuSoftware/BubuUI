using EclipseUI.Elements;
using EclipseUI.Rendering;

namespace EclipseUI.Themes.Fluent;

/// <summary>A visual element owned by a Fluent template, independent of control behavior.</summary>
public abstract class FluentVisual<T> : Element where T : Element
{
    public T Owner { get; set; } = null!;
    protected FluentTheme Palette => Owner.ThemeDefaults as FluentTheme ?? FluentTheme.Light;
    protected static UiColor ColorOf(Color color) => new(color.R, color.G, color.B, color.A);
    protected override Size MeasureCore(Size available) => Size.Empty;
    protected override void ArrangeCore(Rect final) { }
}

using BubuUI.Elements;
using BubuUI.Rendering;

namespace BubuUI.Themes.Fluent;

public class FluentListBoxItemVisual : FluentVisual<ListBoxItem>
{
    public override void Render(IElementDrawingContext context)
    {
        context.FillRectangle(new(Point.Zero, FinalRect.Size), FluentCompositeDrawing.Background(Owner, Palette),
            Visual.GetCornerRadius(Owner) ?? Owner.CornerRadius);
        if (Owner.IsSelected)
            context.FillRectangle(new(3, 6, 3, Math.Max(0, FinalRect.Height - 12)), ColorOf(Owner.IsEffectiveEnabled ? Palette.Accent : Palette.DisabledForeground), 1.5);
    }
}

public class FluentPopupVisual : FluentVisual<Popup>
{
    public FluentPopupVisual() => IsHitTestVisible = false;
    public override Rect? GetDrawingClip()
    {
        var size = Math.Max(0, Palette.PopupShadowSize);
        var extent = Math.Ceiling(size * 3);
        return new(-extent, size - extent, FinalRect.Width + extent * 2, FinalRect.Height + extent * 2);
    }
    public override void Render(IElementDrawingContext context)
    {
        var rect = new Rect(Point.Zero, FinalRect.Size);
        var size = Math.Max(0, Palette.PopupShadowSize);
        var radius = Visual.GetCornerRadius(Owner) ?? Owner.CornerRadius;
        if (size > 0)
            context.DrawShadow(rect, ColorOf(Palette.PopupShadow), size * 2, radius, new(0, size));
        context.FillRectangle(rect, FluentCompositeDrawing.Background(Owner, Palette), radius);
        context.StrokeRectangle(new(.5, .5, Math.Max(0, rect.Width - 1), Math.Max(0, rect.Height - 1)), Owner.BorderBrush, 1, radius);
    }
}

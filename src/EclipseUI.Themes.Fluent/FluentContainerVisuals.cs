using EclipseUI.Elements;
using EclipseUI.Rendering;

namespace EclipseUI.Themes.Fluent;

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

public class FluentScrollBarVisual : FluentVisual<ScrollViewer>
{
    public FluentScrollBarVisual() => IsHitTestVisible = false;
    public override void Render(IElementDrawingContext context)
    {
        foreach (var horizontal in new[] { false, true })
            if (Owner.TryGetScrollBarGeometry(horizontal, out var bar))
                context.FillRectangle(bar.Thumb, ColorOf(Palette.ScrollBarThumb), Owner.ScrollBarThickness / 2);
    }
}

public class FluentPopupVisual : FluentVisual<Popup>
{
    public FluentPopupVisual() => IsHitTestVisible = false;
    public override Rect? GetDrawingClip() => new(-Palette.PopupShadowSize * 2, -Palette.PopupShadowSize * 2,
        FinalRect.Width + Palette.PopupShadowSize * 4, FinalRect.Height + Palette.PopupShadowSize * 4);
    public override void Render(IElementDrawingContext context)
    {
        var rect = new Rect(Point.Zero, FinalRect.Size);
        var size = Palette.PopupShadowSize;
        var radius = Visual.GetCornerRadius(Owner) ?? Owner.CornerRadius;
        for (var i = 3; i >= 1 && size > 0; i--)
        {
            var spread = size * i / 3;
            var shadow = ColorOf(Palette.PopupShadow) with { A = (byte)(Palette.PopupShadow.A / 3) };
            context.FillRectangle(new(-spread, size - spread, rect.Width + spread * 2, rect.Height + spread * 2), shadow, radius + spread);
        }
        context.FillRectangle(rect, FluentCompositeDrawing.Background(Owner, Palette), radius);
        context.StrokeRectangle(new(.5, .5, Math.Max(0, rect.Width - 1), Math.Max(0, rect.Height - 1)), Owner.BorderBrush, 1, radius);
    }
}

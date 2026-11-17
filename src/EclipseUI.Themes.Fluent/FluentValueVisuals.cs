using EclipseUI.Elements;
using EclipseUI.Rendering;

namespace EclipseUI.Themes.Fluent;

internal static class FluentValueColors
{
    internal static UiColor Of(Color color) => new(color.R, color.G, color.B, color.A);
    internal static UiColor Foreground(Element owner, FluentTheme palette) =>
        Visual.GetForegroundResource(owner)?.Resolve(palette) is { } resource ? Of(resource) :
        Visual.GetForeground(owner) is { } color ? Of(color) : owner.Foreground;
    internal static UiColor Accent(Element owner, FluentTheme palette) => Of(!owner.IsEffectiveEnabled
        ? palette.AccentDisabled : owner.IsPressed ? palette.AccentPressed : owner.IsHovered ? palette.AccentHover : palette.Accent);
    internal static UiColor Alt(Element owner, FluentTheme palette) =>
        Visual.GetBackgroundResource(owner)?.Resolve(palette) is { } resource ? Of(resource) :
        Visual.GetBackground(owner) is { } color ? Of(color) : owner.Background;
    internal static UiColor StrongBorder(Element owner, FluentTheme palette) =>
        Of(owner.IsEffectiveEnabled ? palette.ControlStrongBorder : palette.ControlStrongDisabledBorder);
}

/// <summary>The Fluent selection mark. The surrounding layout and label live in the .ui template.</summary>
public class FluentCheckBoxGlyph : FluentVisual<CheckBox>
{
    protected override Size MeasureCore(Size available) => new(Owner.EffectiveGlyphSize, Owner.EffectiveGlyphSize);
    protected override void ArrangeCore(Rect final) { }
    public override void Render(IElementDrawingContext c)
    {
        var size = Math.Min(FinalRect.Width, FinalRect.Height);
        if (size <= 0) return;
        var selected = Owner.Checked || Owner.IsIndeterminate;
        var fill = selected
            ? FluentValueColors.Of(Visual.GetBackgroundResource(Owner)?.Resolve(Palette) ?? Visual.GetBackground(Owner) ??
                (!Owner.IsEffectiveEnabled ? Palette.AccentDisabled : Owner.IsPressed ? Palette.AccentPressed : Owner.IsHovered ? Palette.AccentHover : Palette.Accent))
            : FluentValueColors.Alt(Owner, Palette);
        c.FillRectangle(new(0, 0, size, size), fill, Owner.CornerRadius);
        if (!selected)
            c.StrokeRectangle(new(.5, .5, Math.Max(0, size - 1), Math.Max(0, size - 1)),
                Owner.BorderBrush, 1, Math.Max(0, Owner.CornerRadius - .5));
        var mark = FluentValueColors.Of(Owner.IsEffectiveEnabled ? Palette.OnAccent : Palette.OnAccentDisabled);
        if (Owner.IsIndeterminate)
            c.FillRectangle(new(size * .2, size * .43, size * .6, size * .14), mark);
        else if (Owner.Checked)
            c.Path(FormattableString.Invariant($"M{size * .24} {size * .52} L{size * .43} {size * .71} L{size * .77} {size * .31}"),
                UiColor.Transparent, mark, Math.Max(1.5, size * .11));
    }
}

public class FluentRadioButtonGlyph : FluentVisual<RadioButton>
{
    protected override Size MeasureCore(Size available) => new(Owner.EffectiveGlyphSize, Owner.EffectiveGlyphSize);
    protected override void ArrangeCore(Rect final) { }
    public override void Render(IElementDrawingContext c)
    {
        var size = Math.Min(FinalRect.Width, FinalRect.Height);
        if (size <= 0) return;
        var fill = Owner.Checked ? FluentValueColors.Accent(Owner, Palette) : FluentValueColors.Alt(Owner, Palette);
        var border = Owner.Checked ? fill : Owner.BorderBrush;
        c.Ellipse(new(.5, .5, Math.Max(0, size - 1), Math.Max(0, size - 1)), fill, border, 1);
        if (Owner.Checked)
        {
            var dot = size * (Owner.IsPressed ? .4 : Owner.IsHovered ? .6 : .5);
            c.Ellipse(new((size - dot) / 2, (size - dot) / 2, dot, dot),
                FluentValueColors.Of(Owner.IsEffectiveEnabled ? Palette.OnAccent : Palette.OnAccentDisabled), UiColor.Transparent);
        }
    }
}

public class FluentToggleSwitchGlyph : FluentVisual<ToggleSwitch>
{
    protected override Size MeasureCore(Size available) => new(40, 20);
    protected override void ArrangeCore(Rect final) { }
    public override void Render(IElementDrawingContext c)
    {
        var width = FinalRect.Width;
        var height = FinalRect.Height;
        var fill = Owner.Checked ? FluentValueColors.Accent(Owner, Palette) : FluentValueColors.Alt(Owner, Palette);
        c.FillRectangle(new(0, 0, width, height), fill, height / 2);
        if (!Owner.Checked)
            c.StrokeRectangle(new(.5, .5, Math.Max(0, width - 1), Math.Max(0, height - 1)),
                Owner.BorderBrush, 1, Math.Max(0, height / 2 - .5));
        var knob = Math.Max(0, height * (Owner.IsHovered || Owner.IsPressed ? .7 : .6));
        var center = Owner.Checked ? width - height / 2 : height / 2;
        c.Ellipse(new(center - knob / 2, (height - knob) / 2, knob, knob),
            Owner.Checked ? FluentValueColors.Of(Owner.IsEffectiveEnabled ? Palette.OnAccent : Palette.OnAccentDisabled)
                : FluentValueColors.StrongBorder(Owner, Palette), UiColor.Transparent);
    }
}

public class FluentSliderVisual : FluentVisual<Slider>
{
    protected override Size MeasureCore(Size available) => Owner.Orientation == Orientation.Horizontal
        ? new(120, Math.Max(32, Owner.ThumbSize)) : new(Math.Max(32, Owner.ThumbSize), 120);
    protected override void ArrangeCore(Rect final) { }
    public override void Render(IElementDrawingContext c)
    {
        var vertical = Owner.Orientation == Orientation.Vertical;
        var inset = Owner.TrackInset;
        var x = vertical ? FinalRect.Width / 2 : inset + Math.Max(0, FinalRect.Width - inset * 2) * Owner.Fraction;
        var y = vertical ? FinalRect.Height - inset - Math.Max(0, FinalRect.Height - inset * 2) * Owner.Fraction : FinalRect.Height / 2;
        var accent = FluentValueColors.Accent(Owner, Palette);
        c.FillRectangle(vertical ? new(x - 2, inset, 4, Math.Max(0, FinalRect.Height - inset * 2))
            : new(inset, y - 2, Math.Max(0, FinalRect.Width - inset * 2), 4), FluentValueColors.StrongBorder(Owner, Palette), 2);
        c.FillRectangle(vertical ? new(x - 2, y, 4, Math.Max(0, FinalRect.Height - inset - y))
            : new(inset, y - 2, Math.Max(0, x - inset), 4), accent, 2);
        c.Ellipse(new(x - Owner.ThumbSize / 2, y - Owner.ThumbSize / 2, Owner.ThumbSize, Owner.ThumbSize),
            FluentValueColors.Of(Palette.ThumbBackground), FluentValueColors.Of(Palette.ControlBorder), 1);
        var inner = Owner.ThumbSize * (Owner.IsPressed ? .36 : Owner.IsHovered ? .64 : .5);
        c.Ellipse(new(x - inner / 2, y - inner / 2, inner, inner), accent, UiColor.Transparent);
    }
}

public class FluentProgressBarVisual : FluentVisual<ProgressBar>, IFrameAnimation
{
    private double _phase;
    protected override Size MeasureCore(Size available) => new(120, 3);
    protected override void ArrangeCore(Rect final) { }
    public void Advance(TimeSpan elapsed)
    {
        if (!Owner.IsIndeterminate || !Owner.IsEffectivelyVisible) return;
        _phase = (_phase + elapsed.TotalSeconds * .7) % 1;
        InvalidateRender();
    }
    public override void Render(IElementDrawingContext c)
    {
        c.FillRectangle(new(0, 0, FinalRect.Width, FinalRect.Height), FluentValueColors.StrongBorder(Owner, Palette), FinalRect.Height / 2);
        var x = Owner.IsIndeterminate ? FinalRect.Width * .6 * (1 - Math.Abs(_phase * 2 - 1)) : 0;
        var fraction = Owner.IsIndeterminate ? .4 : Owner.Fraction;
        c.FillRectangle(new(x, 0, FinalRect.Width * fraction, FinalRect.Height), FluentValueColors.Accent(Owner, Palette), FinalRect.Height / 2);
    }
}

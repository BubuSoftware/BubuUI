using BubuUI.Elements;
using BubuUI.Rendering;

namespace BubuUI.Themes.Fluent;

/// <summary>Shared text-editor colors, including the legacy attached visual overrides.</summary>
public static class FluentTextAppearance
{
    private static FluentTheme Palette(TextBox owner) => owner.ThemeDefaults as FluentTheme ?? FluentTheme.Light;
    private static UiColor ColorOf(Color color) => new(color.R, color.G, color.B, color.A);
    public static UiColor Foreground(TextBox owner) =>
        Visual.GetForegroundResource(owner) is { } resource ? ColorOf(resource.Resolve(Palette(owner))) :
        Visual.GetForeground(owner) is { } color ? ColorOf(color) : owner.Foreground;
    public static UiColor Placeholder(TextBox owner) => ColorOf(owner.IsEffectiveEnabled ? Palette(owner).MutedForeground : Palette(owner).DisabledForeground);
    public static UiColor Selection(TextBox owner) => ColorOf(Palette(owner).Accent with { A = 100 });
    public static UiColor CompositionUnderline(TextBox owner) => ColorOf(Palette(owner).Accent);
}

/// <summary>Fluent input surface and focus baseline. Editing remains in TextBox/TextPresenter.</summary>
public class FluentTextBoxChrome : FluentVisual<TextBox>
{
    public FluentTextBoxChrome() => IsHitTestVisible = false;
    public override void Render(IElementDrawingContext context)
    {
        var bounds = new Rect(Point.Zero, FinalRect.Size);
        var background = Visual.GetBackgroundResource(Owner) is { } resource ? ColorOf(resource.Resolve(Palette)) :
            Visual.GetBackground(Owner) is { } color ? ColorOf(color) : Owner.Background;
        var radius = Visual.GetCornerRadius(Owner) ?? Owner.CornerRadius;
        context.FillRectangle(bounds, background, radius);
        context.StrokeRectangle(new(.5, .5, Math.Max(0, bounds.Width - 1), Math.Max(0, bounds.Height - 1)), Owner.BorderBrush, 1, Math.Max(0, radius - .5));
        if (!Owner.IsEffectiveEnabled) return;
        var thickness = Owner.IsFocused ? 2 : 1;
        context.PushRoundedClip(bounds, radius);
        context.FillRectangle(new(0, Math.Max(0, bounds.Height - thickness), bounds.Width, Math.Min(bounds.Height, thickness)),
            ColorOf(Owner.IsFocused ? Palette.Accent : Palette.InputBottomBorder));
        context.PopClip();
    }
}

public class FluentNumericUpDownChrome : FluentTextBoxChrome
{
    public override void Render(IElementDrawingContext context)
    {
        base.Render(context);
        if (Owner is not NumericUpDown number || number.SpinnerWidth <= 0) return;
        var foreground = FluentTextAppearance.Foreground(number);
        var x = FinalRect.Width - number.SpinnerWidth / 2;
        Chevron(context, new(x, FinalRect.Height / 4), foreground, true);
        Chevron(context, new(x, FinalRect.Height * .75), foreground, false);
    }
    private static void Chevron(IElementDrawingContext context, Point center, UiColor foreground, bool up)
    {
        var y = up ? -1.5 : 1.5;
        context.Line(new(center.X - 3, center.Y - y), new(center.X, center.Y + y), foreground, 1.5);
        context.Line(new(center.X, center.Y + y), new(center.X + 3, center.Y - y), foreground, 1.5);
    }
}

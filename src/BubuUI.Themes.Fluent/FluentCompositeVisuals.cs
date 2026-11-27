using System.Globalization;
using Calendar = BubuUI.Elements.Calendar;
using BubuUI.Elements;
using BubuUI.Rendering;

namespace BubuUI.Themes.Fluent;

internal static class FluentCompositeDrawing
{
    internal static UiColor Of(Color c) => new(c.R, c.G, c.B, c.A);
    internal static UiColor Foreground(Element owner, FluentTheme palette) => Visual.GetForegroundResource(owner)?.Resolve(palette) is { } resource ? Of(resource) : Visual.GetForeground(owner) is { } color ? Of(color) : owner.Foreground;
    internal static UiColor Accent(Element owner, FluentTheme palette) => Of(!owner.IsEffectiveEnabled ? palette.AccentDisabled : owner.IsPressed ? palette.AccentPressed : owner.IsHovered ? palette.AccentHover : palette.Accent);
    internal static UiColor Background(Element owner, FluentTheme palette) => Visual.GetBackgroundResource(owner)?.Resolve(palette) is { } resource ? Of(resource) : Visual.GetBackground(owner) is { } color ? Of(color) : owner.Background;
    internal static double CornerRadius(Element owner) => Visual.GetCornerRadius(owner) ?? owner.CornerRadius;
    internal static void Chevron(IElementDrawingContext c, Point center, UiColor color, bool up = false, bool right = false, double size = 4)
    {
        if (right)
        {
            c.Line(new(center.X - size / 2, center.Y - size), new(center.X + size / 2, center.Y), color, 1.5);
            c.Line(new(center.X + size / 2, center.Y), new(center.X - size / 2, center.Y + size), color, 1.5);
        }
        else
        {
            var y = up ? -size / 2 : size / 2;
            c.Line(new(center.X - size, center.Y - y), new(center.X, center.Y + y), color, 1.5);
            c.Line(new(center.X, center.Y + y), new(center.X + size, center.Y - y), color, 1.5);
        }
    }
    internal static void ButtonSurface(IElementDrawingContext c, Element owner, FluentTheme palette, Size size)
    {
        var radius = CornerRadius(owner);
        c.FillRectangle(new(Point.Zero, size), Background(owner, palette), radius);
        var rect = new Rect(.5, .5, Math.Max(0, size.Width - 1), Math.Max(0, size.Height - 1));
        if (owner.IsEffectiveEnabled && !owner.IsPressed)
        {
            var bottom = palette.IsLight; var point = new Point(0, bottom ? 1 : 0);
            c.StrokeRectangle(rect, new UiLinearGradient(Of(palette.ControlSecondaryBorder), owner.BorderBrush, point, point, new(0, bottom ? .5 : -.5), new(0, bottom ? -2.5 : 2.5), .33), 1, Math.Max(0, radius - .5));
        }
        else c.StrokeRectangle(rect, owner.BorderBrush, 1, Math.Max(0, radius - .5));
    }
}

public class FluentDropDownButtonVisual : FluentVisual<DropDownButton>
{
    protected override Size MeasureCore(Size available)
    {
        var text = TextMeasurer.Current.Measure(Owner.Label, Owner.GetValue(TextBlock.FontSizeProperty));
        return new(text.Width + Owner.HorizontalPadding * 2 + 16, text.Height + Owner.VerticalPadding * 2);
    }
    public override void Render(IElementDrawingContext c)
    {
        FluentCompositeDrawing.ButtonSurface(c, Owner, Palette, FinalRect.Size);
        var foreground = FluentCompositeDrawing.Foreground(Owner, Palette);
        var fs = Owner.GetValue(TextBlock.FontSizeProperty);
        var text = TextMeasurer.Current.Measure(Owner.Label, fs);
        c.PushClip(new(Owner.HorizontalPadding, 0, Math.Max(0, FinalRect.Width - Owner.HorizontalPadding * 2 - 16), FinalRect.Height));
        c.Text(Owner.Label, new(Owner.HorizontalPadding, (FinalRect.Height - text.Height) / 2), fs, foreground);
        c.PopClip();
        FluentCompositeDrawing.Chevron(c, new(FinalRect.Width - 12, FinalRect.Height / 2), foreground);
    }
}

public abstract class FluentCompositeFocusVisual<T> : FluentVisual<T> where T : Element
{
    public override void Render(IElementDrawingContext c)
    {
        if (Owner.IsFocusWithin && Owner.IsEffectiveEnabled)
            c.StrokeRectangle(new(.5, .5, Math.Max(0, FinalRect.Width - 1), Math.Max(0, FinalRect.Height - 1)), ColorOf(Palette.Accent), 1, FluentCompositeDrawing.CornerRadius(Owner));
    }
}

public class FluentComboBoxFocusVisual : FluentCompositeFocusVisual<ComboBox> { }
public class FluentDatePickerFocusVisual : FluentCompositeFocusVisual<DatePicker> { }

public class FluentDataGridColumnHeaderVisual : FluentVisual<DataGridColumnHeader>
{
    protected override Size MeasureCore(Size available)
    {
        var text = TextMeasurer.Current.Measure(Owner.Label, Owner.GetValue(TextBlock.FontSizeProperty));
        return new(text.Width + Owner.HorizontalPadding * 2 + (Owner.SortDirection == 0 ? 0 : 16), text.Height + Owner.VerticalPadding * 2);
    }
    public override void Render(IElementDrawingContext c)
    {
        FluentCompositeDrawing.ButtonSurface(c, Owner, Palette, FinalRect.Size);
        var fg = FluentCompositeDrawing.Foreground(Owner, Palette); var fs = Owner.GetValue(TextBlock.FontSizeProperty);
        var text = TextMeasurer.Current.Measure(Owner.Label, fs);
        c.PushClip(new(Owner.HorizontalPadding, 0, Math.Max(0, FinalRect.Width - Owner.HorizontalPadding * 2 - (Owner.SortDirection == 0 ? 0 : 16)), FinalRect.Height));
        c.Text(Owner.Label, new(Owner.HorizontalPadding, (FinalRect.Height - text.Height) / 2), fs, fg); c.PopClip();
        if (Owner.SortDirection != 0) FluentCompositeDrawing.Chevron(c, new(FinalRect.Width - 12, FinalRect.Height / 2), fg, up: Owner.SortDirection > 0);
    }
}

public class FluentMenuItemVisual : FluentVisual<MenuItem>
{
    public override void Render(IElementDrawingContext c)
    {
        c.FillRectangle(new(Point.Zero, FinalRect.Size), Owner.Background, FluentCompositeDrawing.CornerRadius(Owner));
        var fg = FluentCompositeDrawing.Foreground(Owner, Palette);
        if (Owner.Submenu is not null) FluentCompositeDrawing.Chevron(c, new(FinalRect.Width - 10, FinalRect.Height / 2), fg, right: true);
        c.PushClip(Owner.GetLabelBounds());
        c.Text((Owner.IsCheckable ? Owner.Checked ? "✓ " : "   " : "") + Owner.Label, new(Owner.HorizontalPadding, Owner.VerticalPadding), Owner.GetValue(TextBlock.FontSizeProperty), fg);
        c.PopClip();
    }
}

public class FluentTabControlVisual : FluentVisual<TabControl>
{
    public override void Render(IElementDrawingContext c)
    {
        double x = 0; var fs = Owner.GetValue(TextBlock.FontSizeProperty);
        for (var i = 0; i < Owner.HeaderWidths.Count; i++)
        {
            var width = Owner.HeaderWidths[i]; var selected = i == Owner.SelectedIndex;
            c.PushClip(new(x, 0, width, Owner.HeaderHeight));
            c.FillRectangle(new(x, 0, width, Owner.HeaderHeight), selected ? ColorOf(Palette.SelectionBackground) : UiColor.Transparent, FluentCompositeDrawing.CornerRadius(Owner));
            c.Text((Owner.Children[i] as TabItem)?.Header ?? "Tab", new(x + Owner.HeaderPadding, 7), fs, selected ? ColorOf(Palette.SelectionForeground) : FluentCompositeDrawing.Foreground(Owner, Palette));
            if (selected) c.FillRectangle(new(x + Owner.HeaderPadding, Owner.HeaderHeight - 3, Math.Max(0, width - Owner.HeaderPadding * 2), 3), FluentCompositeDrawing.Accent(Owner, Palette), 1.5);
            c.PopClip(); x += width;
        }
    }
}

public class FluentTreeViewItemVisual : FluentVisual<TreeViewItem>
{
    public override Rect? GetDrawingClip() => new Rect(0, 0, FinalRect.Width, Math.Min(Owner.HeaderHeight, FinalRect.Height));
    public override void Render(IElementDrawingContext c)
    {
        c.FillRectangle(new(0, 0, FinalRect.Width, Owner.HeaderHeight), FluentCompositeDrawing.Background(Owner, Palette), FluentCompositeDrawing.CornerRadius(Owner));
        var fg = Owner.IsSelected ? ColorOf(!Owner.IsEffectiveEnabled ? Palette.DisabledForeground : Palette.SelectionForeground) : FluentCompositeDrawing.Foreground(Owner, Palette);
        if (Owner.IsSelected) c.FillRectangle(new(3, 6, 3, Math.Max(0, Owner.HeaderHeight - 12)), FluentCompositeDrawing.Accent(Owner, Palette), 1.5);
        if (Owner.Children.Count > 0) FluentCompositeDrawing.Chevron(c, new(Owner.Indent / 2, Owner.HeaderHeight / 2), fg, right: !Owner.IsExpanded);
        c.Text(Owner.Header, new(Math.Max(0, Owner.HeaderPadding - 4), 4), Owner.GetValue(TextBlock.FontSizeProperty), fg);
    }
}

public class FluentCalendarVisual : FluentVisual<Calendar>
{
    protected override Size MeasureCore(Size available) => new(Owner.PreferredWidth, Owner.HeaderHeight + Owner.WeekHeaderHeight + Owner.DayHeight * 6);
    public override void Render(IElementDrawingContext c)
    {
        var fg = FluentCompositeDrawing.Foreground(Owner, Palette); var cell = FinalRect.Width / 7;
        c.FillRectangle(new(Point.Zero, FinalRect.Size), Owner.Background, FluentCompositeDrawing.CornerRadius(Owner));
        var middle = Owner.HeaderHeight / 2;
        c.Line(new(18, middle - 6), new(12, middle), fg, 1.5); c.Line(new(12, middle), new(18, middle + 6), fg, 1.5);
        FluentCompositeDrawing.Chevron(c, new(FinalRect.Width - 16, middle), fg, right: true, size: 6);
        c.PushClip(new(32, 0, Math.Max(0, FinalRect.Width - 64), Owner.HeaderHeight));
        c.Text(Owner.DisplayMonth.ToString("yyyy-MM", CultureInfo.InvariantCulture), new(cell + 12, 8), 14, fg); c.PopClip();
        for (var i = 0; i < 7; i++)
        {
            c.PushClip(new(i * cell, Owner.HeaderHeight, cell, Owner.WeekHeaderHeight));
            c.Text(new[] { "Su", "Mo", "Tu", "We", "Th", "Fr", "Sa" }[i], new(i * cell + 8, Owner.HeaderHeight + 2), 12, fg); c.PopClip();
        }
        for (var day = 1; day <= DateTime.DaysInMonth(Owner.DisplayMonth.Year, Owner.DisplayMonth.Month); day++)
        {
            var index = day - 1 + (int)Owner.DisplayMonth.DayOfWeek; var date = Owner.DisplayMonth.AddDays(day - 1); var selected = Owner.SelectedDate == date;
            var rect = new Rect(index % 7 * cell + 2, Owner.HeaderHeight + Owner.WeekHeaderHeight + index / 7 * Owner.DayHeight, Math.Max(0, cell - 4), Math.Max(0, Owner.DayHeight - 2));
            c.PushClip(rect); if (selected) c.FillRectangle(rect, FluentCompositeDrawing.Accent(Owner, Palette), Palette.ControlRadius);
            c.Text(day.ToString(CultureInfo.InvariantCulture), new(rect.X + 6, rect.Y + 3), 13, ColorOf(date < Owner.MinimumDate || date > Owner.MaximumDate || !Owner.IsEffectiveEnabled ? Palette.DisabledForeground : selected ? Palette.OnAccent : Palette.Foreground));
            c.PopClip();
        }
    }
}

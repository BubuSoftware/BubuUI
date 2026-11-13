using System.Globalization;

namespace EclipseUI.Elements;

public class Calendar : Element
{
    public static readonly StyledProperty<DateTime?> SelectedDateProperty = StyledProperty<DateTime?>.Register<Calendar>("SelectedDate", null, new() { Invalidate = InvalidateKind.Render });
    public static readonly StyledProperty<DateTime> DisplayMonthProperty = StyledProperty<DateTime>.Register<Calendar>("DisplayMonth", DateTime.Today, new() { Invalidate = InvalidateKind.Render });
    public static readonly RoutedEvent DateChosenEvent = new("DateChosen");
    public static readonly RoutedEvent SelectedDateChangedEvent = new("SelectedDateChanged");
    public DateTime? SelectedDate { get => GetValue(SelectedDateProperty); set => SetValue(SelectedDateProperty, value?.Date); }
    public DateTime DisplayMonth { get => new(GetValue(DisplayMonthProperty).Year, GetValue(DisplayMonthProperty).Month, 1); set => SetValue(DisplayMonthProperty, new(value.Year, value.Month, 1)); }
    public DateTime MinimumDate { get; set; } = DateTime.MinValue;
    public DateTime MaximumDate { get; set; } = DateTime.MaxValue;
    public override bool IsFocusable => true;
    public Calendar()
    {
        PropertyChanged += (_, p) => { if (p == SelectedDateProperty) { if (SelectedDate is { } date) DisplayMonth = date; RaiseEvent(new(SelectedDateChangedEvent)); } };
        AddHandler(PointerPressedEvent, e =>
        {
            if (e is not PointerEventArgs p) return;
            var local = PointFromRoot(p.Position); var cell = FinalRect.Width / 7;
            if (local.Y < 32)
            {
                if (local.X < cell && DisplayMonth > DateTime.MinValue.AddMonths(1)) DisplayMonth = DisplayMonth.AddMonths(-1);
                else if (local.X > FinalRect.Width - cell && DisplayMonth < DateTime.MaxValue.AddMonths(-1)) DisplayMonth = DisplayMonth.AddMonths(1);
            }
            else if (local.Y >= 56)
            {
                var index = (int)((local.Y - 56) / 28) * 7 + (int)(local.X / Math.Max(1, cell)) - (int)DisplayMonth.DayOfWeek;
                if (index >= 0 && index < DateTime.DaysInMonth(DisplayMonth.Year, DisplayMonth.Month))
                {
                    var date = DisplayMonth.AddDays(index);
                    if (date >= MinimumDate && date <= MaximumDate) { SelectDate(date); RaiseEvent(new(DateChosenEvent)); }
                }
            }
            e.Handled = true;
        });
        AddHandler(KeyDownEvent, e =>
        {
            if (e is not KeyEventArgs k) return;
            var date = SelectedDate ?? DisplayMonth;
            try
            {
                switch (k.Key)
                {
                    case Key.Enter: SelectDate(date); RaiseEvent(new(DateChosenEvent)); e.Handled = true; return;
                    case Key.Left: date = date.AddDays(-1); break;
                    case Key.Right: date = date.AddDays(1); break;
                    case Key.Up: date = date.AddDays(-7); break;
                    case Key.Down: date = date.AddDays(7); break;
                    case Key.PageUp: date = date.AddMonths(-1); break;
                    case Key.PageDown: date = date.AddMonths(1); break;
                    case Key.Home: date = new(date.Year, date.Month, 1); break;
                    case Key.End: date = new(date.Year, date.Month, DateTime.DaysInMonth(date.Year, date.Month)); break;
                    default: return;
                }
                SelectDate(date);
            }
            catch (ArgumentOutOfRangeException) { }
            e.Handled = true;
        });
    }
    public void SelectDate(DateTime date) { date = date.Date; if (date >= MinimumDate.Date && date <= MaximumDate.Date) SelectedDate = date; }
    protected internal override Size MeasureCore(Size available) => new(252, 224);
    protected internal override void ArrangeCore(Rect final) { }
    public override void Render(IElementDrawingContext c)
    {
        c.FillRectangle(new(0, 0, FinalRect.Width, FinalRect.Height), c.Background, 4);
        var cell = FinalRect.Width / 7;
        c.Line(new(18, 10), new(12, 16), c.Foreground, 1.5); c.Line(new(12, 16), new(18, 22), c.Foreground, 1.5);
        ControlIcons.Chevron(c, new(FinalRect.Width - 16, 16), right: true, size: 6);
        c.Text(DisplayMonth.ToString("yyyy-MM", CultureInfo.InvariantCulture), new(cell + 12, 8), 14, c.Foreground);
        for (var i = 0; i < 7; i++) c.Text(new[] { "Su", "Mo", "Tu", "We", "Th", "Fr", "Sa" }[i], new(i * cell + 8, 34), 12, c.Foreground);
        for (var day = 1; day <= DateTime.DaysInMonth(DisplayMonth.Year, DisplayMonth.Month); day++)
        {
            var index = day - 1 + (int)DisplayMonth.DayOfWeek; var date = DisplayMonth.AddDays(day - 1); var selected = SelectedDate == date;
            var r = new Rect(index % 7 * cell + 2, 56 + index / 7 * 28, cell - 4, 26);
            if (selected) c.FillRectangle(r, c.Accent, 4);
            c.Text(day.ToString(CultureInfo.InvariantCulture), new(r.X + 6, r.Y + 3), 13, date < MinimumDate || date > MaximumDate ? c.Foreground with { A = 80 } : selected ? c.OnAccent : c.Foreground);
        }
    }
}

public class DatePicker : Panel
{
    protected override FocusVisualMode DefaultFocusVisualMode => FocusVisualMode.Control;
    public override FocusVisualGeometry GetFocusVisualGeometry() => new(this, new(Point.Zero, FinalRect.Size), 4);
    public override void RenderFocusVisual(IElementDrawingContext c) =>
        c.StrokeRectangle(new(.5, .5, Math.Max(0, FinalRect.Width - 1), Math.Max(0, FinalRect.Height - 1)), c.Accent, 1, 4);
    public static readonly StyledProperty<DateTime?> SelectedDateProperty = StyledProperty<DateTime?>.Register<DatePicker>("SelectedDate", null, new() { Invalidate = InvalidateKind.Render });
    public static readonly StyledProperty<bool> IsDropDownOpenProperty = StyledProperty<bool>.Register<DatePicker>("IsDropDownOpen", false, new() { Invalidate = InvalidateKind.Measure });
    public static readonly RoutedEvent SelectedDateChangedEvent = new("SelectedDateChanged");
    private readonly TextBox _editor = new() { Placeholder = "yyyy-MM-dd" };
    private readonly Button _button = new DropDownButton { Width = 32, IsTabStop = false };
    private readonly Calendar _calendar = new();
    private readonly Popup _popup;
    private bool _sync;
    public DateTime? SelectedDate { get => GetValue(SelectedDateProperty); set => SetValue(SelectedDateProperty, value?.Date); }
    public bool IsDropDownOpen { get => GetValue(IsDropDownOpenProperty); set => SetValue(IsDropDownOpenProperty, value); }
    public DateTime MinimumDate { get => _calendar.MinimumDate; set => _calendar.MinimumDate = value; }
    public DateTime MaximumDate { get => _calendar.MaximumDate; set => _calendar.MaximumDate = value; }
    public DatePicker()
    {
        _editor.FocusVisualOwner = this; _button.FocusVisualOwner = this;
        _popup = new Popup { PlacementTarget = this, Content = new Border { Content = _calendar } };
        Children.Add(_editor); Children.Add(_button); Children.Add(_popup);
        AddHandler(PreviewKeyDownEvent, e =>
        {
            if (e is KeyEventArgs { Key: Key.Down } k && k.Modifiers.HasFlag(KeyModifiers.Alt)) { IsDropDownOpen = true; e.Handled = true; }
        });
        _button.AddHandler(Button.ClickEvent, _ => IsDropDownOpen = !IsDropDownOpen);
        _popup.AddHandler(Popup.ClosedEvent, _ => IsDropDownOpen = false);
        _calendar.AddHandler(Calendar.SelectedDateChangedEvent, e => { if (!_sync) SelectedDate = _calendar.SelectedDate; e.Handled = true; });
        _calendar.AddHandler(Calendar.DateChosenEvent, e => { IsDropDownOpen = false; e.Handled = true; });
        _editor.AddHandler(LostFocusEvent, _ => Commit());
        _editor.AddHandler(KeyDownEvent, e => { if (e is KeyEventArgs { Key: Key.Enter }) { Commit(); e.Handled = true; } });
        PropertyChanged += (_, p) =>
        {
            if (p == IsDropDownOpenProperty) { _popup.IsOpen = IsDropDownOpen; if (IsDropDownOpen) _calendar.RequestFocus(); }
            if (p != SelectedDateProperty || _sync) return;
            _sync = true;
            try { _calendar.SelectedDate = SelectedDate; _editor.Text = SelectedDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? ""; }
            finally { _sync = false; }
            RaiseEvent(new(SelectedDateChangedEvent));
        };
    }
    public bool Commit()
    {
        if (string.IsNullOrWhiteSpace(_editor.Text)) { SelectedDate = null; return true; }
        if (DateTime.TryParseExact(_editor.Text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) && date >= MinimumDate && date <= MaximumDate)
        { SelectedDate = date; return true; }
        _editor.Text = SelectedDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? ""; return false;
    }
    protected internal override Size MeasureCore(Size available)
    {
        _button.Measure(available); _editor.Measure(new(Math.Max(0, available.Width - _button.DesiredSize.Width), available.Height)); _popup.Measure(available);
        return new(_button.DesiredSize.Width + _editor.DesiredSize.Width, Math.Max(_editor.DesiredSize.Height, _button.DesiredSize.Height));
    }
    protected internal override void ArrangeCore(Rect final)
    {
        var w = Math.Min(final.Width, _button.DesiredSize.Width); _editor.Arrange(new(0, 0, final.Width - w, final.Height)); _button.Arrange(new(final.Width - w, 0, w, final.Height)); _popup.Arrange(new(0, 0, 0, 0));
    }
}

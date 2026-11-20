using System.Globalization;

namespace EclipseUI.Elements;

public class Calendar : Element
{
    public static readonly StyledProperty<double> HeaderHeightProperty = StyledProperty<double>.Register<Calendar>(nameof(HeaderHeight), 0, new() { Invalidate = InvalidateKind.Measure });
    public static readonly StyledProperty<double> WeekHeaderHeightProperty = StyledProperty<double>.Register<Calendar>(nameof(WeekHeaderHeight), 0, new() { Invalidate = InvalidateKind.Measure });
    public static readonly StyledProperty<double> DayHeightProperty = StyledProperty<double>.Register<Calendar>(nameof(DayHeight), 0, new() { Invalidate = InvalidateKind.Measure });
    public static readonly StyledProperty<double> PreferredWidthProperty = StyledProperty<double>.Register<Calendar>(nameof(PreferredWidth), 0, new() { Invalidate = InvalidateKind.Measure });
    public double HeaderHeight { get => GetValue(HeaderHeightProperty); set => SetValue(HeaderHeightProperty, Math.Max(0, value)); }
    public double WeekHeaderHeight { get => GetValue(WeekHeaderHeightProperty); set => SetValue(WeekHeaderHeightProperty, Math.Max(0, value)); }
    public double DayHeight { get => GetValue(DayHeightProperty); set => SetValue(DayHeightProperty, Math.Max(0, value)); }
    public double PreferredWidth { get => GetValue(PreferredWidthProperty); set => SetValue(PreferredWidthProperty, Math.Max(0, value)); }
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
            if (local.Y < HeaderHeight)
            {
                if (local.X < cell && DisplayMonth > DateTime.MinValue.AddMonths(1)) DisplayMonth = DisplayMonth.AddMonths(-1);
                else if (local.X > FinalRect.Width - cell && DisplayMonth < DateTime.MaxValue.AddMonths(-1)) DisplayMonth = DisplayMonth.AddMonths(1);
            }
            else if (local.Y >= HeaderHeight + WeekHeaderHeight)
            {
                var index = (int)((local.Y - HeaderHeight - WeekHeaderHeight) / Math.Max(1, DayHeight)) * 7 + (int)(local.X / Math.Max(1, cell)) - (int)DisplayMonth.DayOfWeek;
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
    protected internal override Size MeasureCore(Size available) => new(PreferredWidth, HeaderHeight + WeekHeaderHeight + DayHeight * 6);
    protected internal override void ArrangeCore(Rect final) { }
}

public class DatePicker : Panel
{
    public override Rect? GetContentClip() => new Rect(Point.Zero, FinalRect.Size);
    public const string EditorPart = "PART_Editor", ButtonPart = "PART_Button", CalendarPart = "PART_Calendar", PopupPart = "PART_Popup";
    public static readonly StyledProperty<DateTime?> SelectedDateProperty = StyledProperty<DateTime?>.Register<DatePicker>("SelectedDate", null, new() { Invalidate = InvalidateKind.Render });
    public static readonly StyledProperty<bool> IsDropDownOpenProperty = StyledProperty<bool>.Register<DatePicker>("IsDropDownOpen", false, new() { Invalidate = InvalidateKind.Measure });
    public static readonly RoutedEvent SelectedDateChangedEvent = new("SelectedDateChanged");
    private TextBox? _editor;
    private Button? _button;
    private Calendar? _calendar;
    private Popup? _popup;
    private DateTime _minimumDate = DateTime.MinValue, _maximumDate = DateTime.MaxValue;
    private bool _sync;
    public DateTime? SelectedDate { get => GetValue(SelectedDateProperty); set => SetValue(SelectedDateProperty, value?.Date); }
    public bool IsDropDownOpen { get => GetValue(IsDropDownOpenProperty); set => SetValue(IsDropDownOpenProperty, value); }
    public DateTime MinimumDate { get => _minimumDate; set { _minimumDate = value; if (_calendar is not null) _calendar.MinimumDate = value; } }
    public DateTime MaximumDate { get => _maximumDate; set { _maximumDate = value; if (_calendar is not null) _calendar.MaximumDate = value; } }
    public DatePicker()
    {
        AddHandler(PreviewKeyDownEvent, e =>
        {
            if (e is KeyEventArgs { Key: Key.Down } k && k.Modifiers.HasFlag(KeyModifiers.Alt)) { IsDropDownOpen = true; e.Handled = true; }
        });
        PropertyChanged += (_, p) =>
        {
            if (p == IsDropDownOpenProperty) UpdateDropDown();
            if (p == SelectedDateProperty && !_sync) { UpdateDate(); RaiseEvent(new(SelectedDateChangedEvent)); }
        };
    }
    protected override void OnApplyTemplate(TemplateInstance instance)
    {
        base.OnApplyTemplate(instance);
        _editor = instance.FindPart<TextBox>(EditorPart) ?? throw new InvalidOperationException($"DatePicker template requires {EditorPart} of type TextBox.");
        _button = instance.FindPart<Button>(ButtonPart) ?? throw new InvalidOperationException($"DatePicker template requires {ButtonPart} of type Button.");
        _calendar = instance.FindPart<Calendar>(CalendarPart) ?? throw new InvalidOperationException($"DatePicker template requires {CalendarPart} of type Calendar.");
        _popup = instance.FindPart<Popup>(PopupPart) ?? throw new InvalidOperationException($"DatePicker template requires {PopupPart} of type Popup.");
        if (_editor is not null)
        {
            _editor.FocusVisualOwner = this;
            _editor.AddHandler(LostFocusEvent, EditorLostFocus);
            _editor.AddHandler(KeyDownEvent, EditorKeyDown);
        }
        if (_button is not null) { _button.FocusVisualOwner = this; _button.AddHandler(Button.ClickEvent, ButtonClicked); }
        if (_calendar is not null)
        {
            _calendar.MinimumDate = MinimumDate; _calendar.MaximumDate = MaximumDate;
            _calendar.AddHandler(Calendar.SelectedDateChangedEvent, CalendarChanged);
            _calendar.AddHandler(Calendar.DateChosenEvent, DateChosen);
        }
        if (_popup is not null) { _popup.PlacementTarget = this; _popup.LightDismissExclusion = _button; _popup.AddHandler(Popup.ClosedEvent, PopupClosed); }
        UpdateDate(); UpdateDropDown();
    }
    protected override void OnTemplateRemoving()
    {
        if (_editor is not null) { _editor.RemoveHandler(LostFocusEvent, EditorLostFocus); _editor.RemoveHandler(KeyDownEvent, EditorKeyDown); }
        if (_button is not null) _button.RemoveHandler(Button.ClickEvent, ButtonClicked);
        if (_calendar is not null) { _calendar.RemoveHandler(Calendar.SelectedDateChangedEvent, CalendarChanged); _calendar.RemoveHandler(Calendar.DateChosenEvent, DateChosen); }
        if (_popup is not null) { _popup.LightDismissExclusion = null; _popup.RemoveHandler(Popup.ClosedEvent, PopupClosed); }
        _editor = null; _button = null; _calendar = null; _popup = null;
        base.OnTemplateRemoving();
    }
    private void UpdateDate()
    {
        _sync = true;
        try { if (_calendar is not null) _calendar.SelectedDate = SelectedDate; if (_editor is not null) _editor.Text = SelectedDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? ""; }
        finally { _sync = false; }
    }
    private void UpdateDropDown() { if (_popup is not null) _popup.IsOpen = IsDropDownOpen; if (IsDropDownOpen) _calendar?.RequestFocus(); }
    private void ButtonClicked(RoutedEventArgs e) => IsDropDownOpen = !IsDropDownOpen;
    private void PopupClosed(RoutedEventArgs e) => IsDropDownOpen = false;
    private void CalendarChanged(RoutedEventArgs e) { if (!_sync && _calendar is not null) SelectedDate = _calendar.SelectedDate; e.Handled = true; }
    private void DateChosen(RoutedEventArgs e) { IsDropDownOpen = false; e.Handled = true; }
    private void EditorLostFocus(RoutedEventArgs e) => Commit();
    private void EditorKeyDown(RoutedEventArgs e) { if (e is KeyEventArgs { Key: Key.Enter }) { Commit(); e.Handled = true; } }
    public bool Commit()
    {
        if (_editor is null) return true;
        if (string.IsNullOrWhiteSpace(_editor.Text)) { SelectedDate = null; return true; }
        if (DateTime.TryParseExact(_editor.Text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) && date >= MinimumDate && date <= MaximumDate)
        { SelectedDate = date; return true; }
        _editor.Text = SelectedDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? ""; return false;
    }
}

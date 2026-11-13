using System.Globalization;

namespace EclipseUI.Elements;

public class ToggleButton : Button
{
    public static readonly StyledProperty<bool> CheckedProperty = StyledProperty<bool>.Register<ToggleButton>("Checked", false, new() { Invalidate = InvalidateKind.Render });
    public static readonly RoutedEvent CheckedChangedEvent = new("CheckedChanged");
    public bool Checked { get => GetValue(CheckedProperty); set => SetValue(CheckedProperty, value); }
    public override bool UsesDefaultDrawing => false;
    public ToggleButton() => AddHandler(ClickEvent, _ => { Checked = !Checked; RaiseEvent(new(CheckedChangedEvent)); });
    public override void Render(IElementDrawingContext c)
    {
        c.FillRectangle(new(0, 0, FinalRect.Width, FinalRect.Height), Checked ? c.Accent : c.Background, 4);
        c.StrokeRectangle(new(.5, .5, Math.Max(0, FinalRect.Width - 1), Math.Max(0, FinalRect.Height - 1)), c.Border, 1, 4);
        if (Content is null)
        {
            var fs = GetValue(TextBlock.FontSizeProperty); var m = TextMeasurer.Current.Measure(Label, fs);
            c.Text(Label, new((FinalRect.Width - m.Width) / 2, (FinalRect.Height - m.Height) / 2), fs, Checked ? c.OnAccent : c.Foreground);
        }
    }
}

public class ToggleSwitch : CheckBox
{
    public override bool UsesDefaultDrawing => false;
    protected internal override Size MeasureCore(Size available)
    {
        var text = TextMeasurer.Current.Measure(Label, GetValue(TextBlock.FontSizeProperty)); return new(52 + text.Width, Math.Max(28, text.Height));
    }
    public override void Render(IElementDrawingContext c)
    {
        var y = (FinalRect.Height - 22) / 2;
        c.FillRectangle(new(0, y, 40, 22), Checked ? c.Accent : c.Border, 11);
        c.Ellipse(new(Checked ? 21 : 3, y + 3, 16, 16), c.OnAccent, UiColor.Transparent);
        var fs = GetValue(TextBlock.FontSizeProperty); var m = TextMeasurer.Current.Measure(Label, fs);
        c.Text(Label, new(48, (FinalRect.Height - m.Height) / 2), fs, c.Foreground);
    }
}

public class RadioButton : CheckBox
{
    public static readonly StyledProperty<string> GroupNameProperty = StyledProperty<string>.Register<RadioButton>("GroupName", "", new() { Invalidate = InvalidateKind.Render });
    public string GroupName { get => GetValue(GroupNameProperty); set => SetValue(GroupNameProperty, value ?? ""); }
    public override bool UsesDefaultDrawing => false;
    public RadioButton()
    {
        PropertyChanged += (_, p) => { if ((p == CheckedProperty || p == GroupNameProperty) && Checked) UpdateGroup(); };
        AddHandler(KeyDownEvent, e =>
        {
            if (e is not KeyEventArgs k || k.Key is not (Key.Left or Key.Right or Key.Up or Key.Down)) return;
            var root = Parent; if (GroupName.Length > 0) while (root?.Parent is not null) root = root.Parent;
            var peers = root?.Descendants().OfType<RadioButton>().Where(r => r.GroupName == GroupName && (GroupName.Length > 0 || ReferenceEquals(r.Parent, Parent)) && r.IsEffectiveEnabled && r.IsEffectivelyVisible).ToArray() ?? [];
            if (peers.Length == 0) return;
            var index = Array.IndexOf(peers, this); var next = peers[(index + (k.Key is Key.Left or Key.Up ? -1 : 1) + peers.Length) % peers.Length];
            next.Toggle(); next.RequestFocus(); e.Handled = true;
        });
    }
    private void UpdateGroup()
    {
        Element? scope = Parent;
        if (GroupName.Length > 0) while (scope?.Parent is not null) scope = scope.Parent;
        if (scope is null) return;
        foreach (var other in scope.Descendants().OfType<RadioButton>())
            if (!ReferenceEquals(this, other) && other.GroupName == GroupName && (GroupName.Length > 0 || ReferenceEquals(Parent, other.Parent))) other.Checked = false;
    }
    protected override void OnAttached() { if (Checked) UpdateGroup(); base.OnAttached(); }
    public override void Toggle()
    {
        if (Checked) return; Checked = true; IsIndeterminate = false; UpdateGroup(); RaiseEvent(new(CheckedChangedEvent));
    }
    public override void Render(IElementDrawingContext c)
    {
        var fs = GetValue(TextBlock.FontSizeProperty); var size = fs * 1.1; var y = (FinalRect.Height - size) / 2;
        c.Ellipse(new(1, y + 1, size - 2, size - 2), c.Background, Checked ? c.Accent : c.Border, 1.5);
        if (Checked) c.Ellipse(new(size * .25, y + size * .25, size * .5, size * .5), c.Accent, UiColor.Transparent);
        var m = TextMeasurer.Current.Measure(Label, fs); c.Text(Label, new(size + 8, (FinalRect.Height - m.Height) / 2), fs, c.Foreground);
    }
}

public abstract class RangeControl : Element
{
    public static readonly StyledProperty<double> MinimumProperty = StyledProperty<double>.Register<RangeControl>("Minimum", 0, new() { Invalidate = InvalidateKind.Render });
    public static readonly StyledProperty<double> MaximumProperty = StyledProperty<double>.Register<RangeControl>("Maximum", 100, new() { Invalidate = InvalidateKind.Render });
    public static readonly StyledProperty<double> ValueProperty = StyledProperty<double>.Register<RangeControl>("Value", 0, new() { Invalidate = InvalidateKind.Render });
    public static readonly RoutedEvent ValueChangedEvent = new("ValueChanged");
    private bool _coercing;
    public double Minimum { get => GetValue(MinimumProperty); set { Validate(value); SetValue(MinimumProperty, value); } }
    public double Maximum { get => GetValue(MaximumProperty); set { Validate(value); SetValue(MaximumProperty, value); } }
    public double Value { get => Math.Clamp(GetValue(ValueProperty), Minimum, Math.Max(Minimum, Maximum)); set { Validate(value); SetValue(ValueProperty, Math.Clamp(value, Minimum, Math.Max(Minimum, Maximum))); } }
    protected double Fraction => Maximum <= Minimum ? 0 : (Value - Minimum) / (Maximum - Minimum);
    private static void Validate(double n) { if (!double.IsFinite(n)) throw new ArgumentOutOfRangeException(nameof(n)); }
    protected RangeControl() => PropertyChanged += (_, p) =>
    {
        if (_coercing) return;
        if (p == MinimumProperty || p == MaximumProperty || p == ValueProperty)
        {
            _coercing = true;
            try { if (GetValue(ValueProperty) != Value) SetValue(ValueProperty, Value); }
            finally { _coercing = false; }
            RaiseEvent(new(ValueChangedEvent));
        }
    };
    protected internal override void ArrangeCore(Rect final) { }
}

public class Slider : RangeControl
{
    public static readonly StyledProperty<double> SmallChangeProperty = StyledProperty<double>.Register<Slider>("SmallChange", 1, new() { Invalidate = InvalidateKind.Render });
    public static readonly StyledProperty<double> TickFrequencyProperty = StyledProperty<double>.Register<Slider>("TickFrequency", 0, new() { Invalidate = InvalidateKind.Render });
    public static readonly StyledProperty<Orientation> OrientationProperty = StyledProperty<Orientation>.Register<Slider>("Orientation", Orientation.Horizontal, new() { Invalidate = InvalidateKind.Measure });
    public double SmallChange { get => GetValue(SmallChangeProperty); set => SetValue(SmallChangeProperty, Math.Max(0, value)); }
    public double TickFrequency { get => GetValue(TickFrequencyProperty); set => SetValue(TickFrequencyProperty, Math.Max(0, value)); }
    public Orientation Orientation { get => GetValue(OrientationProperty); set => SetValue(OrientationProperty, value); }
    public override bool IsFocusable => true;
    private bool _drag;
    public Slider()
    {
        AddHandler(PointerPressedEvent, e => { if (e is PointerEventArgs p && p.Button == PointerButton.Left) { _drag = true; SetFromPointer(p); e.Handled = true; } });
        AddHandler(PointerMovedEvent, e => { if (_drag && e is PointerEventArgs p) { SetFromPointer(p); e.Handled = true; } });
        AddHandler(PointerReleasedEvent, _ => _drag = false);
        AddHandler(KeyDownEvent, e =>
        {
            if (e is not KeyEventArgs k) return;
            switch (k.Key)
            {
                case Key.Left: case Key.Down: Value -= SmallChange; break;
                case Key.Right: case Key.Up: Value += SmallChange; break;
                case Key.Home: Value = Minimum; break;
                case Key.End: Value = Maximum; break;
                case Key.PageDown: Value -= SmallChange * 10; break;
                case Key.PageUp: Value += SmallChange * 10; break;
                default: return;
            }
            e.Handled = true;
        });
    }
    private void SetFromPointer(PointerEventArgs p)
    {
        var local = PointFromRoot(p.Position); var vertical = Orientation == Orientation.Vertical;
        var fraction = vertical ? 1 - (local.Y - 8) / Math.Max(1, FinalRect.Height - 16) : (local.X - 8) / Math.Max(1, FinalRect.Width - 16);
        var value = Minimum + Math.Clamp(fraction, 0, 1) * Math.Max(0, Maximum - Minimum);
        if (TickFrequency > 0) value = Minimum + Math.Round((value - Minimum) / TickFrequency, MidpointRounding.AwayFromZero) * TickFrequency;
        Value = value;
    }
    protected internal override Size MeasureCore(Size available) => Orientation == Orientation.Horizontal ? new(120, 24) : new(24, 120);
    public override void Render(IElementDrawingContext c)
    {
        var vertical = Orientation == Orientation.Vertical;
        var x = vertical ? FinalRect.Width / 2 : 8 + Math.Max(0, FinalRect.Width - 16) * Fraction;
        var y = vertical ? FinalRect.Height - 8 - Math.Max(0, FinalRect.Height - 16) * Fraction : FinalRect.Height / 2;
        c.FillRectangle(vertical ? new(FinalRect.Width / 2 - 2, 8, 4, Math.Max(0, FinalRect.Height - 16)) : new(8, FinalRect.Height / 2 - 2, Math.Max(0, FinalRect.Width - 16), 4), c.Border, 2);
        c.Ellipse(new(x - 7, y - 7, 14, 14), c.Accent, c.OnAccent, 1);
    }
}

public class ProgressBar : RangeControl, IFrameAnimation
{
    public static readonly StyledProperty<bool> IsIndeterminateProperty = StyledProperty<bool>.Register<ProgressBar>("IsIndeterminate", false, new() { Invalidate = InvalidateKind.Render });
    public bool IsIndeterminate { get => GetValue(IsIndeterminateProperty); set => SetValue(IsIndeterminateProperty, value); }
    private double _phase;
    public void Advance(TimeSpan elapsed)
    {
        if (!IsIndeterminate || !IsEffectivelyVisible) return;
        _phase = (_phase + elapsed.TotalSeconds * .7) % 1; InvalidateRender();
    }
    protected internal override Size MeasureCore(Size available) => new(120, 12);
    public override void Render(IElementDrawingContext c)
    {
        c.FillRectangle(new(0, 0, FinalRect.Width, FinalRect.Height), c.Background, FinalRect.Height / 2);
        c.FillRectangle(new(IsIndeterminate ? FinalRect.Width * .6 * (1 - Math.Abs(_phase * 2 - 1)) : 0, 0, FinalRect.Width * (IsIndeterminate ? .4 : Fraction), FinalRect.Height), c.Accent, FinalRect.Height / 2);
    }
}

public class NumericUpDown : TextBox
{
    public static readonly StyledProperty<double> ValueProperty = StyledProperty<double>.Register<NumericUpDown>("Value", 0, new() { Invalidate = InvalidateKind.Render });
    public static readonly StyledProperty<double> MinimumProperty = StyledProperty<double>.Register<NumericUpDown>("Minimum", double.MinValue, new() { Invalidate = InvalidateKind.Render });
    public static readonly StyledProperty<double> MaximumProperty = StyledProperty<double>.Register<NumericUpDown>("Maximum", double.MaxValue, new() { Invalidate = InvalidateKind.Render });
    public static readonly StyledProperty<double> IncrementProperty = StyledProperty<double>.Register<NumericUpDown>("Increment", 1, new() { Invalidate = InvalidateKind.Render });
    public static readonly RoutedEvent ValueChangedEvent = new("ValueChanged");
    public double Value { get => Math.Clamp(GetValue(ValueProperty), Minimum, Math.Max(Minimum, Maximum)); set { if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value)); SetValue(ValueProperty, Math.Clamp(value, Minimum, Math.Max(Minimum, Maximum))); } }
    public double Minimum { get => GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double Increment { get => GetValue(IncrementProperty); set => SetValue(IncrementProperty, Math.Max(0, value)); }
    public NumericUpDown()
    {
        Text = "0";
        PropertyChanged += (_, p) => { if (p == ValueProperty || p == MinimumProperty || p == MaximumProperty) { Text = Value.ToString(CultureInfo.InvariantCulture); RaiseEvent(new(ValueChangedEvent)); } };
        AddHandler(LostFocusEvent, _ => Commit());
        AddHandler(PointerWheelChangedEvent, e => { if (IsFocused && !IsReadOnly && e is WheelEventArgs w) { Value += Math.Sign(w.Delta) * Increment; e.Handled = true; } });
    }
    protected override void HandlePointerPressed(PointerEventArgs p)
    {
        if (!IsReadOnly && PointFromRoot(p.Position).X > FinalRect.Width - 20)
            Value += (PointFromRoot(p.Position).Y < FinalRect.Height / 2 ? 1 : -1) * Increment;
        else base.HandlePointerPressed(p);
    }

    public bool Commit()
    {
        if (double.TryParse(Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)) { Value = value; Text = Value.ToString(CultureInfo.InvariantCulture); return true; }
        Text = Value.ToString(CultureInfo.InvariantCulture); return false;
    }
    protected override void HandleKey(RoutedEventArgs args)
    {
        if (args is KeyEventArgs k && k.Key is Key.Up or Key.Down or Key.Enter)
        {
            if (!IsReadOnly) { Commit(); if (k.Key != Key.Enter) Value += (k.Key == Key.Up ? 1 : -1) * Increment; }
            args.Handled = true; return;
        }
        base.HandleKey(args);
    }
    public override void Render(IElementDrawingContext c)
    {
        base.Render(c);
        c.FillRectangle(new(Math.Max(0, FinalRect.Width - 20), 1, 19, Math.Max(0, FinalRect.Height - 2)), c.Background);
        ControlIcons.Chevron(c, new(FinalRect.Width - 10, FinalRect.Height / 4), up: true, size: 3);
        ControlIcons.Chevron(c, new(FinalRect.Width - 10, FinalRect.Height * .75), size: 3);
    }
}

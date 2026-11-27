using System.Globalization;

namespace BubuUI.Elements;

public class ToggleButton : Button
{
    public static readonly StyledProperty<bool> CheckedProperty = StyledProperty<bool>.Register<ToggleButton>("Checked", false, new() { Invalidate = InvalidateKind.Render });
    public static readonly RoutedEvent CheckedChangedEvent = new("CheckedChanged");
    public bool Checked { get => GetValue(CheckedProperty); set => SetValue(CheckedProperty, value); }
    public ToggleButton() => AddHandler(ClickEvent, _ => { Checked = !Checked; RaiseEvent(new(CheckedChangedEvent)); });
}

public class ToggleSwitch : CheckBox
{
}

public class RadioButton : CheckBox
{
    public static readonly StyledProperty<string> GroupNameProperty = StyledProperty<string>.Register<RadioButton>("GroupName", "", new() { Invalidate = InvalidateKind.Render });
    public string GroupName { get => GetValue(GroupNameProperty); set => SetValue(GroupNameProperty, value ?? ""); }
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
    public double Fraction => Maximum <= Minimum ? 0 : (Value - Minimum) / (Maximum - Minimum);
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
    public static readonly StyledProperty<double> ThumbSizeProperty = StyledProperty<double>.Register<Slider>("ThumbSize", 0, new() { Invalidate = InvalidateKind.Measure });
    public double ThumbSize { get => GetValue(ThumbSizeProperty); set => SetValue(ThumbSizeProperty, Math.Max(0, value)); }
    public double TrackInset => ThumbSize / 2;
    public static readonly StyledProperty<double> SmallChangeProperty = StyledProperty<double>.Register<Slider>("SmallChange", 1, new() { Invalidate = InvalidateKind.Render });
    public static readonly StyledProperty<double> TickFrequencyProperty = StyledProperty<double>.Register<Slider>("TickFrequency", 0, new() { Invalidate = InvalidateKind.Render });
    public static readonly StyledProperty<Orientation> OrientationProperty = StyledProperty<Orientation>.Register<Slider>("Orientation", Orientation.Horizontal, new() { Invalidate = InvalidateKind.Measure });
    public double SmallChange { get => GetValue(SmallChangeProperty); set => SetValue(SmallChangeProperty, Math.Max(0, value)); }
    public double TickFrequency { get => GetValue(TickFrequencyProperty); set => SetValue(TickFrequencyProperty, Math.Max(0, value)); }
    public Orientation Orientation { get => GetValue(OrientationProperty); set => SetValue(OrientationProperty, value); }
    public override bool IsFocusable => true;
    private bool _drag;
    internal override void CancelPointerInteraction() => _drag = false;
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
        var fraction = vertical ? 1 - (local.Y - TrackInset) / Math.Max(1, FinalRect.Height - TrackInset * 2) : (local.X - TrackInset) / Math.Max(1, FinalRect.Width - TrackInset * 2);
        var value = Minimum + Math.Clamp(fraction, 0, 1) * Math.Max(0, Maximum - Minimum);
        if (TickFrequency > 0) value = Minimum + Math.Round((value - Minimum) / TickFrequency, MidpointRounding.AwayFromZero) * TickFrequency;
        Value = value;
    }
    protected internal override Size MeasureCore(Size available) => Size.Empty;
}

public class ProgressBar : RangeControl
{
    public static readonly StyledProperty<bool> IsIndeterminateProperty = StyledProperty<bool>.Register<ProgressBar>("IsIndeterminate", false, new() { Invalidate = InvalidateKind.Render });
    public bool IsIndeterminate { get => GetValue(IsIndeterminateProperty); set => SetValue(IsIndeterminateProperty, value); }
    protected internal override Size MeasureCore(Size available) => Size.Empty;
}

public class NumericUpDown : TextBox
{
    public static readonly StyledProperty<double> SpinnerWidthProperty = StyledProperty<double>.Register<NumericUpDown>(nameof(SpinnerWidth), 0, new() { Invalidate = InvalidateKind.Measure });
    public double SpinnerWidth { get => GetValue(SpinnerWidthProperty); set => SetValue(SpinnerWidthProperty, Math.Max(0, value)); }
    protected override Rect TextViewport => base.TextViewport with { Size = new(Math.Max(0, base.TextViewport.Width - SpinnerWidth), base.TextViewport.Height) };
    public override Size MeasureTextContent(int minimumLines = 1)
    {
        var size = base.MeasureTextContent(minimumLines);
        return new(size.Width + SpinnerWidth, size.Height);
    }
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
        if (!IsReadOnly && SpinnerWidth > 0 && PointFromRoot(p.Position).X > FinalRect.Width - SpinnerWidth)
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
}

using System;

namespace EclipseUI.Elements;

public sealed class Button : Element
{
    public static readonly RoutedEvent ClickEvent = new RoutedEvent("Click", RoutingStrategy.Bubble);

    public static readonly StyledProperty<string> LabelProperty =
        StyledProperty<string>.Register<Button>("Label", "",
            new PropertyOptions { Invalidate = InvalidateKind.Measure });

    private const double HPad = 12, VPad = 6;

    private bool _pressed;

    public Button()
    {
        AddHandler(InputEvents.PointerPressedEvent, () => _pressed = true);
        AddHandler(InputEvents.PointerReleasedEvent, OnPointerReleased);
        AddHandler(InputEvents.KeyDownEvent, OnKeyDown);
    }

    public override bool IsFocusable => true;

    public string Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public void Click() => RaiseEvent(new RoutedEventArgs(ClickEvent));

    private void OnPointerReleased()
    {
        if (!_pressed) return;
        _pressed = false;
        Click();
    }

    private void OnKeyDown(RoutedEventArgs args)
    {
        if (args is KeyEventArgs key && key.Key is Key.Space or Key.Enter)
            Click();
    }

    protected internal override Size MeasureCore(Size available)
    {
        var metrics = TextMeasurer.Current.Measure(Label, GetValue(TextBlock.FontSizeProperty));
        return new Size(metrics.Width + HPad * 2, metrics.Height + VPad * 2);
    }

    protected internal override void ArrangeCore(Rect final) { }
}

public sealed class CheckBox : Element
{
    public static readonly StyledProperty<bool> CheckedProperty =
        StyledProperty<bool>.Register<CheckBox>("Checked", false,
            new PropertyOptions { Invalidate = InvalidateKind.Render });

    public static readonly StyledProperty<string> LabelProperty =
        StyledProperty<string>.Register<CheckBox>("Label", "",
            new PropertyOptions { Invalidate = InvalidateKind.Measure });

    private bool _pressed;

    public CheckBox()
    {
        AddHandler(InputEvents.PointerPressedEvent, () => _pressed = true);
        AddHandler(InputEvents.PointerReleasedEvent, OnPointerReleased);
        AddHandler(InputEvents.KeyDownEvent, OnKeyDown);
    }

    public override bool IsFocusable => true;

    public bool Checked
    {
        get => GetValue(CheckedProperty);
        set => SetValue(CheckedProperty, value);
    }

    public string Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public void Toggle() => Checked = !Checked;

    private void OnPointerReleased()
    {
        if (!_pressed) return;
        _pressed = false;
        Toggle();
    }

    private void OnKeyDown(RoutedEventArgs args)
    {
        if (args is KeyEventArgs key && key.Key == Key.Space)
            Toggle();
    }

    protected internal override Size MeasureCore(Size available)
    {
        var fontSize = GetValue(TextBlock.FontSizeProperty);
        var metrics = TextMeasurer.Current.Measure(Label, fontSize);
        return new Size(fontSize * 1.1 + 8 + metrics.Width + 16, Math.Max(metrics.Height, fontSize * 1.4) + 8);
    }

    protected internal override void ArrangeCore(Rect final) { }
}

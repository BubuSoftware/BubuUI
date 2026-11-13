using System;

namespace EclipseUI.Elements;

[UiContent(UiContentKind.Text, Target = nameof(Label))]
public class Button : Panel
{
    public static readonly RoutedEvent ClickEvent = new RoutedEvent("Click", RoutingStrategy.Bubble);

    public static readonly StyledProperty<string> LabelProperty =
        StyledProperty<string>.Register<Button>("Label", "",
            new PropertyOptions { Invalidate = InvalidateKind.Measure });

    private const double HPad = 12, VPad = 6;

    public Button()
    {
        AddHandler(Element.PointerReleasedEvent, OnPointerReleased);
        AddHandler(Element.KeyDownEvent, OnKeyDown);
    }

    public override bool IsFocusable => true;

    /// <summary>指针按下未释放（D102）：继承 <see cref="Element.IsPressed"/>（InputRouter 维护）。</summary>

    public string Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    [UiSlot("Content")]
    public Element? Content { get => Children.Count == 0 ? null : Children[0]; set => Children.ReplaceAll(value is null ? [] : [value]); }

    public void Click() => RaiseEvent(new RoutedEventArgs(ClickEvent));

    private void OnPointerReleased(RoutedEventArgs args)
    {
        // 门控：收到 Released 但未处于按下态（如手工路由）不触发。
        // InputRouter 释放时先路由后清状态，这里读到的仍是按下末尾（D102 时序契约）。
        if (!IsPressed || args is PointerEventArgs p && (p.Button != PointerButton.Left)) return;
        Click();
    }

    private void OnKeyDown(RoutedEventArgs args)
    {
        if (args is KeyEventArgs key && key.Key is Key.Space or Key.Enter)
        { Click(); }
    }

    protected internal override Size MeasureCore(Size available)
    {
        if (Content is not null)
        {
            Content.Measure(new(Math.Max(0, available.Width - HPad * 2), Math.Max(0, available.Height - VPad * 2)));
            return new(Content.DesiredSize.Width + HPad * 2, Content.DesiredSize.Height + VPad * 2);
        }
        var metrics = TextMeasurer.Current.Measure(Label, GetValue(TextBlock.FontSizeProperty));
        return new Size(metrics.Width + HPad * 2, metrics.Height + VPad * 2);
    }

    protected internal override void ArrangeCore(Rect final) => Content?.Arrange(new(HPad, VPad, Math.Max(0, final.Width - HPad * 2), Math.Max(0, final.Height - VPad * 2)));
}

[UiContent(UiContentKind.Text, Target = nameof(Label))]
public class CheckBox : Element
{
    public static readonly RoutedEvent CheckedChangedEvent = new RoutedEvent("CheckedChanged", RoutingStrategy.Bubble);

    public static readonly StyledProperty<bool> CheckedProperty =
        StyledProperty<bool>.Register<CheckBox>("Checked", false,
            new PropertyOptions { Invalidate = InvalidateKind.Render });

    public static readonly StyledProperty<string> LabelProperty =
        StyledProperty<string>.Register<CheckBox>("Label", "",
            new PropertyOptions { Invalidate = InvalidateKind.Measure });

    public static readonly StyledProperty<bool> IsThreeStateProperty = StyledProperty<bool>.Register<CheckBox>("IsThreeState", false, new() { Invalidate = InvalidateKind.Render });
    public static readonly StyledProperty<bool> IsIndeterminateProperty = StyledProperty<bool>.Register<CheckBox>("IsIndeterminate", false, new() { Invalidate = InvalidateKind.Render });
    public bool IsThreeState { get => GetValue(IsThreeStateProperty); set => SetValue(IsThreeStateProperty, value); }
    public bool IsIndeterminate { get => GetValue(IsIndeterminateProperty); set => SetValue(IsIndeterminateProperty, value); }
    public CheckBox()
    {
        AddHandler(Element.PointerReleasedEvent, OnPointerReleased);
        AddHandler(Element.KeyDownEvent, OnKeyDown);
    }

    public override bool IsFocusable => true;

    /// <summary>指针按下未释放（D102）：继承 <see cref="Element.IsPressed"/>（InputRouter 维护）。</summary>

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

    // 只在交互路径广播：绑定回推走 setter，若也广播会让模板的 OnCheckedChanged 被源写回再次触发。
    public virtual void Toggle()
    {
        if (IsThreeState)
        {
            if (IsIndeterminate) { IsIndeterminate = false; Checked = false; }
            else if (Checked) { Checked = false; IsIndeterminate = true; }
            else Checked = true;
        }
        else { IsIndeterminate = false; Checked = !Checked; }
        RaiseEvent(new RoutedEventArgs(CheckedChangedEvent) { Source = this });
    }

    private void OnPointerReleased(RoutedEventArgs args)
    {
        // 门控与 Button 相同（D102）：InputRouter 先路由后清 IsPressed，按下末尾触发 Toggle。
        if (!IsPressed || args is PointerEventArgs p && (p.Button != PointerButton.Left)) return;
        Toggle();
    }

    private void OnKeyDown(RoutedEventArgs args)
    {
        if (args is KeyEventArgs key && key.Key == Key.Space)
        { Toggle(); }
    }

    protected internal override Size MeasureCore(Size available)
    {
        var fontSize = GetValue(TextBlock.FontSizeProperty);
        var metrics = TextMeasurer.Current.Measure(Label, fontSize);
        return new Size(fontSize * 1.1 + 8 + metrics.Width + 16, Math.Max(metrics.Height, fontSize * 1.4) + 8);
    }

    protected internal override void ArrangeCore(Rect final) { }
}

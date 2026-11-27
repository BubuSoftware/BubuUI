using System;

namespace BubuUI.Elements;

[UiContent(UiContentKind.Text, Target = nameof(Label))]
public class Button : TemplatedControl
{
    public override Rect? GetContentClip() => TemplateRoot is null ? GetLabelBounds() : null;
    /// <summary>Label/content area; compound buttons can reserve space for an icon.</summary>
    public virtual Rect GetLabelBounds() => new(HorizontalPadding, VerticalPadding, Math.Max(0, FinalRect.Width - HorizontalPadding * 2), Math.Max(0, FinalRect.Height - VerticalPadding * 2));
    public static readonly RoutedEvent ClickEvent = new RoutedEvent("Click", RoutingStrategy.Bubble);

    public static readonly StyledProperty<string> LabelProperty =
        StyledProperty<string>.Register<Button>("Label", "",
            new PropertyOptions { Invalidate = InvalidateKind.Measure });

    public static readonly StyledProperty<double> HorizontalPaddingProperty = StyledProperty<double>.Register<Button>("HorizontalPadding", 0, new() { Invalidate = InvalidateKind.Measure });
    public static readonly StyledProperty<HorizontalAlignment> LabelAlignmentProperty = StyledProperty<HorizontalAlignment>.Register<Button>("LabelAlignment", HorizontalAlignment.Center, new() { Invalidate = InvalidateKind.Render });
    public HorizontalAlignment LabelAlignment { get => GetValue(LabelAlignmentProperty); set => SetValue(LabelAlignmentProperty, value); }
    public Point GetLabelOrigin(Size size)
    {
        var bounds = GetLabelBounds();
        return new(bounds.X + (LabelAlignment is HorizontalAlignment.Left or HorizontalAlignment.Stretch ? 0 : LabelAlignment == HorizontalAlignment.Right ? bounds.Width - size.Width : (bounds.Width - size.Width) / 2), bounds.Y + (bounds.Height - size.Height) / 2);
    }
    public static readonly StyledProperty<double> VerticalPaddingProperty = StyledProperty<double>.Register<Button>("VerticalPadding", 0, new() { Invalidate = InvalidateKind.Measure });
    public double HorizontalPadding { get => GetValue(HorizontalPaddingProperty); set => SetValue(HorizontalPaddingProperty, Math.Max(0, value)); }
    public double VerticalPadding { get => GetValue(VerticalPaddingProperty); set => SetValue(VerticalPaddingProperty, Math.Max(0, value)); }
    private double HPad => HorizontalPadding;
    private double VPad => VerticalPadding;

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
    public Element? Content
    {
        get => _content;
        set
        {
            if (ReferenceEquals(_content, value)) return;
            if (_content?.Parent is Panel old) old.Children.Remove(_content);
            _content = value;
            if (TemplateRoot is null && value is not null) Children.Add(value);
            InvalidateMeasure(); Invalidate();
            if (TemplateRoot is not null) ApplyTemplate();
        }
    }
    private Element? _content;
    public override void Dispose()
    {
        try { base.Dispose(); }
        finally { _content?.Dispose(); }
    }
    protected override void OnTemplateRemoving()
    {
        if (_content?.Parent is Panel old) old.Children.Remove(_content);
        base.OnTemplateRemoving();
    }

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
        if (TemplateRoot is not null) return base.MeasureCore(available);
        if (Content is not null && Content.Parent is null) Children.Add(Content);
        if (Content is not null)
        {
            Content.Measure(new(Math.Max(0, available.Width - HPad * 2), Math.Max(0, available.Height - VPad * 2)));
            return new(Content.DesiredSize.Width + HPad * 2, Content.DesiredSize.Height + VPad * 2);
        }
        var metrics = TextMeasurer.Current.Measure(Label, GetValue(TextBlock.FontSizeProperty));
        return new Size(metrics.Width + HPad * 2, metrics.Height + VPad * 2);
    }

    protected internal override void ArrangeCore(Rect final)
    {
        if (TemplateRoot is not null) base.ArrangeCore(final);
        else Content?.Arrange(new(HPad, VPad, Math.Max(0, final.Width - HPad * 2), Math.Max(0, final.Height - VPad * 2)));
    }
}

[UiContent(UiContentKind.Text, Target = nameof(Label))]
public class CheckBox : Element
{
    /// <summary>Space reserved for the selection indicator; the control theme supplies its size.</summary>
    public static readonly StyledProperty<double> GlyphSizeProperty = StyledProperty<double>.Register<CheckBox>("GlyphSize", 0, new() { Invalidate = InvalidateKind.Measure });
    public double GlyphSize { get => GetValue(GlyphSizeProperty); set => SetValue(GlyphSizeProperty, Math.Max(0, value)); }
    public double EffectiveGlyphSize => GlyphSize;
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
        return new Size(EffectiveGlyphSize + metrics.Width, Math.Max(metrics.Height, EffectiveGlyphSize));
    }

    protected internal override void ArrangeCore(Rect final) { }
}

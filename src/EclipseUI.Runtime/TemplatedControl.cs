namespace EclipseUI.Elements;

/// <summary>Convenient panel base for controls whose content is provided by a visual template.</summary>
public class TemplatedControl : Panel
{
    protected internal override Size MeasureCore(Size available) => Size.Empty;
    protected internal override void ArrangeCore(Rect final) { }
}

/// <summary>Projects caller-owned content. Replacing a template detaches it without disposing it.</summary>
[UiContent(UiContentKind.None)]
public class ContentPresenter : ContentControl
{
    public static readonly StyledProperty<Element?> ContentProperty = StyledProperty<Element?>.Register<ContentPresenter>(nameof(Content), null, new() { Invalidate = InvalidateKind.Measure });
    public ContentPresenter() => PropertyChanged += (_, property) => { if (property == ContentProperty) SyncContent(); };
    public static readonly StyledProperty<string> LabelProperty = StyledProperty<string>.Register<ContentPresenter>(nameof(Label), "", new() { Invalidate = InvalidateKind.Measure });
    public static readonly StyledProperty<double> HorizontalPaddingProperty = StyledProperty<double>.Register<ContentPresenter>(nameof(HorizontalPadding), 0, new() { Invalidate = InvalidateKind.Measure });
    public static readonly StyledProperty<double> VerticalPaddingProperty = StyledProperty<double>.Register<ContentPresenter>(nameof(VerticalPadding), 0, new() { Invalidate = InvalidateKind.Measure });
    public static readonly StyledProperty<HorizontalAlignment> TextAlignmentProperty = StyledProperty<HorizontalAlignment>.Register<ContentPresenter>(nameof(TextAlignment), HorizontalAlignment.Center, new() { Invalidate = InvalidateKind.Render });
    public string Label { get => GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public double HorizontalPadding { get => GetValue(HorizontalPaddingProperty); set => SetValue(HorizontalPaddingProperty, Math.Max(0, value)); }
    public double VerticalPadding { get => GetValue(VerticalPaddingProperty); set => SetValue(VerticalPaddingProperty, Math.Max(0, value)); }
    public HorizontalAlignment TextAlignment { get => GetValue(TextAlignmentProperty); set => SetValue(TextAlignmentProperty, value); }
    private Element? _presented;
    public new Element? Content
    {
        get => GetValue(ContentProperty);
        set => SetValue(ContentProperty, value);
    }
    internal void SyncContent()
    {
        if (TemplatedParent?.IsBuildingTemplate == true) return;
        var value = Content;
        // Handwritten factories assign Content before the template engine can set
        // TemplatedParent. Keep the current projection intact until validation ends.
        for (var owner = value; owner is not null; owner = owner.Parent)
            if (owner.IsBuildingTemplate) return;
        if (ReferenceEquals(value, _presented)) return;
        for (Element? ancestor = this; ancestor is not null; ancestor = ancestor.Parent)
            if (ReferenceEquals(value, ancestor)) throw new InvalidOperationException("Content cannot contain its presenter or an ancestor.");
        if (value?.Parent is Panel old) old.Children.Remove(value);
        _presented = value;
        base.Content = value;
    }
    internal void ReleaseContent() { base.Content = null; _presented = null; }
    public override void Dispose() { ReleaseContent(); base.Dispose(); }
    protected internal override Size MeasureCore(Size available)
    {
        var inner = new Size(Math.Max(0, available.Width - 2 * HorizontalPadding), Math.Max(0, available.Height - 2 * VerticalPadding));
        Content?.Measure(inner);
        var text = TextMeasurer.Current.Measure(Label, GetValue(TextBlock.FontSizeProperty));
        var size = Content?.DesiredSize ?? new Size(text.Width, text.Height);
        return new(size.Width + 2 * HorizontalPadding, size.Height + 2 * VerticalPadding);
    }
    protected internal override void ArrangeCore(Rect final) => Content?.Arrange(new(HorizontalPadding, VerticalPadding,
        Math.Max(0, final.Width - 2 * HorizontalPadding), Math.Max(0, final.Height - 2 * VerticalPadding)));
    public override Rect? GetContentClip() => new Rect(HorizontalPadding, VerticalPadding,
        Math.Max(0, FinalRect.Width - 2 * HorizontalPadding), Math.Max(0, FinalRect.Height - 2 * VerticalPadding));
    public override void Render(IElementDrawingContext context)
    {
        if (Content is not null) return;
        var font = GetValue(TextBlock.FontSizeProperty);
        var text = TextMeasurer.Current.Measure(Label, font);
        var x = TextAlignment == HorizontalAlignment.Left ? HorizontalPadding : TextAlignment == HorizontalAlignment.Right ? FinalRect.Width - HorizontalPadding - text.Width : (FinalRect.Width - text.Width) / 2;
        context.PushClip(new(HorizontalPadding, VerticalPadding,
            Math.Max(0, FinalRect.Width - HorizontalPadding * 2), Math.Max(0, FinalRect.Height - VerticalPadding * 2)));
        context.Text(Label, new(x, (FinalRect.Height - text.Height) / 2), font, Foreground);
        context.PopClip();
    }
}

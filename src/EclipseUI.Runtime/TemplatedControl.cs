namespace EclipseUI.Elements;

/// <summary>Behavioral control with an independently replaceable visual tree.</summary>
public class TemplatedControl : Panel
{
    public static readonly StyledProperty<UiColor> BackgroundProperty = StyledProperty<UiColor>.Register<TemplatedControl>(nameof(Background), UiColor.Transparent, new() { Invalidate = InvalidateKind.Render });
    public static readonly StyledProperty<UiColor> ForegroundProperty = StyledProperty<UiColor>.Register<TemplatedControl>(nameof(Foreground), new(0, 0, 0), new() { Inherits = true, Invalidate = InvalidateKind.Render });
    public static readonly StyledProperty<UiColor> BorderBrushProperty = StyledProperty<UiColor>.Register<TemplatedControl>(nameof(BorderBrush), UiColor.Transparent, new() { Invalidate = InvalidateKind.Render });
    public static readonly StyledProperty<double> CornerRadiusProperty = StyledProperty<double>.Register<TemplatedControl>(nameof(CornerRadius), 4, new() { Invalidate = InvalidateKind.Render });
    public UiColor Background { get => GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }
    public UiColor Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public UiColor BorderBrush { get => GetValue(BorderBrushProperty); set => SetValue(BorderBrushProperty, value); }
    public double CornerRadius { get => GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, Math.Max(0, value)); }
    public static readonly StyledProperty<ControlTemplate?> TemplateProperty = StyledProperty<ControlTemplate?>.Register<TemplatedControl>(nameof(Template), null, new() { Invalidate = InvalidateKind.Measure });
    public static readonly StyledProperty<ControlTheme?> ThemeProperty = StyledProperty<ControlTheme?>.Register<TemplatedControl>(nameof(Theme), null, new() { Invalidate = InvalidateKind.Measure });
    public ControlTemplate? Template { get => GetValue(TemplateProperty); set => SetValue(TemplateProperty, value); }
    public ControlTheme? Theme { get => GetValue(ThemeProperty); set => SetValue(ThemeProperty, value); }
    public Element? TemplateRoot => _instance?.Root;
    public override bool UsesDefaultDrawing => TemplateRoot is null;
    private TemplateInstance? _instance;
    private ControlTemplate? _appliedTemplate;
    private ControlTheme? _appliedTheme;
    private bool _applying;
    internal bool IsBuildingTemplate { get; private set; }
    private long _lastTemplateVersion = -1;
    private IElementThemeDefaults? _lastDefaults;

    public TemplatedControl() => PropertyChanged += (_, _) => { if (!_applying) RefreshTemplate(force: true); };
    protected override void PrepareLayout() { base.PrepareLayout(); RefreshTemplate(); }
    protected override void OnStylesChanged() { base.OnStylesChanged(); RefreshTemplate(force: true); }
    protected override void OnUpdated() { base.OnUpdated(); RefreshTemplate(force: true); }
    protected virtual void OnApplyTemplate(TemplateInstance instance) { }
    protected virtual void OnTemplateRemoving() { }
    public void ApplyTemplate() { EnsureInitialized(); RefreshTemplate(force: true); }

    private void RefreshTemplate(bool force = false)
    {
        if (_applying || !IsInitialized || IsDisposed) return;
        if (!force && !_needsMeasure && _lastTemplateVersion == RenderVersion && ReferenceEquals(_lastDefaults, ThemeDefaults)) return;
        _applying = true;
        try
        {
            var theme = Theme;
            if (!ReferenceEquals(theme, _appliedTheme))
            {
                ReplaceControlThemeValues(new Dictionary<StyledProperty, ThemeSetter>());
                if (_instance is not null)
                    foreach (var element in _instance.Elements) element.ReplaceControlThemeValues(new Dictionary<StyledProperty, ThemeSetter>());
            }
            theme?.Apply(this, null);
            var template = Template ?? theme?.Template;
            if (!ReferenceEquals(template, _appliedTemplate))
            {
                if (template is not null && !template.TargetType.IsInstanceOfType(this))
                    throw new InvalidOperationException($"Template requires {template.TargetType.Name}.");
                var previous = _instance;
                var focusManager = FocusManager.GetFor(this);
                var focused = focusManager?.Focused;
                var restoreFocus = focused is not null && !ReferenceEquals(focused, this) &&
                    Descendants().Contains(focused) && (previous is null || !previous.Elements.Contains(focused));
                var focusVisibility = focused?.IsFocusVisible == true ? FocusVisibility.Show : FocusVisibility.Auto;
                TemplateInstance? next = null;
                if (template is not null)
                {
                    try
                    {
                        IsBuildingTemplate = true;
                        next = template.Build(this);
                        if (ReferenceEquals(next.Root, this) || next.Root.Parent is not null || next.Root.IsDisposed)
                        {
                            next = null; // A foreign or already installed tree must not be destroyed on failure.
                            throw new InvalidOperationException("A template must return a fresh, unattached visual root.");
                        }
                        if (next.Elements.Any(e => ReferenceEquals(e, this))) throw new InvalidOperationException("A template cannot contain its owner.");
                        var names = new HashSet<string>(StringComparer.Ordinal);
                        foreach (var element in next.Elements)
                        {
                            if (element.TemplatedParent is null) element.TemplatedParent = this;
                            if (ReferenceEquals(element.TemplatedParent, this) && element.Part is { Length: > 0 } name && !names.Add(name))
                                throw new InvalidOperationException($"Duplicate template part '{name}'.");
                        }
                    }
                    catch { next?.Dispose(); throw; }
                    finally { IsBuildingTemplate = false; }
                }
                try
                {
                    OnTemplateRemoving();
                    previous?.ReleaseContent();
                    if (previous is not null) Children.Remove(previous.Root);
                    _instance = next;
                    if (next is not null)
                    {
                        Children.Add(next.Root);
                        next.ActivateContent();
                        OnApplyTemplate(next);
                    }
                    _appliedTemplate = template;
                }
                catch
                {
                    next?.Dispose();
                    _instance = previous;
                    if (previous is not null) { Children.Add(previous.Root); previous.ActivateContent(); }
                    if (restoreFocus && focusManager?.Focused is null) focused!.RequestFocus(FocusOrigin.Restore, focusVisibility);
                    throw;
                }
                previous?.Dispose();
                if (restoreFocus && focusManager?.Focused is null && focused!.IsAttached)
                    focused.RequestFocus(FocusOrigin.Restore, focusVisibility);
                InvalidateMeasure(); InvalidateRender();
            }
            _appliedTheme = theme;
            _instance?.Update();
            theme?.Apply(this, _instance);
            Styling.Provider?.ApplyTemplateStyles(this);
            _lastTemplateVersion = RenderVersion;
            _lastDefaults = ThemeDefaults;
        }
        finally { _applying = false; }
    }

    protected internal override Size MeasureCore(Size available)
    {
        if (_instance is null) return Size.Empty;
        _instance.Root.Measure(available); return _instance.Root.DesiredSize;
    }
    protected internal override void ArrangeCore(Rect final)
    {
        _instance?.Root.Arrange(new(0, 0, final.Width, final.Height));
    }
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
    public static readonly StyledProperty<UiColor> ForegroundProperty = StyledProperty<UiColor>.Register<ContentPresenter>(nameof(Foreground), new(0, 0, 0), new() { Inherits = true, Invalidate = InvalidateKind.Render });
    public string Label { get => GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public double HorizontalPadding { get => GetValue(HorizontalPaddingProperty); set => SetValue(HorizontalPaddingProperty, Math.Max(0, value)); }
    public double VerticalPadding { get => GetValue(VerticalPaddingProperty); set => SetValue(VerticalPaddingProperty, Math.Max(0, value)); }
    public HorizontalAlignment TextAlignment { get => GetValue(TextAlignmentProperty); set => SetValue(TextAlignmentProperty, value); }
    public UiColor Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
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
        context.Text(Label, new(x, (FinalRect.Height - text.Height) / 2), font, Foreground);
    }
}

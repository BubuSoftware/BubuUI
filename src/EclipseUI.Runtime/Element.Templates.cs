using EclipseUI.Elements;

namespace EclipseUI;

public abstract partial class Element
{
    [UiAnimatable]
    public static readonly StyledProperty<UiColor> BackgroundProperty = StyledProperty<UiColor>.Register<Element>(nameof(Background), UiColor.Transparent, new() { Invalidate = InvalidateKind.Render, Animatable = true });
    public static readonly StyledProperty<UiColor> ForegroundProperty = StyledProperty<UiColor>.Register<Element>(nameof(Foreground), new(0, 0, 0), new() { Inherits = true, Invalidate = InvalidateKind.Render });
    public static readonly StyledProperty<UiColor> BorderBrushProperty = StyledProperty<UiColor>.Register<Element>(nameof(BorderBrush), UiColor.Transparent, new() { Invalidate = InvalidateKind.Render });
    [UiAnimatable]
    public static readonly StyledProperty<double> CornerRadiusProperty = StyledProperty<double>.Register<Element>(nameof(CornerRadius), 0, new() { Invalidate = InvalidateKind.Render, Animatable = true });
    public UiColor Background { get => GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }
    public UiColor Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public UiColor BorderBrush { get => GetValue(BorderBrushProperty); set => SetValue(BorderBrushProperty, value); }
    public double CornerRadius { get => GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, Math.Max(0, value)); }
    public static readonly StyledProperty<ControlTemplate?> TemplateProperty = StyledProperty<ControlTemplate?>.Register<Element>(nameof(Template), null, new() { Invalidate = InvalidateKind.Measure });
    public static readonly StyledProperty<ControlTheme?> ThemeProperty = StyledProperty<ControlTheme?>.Register<Element>(nameof(Theme), null, new() { Invalidate = InvalidateKind.Measure });
    public ControlTemplate? Template { get => GetValue(TemplateProperty); set => SetValue(TemplateProperty, value); }
    public ControlTheme? Theme { get => GetValue(ThemeProperty); set => SetValue(ThemeProperty, value); }
    [UiIgnore] public Element? TemplateRoot => _templateInstance?.Root;
    [UiIgnore] public Element? ContentParent => _parentCollection?.Owner ?? Parent;
    private TemplateInstance? _templateInstance;
    private ElementCollection? _templateRoots;
    private ControlTemplate? _appliedTemplate;
    private ControlTheme? _appliedTheme;
    private bool _applyingTemplate;
    internal bool IsBuildingTemplate { get; private set; }
    private long _lastTemplateVersion = -1;
    private IElementThemeDefaults? _lastTemplateDefaults;

    protected Element() => PropertyChanged += (_, _) => RefreshTemplate(force: true);
    protected virtual void OnApplyTemplate(TemplateInstance instance) { }
    protected virtual void OnTemplateRemoving() { }
    public void ApplyTemplate() { EnsureInitialized(); RefreshTemplate(force: true); }
    protected override void OnUpdated() { base.OnUpdated(); RefreshTemplate(force: true); }

    private void InvalidateTemplateLayout(bool measure)
    {
        if (_templateInstance is null) return;
        // A presenter delegates to its owner's layout. Its local rectangle can remain
        // unchanged when owner properties such as scroll offsets change.
        foreach (var element in _templateInstance.Elements)
        {
            if (measure) element._needsMeasure = true;
            element._needsArrange = true;
        }
    }

    private void RefreshTemplate(bool force = false)
    {
        if (_applyingTemplate || !IsInitialized || IsDisposed) return;
        if (!force && !_needsMeasure && _lastTemplateVersion == RenderVersion && ReferenceEquals(_lastTemplateDefaults, ThemeDefaults)) return;
        var suppressTransitions = _suppressTransitions;
        if (_lastTemplateVersion < 0) _suppressTransitions = true;
        _applyingTemplate = true;
        try
        {
            var theme = Theme;
            if (!ReferenceEquals(theme, _appliedTheme))
            {
                using (BeginValueBatch())
                {
                    ClearControlThemeTransitions();
                    ReplaceControlThemeValues(new Dictionary<StyledProperty, ThemeSetter>());
                    theme?.Apply(this, null);
                }
                if (_templateInstance is not null)
                    foreach (var element in _templateInstance.Elements)
                    {
                        using (element.BeginValueBatch())
                        {
                            element.ClearControlThemeTransitions();
                            element.ReplaceControlThemeValues(new Dictionary<StyledProperty, ThemeSetter>());
                        }
                    }
            }
            else theme?.Apply(this, null);
            var template = Template ?? theme?.Template;
            if (!ReferenceEquals(template, _appliedTemplate))
            {
                if (template is not null && !template.TargetType.IsInstanceOfType(this))
                    throw new InvalidOperationException($"Template requires {template.TargetType.Name}.");
                var previous = _templateInstance;
                var focus = FocusManager.GetFor(this);
                var focused = focus?.Focused;
                var restore = focused is not null && !ReferenceEquals(focused, this) && Descendants().Contains(focused) &&
                    (previous is null || !previous.Elements.Contains(focused));
                var visibility = focused?.IsFocusVisible == true ? FocusVisibility.Show : FocusVisibility.Auto;
                TemplateInstance? next = null;
                if (template is not null)
                {
                    try
                    {
                        IsBuildingTemplate = true;
                        next = template.Build(this);
                        next.SetOwner(this);
                        if (ReferenceEquals(next.Root, this) || next.Root.Parent is not null || next.Root.IsDisposed)
                        { next = null; throw new InvalidOperationException("A template must return a fresh, unattached visual root."); }
                        if (next.Elements.Any(e => ReferenceEquals(e, this))) throw new InvalidOperationException("A template cannot contain its owner.");
                        var names = new HashSet<string>(StringComparer.Ordinal);
                        foreach (var element in next.Elements)
                        {
                            element.TemplatedParent ??= this;
                            if (ReferenceEquals(element.TemplatedParent, this) && element.Part is { Length: > 0 } name && !names.Add(name))
                                throw new InvalidOperationException($"Duplicate template part '{name}'.");
                        }
                    }
                    catch { next?.Dispose(); throw; }
                    finally { IsBuildingTemplate = false; }
                }
                var removingCompleted = false;
                try
                {
                    OnTemplateRemoving();
                    removingCompleted = true;
                    previous?.ReleaseContent();
                    if (previous is not null) _templateRoots!.Remove(previous.Root);
                    _templateInstance = next;
                    if (next is not null)
                    {
                        (_templateRoots ??= new(this)).Add(next.Root);
                        next.ActivateContent();
                        OnApplyTemplate(next);
                    }
                    _appliedTemplate = template;
                }
                catch
                {
                    if (next is not null)
                    {
                        next.ReleaseContent();
                        _templateRoots?.Remove(next.Root);
                        next.Dispose();
                    }
                    _templateInstance = previous;
                    if (previous is not null)
                    {
                        if (previous.Root.Parent is null) _templateRoots!.Add(previous.Root);
                        previous.ActivateContent();
                        if (removingCompleted) OnApplyTemplate(previous);
                    }
                    if (restore && focus?.Focused is null) focused!.RequestFocus(FocusOrigin.Restore, visibility);
                    throw;
                }
                previous?.Dispose();
                if (restore && focus?.Focused is null && focused!.IsAttached) focused.RequestFocus(FocusOrigin.Restore, visibility);
                InvalidateMeasure();
                _renderVersion++;
            }
            _appliedTheme = theme;
            _templateInstance?.Update();
            theme?.Apply(this, _templateInstance);
            Styling.Provider?.ApplyTemplateStyles(this);
            if (_templateInstance is not null)
                foreach (var element in _templateInstance.Elements) element._renderVersion++;
            _lastTemplateVersion = RenderVersion;
            _lastTemplateDefaults = ThemeDefaults;
        }
        finally { _applyingTemplate = false; _suppressTransitions = suppressTransitions; }
    }
}

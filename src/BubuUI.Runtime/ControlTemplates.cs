using BubuUI.Elements;

namespace BubuUI;

/// <summary>A reusable definition. Each application creates a fresh, owned visual tree.</summary>
public abstract class ControlTemplate
{
    public abstract Type TargetType { get; }
    public abstract TemplateInstance Build(Element owner);
}

public abstract class ControlTemplate<T> : ControlTemplate where T : Element
{
    public sealed override Type TargetType => typeof(T);
    public sealed override TemplateInstance Build(Element owner) =>
        owner is T typed ? Build(typed) : throw new ArgumentException($"Template requires {typeof(T).Name}.");
    protected abstract TemplateInstance Build(T owner);
}

public sealed class FuncControlTemplate<T>(Func<T, TemplateInstance> build) : ControlTemplate<T> where T : Element
{
    protected override TemplateInstance Build(T owner) => build(owner);
}

public sealed class TemplateInstance : IDisposable
{
    private readonly Action? _update;
    private Element? _owner;
    private bool _disposed;
    public Element Root { get; }
    public TemplateInstance(Element root, Action? update = null)
    {
        Root = root ?? throw new ArgumentNullException(nameof(root));
        if (root.Parent is not null || root.IsDisposed) throw new ArgumentException("Template root must be new and unattached.");
        _update = update;
    }
    internal void SetOwner(Element owner) => _owner = owner;
    public IEnumerable<Element> Elements => OwnedElements(Root);
    private IEnumerable<Element> OwnedElements(Element root)
    {
        yield return root;
        if (root is ContentPresenter or ItemsPresenter) yield break;
        // Authored nested control content belongs to this template even after that control
        // installs its own skin. Its generated template and data-created items do not.
        if (root is Panel panel)
            foreach (var child in panel.Children)
                if (_update is null || ReferenceEquals(child.TemplatedParent, _owner) || child.IsLayoutTransparent)
                    foreach (var descendant in OwnedElements(child)) yield return descendant;
    }
    public T? FindPart<T>(string name) where T : Element => Elements.FirstOrDefault(e => e.Part == name) as T;
    public void Update() { if (!_disposed) _update?.Invoke(); }
    public void ReleaseContent()
    {
        foreach (var presenter in Elements.OfType<ContentPresenter>().ToArray()) presenter.ReleaseContent();
        foreach (var presenter in Elements.OfType<ItemsPresenter>().ToArray()) presenter.ReleaseContent();
    }
    internal void ActivateContent()
    {
        foreach (var presenter in Elements.OfType<ContentPresenter>()) presenter.SyncContent();
        foreach (var presenter in Elements.OfType<ItemsPresenter>()) presenter.ActivateContent();
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ReleaseContent();
        Root.Dispose();
    }
}

/// <summary>Immutable setters are evaluated per instance; no element is stored in a theme.</summary>
public sealed class ThemeSetter
{
    internal StyledProperty Property { get; }
    public bool IsTransition { get; }
    private readonly Action<Element, bool> _apply;
    private ThemeSetter(StyledProperty property, Action<Element, bool> apply, bool isTransition = false) { Property = property; _apply = apply; IsTransition = isTransition; }
    public static ThemeSetter Value<T>(StyledProperty<T> property, T value) =>
        new(property, (e, style) => { if (style) e.SetRuleValue(property, value); else e.SetControlThemeValue(property, value); });
    public static ThemeSetter Dynamic<T>(StyledProperty<T> property, Func<Element, T> value) =>
        new(property, (e, style) => { if (style) e.SetRuleValue(property, value(e)); else e.SetControlThemeValue(property, value(e)); });
    public static ThemeSetter Transition<T>(StyledProperty<T> property, TransitionSpec spec) =>
        new(property, (e, style) => { if (style) e.SetRuleTransition(property, spec); else e.SetControlThemeTransition(property, spec); }, true);
    internal void Apply(Element element) => _apply(element, false);
    public void ApplyStyle(Element element) => _apply(element, true);
    public StyledProperty TargetProperty => Property;
}

public sealed record ThemeRule(PseudoStates States, Func<Element, bool>? PartSelector, IReadOnlyList<ThemeSetter> Setters);

public sealed class ControlTheme
{
    public Type TargetType { get; }
    public ControlTemplate Template { get; }
    public IReadOnlyList<ThemeRule> Rules { get; }
    public ControlTheme(Type targetType, ControlTemplate template, params ThemeRule[] rules)
    {
        if (!typeof(Element).IsAssignableFrom(targetType) || !template.TargetType.IsAssignableFrom(targetType))
            throw new ArgumentException("Control theme and template target types are incompatible.");
        TargetType = targetType; Template = template;
        Rules = Array.AsReadOnly(rules.Select(r => r with { Setters = Array.AsReadOnly(r.Setters.ToArray()) }).ToArray());
    }
    internal void Apply(Element owner, TemplateInstance? instance)
    {
        if (!TargetType.IsInstanceOfType(owner)) throw new InvalidOperationException($"Theme requires {TargetType.Name}.");
        ApplyTo(owner, owner, false);
        if (instance is not null)
            foreach (var part in instance.Elements.Where(e => ReferenceEquals(e.TemplatedParent, owner))) ApplyTo(owner, part, true);
    }
    private void ApplyTo(Element owner, Element target, bool part)
    {
        var setters = new Dictionary<StyledProperty, ThemeSetter>();
        var transitions = new Dictionary<StyledProperty, ThemeSetter>();
        foreach (var rule in Rules)
            if ((owner.PseudoStates & rule.States) == rule.States &&
                (part ? rule.PartSelector?.Invoke(target) == true : rule.PartSelector is null))
                foreach (var setter in rule.Setters) (setter.IsTransition ? transitions : setters)[setter.Property] = setter;
        using var batch = target.BeginValueBatch();
        target.ReplaceControlThemeTransitions(transitions);
        target.ReplaceControlThemeValues(setters);
    }
}

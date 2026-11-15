using EclipseUI.Elements;

namespace EclipseUI;

/// <summary>A reusable definition. Each application creates a fresh, owned visual tree.</summary>
public abstract class ControlTemplate
{
    public abstract Type TargetType { get; }
    public abstract TemplateInstance Build(TemplatedControl owner);
}

public abstract class ControlTemplate<T> : ControlTemplate where T : TemplatedControl
{
    public sealed override Type TargetType => typeof(T);
    public sealed override TemplateInstance Build(TemplatedControl owner) =>
        owner is T typed ? Build(typed) : throw new ArgumentException($"Template requires {typeof(T).Name}.");
    protected abstract TemplateInstance Build(T owner);
}

public sealed class FuncControlTemplate<T>(Func<T, TemplateInstance> build) : ControlTemplate<T> where T : TemplatedControl
{
    protected override TemplateInstance Build(T owner) => build(owner);
}

public sealed class TemplateInstance : IDisposable
{
    private readonly Action? _update;
    private bool _disposed;
    public Element Root { get; }
    public TemplateInstance(Element root, Action? update = null)
    {
        Root = root ?? throw new ArgumentNullException(nameof(root));
        if (root.Parent is not null || root.IsDisposed) throw new ArgumentException("Template root must be new and unattached.");
        _update = update;
    }
    public IEnumerable<Element> Elements => OwnedElements(Root);
    private static IEnumerable<Element> OwnedElements(Element root)
    {
        yield return root;
        if (root is ContentPresenter or TemplatedControl) yield break;
        if (root is Panel panel)
            foreach (var child in panel.Children)
                foreach (var descendant in OwnedElements(child)) yield return descendant;
    }
    public T? FindPart<T>(string name) where T : Element => Elements.FirstOrDefault(e => e.Part == name) as T;
    public void Update() { if (!_disposed) _update?.Invoke(); }
    public void ReleaseContent()
    {
        foreach (var presenter in Elements.OfType<ContentPresenter>().ToArray()) presenter.ReleaseContent();
    }
    internal void ActivateContent()
    {
        foreach (var presenter in Elements.OfType<ContentPresenter>()) presenter.SyncContent();
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
    private readonly Action<Element, bool> _apply;
    private ThemeSetter(StyledProperty property, Action<Element, bool> apply) { Property = property; _apply = apply; }
    public static ThemeSetter Value<T>(StyledProperty<T> property, T value) =>
        new(property, (e, style) => { if (style) e.SetRuleValue(property, value); else e.SetControlThemeValue(property, value); });
    public static ThemeSetter Dynamic<T>(StyledProperty<T> property, Func<Element, T> value) =>
        new(property, (e, style) => { if (style) e.SetRuleValue(property, value(e)); else e.SetControlThemeValue(property, value(e)); });
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
        if (!typeof(TemplatedControl).IsAssignableFrom(targetType) || !template.TargetType.IsAssignableFrom(targetType))
            throw new ArgumentException("Control theme and template target types are incompatible.");
        TargetType = targetType; Template = template;
        Rules = Array.AsReadOnly(rules.Select(r => r with { Setters = Array.AsReadOnly(r.Setters.ToArray()) }).ToArray());
    }
    internal void Apply(TemplatedControl owner, TemplateInstance? instance)
    {
        if (!TargetType.IsInstanceOfType(owner)) throw new InvalidOperationException($"Theme requires {TargetType.Name}.");
        ApplyTo(owner, owner, false);
        if (instance is not null)
            foreach (var part in instance.Elements.Where(e => ReferenceEquals(e.TemplatedParent, owner))) ApplyTo(owner, part, true);
    }
    private void ApplyTo(TemplatedControl owner, Element target, bool part)
    {
        var setters = new Dictionary<StyledProperty, ThemeSetter>();
        foreach (var rule in Rules)
            if ((owner.PseudoStates & rule.States) == rule.States &&
                (part ? rule.PartSelector?.Invoke(target) == true : rule.PartSelector is null))
                foreach (var setter in rule.Setters) setters[setter.Property] = setter;
        target.ReplaceControlThemeValues(setters);
    }
}

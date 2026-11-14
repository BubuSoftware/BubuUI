namespace EclipseUI.Rendering;

/// <summary>Resolves a window's theme on its UI thread without replacing unchanged palettes.</summary>
public sealed class ThemeBinding
{
    private IHostTheme _source;
    private readonly ISystemThemeProvider? _system;
    public ThemeBinding(IHostTheme source, ISystemThemeProvider? system = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _system = system;
        Current = Resolve();
    }
    public ElementTheme Current { get; private set; }
    public void SetSource(IHostTheme source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var previous = _source;
        _source = source;
        try { Current = Resolve(); }
        catch { _source = previous; throw; }
    }
    public ElementTheme Resolve()
    {
        var resolved = _source is IHostThemeSource source ? source.Resolve(_system) : _source;
        return resolved as ElementTheme ?? throw new ArgumentException("Theme must resolve to an ElementTheme.");
    }
    public void Update(ElementPainter painter, Element root)
    {
        Current = Resolve();
        painter.ReplaceTheme(Current);
        root.SetThemeDefaults(Current as IElementThemeDefaults);
    }
}

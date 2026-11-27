namespace BubuUI;

public enum ThemeVariant { System, Light, Dark }

/// <summary>Platform preference, queried on the UI thread. Unknown preferences use Light.</summary>
public interface ISystemThemeProvider
{
    ThemeVariant Variant { get; }
}

/// <summary>A theme which resolves to a concrete palette, including follow-system themes.</summary>
public interface IHostThemeSource : IHostTheme
{
    IHostTheme Resolve(ISystemThemeProvider? systemTheme = null);
}

/// <summary>Scoped defaults below explicit, template, class and inherited values.</summary>
public interface IElementThemeDefaults
{
    bool IsLight => true;
    T GetDefault<T>(Element element, StyledProperty<T> property);
}

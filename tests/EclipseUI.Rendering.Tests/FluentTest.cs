global using EclipseUI.Themes.Fluent;

namespace EclipseUI.Rendering.Tests;

internal static class FluentTest
{
    /// <summary>Visual-control fixtures explicitly install a skin before their first layout.</summary>
    internal static T Skin<T>(T root) where T : Element
    {
        if (root.ThemeDefaults is null) root.SetThemeDefaults(FluentTheme.Dark);
        return root;
    }

    internal static int VisualCount(Element root) => 1 + root.Descendants().Count();
}

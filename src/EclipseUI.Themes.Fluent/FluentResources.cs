using EclipseUI.Rendering;
using EclipseUI.Elements;

namespace EclipseUI.Themes.Fluent;

/// <summary>Typed shared palette resources used by every compiled Fluent skin.</summary>
public static class FluentResources
{
    private static ThemeResource<UiColor> Color(Func<FluentTheme, Color> select) => Color((_, theme) => select(theme));
    private static ThemeResource<UiColor> Color(Func<Element, FluentTheme, Color> select) => new(element =>
    {
        var color = select(element, element.ThemeDefaults as FluentTheme ?? FluentTheme.Light);
        return new(color.R, color.G, color.B, color.A);
    });
    public static readonly ThemeResource<UiColor> ControlBackground = Color(theme => theme.ControlBackground);
    public static readonly ThemeResource<UiColor> Foreground = Color(theme => theme.Foreground);
    public static readonly ThemeResource<UiColor> ControlBorder = Color(theme => theme.ControlBorder);
    public static readonly ThemeResource<UiColor> ControlHoverBackground = Color(theme => theme.ControlHoverBackground);
    public static readonly ThemeResource<UiColor> ControlPressedBackground = Color(theme => theme.ControlPressedBackground);
    public static readonly ThemeResource<UiColor> PressedForeground = Color(theme => theme.PressedForeground);
    public static readonly ThemeResource<UiColor> DisabledBackground = Color(theme => theme.DisabledBackground);
    public static readonly ThemeResource<UiColor> DisabledForeground = Color(theme => theme.DisabledForeground);
    public static readonly ThemeResource<UiColor> InputActiveBackground = Color(theme => theme.InputActiveBackground);
    public static readonly ThemeResource<UiColor> PopupBackground = Color(theme => theme.PopupBackground);
    public static readonly ThemeResource<UiColor> PopupBorder = Color(theme => theme.PopupBorder);
    public static readonly ThemeResource<UiColor> ListItemBackground = Color((element, theme) =>
        element is ListBoxItem { IsSelected: true } ? theme.SelectionBackground :
        element.IsPressed ? theme.ItemPressedBackground : element.IsHovered ? theme.ItemHoverBackground : theme.ItemBackground);
    public static readonly ThemeResource<UiColor> TreeItemBackground = Color((element, theme) =>
        element is TreeViewItem { IsSelected: true } ? theme.SelectionBackground :
        element.IsPressed ? theme.ItemPressedBackground : element.IsHovered ? theme.ItemHoverBackground : theme.ItemBackground);
    public static readonly ThemeResource<UiColor> ItemHoverBackground = Color(theme => theme.ItemHoverBackground);
    public static readonly ThemeResource<UiColor> ControlAltBackground = Color(theme => theme.ControlAltBackground);
    public static readonly ThemeResource<UiColor> ControlAltHoverBackground = Color(theme => theme.ControlAltHoverBackground);
    public static readonly ThemeResource<UiColor> ControlAltPressedBackground = Color(theme => theme.ControlAltPressedBackground);
    public static readonly ThemeResource<UiColor> ControlStrongBorder = Color(theme => theme.ControlStrongBorder);
    public static readonly ThemeResource<UiColor> ControlStrongDisabledBorder = Color(theme => theme.ControlStrongDisabledBorder);
}

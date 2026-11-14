using EclipseUI.Elements;
using EclipseUI.Rendering;

namespace EclipseUI.Themes.Fluent;

/// <summary>Fluent 2 inspired desktop palettes. All instances are immutable and shared.</summary>
public sealed class FluentTheme : ElementTheme, IElementThemeDefaults
{
    public new static FluentTheme Light { get; } = new(true);
    public new static FluentTheme Dark { get; } = new(false);
    public static IHostTheme System { get; } = new SystemSource();
    public override bool IsLight { get; }
    private readonly Dictionary<StyledProperty, object> _defaults;
    private static readonly object ControlMinHeight = 32d;

    private FluentTheme(bool light)
    {
        IsLight = light;
        Surface = light ? new(243, 243, 243) : new(32, 32, 32);
        PanelBackground = light ? new(255, 255, 255) : new(43, 43, 43);
        PanelBorder = light ? new(229, 229, 229) : new(58, 58, 58);
        ItemBackground = new(0, 0, 0, 0);
        ItemBorder = new(0, 0, 0, 0);
        Foreground = light ? new(36, 36, 36) : new(255, 255, 255);
        MutedForeground = light ? new(97, 97, 97) : new(214, 214, 214);
        Accent = light ? new(15, 108, 189) : new(71, 158, 245);
        AccentPressed = light ? new(12, 59, 94) : new(40, 135, 226);
        OnAccent = light ? new(255, 255, 255) : new(0, 0, 0);
        ControlBackground = light ? new(255, 255, 255) : new(51, 51, 51);
        ControlBorder = light ? new(209, 209, 209) : new(102, 102, 102);
        FocusRing = light ? new(36, 36, 36) : new(255, 255, 255);
        DisabledForeground = light ? new(161, 161, 161) : new(117, 117, 117);
        DisabledBackground = light ? new(240, 240, 240) : new(45, 45, 45);
        ScrollBarThumb = light ? new(138, 138, 138) : new(161, 161, 161);
        PanelRadius = 8; ItemRadius = 4; ButtonRadius = 4; CheckBoxRadius = 4;
        FocusRingWidth = 2; FocusRingOffset = 2; MutedFontSizeBelow = 0;
        _defaults = new()
        {
            [TextBlock.FontSizeProperty] = 14d,
            [Button.HorizontalPaddingProperty] = 12d,
            [Button.VerticalPaddingProperty] = 5d,
            [Elements.Border.BorderColorProperty] = ToUi(ControlBorder),
            [Elements.Border.CornerRadiusProperty] = ControlRadius,
            [Elements.Border.BackgroundProperty] = ToUi(PopupBackground),
        };
    }

    public override Color AccentHover => IsLight ? new(17, 94, 163) : new(98, 171, 245);
    public override Color ButtonBackground => ControlBackground;
    public override Color ButtonHoverBackground => ControlHoverBackground;
    public override Color ButtonPressedBackground => ControlPressedBackground;
    public override Color ButtonForeground => Foreground;
    public override Color ButtonBorder => ControlBorder;
    public override Color ControlHoverBackground => IsLight ? new(245, 245, 245) : new(61, 61, 61);
    public override Color ControlPressedBackground => IsLight ? new(224, 224, 224) : new(41, 41, 41);
    public override Color ControlHoverBorder => IsLight ? new(112, 112, 112) : new(153, 153, 153);
    public override Color DisabledBorder => IsLight ? new(224, 224, 224) : new(66, 66, 66);
    public override Color SelectionBackground => IsLight ? new(235, 243, 252) : new(10, 46, 74);
    public override Color SelectionForeground => Foreground;
    public override Color ItemHoverBackground => IsLight ? new(245, 245, 245) : new(56, 56, 56);
    public override Color ItemPressedBackground => IsLight ? new(224, 224, 224) : new(48, 48, 48);
    public override Color PopupBorder => IsLight ? new(209, 209, 209) : new(82, 82, 82);
    public override Color PopupShadow => new(0, 0, 0, IsLight ? (byte)24 : (byte)64);
    public override double PopupShadowSize => 4;
    public override bool TransparentPanels => true;

    public static IHostTheme For(ThemeVariant variant) => variant switch
    {
        ThemeVariant.System => System, ThemeVariant.Light => Light, ThemeVariant.Dark => Dark,
        _ => throw new ArgumentOutOfRangeException(nameof(variant)),
    };

    public T GetDefault<T>(Element element, StyledProperty<T> property)
    {
        if (ReferenceEquals(property, Element.MinHeightProperty) && element is Button or TextBox or ComboBox or DatePicker)
            return (T)ControlMinHeight;
        // Ordinary borders stay transparent; popup borders share their enclosing surface.
        if (ReferenceEquals(property, Elements.Border.BackgroundProperty) && element.Parent is not Popup)
            return property.DefaultValue;
        return _defaults.TryGetValue(property, out var value) && value is T result ? result : property.DefaultValue;
    }

    private static UiColor ToUi(Color color) => new(color.R, color.G, color.B, color.A);
    private sealed class SystemSource : IHostThemeSource
    {
        public IHostTheme Resolve(ISystemThemeProvider? systemTheme = null) =>
            (systemTheme ?? SystemThemeProvider.Instance).Variant == ThemeVariant.Dark ? Dark : Light;
    }
}

public static class FluentAppBuilderExtensions
{
    /// <summary>Install the default desktop skin; System follows preference changes while the window runs.</summary>
    public static AppBuilder UseFluentTheme(this AppBuilder builder, ThemeVariant variant = ThemeVariant.System)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.UseTheme(FluentTheme.For(variant));
    }
}

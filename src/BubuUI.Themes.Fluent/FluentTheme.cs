using BubuUI.Elements;
using BubuUI.Rendering;

namespace BubuUI.Themes.Fluent;

/// <summary>Windows 11 / WinUI control resources, with a deterministic blue accent fallback.</summary>
public sealed class FluentTheme : ElementTheme, IElementThemeDefaults
{
    public new static FluentTheme Light { get; } = new(true);
    public new static FluentTheme Dark { get; } = new(false);
    public static IHostTheme System { get; } = new SystemSource();
    public override bool IsLight { get; }
    private readonly Dictionary<StyledProperty, object> _defaults;

    public IFluentInteractionPreferences InteractionPreferences { get; }

    public FluentTheme(bool light, IFluentInteractionPreferences? interactionPreferences = null)
    {
        InteractionPreferences = interactionPreferences ?? SystemInteractionPreferences.Instance;
        IsLight = light;
        Surface = light ? new(243, 243, 243) : new(32, 32, 32);
        PanelBackground = light ? new(255, 255, 255) : new(43, 43, 43);
        PanelBorder = light ? new(0, 0, 0, 15) : new(0, 0, 0, 25);
        ItemBackground = new(0, 0, 0, 0);
        ItemBorder = new(0, 0, 0, 0);
        Foreground = light ? new(0, 0, 0, 228) : new(255, 255, 255);
        MutedForeground = light ? new(0, 0, 0, 158) : new(255, 255, 255, 197);
        Accent = light ? new(0, 95, 184) : new(96, 205, 255);
        AccentPressed = Accent with { A = 204 };
        OnAccent = light ? new(255, 255, 255) : new(0, 0, 0);
        ControlBackground = light ? new(255, 255, 255, 179) : new(255, 255, 255, 15);
        ControlBorder = light ? new(0, 0, 0, 15) : new(255, 255, 255, 18);
        FocusRing = light ? new(0, 0, 0, 228) : new(255, 255, 255);
        DisabledForeground = light ? new(0, 0, 0, 92) : new(255, 255, 255, 93);
        DisabledBackground = light ? new(249, 249, 249, 77) : new(255, 255, 255, 11);
        ScrollBarThumb = light ? new(0, 0, 0, 114) : new(255, 255, 255, 139);
        ScrollBarTrack = light ? new(249, 249, 249) : new(44, 44, 44);
        PanelRadius = 8; ItemRadius = 4; ButtonRadius = 4; CheckBoxRadius = 4;
        FocusRingWidth = 2; FocusRingOffset = 2; MutedFontSizeBelow = 0;
        _defaults = new()
        {
            [TextBlock.FontSizeProperty] = 14d,
            [Element.ForegroundProperty] = ToUi(Foreground),
            [Visual.FocusRingWidthProperty] = 2d,
            [Visual.FocusRingOffsetProperty] = 2d,
            [Visual.FocusRingColorProperty] = FocusRing,
        };
    }

    public override Color FocusRingInner => IsLight ? new(255, 255, 255, 179) : new(0, 0, 0, 179);
    public Color ScrollBarDisabled => IsLight ? new(0, 0, 0, 81) : new(255, 255, 255, 63);
    public override Color AccentHover => Accent with { A = 230 };
    public override Color ButtonBackground => ControlBackground;
    public override Color ButtonHoverBackground => ControlHoverBackground;
    public override Color ButtonPressedBackground => ControlPressedBackground;
    public override Color ButtonForeground => Foreground;
    public override Color ButtonBorder => ControlBorder;
    public override Color ControlHoverBackground => IsLight ? new(249, 249, 249, 128) : new(255, 255, 255, 21);
    public override Color ControlPressedBackground => IsLight ? new(249, 249, 249, 77) : new(255, 255, 255, 8);
    public override Color ControlHoverBorder => ControlBorder;
    public override Color DisabledBorder => ControlBorder;
    public override Color ControlSecondaryBorder => IsLight ? new(0, 0, 0, 41) : new(255, 255, 255, 24);
    public override Color ControlStrongBorder => IsLight ? new(0, 0, 0, 114) : new(255, 255, 255, 139);
    public override Color ControlStrongDisabledBorder => IsLight ? new(0, 0, 0, 55) : new(255, 255, 255, 40);
    public override Color ControlAltBackground => new(0, 0, 0, IsLight ? (byte)6 : (byte)25);
    public override Color ControlAltHoverBackground => IsLight ? new(0, 0, 0, 15) : new(255, 255, 255, 11);
    public override Color ControlAltPressedBackground => IsLight ? new(0, 0, 0, 24) : new(255, 255, 255, 18);
    public override Color ThumbBackground => IsLight ? new(255, 255, 255) : new(69, 69, 69);
    public override Color InputActiveBackground => IsLight ? new(255, 255, 255) : new(30, 30, 30, 179);
    public override Color InputBottomBorder => IsLight ? new(0, 0, 0, 114) : new(255, 255, 255, 139);
    public override Color AccentDisabled => IsLight ? new(0, 0, 0, 55) : new(255, 255, 255, 40);
    public override Color AccentBorder => new(255, 255, 255, 20);
    public override Color AccentSecondaryBorder => new(0, 0, 0, IsLight ? (byte)102 : (byte)35);
    public override Color OnAccentDisabled => IsLight ? new(255, 255, 255) : new(255, 255, 255, 135);
    public override Color PressedForeground => MutedForeground;
    public override Color SelectionBackground => ItemHoverBackground;
    public override Color SelectionForeground => Foreground;
    public override Color ItemHoverBackground => IsLight ? new(0, 0, 0, 9) : new(255, 255, 255, 15);
    public override Color ItemPressedBackground => IsLight ? new(0, 0, 0, 6) : new(255, 255, 255, 10);
    public override Color PopupBorder => IsLight ? new(0, 0, 0, 15) : new(0, 0, 0, 51);
    public override Color PopupShadow => new(0, 0, 0, IsLight ? (byte)24 : (byte)64);
    public override double PopupShadowSize => 4;
    public override bool TransparentPanels => true;

    public static IHostTheme For(ThemeVariant variant) => variant switch
    {
        ThemeVariant.System => System, ThemeVariant.Light => Light, ThemeVariant.Dark => Dark,
        _ => throw new ArgumentOutOfRangeException(nameof(variant)),
    };

    private static readonly IReadOnlyDictionary<Type, ControlTheme> ControlThemes = new Dictionary<Type, ControlTheme>
    {
        [typeof(Button)] = StyleBootstrap.FluentButton,
        [typeof(ToggleButton)] = StyleBootstrap.FluentToggleButton,
        [typeof(CheckBox)] = StyleBootstrap.FluentCheckBox,
        [typeof(RadioButton)] = StyleBootstrap.FluentRadioButton,
        [typeof(ToggleSwitch)] = StyleBootstrap.FluentToggleSwitch,
        [typeof(Slider)] = StyleBootstrap.FluentSlider,
        [typeof(ProgressBar)] = StyleBootstrap.FluentProgressBar,
        [typeof(TextBox)] = StyleBootstrap.FluentTextBox,
        [typeof(PasswordBox)] = StyleBootstrap.FluentPasswordBox,
        [typeof(NumericUpDown)] = StyleBootstrap.FluentNumericUpDown,
        [typeof(ComboBox)] = StyleBootstrap.FluentComboBox,
        [typeof(DatePicker)] = StyleBootstrap.FluentDatePicker,
        [typeof(Calendar)] = StyleBootstrap.FluentCalendar,
        [typeof(DropDownButton)] = StyleBootstrap.FluentDropDownButton,
        [typeof(DataGridColumnHeader)] = StyleBootstrap.FluentDataGridColumnHeader,
        [typeof(MenuItem)] = StyleBootstrap.FluentMenuItem,
        [typeof(TreeViewItem)] = StyleBootstrap.FluentTreeViewItem,
        [typeof(TreeView)] = StyleBootstrap.FluentTreeView,
        [typeof(TabControl)] = StyleBootstrap.FluentTabControl,
        [typeof(DataGrid)] = StyleBootstrap.FluentDataGrid,
        [typeof(ListBoxItem)] = StyleBootstrap.FluentListBoxItem,
        [typeof(ItemsControl)] = StyleBootstrap.FluentItemsControl,
        [typeof(ScrollViewer)] = StyleBootstrap.FluentScrollViewer,
        [typeof(Popup)] = StyleBootstrap.FluentPopup,
    };

    public T GetDefault<T>(Element element, StyledProperty<T> property)
    {
        if (ReferenceEquals(property, Element.ThemeProperty))
            for (var type = element.GetType(); type is not null; type = type.BaseType)
                if (ControlThemes.TryGetValue(type, out var theme)) return (T)(object)theme;
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

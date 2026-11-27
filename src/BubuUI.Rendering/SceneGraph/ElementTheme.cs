namespace BubuUI.Rendering;

/// <summary>
/// 场景图绘制的视觉参数（S-A 主题令牌化）：可替换 token 表——宿主换一套实例即换主题
/// （<see cref="ElementPainter.ReplaceTheme"/>），亮暗切换 = 换表 + 全树重绘。
/// 刻意保持「数据 + 默认值」，不引入任何布局语义；控件级覆盖（class/inline）走 S-B 有效值分层。
/// 实现 Runtime 的 <see cref="IHostTheme"/> 标记（D101）：AppBuilder/Window 描述默认主题时
/// 不依赖 Rendering 类型（调用侧直接传 <c>ElementTheme.Dark/Light</c>，隐式向上转型）。
/// </summary>
public class ElementTheme : IHostTheme
{
    /// <summary>预置深色主题（原默认值）。</summary>
    public static ElementTheme Dark { get; } = new();

    /// <summary>预置浅色主题。</summary>
    public static ElementTheme Light { get; } = new LightTheme();

    /// <summary>明暗标志（S-C 主题资源用）：<see cref="ThemeColor"/> 据此取色。
    /// 自定义 token 表按视觉明暗覆盖此属性。</summary>
    public virtual bool IsLight => false;

    // ---- 颜色（深色）----
    public virtual Color Surface { get; init; } = new(15, 17, 25);
    public virtual Color PanelBackground { get; init; } = new(27, 31, 42);
    public virtual Color PanelBorder { get; init; } = new(46, 53, 70);
    public virtual Color ItemBackground { get; init; } = new(35, 41, 55);
    public virtual Color ItemBorder { get; init; } = new(52, 60, 80);
    public virtual Color Foreground { get; init; } = new(232, 237, 247);
    public virtual Color MutedForeground { get; init; } = new(148, 158, 184);
    public virtual Color Accent { get; init; } = new(96, 152, 255);
    public virtual Color AccentPressed { get; init; } = new(70, 122, 224);
    public virtual Color OnAccent { get; init; } = new(255, 255, 255);
    public virtual Color ControlBackground { get; init; } = new(44, 51, 68);
    public virtual Color ControlBorder { get; init; } = new(78, 88, 112);
    public virtual Color FocusRing { get; init; } = new(126, 186, 255);
    public virtual Color FocusRingInner => new(0, 0, 0, 0);
    public virtual Color DisabledForeground { get; init; } = new(122, 128, 148);
    public virtual Color DisabledBackground { get; init; } = new(60, 66, 84);
    public virtual Color ScrollBarTrack { get; init; } = new(0, 0, 0, 0);
    public virtual Color ScrollBarThumb { get; init; } = new(96, 106, 132);

    // Semantic control tokens. Existing custom palettes retain their original defaults.
    public virtual Color AccentHover => Accent;
    public virtual Color ButtonBackground => Accent;
    public virtual Color ButtonHoverBackground => ButtonBackground;
    public virtual Color ButtonPressedBackground => AccentPressed;
    public virtual Color ButtonForeground => OnAccent;
    public virtual Color ButtonBorder => new(0, 0, 0, 0);
    public virtual Color ControlHoverBackground => ControlBackground;
    public virtual Color ControlPressedBackground => ControlBackground;
    public virtual Color ControlHoverBorder => ControlBorder;
    public virtual Color DisabledBorder => ControlBorder;
    public virtual Color SelectionBackground => Accent;
    public virtual Color SelectionForeground => OnAccent;
    public virtual Color ItemHoverBackground => ItemBackground;
    public virtual Color ItemPressedBackground => ItemBackground;
    public virtual Color PopupBackground => PanelBackground;
    public virtual Color PopupBorder => PanelBorder;
    public virtual Color PopupShadow => new(0, 0, 0, 0);
    public virtual double ControlRadius => 4;
    public virtual double PopupRadius => PanelRadius;
    public virtual double PopupShadowSize => 0;
    public virtual bool TransparentPanels => false;
    public virtual Color ControlStrongBorder => ControlBorder;
    public virtual Color ControlStrongDisabledBorder => DisabledBorder;
    public virtual Color ControlAltBackground => ControlBackground;
    public virtual Color ControlAltHoverBackground => ControlHoverBackground;
    public virtual Color ControlAltPressedBackground => ControlPressedBackground;
    public virtual Color ThumbBackground => OnAccent;
    public virtual Color InputActiveBackground => ControlBackground;
    public virtual Color InputBottomBorder => ControlBorder;
    public virtual Color ControlSecondaryBorder => ControlBorder;
    public virtual Color AccentDisabled => DisabledBackground;
    public virtual Color AccentBorder => ControlBorder;
    public virtual Color AccentSecondaryBorder => ControlSecondaryBorder;
    public virtual Color OnAccentDisabled => DisabledForeground;
    public virtual Color PressedForeground => Foreground;

    // ---- 形状 ----
    public virtual double PanelRadius { get; init; } = 12;
    public virtual double ItemRadius { get; init; } = 8;
    public virtual double ButtonRadius { get; init; } = 8;
    public virtual double CheckBoxRadius { get; init; } = 4;
    public virtual double BorderWidth { get; init; } = 1;
    public virtual double FocusRingWidth { get; init; } = 2;
    public virtual double FocusRingOffset { get; init; } = 2;

    // ---- 文本 ----
    /// <summary>小于该字号用次要文本色（G2 的临时规则，样式系统会替代）。</summary>
    public virtual double MutedFontSizeBelow { get; init; } = 14;
}

/// <summary>浅色主题（S-A 预置第二套 token 表，验证可替换性）。</summary>
public sealed class LightTheme : ElementTheme
{
    // ---- 颜色（浅色）----
    public override Color Surface { get; init; } = new(248, 249, 252);
    public override Color PanelBackground { get; init; } = new(255, 255, 255);
    public override Color PanelBorder { get; init; } = new(224, 229, 240);
    public override Color ItemBackground { get; init; } = new(242, 244, 249);
    public override Color ItemBorder { get; init; } = new(222, 227, 238);
    public override Color Foreground { get; init; } = new(30, 34, 44);
    public override Color MutedForeground { get; init; } = new(108, 116, 136);
    public override Color Accent { get; init; } = new(52, 118, 255);
    public override Color AccentPressed { get; init; } = new(38, 96, 220);
    public override Color OnAccent { get; init; } = new(255, 255, 255);
    public override Color ControlBackground { get; init; } = new(244, 246, 251);
    public override Color ControlBorder { get; init; } = new(205, 212, 226);
    public override Color FocusRing { get; init; } = new(52, 118, 255);
    public override Color DisabledForeground { get; init; } = new(158, 164, 180);
    public override Color DisabledBackground { get; init; } = new(226, 229, 236);
    public override Color ScrollBarThumb { get; init; } = new(176, 183, 198);

    // ---- 形状 ----（沿用深色默认：Radius/BorderWidth/FocusRingWidth 不覆盖）

    // ---- 文本 ----
    public override double MutedFontSizeBelow { get; init; } = 14;

    // ---- 明暗 ----
    public override bool IsLight => true;
}

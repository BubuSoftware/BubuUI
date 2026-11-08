namespace EclipseUI.Rendering;

/// <summary>
/// 场景图绘制的默认视觉参数。G2 的临时主题：样式系统（G4）落地后由选择器解析结果替换。
/// 刻意保持“数据 + 默认值”，不引入任何布局语义。
/// </summary>
public sealed class ElementTheme
{
    // ---- 颜色（深色）----
    public Color Surface { get; init; } = new(15, 17, 25);
    public Color PanelBackground { get; init; } = new(27, 31, 42);
    public Color PanelBorder { get; init; } = new(46, 53, 70);
    public Color ItemBackground { get; init; } = new(35, 41, 55);
    public Color ItemBorder { get; init; } = new(52, 60, 80);
    public Color Foreground { get; init; } = new(232, 237, 247);
    public Color MutedForeground { get; init; } = new(148, 158, 184);
    public Color Accent { get; init; } = new(96, 152, 255);
    public Color AccentPressed { get; init; } = new(70, 122, 224);
    public Color OnAccent { get; init; } = new(255, 255, 255);
    public Color ControlBackground { get; init; } = new(44, 51, 68);
    public Color ControlBorder { get; init; } = new(78, 88, 112);
    public Color FocusRing { get; init; } = new(126, 186, 255);
    public Color ScrollBarTrack { get; init; } = new(0, 0, 0, 0);
    public Color ScrollBarThumb { get; init; } = new(96, 106, 132);

    // ---- 形状 ----
    public double PanelRadius { get; init; } = 12;
    public double ItemRadius { get; init; } = 8;
    public double ButtonRadius { get; init; } = 8;
    public double CheckBoxRadius { get; init; } = 4;
    public double BorderWidth { get; init; } = 1;
    public double FocusRingWidth { get; init; } = 2;

    // ---- 文本 ----
    /// <summary>小于该字号用次要文本色（G2 的临时规则，样式系统会替代）。</summary>
    public double MutedFontSizeBelow { get; init; } = 14;
}

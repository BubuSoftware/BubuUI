using EclipseUI;
using EclipseUI.Elements;

namespace EclipseUI.Rendering;

/// <summary>
/// 主题感知颜色（S-C 最小资源查找）：暗/亮各一色，<see cref="ElementPainter"/> 在
/// 构建绘制命令时按当前主题解析成具体颜色——解析发生在段构建期，主题切换的
/// ReplaceTheme → 丢段 → 重建链路天然让资源色跟随主题刷新，无需新增失效维度。
/// </summary>
public readonly record struct ThemeColor(Color Dark, Color Light)
{
    /// <summary>按主题明暗标志取对应色。</summary>
    public Color Resolve(ElementTheme theme) => theme.IsLight ? Light : Dark;
}

/// <summary>
/// 元素级视觉覆盖属性（S-B）：值非空时 <see cref="ElementPainter"/> 用它替换主题令牌，
/// null（默认）回落 <see cref="ElementTheme"/>——现有绘制语义零回归。
/// 模板作者经 Class 样式或 C# 侧 <c>Visual.SetBackground(el, color)</c> 设置。
/// S-C：颜色可升级为 <see cref="ThemeColor"/> 资源（BackgroundResource/ForegroundResource，
/// 优先级最高），按当前主题解析暗/亮两色。
/// </summary>
public static class Visual
{
    public static readonly StyledProperty<Color?> BackgroundProperty =
        StyledProperty<Color?>.Register<Element>("Visual.Background", null);

    public static readonly StyledProperty<Color?> ForegroundProperty =
        StyledProperty<Color?>.Register<Element>("Visual.Foreground", null);

    public static readonly StyledProperty<double?> CornerRadiusProperty =
        StyledProperty<double?>.Register<Element>("Visual.CornerRadius", null);

    // ---- S-C：主题资源（优先级 resource > 静态覆盖 > 主题令牌）----

    public static readonly StyledProperty<ThemeColor?> BackgroundResourceProperty =
        StyledProperty<ThemeColor?>.Register<Element>("Visual.BackgroundResource", null);

    public static readonly StyledProperty<ThemeColor?> ForegroundResourceProperty =
        StyledProperty<ThemeColor?>.Register<Element>("Visual.ForegroundResource", null);

    /// <summary>焦点环宽度覆盖（E3）：null 回落主题令牌 <see cref="ElementTheme.FocusRingWidth"/>。</summary>
    public static readonly StyledProperty<double?> FocusRingWidthProperty =
        StyledProperty<double?>.Register<Element>("Visual.FocusRingWidth", null);

    public static Color? GetBackground(Element element) => element.GetValue(BackgroundProperty);

    /// <summary>null 表示取消覆盖（清除 local 层，回落 class 样式/主题）。</summary>
    public static void SetBackground(Element element, Color? color)
    {
        if (color is null) element.ClearLocalValue(BackgroundProperty);
        else element.SetValue(BackgroundProperty, color);
    }

    public static Color? GetForeground(Element element) => element.GetValue(ForegroundProperty);

    public static void SetForeground(Element element, Color? color)
    {
        if (color is null) element.ClearLocalValue(ForegroundProperty);
        else element.SetValue(ForegroundProperty, color);
    }

    public static double? GetCornerRadius(Element element) => element.GetValue(CornerRadiusProperty);

    public static void SetCornerRadius(Element element, double? radius)
    {
        if (radius is null) element.ClearLocalValue(CornerRadiusProperty);
        else element.SetValue(CornerRadiusProperty, radius);
    }

    public static ThemeColor? GetBackgroundResource(Element element) =>
        element.GetValue(BackgroundResourceProperty);

    /// <summary>主题资源背景：亮暗切换自动跟随（null 清除，回落静态覆盖/主题）。</summary>
    public static void SetBackgroundResource(Element element, ThemeColor? resource)
    {
        if (resource is null) element.ClearLocalValue(BackgroundResourceProperty);
        else element.SetValue(BackgroundResourceProperty, resource);
    }

    public static ThemeColor? GetForegroundResource(Element element) =>
        element.GetValue(ForegroundResourceProperty);

    /// <summary>主题资源前景：亮暗切换自动跟随（null 清除，回落静态覆盖/主题）。</summary>
    public static void SetForegroundResource(Element element, ThemeColor? resource)
    {
        if (resource is null) element.ClearLocalValue(ForegroundResourceProperty);
        else element.SetValue(ForegroundResourceProperty, resource);
    }

    public static double? GetFocusRingWidth(Element element) => element.GetValue(FocusRingWidthProperty);

    /// <summary>焦点环宽度覆盖（null 清除，回落主题令牌）。</summary>
    public static void SetFocusRingWidth(Element element, double? width)
    {
        if (width is null) element.ClearLocalValue(FocusRingWidthProperty);
        else element.SetValue(FocusRingWidthProperty, width);
    }
}

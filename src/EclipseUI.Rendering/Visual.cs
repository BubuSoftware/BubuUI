using EclipseUI;
using EclipseUI.Elements;

namespace EclipseUI.Rendering;

/// <summary>
/// 元素级视觉覆盖属性（S-B）：值非空时 <see cref="ElementPainter"/> 用它替换主题令牌，
/// null（默认）回落 <see cref="ElementTheme"/>——现有绘制语义零回归。
/// 模板作者经 Class 样式或 C# 侧 <c>Visual.SetBackground(el, color)</c> 设置。
/// </summary>
public static class Visual
{
    public static readonly StyledProperty<Color?> BackgroundProperty =
        StyledProperty<Color?>.Register<Element>("Visual.Background", null);

    public static readonly StyledProperty<Color?> ForegroundProperty =
        StyledProperty<Color?>.Register<Element>("Visual.Foreground", null);

    public static readonly StyledProperty<double?> CornerRadiusProperty =
        StyledProperty<double?>.Register<Element>("Visual.CornerRadius", null);

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
}

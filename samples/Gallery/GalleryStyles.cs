using EclipseUI.Rendering;

namespace Gallery;

/// <summary>
/// 画廊 Class 样式表（S-B/S-C）：宿主启动期定义、元素经 <c>Classes</c> 声明。
/// 颜色用 <see cref="ThemeColor"/> 双色资源——亮暗切换自动跟随（S-C 主题资源查找）。
/// </summary>
public static class GalleryStyles
{
    /// <summary>默认注册到全局 <see cref="StyleRegistry.Instance"/>（无 AppBuilder 的宿主用）。</summary>
    public static void Define() => Define(StyleRegistry.Instance);

    /// <summary>AppBuilder 形态（D101）：<c>UseStyles(StyleRegistry.Instance, GalleryStyles.Define)</c> 方法组直连。</summary>
    public static void Define(StyleRegistry registry)
    {
        registry.Define("primary", s => s
            .Background(new ThemeColor(                     // 主操作：绿色（暗亮两档）
                Dark: new Color(34, 160, 100),
                Light: new Color(28, 140, 88))));
        registry.Define("tool", s => s
            .Background(new ThemeColor(                     // 工具按钮：中性灰（暗亮两档）
                Dark: new Color(96, 106, 132),
                Light: new Color(176, 183, 198))));
    }
}

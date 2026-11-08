using EclipseUI.Rendering;

namespace Gallery;

/// <summary>
/// 画廊 Class 样式表（S-B/S-C）：宿主启动期定义、元素经 <c>Classes</c> 声明。
/// 颜色用 <see cref="ThemeColor"/> 双色资源——亮暗切换自动跟随（S-C 主题资源查找）。
/// </summary>
public static class GalleryStyles
{
    public static void Define()
    {
        var registry = StyleRegistry.Instance;
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

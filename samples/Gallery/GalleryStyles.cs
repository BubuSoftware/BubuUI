using EclipseUI.Rendering;

namespace Gallery;

/// <summary>
/// 画廊 Class 样式表（S-B）：宿主启动期定义、元素经 <c>Classes</c> 声明。
/// 颜色为静态值、不随主题重解析——按主题的资源查找属 S-C。
/// </summary>
public static class GalleryStyles
{
    public static void Define()
    {
        var registry = StyleRegistry.Instance;
        registry.Define("primary", s => s
            .Background(new Color(34, 160, 100)));   // 主操作：绿色（亮暗主题均可读）
        registry.Define("tool", s => s
            .Background(new Color(96, 106, 132)));   // 工具按钮：中性灰
    }
}

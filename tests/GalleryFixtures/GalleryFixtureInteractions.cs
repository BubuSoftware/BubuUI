using EclipseUI;
using EclipseUI.Elements;

namespace Gallery;

/// <summary>Application wiring retained only for framework regression fixtures.</summary>
public static class GalleryFixtureInteractions
{
    public static void Wire(LegacyWindow mainWindow)
    {
        // E5 注册伪类（design/17 §3.2）：'spinning' 已由 app.eustyle 的 @states 启动期注册
        // （Register 幂等，重复注册返回同一掩码），掩码即凭证——:spinning 变体命中即变体色。
        mainWindow.BusyChip.SetRegisteredState(PseudoStatesRegistry.Register("spinning"), true);

        // design/19 E6 类通道演示：标签初值由模板快照写入；hover 翻 Glow 6→12 后经
        // PropertyChanged 重写 Label（值已落 class 层，读回即新值，无需组件失效）。
        mainWindow.BadgeDemo.PropertyChanged += (_, property) =>
        {
            if (ReferenceEquals(property, BadgeStyles.GlowProperty))
                mainWindow.BadgeDemo.Label = mainWindow.BadgeText;
        };

        // 框架边界（代码优先 API，暂无模板语法）：虚拟化条目工厂与启用开关
        // （IsEnabled 是普通 CLR 属性，未进模板目录——E3 语义自有单测覆盖）。
        mainWindow.ShowcasePanel.virtualList.ItemFactory = i => new Button { Label = $"virtual item {i + 1}" };
        mainWindow.DisabledDemo.IsEnabled = false;
    }
}

using EclipseUI;
using EclipseUI.Elements;
using EclipseUI.Rendering;
using Microsoft.Extensions.DependencyInjection;

namespace Gallery;

/// <summary>
/// 画廊应用（D101，Avalonia 式应用定义）：平台壳只做 <c>AppBuilder.Configure&lt;GalleryApp&gt;()...Start(args)</c>。
/// design/20 P2 模板化后，窗口与整页 UI 由 <c>MainWindow.ui</c> 一个文件声明，应用侧只剩
/// 一行窗口装配 + 树内不表达的应用级接线（主题切换 / 注册伪类掩码 / CLR 事件桥 / 虚拟化
/// 条目工厂 / 启用开关——Application 关切或代码优先 API，不再是手工元素树）。
/// CLI 选项经 <see cref="Application.Services"/>（依赖注入）消费；主题切换经
/// <see cref="Application.SetTheme"/>——宿主运行期由桌面生命周期注入 <see cref="Application.Host"/>。
/// </summary>
public sealed class GalleryApp : Application
{
    public override void OnFrameworkInitializationCompleted()
    {
        var options = Services.GetRequiredService<GalleryLaunchOptions>();

        var mainWindow = new MainWindow { W = options.Width, H = options.Height };
        mainWindow.EnsureInitialized();
        var window = mainWindow;

        // CLI 帧循环/调试旋钮：在平台创建窗口之前设置。
        window.MaxFramesPerSecond = options.MaxFps;
        window.AlwaysRender = options.AlwaysRender;
        window.MaxTicks = options.Frames;

        // S-A 亮暗切换：换 token 表 → 段缓存失效 → 整帧重绘。初值主题由 AppBuilder.UseTheme 提供。
        var dark = true;
        mainWindow.ThemeButton.AddHandler(Button.ClickEvent, () =>
        {
            dark = !dark;
            SetTheme(dark ? ElementTheme.Dark : ElementTheme.Light);
        });

        // 应用级接线（注册伪类 / 读回订阅 / 条目工厂 / 启用开关）——与验证壳共用。
        WireInteractions(mainWindow);
        MainWindow = window;
    }

    /// <summary>
    /// 应用级接线（design/20 P2：树内不表达的应用关切，GalleryApp 与 GalleryCheck 验证壳共用）。
    /// 主题切换不在此列——两台宿主的 SetTheme 目标不同（Application vs WindowHost）。
    /// </summary>
    public static void WireInteractions(MainWindow mainWindow)
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

using Microsoft.Extensions.DependencyInjection;

namespace EclipseUI;

/// <summary>
/// 主题对象标记：让 Runtime 层的窗口对象/构建器能表达「默认主题」而不认识具体
/// 主题类型（<c>ElementTheme</c> 在 Rendering——依赖方向 Rendering→Runtime，不可反向）。
/// </summary>
public interface IHostTheme
{
}

/// <summary>
/// 窗口宿主运行期外观（D101）：把「运行时换主题」这类宿主动作暴露给 <see cref="Application"/>，
/// 不让 Runtime 依赖具体平台类型（<c>WindowHost</c> 在 Platform.Windows 实现本接口）。
/// </summary>
public interface IWindowHost
{
    /// <summary>运行时切换主题（S-A）：换 token 表 → 段缓存失效 → 整帧重绘。</summary>
    void SetTheme(IHostTheme theme);
}

/// <summary>
/// 应用生命周期（D101）：平台包（如 <c>UseWindows()</c>）注册实现；
/// <see cref="AppBuilder.Start"/> 装配完成后把控制权交给它（桌面形态 = 组窗口 + 跑消息/帧循环直到关闭）。
/// </summary>
public interface IApplicationLifetime
{
    /// <summary>运行主生命周期，阻塞返回进程退出码。</summary>
    int Run(Application app, string[] args);
}

/// <summary>
/// 平台/后端装配点（D101）：各扩展包以 <c>UseXxx()</c> 扩展方法向容器注册自己的服务
/// （窗口工厂、表面工厂、文本管线、生命周期……），核心 <see cref="AppBuilder"/> 不认识任何具体平台。
/// </summary>
public interface IPlatformSetup
{
    /// <summary>向服务集合注册本平台/后端提供的能力。</summary>
    void RegisterServices(IServiceCollection services);
}

/// <summary>空服务提供者：未调用 <see cref="AppBuilder.Build"/> 前的占位（GetService 恒 null）。</summary>
internal sealed class EmptyServiceProvider : IServiceProvider
{
    public static EmptyServiceProvider Instance { get; } = new();
    public object? GetService(Type serviceType) => null;
}

/// <summary>
/// 应用基类（D101，Avalonia 式启动形态）：子类在 <see cref="OnFrameworkInitializationCompleted"/>
/// 里创建 <see cref="MainWindow"/>（<see cref="Window"/> 实例），平台生命周期负责为它创建
/// 真实窗口 + 帧循环。依赖注入经 <see cref="Services"/>（Microsoft.Extensions.DependencyInjection）。
/// </summary>
public abstract class Application
{
    /// <summary>应用容器；<see cref="AppBuilder.Build"/> 前为空实现。</summary>
    public IServiceProvider Services { get; internal set; } = EmptyServiceProvider.Instance;

    /// <summary>主窗口对象；子类在 <see cref="OnFrameworkInitializationCompleted"/> 里设置。</summary>
    public Window? MainWindow { get; set; }

    /// <summary>构建期默认主题（<see cref="AppBuilder.UseTheme"/>，传 <c>ElementTheme.Dark/Light</c> 等即可）；窗口未显式指定时生效。</summary>
    public IHostTheme? DefaultTheme { get; internal set; }

    /// <summary>宿主运行期由平台生命周期注入（应用代码勿写）；窗口关闭后生命周期置回 null。</summary>
    public IWindowHost? Host { get; set; }

    /// <summary>装配完成回调：创建 <see cref="MainWindow"/>、订阅应用级事件。</summary>
    public virtual void OnFrameworkInitializationCompleted()
    {
    }

    /// <summary>
    /// 便捷入口：切换宿主主题（S-A 亮暗切换经此接线，替代宿主层直接持有 WindowHost）。
    /// 宿主未运行（<see cref="Host"/> 为 null）时静默忽略。
    /// </summary>
    public void SetTheme(IHostTheme theme) => Host?.SetTheme(theme);
}

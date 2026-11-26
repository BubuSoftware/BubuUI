namespace Gallery.Services;

/// <summary>
/// K37 <c>@inject</c> 画廊示例背后的单例状态服务：由平台宿主在
/// <c>AppBuilder.ConfigureServices</c> 注册为单例，模板侧经 <c>@inject</c> 解析——
/// 见 <c>DependencyInjectionExample.ui</c>。单例语义让 <see cref="Clicks"/> 跨「Reset controls」
/// 的视觉树重建存续，而 <see cref="Builds"/> 每次重建 +1，直观展示服务生命周期与 UI 生命周期解耦。
/// </summary>
public interface IGalleryDemoState
{
    /// <summary>累计点击数：跨视觉树重建存续（单例）。</summary>
    int Clicks { get; }

    /// <summary>示例行构建次数：每次「Reset controls」重建视觉树 +1。</summary>
    int Builds { get; }

    void RegisterClick();
    void RegisterBuild();
}

/// <summary>默认内存实现；一次应用运行一个实例。</summary>
public sealed class GalleryDemoState : IGalleryDemoState
{
    public int Clicks { get; private set; }
    public int Builds { get; private set; }

    public void RegisterClick() => Clicks++;
    public void RegisterBuild() => Builds++;
}

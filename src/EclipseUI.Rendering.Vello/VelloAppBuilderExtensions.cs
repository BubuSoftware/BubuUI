using EclipseUI;
using EclipseUI.Rendering;
using Microsoft.Extensions.DependencyInjection;

namespace EclipseUI.Rendering.Vello;

/// <summary>
/// AppBuilder 扩展（D101）：注册 Vello 上屏表面工厂。
/// <code>
/// AppBuilder.Configure&lt;App&gt;().UseWindows().UseVello().Start(args);
/// </code>
/// </summary>
public static class VelloAppBuilderExtensions
{
    /// <summary>使用 Vello 渲染后端上屏（GPU 优先；<c>new VelloSurfaceOptions { PreferVsync = false }</c> 可调表面行为）。</summary>
    public static AppBuilder UseVello(this AppBuilder builder, VelloSurfaceOptions? options = null)
    {
        if (builder is null) throw new ArgumentNullException(nameof(builder));
        return builder.UseSetup(new VelloSurfaceSetup(options ?? new VelloSurfaceOptions()));
    }

    private sealed class VelloSurfaceSetup(VelloSurfaceOptions options) : IPlatformSetup
    {
        public void RegisterServices(IServiceCollection services)
            => services.AddSingleton<ISurfaceFactory>(new VelloSurfaceFactory(options));
    }

    private sealed class VelloSurfaceFactory(VelloSurfaceOptions options) : ISurfaceFactory
    {
        public ISurfacePresenter Create(nint handle, int width, int height)
            => VelloSurfaceBackend.Create(handle, width, height, options);

        public ISurfacePresenter Create(NativeWindowHandle window, int width, int height)
            => VelloSurfaceBackend.Create(window, width, height, options);
    }
}

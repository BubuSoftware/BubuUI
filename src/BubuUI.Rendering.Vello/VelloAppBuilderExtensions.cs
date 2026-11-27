using BubuUI;
using BubuUI.Rendering;
using Microsoft.Extensions.DependencyInjection;

namespace BubuUI.Rendering.Vello;

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
            => services.AddSingleton<ISurfaceFactory>(_ => new VelloSurfaceFactory(options));
    }

    private sealed class VelloSurfaceFactory(VelloSurfaceOptions options) : ISurfaceFactory, IDisposable
    {
        private readonly object _gate = new();
        private VelloDeviceContext? _windowsDevice;
        private bool _disposed;

        public ISurfacePresenter Create(nint handle, int width, int height)
            => Create(NativeWindowHandle.FromWin32(handle), width, height);

        public ISurfacePresenter Create(NativeWindowHandle window, int width, int height)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (window.Kind != NativeWindowKind.Win32)
                    return VelloSurfaceBackend.Create(window, width, height, options);

                // Popup surfaces keep their own swapchain and frame state, but avoid
                // enumerating adapters and creating a device on every dropdown click.
                var surface = VelloSurfaceBackend.Create(window, width, height, options, _windowsDevice);
                _windowsDevice ??= surface.RetainDeviceContext();
                return surface;
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _windowsDevice?.Release();
                _windowsDevice = null;
            }
        }
    }
}

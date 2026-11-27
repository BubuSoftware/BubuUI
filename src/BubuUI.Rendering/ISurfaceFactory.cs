namespace BubuUI.Rendering;

/// <summary>
/// 表面工厂（D101）：把「窗口句柄 → 上屏表面」抽象出来，让平台生命周期
/// （<see cref="BubuUI.IApplicationLifetime"/> 实现）不依赖具体渲染后端——
/// Vello 包以 <c>UseVello()</c> 注册本接口，未来其他后端（CPU/Skia/...）同法接入。
/// </summary>
public interface ISurfaceFactory
{
    /// <summary>为窗口客户区创建上屏表面（生命周期负责随窗口一起释放返回值）。</summary>
    ISurfacePresenter Create(nint handle, int width, int height);

    /// <summary>Create a surface using explicit platform and display handles.</summary>
    ISurfacePresenter Create(NativeWindowHandle window, int width, int height)
        => window.Kind == NativeWindowKind.Win32
            ? Create(window.Handle, width, height)
            : throw new PlatformNotSupportedException($"This surface factory does not support {window.Kind} windows.");
}

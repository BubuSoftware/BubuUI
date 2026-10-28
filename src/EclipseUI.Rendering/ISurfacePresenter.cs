namespace EclipseUI.Rendering;

/// <summary>
/// 可呈现目标：像素留在 GPU（或等价的可视表面），渲染完即上屏。
/// 与 <see cref="RenderTarget"/>（CPU 缓冲区，供 PNG/像素探针）并列，不互相替代。
/// 尺寸以设备像素为单位；DPI 缩放由宿主在调用前完成。
/// </summary>
public interface ISurfacePresenter : IDisposable
{
    string Name { get; }

    RenderCapabilities Capabilities { get; }

    /// <summary>当前表面宽度（设备像素）。</summary>
    int Width { get; }

    /// <summary>当前表面高度（设备像素）。</summary>
    int Height { get; }

    /// <summary>
    /// 改变表面尺寸。同一尺寸重复调用应为 no-op。
    /// 实现须自行重建依赖尺寸的资源（renderer/swapchain）。
    /// </summary>
    void Resize(int width, int height);

    /// <summary>渲染并呈现一帧。调用方保证尺寸与 <see cref="Width"/>/<see cref="Height"/> 一致。</summary>
    void Render(DisplayList list);
}

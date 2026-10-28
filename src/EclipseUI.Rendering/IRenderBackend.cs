namespace EclipseUI.Rendering;

/// <summary>后端能力位：功能矩阵与降级路径据此选择实现。</summary>
[Flags]
public enum RenderCapabilities
{
    None = 0,
    SolidFill = 1 << 0,
    Stroke = 1 << 1,
    Dash = 1 << 2,
    Clip = 1 << 3,
    LayerOpacity = 1 << 4,
    BlendModes = 1 << 5,
    Gradients = 1 << 6,
    ImageScaling = 1 << 7,
    GlyphRun = 1 << 8,
    Blur = 1 << 9,

    All = SolidFill | Stroke | Dash | Clip | LayerOpacity | BlendModes | Gradients | ImageScaling | GlyphRun | Blur,
}

/// <summary>
/// 渲染后端。契约层不认识 Vello；换后端只替换实现。
/// 实现须支持同一 <see cref="DisplayList"/> 的重复渲染（幂等），CPU 目标要可复现。
/// </summary>
public interface IRenderBackend : IDisposable
{
    string Name { get; }

    RenderCapabilities Capabilities { get; }

    /// <summary>渲染到 <paramref name="target"/>；目标尺寸来源以 target 为准。</summary>
    void Render(DisplayList list, RenderTarget target);
}

using EclipseUI.Rendering;
using VelloSharp;

namespace EclipseUI.Rendering.Vello;

public sealed class VelloBackendOptions
{
    /// <summary>使用 vello_cpu 光栅化（CI/无窗口首选，输出可复现）。</summary>
    public bool UseCpu { get; init; } = true;

    public AntialiasingMode Antialiasing { get; init; } = AntialiasingMode.Area;

    /// <summary>渲染前校验 display list 的栈配对（调试/测试用）。</summary>
    public bool ValidateDisplayList { get; init; }
}

/// <summary>
/// Vello CPU 离屏后端：写 <see cref="RenderTarget"/>，输出确定、可复现，供 CI 与 PNG 导出。
/// 逐帧 GPU 渲染请用 <see cref="VelloSurfaceBackend"/>。
/// </summary>
public sealed class VelloCpuBackend : IRenderBackend
{
    private readonly VelloBackendOptions _options;
    private readonly VelloSceneBuilder _builder = new();
    private readonly Scene _scene = new();
    private Renderer? _renderer;
    private int _rendererWidth;
    private int _rendererHeight;

    public VelloCpuBackend()
        : this(new VelloBackendOptions())
    {
    }

    public VelloCpuBackend(VelloBackendOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public string Name => "Vello (CPU)";

    public RenderCapabilities Capabilities => RenderCapabilities.All;

    public void Render(DisplayList list, RenderTarget target)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(target);

        if (_options.ValidateDisplayList)
        {
            var errors = list.Validate();
            if (errors.Count > 0)
                throw new InvalidOperationException("invalid display list: " + string.Join("; ", errors));
        }

        EnsureRenderer(target.Width, target.Height);

        _scene.Reset();
        _builder.Build(_scene, list, target.Size);

        var parameters = new RenderParams(
            (uint)target.Width,
            (uint)target.Height,
            RgbaColor.FromBytes(0, 0, 0, 0),
            _options.Antialiasing,
            RenderFormat.Rgba8);

        _renderer!.Render(_scene, parameters, target.Pixels, target.Stride);
    }

    private void EnsureRenderer(int width, int height)
    {
        if (_renderer is not null && _rendererWidth == width && _rendererHeight == height)
            return;

        _renderer?.Dispose();
        _renderer = new Renderer((uint)width, (uint)height, new RendererOptions(useCpu: _options.UseCpu));
        _rendererWidth = width;
        _rendererHeight = height;
    }

    public void Dispose()
    {
        _builder.Dispose();
        _scene.Dispose();
        _renderer?.Dispose();
        _renderer = null;
    }
}

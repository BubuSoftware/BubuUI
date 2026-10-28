using System.Diagnostics;
using System.Runtime.Versioning;
using EclipseUI.Rendering;
using VelloSharp;

namespace EclipseUI.Rendering.Vello;

/// <summary>单帧表面呈现的各阶段耗时（毫秒）。</summary>
public readonly record struct SurfaceFrameTimings(
    double AcquireMilliseconds,
    double RenderMilliseconds,
    double PresentMilliseconds);

public sealed class VelloSurfaceOptions
{
    public AntialiasingMode Antialiasing { get; init; } = AntialiasingMode.Area;

    /// <summary>优先使用的 wgpu 后端；null 表示由 wgpu 自动选择。</summary>
    public WgpuBackend? PreferredBackend { get; init; } = WgpuBackend.Dx12;

    /// <summary>优先垂直同步；false 时选 Immediate（仅用于测帧率上限）。</summary>
    public bool PreferVsync { get; init; } = true;

    /// <summary>
    /// 优先选择非 sRGB 表面格式。<see cref="RenderFormat"/> 只表达 8bit 通道序，
    /// 像素已是 sRGB 编码值；写进 _SRGB 视图会被再做一次编码（画面发白）。
    /// </summary>
    public bool PreferNonSrgbFormat { get; init; } = true;

    public bool ValidateDisplayList { get; init; }
}

/// <summary>
/// Vello GPU surface 后端：wgpu instance/adapter/device + 平台表面（Win32 HWND）。
/// 帧路径：AcquireNextTexture → RenderSurface → Present，不经过 CPU 回读。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class VelloSurfaceBackend : ISurfacePresenter
{
    private readonly VelloSurfaceOptions _options;
    private readonly VelloSceneBuilder _builder = new();
    private readonly Scene _scene = new();

    private WgpuInstance? _instance;
    private WgpuAdapter? _adapter;
    private WgpuDevice? _device;
    private WgpuRenderer? _renderer;
    private WgpuSurface? _surface;
    private WgpuTextureFormat _format;
    private RenderFormat _renderFormat = RenderFormat.Rgba8;
    private PresentMode _presentMode;

    private VelloSurfaceBackend(VelloSurfaceOptions options)
    {
        _options = options;
    }

    public string Name => "Vello (GPU surface)";

    public RenderCapabilities Capabilities => RenderCapabilities.All;

    public int Width { get; private set; }

    public int Height { get; private set; }

    /// <summary>请求使用的 wgpu 后端；null 表示由 wgpu 自动选择。</summary>
    public WgpuBackend? BackendType { get; private set; }

    public WgpuTextureFormat Format => _format;

    /// <summary>实际配置的呈现模式（诊断用）。</summary>
    public PresentMode PresentMode => _presentMode;

    /// <summary>表面能力摘要（格式 / 呈现模式 / alpha 模式），用于诊断。</summary>
    public string Diagnostics { get; private set; } = string.Empty;

    /// <summary>已成功 Present 的帧数。</summary>
    public long PresentedFrames { get; private set; }

    /// <summary>上一帧各阶段耗时（毫秒）：acquire 阻塞通常等于等待可用图像 / 垂直同步。</summary>
    public SurfaceFrameTimings LastTimings { get; private set; }

    public static VelloSurfaceBackend Create(nint hwnd, int width, int height, VelloSurfaceOptions? options = null)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("VelloSurfaceBackend requires Windows.");
        if (hwnd == nint.Zero)
            throw new ArgumentException("window handle must not be zero", nameof(hwnd));
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));

        var backend = new VelloSurfaceBackend(options ?? new VelloSurfaceOptions());
        try
        {
            backend.Initialize(hwnd, width, height);
            return backend;
        }
        catch
        {
            backend.Dispose();
            throw;
        }
    }

    private void Initialize(nint hwnd, int width, int height)
    {
        (_instance, _adapter) = CreateInstanceAndAdapter();
        if (_adapter is null)
            throw new InvalidOperationException("wgpu: no compatible adapter found.");
        BackendType = _options.PreferredBackend;

        _device = _adapter.RequestDevice()
            ?? throw new InvalidOperationException("wgpu: RequestDevice returned null.");

        var descriptor = new SurfaceDescriptor
        {
            Width = (uint)width,
            Height = (uint)height,
            PresentMode = _options.PreferVsync ? PresentMode.AutoVsync : PresentMode.AutoNoVsync,
            Handle = SurfaceHandle.FromWin32(hwnd),
        };

        _surface = WgpuSurface.Create(_instance, descriptor)
            ?? throw new InvalidOperationException("wgpu: WgpuSurface.Create returned null.");

        var capabilities = _surface.GetCapabilities(_adapter);
        _format = SelectFormat(capabilities.Formats);
        _renderFormat = _format is WgpuTextureFormat.Bgra8Unorm or WgpuTextureFormat.Bgra8UnormSrgb
            ? RenderFormat.Bgra8
            : RenderFormat.Rgba8;
        _presentMode = SelectPresentMode(capabilities.PresentModes);
        Diagnostics = $"formats=[{string.Join(",", capabilities.Formats)}] " +
                      $"present=[{string.Join(",", capabilities.PresentModes)}] " +
                      $"alpha=[{string.Join(",", capabilities.AlphaModes)}] " +
                      $"usage={capabilities.Usage}";

        _renderer = new WgpuRenderer(_device);
        Configure(width, height);

        Width = width;
        Height = height;
    }

    private (WgpuInstance Instance, WgpuAdapter? Adapter) CreateInstanceAndAdapter()
    {
        if (_options.PreferredBackend is { } preferred)
        {
            try
            {
                var instance = new WgpuInstance(new WgpuInstanceOptions { Backends = preferred });
                var adapter = instance.RequestAdapter();
                if (adapter is not null) return (instance, adapter);

                // 该后端不可用：整体释放后再回退，避免悬挂的 adapter id。
                instance.Dispose();
            }
            catch
            {
                // 回退到默认后端组合。
            }
        }

        var fallback = new WgpuInstance();
        return (fallback, fallback.RequestAdapter());
    }

    private WgpuTextureFormat SelectFormat(IReadOnlyList<WgpuTextureFormat> available)
    {
        var preferred = _surface!.GetPreferredFormat(_adapter!);
        if (!_options.PreferNonSrgbFormat) return preferred;

        foreach (var candidate in new[] { WgpuTextureFormat.Bgra8Unorm, WgpuTextureFormat.Rgba8Unorm })
        {
            if (available.Contains(candidate)) return candidate;
        }

        return preferred;
    }

    private PresentMode SelectPresentMode(IReadOnlyList<PresentMode> available)
    {
        if (available.Count == 0) return PresentMode.AutoVsync;

        var preferred = _options.PreferVsync
            ? new[] { PresentMode.Fifo, PresentMode.AutoVsync }
            : new[] { PresentMode.Immediate, PresentMode.AutoNoVsync };

        foreach (var mode in preferred)
        {
            if (available.Contains(mode)) return mode;
        }

        return available[0];
    }

    private void Configure(int width, int height)
    {
        var capabilities = _surface!.GetCapabilities(_adapter!);
        _surface.Configure(_device!, new WgpuSurfaceConfiguration
        {
            Width = (uint)width,
            Height = (uint)height,
            Format = _format,
            Usage = capabilities.Usage,
            PresentMode = _presentMode,
            AlphaMode = WgpuCompositeAlphaMode.Opaque,
            ViewFormats = Array.Empty<WgpuTextureFormat>(),
        });
    }

    public void Resize(int width, int height)
    {
        if (width <= 0 || height <= 0) return;
        if (width == Width && height == Height) return;

        Configure(width, height);
        Width = width;
        Height = height;
    }

    public void Render(DisplayList list)
    {
        ArgumentNullException.ThrowIfNull(list);
        ObjectDisposedException.ThrowIf(_surface is null, this);

        if (_options.ValidateDisplayList)
        {
            var errors = list.Validate();
            if (errors.Count > 0)
                throw new InvalidOperationException("invalid display list: " + string.Join("; ", errors));
        }

        WgpuSurfaceTexture texture;
        var acquireStart = Stopwatch.GetTimestamp();
        try
        {
            texture = _surface!.AcquireNextTexture();
        }
        catch (Exception)
        {
            // 交换链失效（最小化、DPI 迁移、驱动重置）时重配一次再试。
            Configure(Width, Height);
            texture = _surface!.AcquireNextTexture();
        }
        var renderStart = Stopwatch.GetTimestamp();

        double presentMilliseconds;
        using (texture)
        using (var view = texture.CreateView())
        {
            _scene.Reset();
            _builder.Build(_scene, list, new Size(Width, Height));

            var parameters = new RenderParams(
                (uint)Width,
                (uint)Height,
                RgbaColor.FromBytes(0, 0, 0, 255),
                _options.Antialiasing,
                _renderFormat);

            _renderer!.RenderSurface(_scene, view, parameters, _format);

            var presentStart = Stopwatch.GetTimestamp();
            texture.Present();
            presentMilliseconds = Elapsed(presentStart);
            LastTimings = new SurfaceFrameTimings(
                Elapsed(acquireStart),
                Elapsed(renderStart) - presentMilliseconds,
                presentMilliseconds);
        }

        PresentedFrames++;
    }

    private static double Elapsed(long startTimestamp)
        => (Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency;

    public void Dispose()
    {
        _renderer?.Dispose();
        _renderer = null;
        _surface?.Dispose();
        _surface = null;
        _device?.Dispose();
        _device = null;
        _adapter?.Dispose();
        _adapter = null;
        _instance?.Dispose();
        _instance = null;
        _scene.Dispose();
        _builder.Dispose();
    }
}

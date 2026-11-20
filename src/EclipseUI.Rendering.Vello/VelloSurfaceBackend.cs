using System.Diagnostics;
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
    /// <summary>Rasterize with vello_cpu and upload the finished frame instead of running compute shaders.</summary>
    public bool UseSoftwareRasterizer { get; init; }

    public AntialiasingMode Antialiasing { get; init; } = AntialiasingMode.Area;

    /// <summary>优先使用的 wgpu 后端；null 表示由 wgpu 自动选择。</summary>
    public WgpuBackend? PreferredBackend { get; init; } = OperatingSystem.IsWindows()
        ? WgpuBackend.Dx12
        : OperatingSystem.IsMacOS() ? WgpuBackend.Metal : WgpuBackend.Vulkan;

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
/// Vello GPU surface 后端：wgpu instance/adapter/device + Win32/AppKit/X11/Wayland 表面。
/// 帧路径：AcquireNextTexture → RenderSurface → Present，不经过 CPU 回读。
/// </summary>
public sealed class VelloSurfaceBackend : ISurfacePresenter
{
    private readonly VelloSurfaceOptions _options;
    private readonly VelloRetainedSceneBuilder _builder = new();
    private readonly Scene _scene = new();

    private WgpuInstance? _instance;
    private WgpuAdapter? _adapter;
    private WgpuDevice? _device;
    private WgpuRenderer? _renderer;
    private WgpuSurface? _surface;
    private GpuRetainedFrame? _retained;
    private VelloSparseBackend? _software;
    private RenderTarget? _softwareTarget;
    private WgpuTextureFormat _format;
    private PresentMode _presentMode;
    private WgpuCompositeAlphaMode _alphaMode;
    private bool _transparent;

    private VelloSurfaceBackend(VelloSurfaceOptions options)
    {
        _options = options;
    }

    public string Name => _options.UseSoftwareRasterizer ? "Vello (CPU raster, GPU surface)" : "Vello (GPU surface)";

    public string Description => $"backend: {Name}, wgpu={BackendType}, format={Format}, present={PresentMode}, surface={Width}x{Height} px";

    public RenderCapabilities Capabilities => RenderCapabilities.All;

    public int Width { get; private set; }

    public int Height { get; private set; }

    /// <summary>请求使用的 wgpu 后端；null 表示由 wgpu 自动选择。</summary>
    public WgpuBackend? BackendType { get; private set; }

    public WgpuTextureFormat Format => _format;

    /// <summary>实际配置的呈现模式（诊断用）。</summary>
    public PresentMode PresentMode => _presentMode;
    public WgpuCompositeAlphaMode AlphaMode => _alphaMode;
    public bool SupportsTransparency => _transparent && _alphaMode is WgpuCompositeAlphaMode.Premultiplied
        or WgpuCompositeAlphaMode.PostMultiplied or WgpuCompositeAlphaMode.Inherit;

    /// <summary>呈现模式是否垂直同步锁定（Fifo/AutoVsync）。</summary>
    public bool IsVsync => _presentMode is PresentMode.Fifo or PresentMode.AutoVsync;

    /// <summary>表面能力摘要（格式 / 呈现模式 / alpha 模式），用于诊断。</summary>
    public string Diagnostics { get; private set; } = string.Empty;

    /// <summary>已成功 Present 的帧数。</summary>
    public long PresentedFrames { get; private set; }

    /// <summary>上一帧各阶段耗时（毫秒）：acquire 阻塞通常等于等待可用图像 / 垂直同步。</summary>
    public SurfaceFrameTimings LastTimings { get; private set; }
    public long LastRasterizedPixels => _retained?.RasterizedPixels ?? 0;
    public int LastReusedSceneFragments { get; private set; }
    public int LastBuiltSceneFragments { get; private set; }

    public static VelloSurfaceBackend Create(nint hwnd, int width, int height, VelloSurfaceOptions? options = null)
        => Create(NativeWindowHandle.FromWin32(hwnd), width, height, options);

    public static VelloSurfaceBackend Create(NativeWindowHandle window, int width, int height, VelloSurfaceOptions? options = null)
    {
        if (window.Kind == NativeWindowKind.Unknown || window.Handle == 0)
            throw new ArgumentException("A valid native window handle is required.", nameof(window));
        var supported = window.Kind switch
        {
            NativeWindowKind.Win32 => OperatingSystem.IsWindows(),
            NativeWindowKind.AppKit => OperatingSystem.IsMacOS(),
            NativeWindowKind.Xlib or NativeWindowKind.Wayland => OperatingSystem.IsLinux(),
            _ => false,
        };
        if (!supported)
            throw new PlatformNotSupportedException($"Cannot create a {window.Kind} surface on this operating system.");
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));

        var backend = new VelloSurfaceBackend(options ?? new VelloSurfaceOptions());
        try
        {
            backend.Initialize(window, width, height);
            return backend;
        }
        catch
        {
            backend.Dispose();
            throw;
        }
    }

    private void Initialize(NativeWindowHandle window, int width, int height)
    {
        var descriptor = new SurfaceDescriptor
        {
            Width = (uint)width,
            Height = (uint)height,
            PresentMode = _options.PreferVsync ? PresentMode.AutoVsync : PresentMode.AutoNoVsync,
            Handle = window.Kind switch
            {
                NativeWindowKind.Win32 => SurfaceHandle.FromWin32(window.Handle),
                NativeWindowKind.Xlib => SurfaceHandle.FromXlib(unchecked((ulong)(nuint)window.Handle), window.Display, window.Screen, 0),
                NativeWindowKind.Wayland => SurfaceHandle.FromWayland(window.Handle, window.Display),
                NativeWindowKind.AppKit => SurfaceHandle.FromAppKit(window.Handle),
                _ => throw new PlatformNotSupportedException($"Unsupported window kind: {window.Kind}."),
            },
        };
        CreateSurfaceAndAdapter(descriptor);
        _device = _adapter!.RequestDevice()
            ?? throw new InvalidOperationException("wgpu: RequestDevice returned null.");

        var capabilities = _surface!.GetCapabilities(_adapter);
        _format = SelectFormat(capabilities.Formats);
        _presentMode = SelectPresentMode(capabilities.PresentModes);
        _transparent = window.IsTransparent;
        _alphaMode = SelectAlphaMode(capabilities.AlphaModes, _transparent);
        Diagnostics = $"formats=[{string.Join(",", capabilities.Formats)}] " +
                      $"present=[{string.Join(",", capabilities.PresentModes)}] " +
                      $"alpha=[{string.Join(",", capabilities.AlphaModes)}] " +
                      $"usage={capabilities.Usage}";

        if (_options.UseSoftwareRasterizer) _software = new VelloSparseBackend();
        else _renderer = new WgpuRenderer(_device);
        _retained = new GpuRetainedFrame(_device, _renderer, _format, _transparent, _alphaMode == WgpuCompositeAlphaMode.PostMultiplied);
        Configure(width, height);

        Width = width;
        Height = height;
        _retained?.Reset();
    }

    private void CreateSurfaceAndAdapter(SurfaceDescriptor descriptor)
    {
        if (_options.PreferredBackend is { } preferred)
        {
            try
            {
                Create(preferred);
                return;
            }
            catch
            {
                _surface?.Dispose();
                _surface = null;
                _instance?.Dispose();
                _instance = null;
            }
        }

        Create(null);

        void Create(WgpuBackend? backend)
        {
            _instance = backend is { } value
                ? new WgpuInstance(new WgpuInstanceOptions { Backends = value })
                : new WgpuInstance();
            _surface = WgpuSurface.Create(_instance, descriptor)
                ?? throw new InvalidOperationException("wgpu: WgpuSurface.Create returned null.");
            _adapter = _instance.RequestAdapter(new WgpuRequestAdapterOptions { CompatibleSurface = _surface })
                ?? throw new InvalidOperationException("wgpu: no surface-compatible adapter found.");
            BackendType = backend;
        }
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

    private static WgpuCompositeAlphaMode SelectAlphaMode(IReadOnlyList<WgpuCompositeAlphaMode> available, bool transparent)
    {
        if (transparent)
            foreach (var mode in new[] { WgpuCompositeAlphaMode.Premultiplied, WgpuCompositeAlphaMode.PostMultiplied, WgpuCompositeAlphaMode.Inherit })
                if (available.Contains(mode)) return mode;
        return available.Contains(WgpuCompositeAlphaMode.Opaque) ? WgpuCompositeAlphaMode.Opaque
            : available.Count > 0 ? available[0] : WgpuCompositeAlphaMode.Auto;
    }

    private void Configure(int width, int height)
    {
        _surface!.Configure(_device!, new WgpuSurfaceConfiguration
        {
            Width = (uint)width,
            Height = (uint)height,
            Format = _format,
            // RenderSurface blits to the swapchain through a render pass. Advertising
            // every supported usage also adds StorageBinding on some Vulkan drivers,
            // but BGRA storage requires an optional device feature we do not enable.
            Usage = WgpuTextureUsage.RenderAttachment,
            PresentMode = _presentMode,
            AlphaMode = _alphaMode,
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
        _retained?.Reset();
    }

    public void Render(DisplayList list)
    {
        var damage = new FrameDamage(); damage.AddFull();
        Render(new PaintedFrame(list, damage));
    }

    public void Render(PaintedFrame frame, double scale = 1)
    {
        try { RenderCore(frame, scale); }
        catch { _retained?.Reset(); throw; }
    }

    private void RenderCore(PaintedFrame frame, double scale)
    {
        LastReusedSceneFragments = LastBuiltSceneFragments = 0;
        var list = frame.List;
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
            if (_software is not null)
            {
                if (_softwareTarget is null || _softwareTarget.Width != Width || _softwareTarget.Height != Height)
                    _softwareTarget = new RenderTarget(Width, Height);
                _software.Render(list, _softwareTarget);
                _retained!.PresentBitmap(_softwareTarget, view);
            }
            else
            {
                var region = GpuDamageRegion.Resolve(frame.Damage, Width, Height, scale, _retained!.HasFrame);
                if (region.Area > 0)
                {
                    _scene.Reset();
                    _builder.Build(_scene, list, new Size(Width, Height));
                    LastReusedSceneFragments = _builder.ReusedFragments;
                    LastBuiltSceneFragments = _builder.BuiltFragments;
                }
                _retained.Render(_scene, region, view, Width, Height, _options.Antialiasing);
            }

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
        _software?.Dispose();
        _software = null;
        _softwareTarget = null;
        _retained?.Dispose();
        _retained = null;
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

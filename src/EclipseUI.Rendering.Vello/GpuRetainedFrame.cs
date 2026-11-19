using System.Numerics;
using EclipseUI.Rendering;
using VelloSharp;

namespace EclipseUI.Rendering.Vello;

/// <summary>Rasterizes only damage into a small texture, then copies it into a persistent GPU frame.</summary>
internal sealed class GpuRetainedFrame : IDisposable
{
    private readonly WgpuDevice _device;
    private readonly WgpuRenderer? _renderer;
    private readonly WgpuQueue _queue;
    private readonly WgpuBindGroupLayout _bindings;
    private readonly WgpuPipelineLayout _layout;
    private readonly WgpuShaderModule _shader;
    private readonly WgpuRenderPipeline _copyPipeline, _presentPipeline;
    private readonly Scene _patchScene = new();
    private WgpuTexture? _frame, _patch;
    private WgpuTextureView? _frameView, _patchView;
    private WgpuBindGroup? _frameBinding, _patchBinding;
    private int _width, _height, _patchWidth, _patchHeight;
    private bool _hasFrame;
    public long RasterizedPixels { get; private set; }
    public GpuDamageRegion LastRegion { get; private set; }

    public GpuRetainedFrame(WgpuDevice device, WgpuRenderer? renderer, WgpuTextureFormat surfaceFormat)
    {
        _device = device; _renderer = renderer; _queue = device.GetQueue();
        try
        {
            _bindings = device.CreateBindGroupLayout(new()
            {
                Entries = [new(0, WgpuShaderStage.Fragment, new WgpuTextureBindingLayout
                { SampleType = WgpuTextureSampleType.Float, Dimension = WgpuTextureViewDimension.D2 })]
            });
            _layout = device.CreatePipelineLayout(new() { BindGroupLayouts = [_bindings] });
            _shader = device.CreateShaderModule(new WgpuShaderModuleDescriptor(Shader));
            _copyPipeline = CreatePipeline(WgpuTextureFormat.Rgba8Unorm, "copy");
            _presentPipeline = CreatePipeline(surfaceFormat,
                surfaceFormat is WgpuTextureFormat.Bgra8UnormSrgb or WgpuTextureFormat.Rgba8UnormSrgb ? "present_srgb" : "copy");
        }
        catch { Dispose(); throw; }
    }

    private WgpuRenderPipeline CreatePipeline(WgpuTextureFormat format, string fragment) => _device.CreateRenderPipeline(new()
    {
        Layout = _layout,
        Vertex = new() { Module = _shader, EntryPoint = "vertex", Buffers = [] },
        Fragment = new() { Module = _shader, EntryPoint = fragment, Targets = [new() { Format = format, WriteMask = WgpuColorWriteMask.All }] },
        Primitive = new() { Topology = WgpuPrimitiveTopology.TriangleList, FrontFace = WgpuFrontFace.Ccw, CullMode = WgpuCullMode.None },
        Multisample = new() { Count = 1, Mask = uint.MaxValue }
    });

    public bool HasFrame => _hasFrame;
    public void Reset() => _hasFrame = false;

    /// <summary>Uploads a completed software frame; Metal only presents the texture.</summary>
    public void PresentBitmap(RenderTarget pixels, WgpuTextureView surface)
    {
        EnsureFrame(pixels.Width, pixels.Height);
        _queue.WriteTexture(
            new WgpuImageCopyTexture { Texture = _frame!, Aspect = WgpuTextureAspect.All },
            pixels.Pixels,
            new WgpuTextureDataLayout { BytesPerRow = (uint)pixels.Stride, RowsPerImage = (uint)pixels.Height },
            new WgpuExtent3D { Width = (uint)pixels.Width, Height = (uint)pixels.Height, DepthOrArrayLayers = 1 });
        _hasFrame = true;
        LastRegion = new(0, 0, pixels.Width, pixels.Height);
        RasterizedPixels = LastRegion.Area;
        Copy(surface, _frameBinding!, _presentPipeline, LastRegion, false);
    }

    private void EnsureFrame(int width, int height)
    {
        if (_width == width && _height == height) return;
        DisposeFrame();
        (_frame, _frameView, _frameBinding) = CreateTexture(width, height, false);
        _width = width; _height = height;
        _hasFrame = false;
    }

    public void Render(Scene scene, GpuDamageRegion region, WgpuTextureView surface, int width, int height, AntialiasingMode antialiasing)
    {
        RasterizedPixels = 0;
        LastRegion = region;
        EnsureFrame(width, height);
        if (region.Area > 0)
        {
            if (_patchWidth != region.Width || _patchHeight != region.Height)
            {
                _patchBinding?.Dispose(); _patchView?.Dispose(); _patch?.Dispose();
                _patchBinding = null; _patchView = null; _patch = null;
                _patchWidth = _patchHeight = 0;
                (_patch, _patchView, _patchBinding) = CreateTexture(region.Width, region.Height, true);
                _patchWidth = region.Width; _patchHeight = region.Height;
            }
            _patchScene.Reset();
            _patchScene.Append(scene, Matrix3x2.CreateTranslation(-region.X, -region.Y));
            _renderer!.Render(_patchScene, _patchView!, new RenderParams((uint)region.Width, (uint)region.Height,
                RgbaColor.FromBytes(0, 0, 0, 255), antialiasing, RenderFormat.Rgba8));
            Copy(_frameView!, _patchBinding!, _copyPipeline, region, _hasFrame);
            _hasFrame = true;
            RasterizedPixels = region.Area;
        }
        if (!_hasFrame) throw new InvalidOperationException("A retained frame must be initialized with full damage.");
        Copy(surface, _frameBinding!, _presentPipeline, new(0, 0, width, height), false);
    }

    private (WgpuTexture, WgpuTextureView, WgpuBindGroup) CreateTexture(int width, int height, bool raster)
    {
        var texture = _device.CreateTexture(new()
        {
            Size = new() { Width = (uint)width, Height = (uint)height, DepthOrArrayLayers = 1 },
            MipLevelCount = 1, SampleCount = 1, Dimension = WgpuTextureDimension.D2, Format = WgpuTextureFormat.Rgba8Unorm,
            Usage = WgpuTextureUsage.TextureBinding | (raster ? WgpuTextureUsage.StorageBinding : WgpuTextureUsage.RenderAttachment | WgpuTextureUsage.CopyDst),
            ViewFormats = []
        });
        WgpuTextureView? view = null;
        try
        {
            view = texture.CreateView();
            var binding = _device.CreateBindGroup(new() { Layout = _bindings, Entries = [WgpuBindGroupEntry.CreateTextureView(0, view)] });
            return (texture, view, binding);
        }
        catch { view?.Dispose(); texture.Dispose(); throw; }
    }

    private void Copy(WgpuTextureView target, WgpuBindGroup source, WgpuRenderPipeline pipeline, GpuDamageRegion region, bool preserve)
    {
        using var encoder = _device.CreateCommandEncoder();
        using (var pass = encoder.BeginRenderPass(new()
        {
            ColorAttachments = [new() { View = target, Load = preserve ? WgpuLoadOp.Load : WgpuLoadOp.Clear, Store = WgpuStoreOp.Store }]
        }))
        {
            pass.SetPipeline(pipeline);
            pass.SetBindGroup(0, source, []);
            pass.SetViewport(region.X, region.Y, region.Width, region.Height, 0, 1);
            pass.SetScissorRect((uint)region.X, (uint)region.Y, (uint)region.Width, (uint)region.Height);
            pass.Draw(3, 1, 0, 0);
            pass.End();
        }
        using var commands = encoder.Finish();
        _queue.Submit([commands]);
    }

    private void DisposeFrame()
    {
        _frameBinding?.Dispose(); _frameView?.Dispose(); _frame?.Dispose();
        _frameBinding = null; _frameView = null; _frame = null;
        _width = _height = 0;
        _hasFrame = false;
    }
    public void Dispose()
    {
        DisposeFrame();
        _patchBinding?.Dispose(); _patchView?.Dispose(); _patch?.Dispose();
        _patchScene.Dispose();
        _presentPipeline?.Dispose(); _copyPipeline?.Dispose(); _shader?.Dispose(); _layout?.Dispose(); _bindings?.Dispose(); _queue?.Dispose();
    }

    private const string Shader = """
        struct VertexOutput { @builtin(position) position: vec4<f32>, @location(0) uv: vec2<f32> }
        @group(0) @binding(0) var source: texture_2d<f32>;
        @vertex fn vertex(@builtin(vertex_index) index: u32) -> VertexOutput {
            let positions = array<vec2<f32>, 3>(vec2(-1., -1.), vec2(3., -1.), vec2(-1., 3.));
            var output: VertexOutput;
            output.position = vec4(positions[index], 0., 1.);
            output.uv = positions[index] * vec2(.5, -.5) + vec2(.5, .5);
            return output;
        }
        fn read(uv: vec2<f32>) -> vec4<f32> {
            let dimensions = vec2<i32>(textureDimensions(source));
            return textureLoad(source, clamp(vec2<i32>(uv * vec2<f32>(dimensions)), vec2(0), dimensions - vec2(1)), 0);
        }
        @fragment fn copy(input: VertexOutput) -> @location(0) vec4<f32> { return read(input.uv); }
        @fragment fn present_srgb(input: VertexOutput) -> @location(0) vec4<f32> {
            let color = read(input.uv);
            let linear = select(pow((color.rgb + vec3(.055)) / vec3(1.055), vec3(2.4)), color.rgb / vec3(12.92), color.rgb <= vec3(.04045));
            return vec4(linear, color.a);
        }
        """;
}

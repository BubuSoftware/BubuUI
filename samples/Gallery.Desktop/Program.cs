using EclipseUI;
using EclipseUI.Elements;
using EclipseUI.Platform.Windows;
using EclipseUI.Rendering;
using EclipseUI.Rendering.Vello;
using EclipseUI.Text.Shaping;
using Gallery;

// EclipseUI 画廊 · Windows 展示入口（Avalonia 式分层：共享 UI 在 samples/Gallery，本工程只是平台壳）。
//   dotnet run --project samples/Gallery.Desktop -c Release
//   dotnet run --project samples/Gallery.Desktop -c Release -- --shot out/gallery-window.png
//   dotnet run --project samples/Gallery.Desktop -c Release -- --cpu --shot out/gallery-cpu.png
// 自检/damage 统计/帧测量等验证能力在 sandbox/GalleryCheck。

int width = 1000;
int height = 760;
int? frames = null;
string? shotPath = null;
bool vsync = true;
bool useCpu = false;
bool alwaysRender = false;
int maxFps = 0;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--frames" when i + 1 < args.Length:
            frames = int.Parse(args[++i]);
            break;
        case "--width" when i + 1 < args.Length:
            width = int.Parse(args[++i]);
            break;
        case "--height" when i + 1 < args.Length:
            height = int.Parse(args[++i]);
            break;
        case "--shot" when i + 1 < args.Length:
            shotPath = args[++i];
            break;
        case "--no-vsync":
            vsync = false;
            break;
        case "--max-fps" when i + 1 < args.Length:
            maxFps = int.Parse(args[++i]);
            break;
        case "--cpu":
            useCpu = true;
            break;
        case "--always-render":
            alwaysRender = true;
            break;
        default:
            Console.Error.WriteLine($"unknown argument: {args[i]}");
            return 2;
    }
}

if (maxFps == 0 && vsync) maxFps = 60;

using var shaper = TextShaperFactory.TryCreate();
using var harfBuzz = shaper is null ? null : new HarfBuzzTextPainter(shaper);
ITextPainter textPainter = harfBuzz is null ? new MeasuringTextPainter() : harfBuzz;
// 布局与绘制共用同一个 shaping 缓存（同一字体、同一进距）。
if (harfBuzz is not null) TextMeasurer.Current = harfBuzz;

var theme = new ElementTheme();
var scene = GalleryScene.Build();
var galleryRoot = new GalleryRoot(scene.Counter, scene.Tasks, scene.Root);
galleryRoot.Mount();
var gallery = galleryRoot.Root;
var counter = scene.Counter;

using (galleryRoot)
{
    if (useCpu || !OperatingSystem.IsWindows())
        return RunCpu();

    using var window = new Win32Window("EclipseUI Gallery", width, height);
    using var presenter = VelloSurfaceBackend.Create(
        window.Handle,
        Math.Max(window.ClientWidth, 1),
        Math.Max(window.ClientHeight, 1),
        new VelloSurfaceOptions { PreferVsync = vsync });

    Console.WriteLine($"backend: {presenter.Name}, wgpu={presenter.BackendType}, format={presenter.Format}, present={presenter.PresentMode}");
    Console.WriteLine($"surface: {presenter.Width}x{presenter.Height} px, vsync={vsync}, max-fps={maxFps}");

    using var host = new WindowHost(window, presenter, gallery, new WindowHostOptions
    {
        Theme = theme,
        TextPainter = textPainter,
        MaxFramesPerSecond = maxFps,
        AlwaysRender = alwaysRender,
    }, galleryRoot);

    host.FrameTicked += (_, e) =>
    {
        if (e.Iteration == 1)
            Console.WriteLine($"first frame: {presenter.Width}x{presenter.Height} px, {gallery.Descendants().Count()} elements");
    };

    var rendered = host.Run(frames);

    Console.WriteLine($"ticks={host.Ticks} rendered={rendered} skipped={host.SkippedFrames} " +
                      $"logical={window.LogicalClientSize.Width:F0}x{window.LogicalClientSize.Height:F0} scale={window.ScaleFactor:F2}");
    Console.WriteLine($"state: counter={counter.count} showButton={counter.showButton} tasks={scene.Tasks.tasks.Count}");

    if (shotPath is not null && rendered > 0)
        GallerySnapshot.SavePng(gallery, window.LogicalClientSize, window.ScaleFactor,
            host.Input.Focus.Focused, shotPath, theme, textPainter);
}

return 0;

// ---- 离屏路径（无窗口截图对照）----

int RunCpu()
{
    var size = new Size(width, height);
    gallery.Measure(size);
    gallery.Arrange(new Rect(Point.Zero, size));

    var path = shotPath ?? Path.Combine(Environment.CurrentDirectory, "out", "gallery-cpu.png");
    GallerySnapshot.SavePng(gallery, size, 1.0, null, path, theme, textPainter);
    return 0;
}

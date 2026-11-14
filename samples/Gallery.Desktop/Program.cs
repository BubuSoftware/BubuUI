using System.Diagnostics.CodeAnalysis;
using EclipseUI;
using EclipseUI.Platform.Windows;
using EclipseUI.Platform.Linux;
using EclipseUI.Platform.MacOS;
using EclipseUI.Rendering;
using EclipseUI.Rendering.Vello;
using EclipseUI.Text.Shaping;
using Gallery;
using Microsoft.Extensions.DependencyInjection;

// EclipseUI 画廊 · Windows/Linux/macOS 桌面入口；共享 UI 在 samples/Gallery。
//   dotnet run --project samples/Gallery.Desktop -c Release
//   dotnet run --project samples/Gallery.Desktop -c Release -- --shot out/gallery-cpu.png   （离屏快照）
//   dotnet run --project samples/Gallery.Desktop -c Release -- --cpu --shot out/gallery-cpu.png
// 离屏对照与窗口走同一套 builder 装配（样式/主题/文本管线一致）；自检/damage 统计/帧测量在 sandbox/GalleryCheck。

var options = ParseArgs(args);
if (options is null)
    return 2;

var builder = AppBuilder.Configure<GalleryApp>()
    .UseVello(new VelloSurfaceOptions { PreferVsync = options.Vsync })
    .UseHarfBuzzText()
    .UseTheme(ElementTheme.Dark)
    .UseStyles(StyleRegistry.Instance, StyleBootstrap.Apply)
    .ConfigureServices(s => s.AddSingleton(options));

// --cpu / --shot：离屏 CPU 快照路径（不开窗口）——Build() 装配后直接 measure/arrange 出图。
if (options.Offscreen)
    return RunOffscreen(builder, options);

if (OperatingSystem.IsWindows()) builder.UseWindows();
else if (OperatingSystem.IsLinux()) builder.UseLinux();
else if (OperatingSystem.IsMacOS()) builder.UseMacOS();
else throw new PlatformNotSupportedException("Gallery.Desktop supports Windows, Linux and macOS.");

return builder.Start(args);

// The gallery's editable DataGrid resolves these row properties by name after trimming.
[DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties, typeof(DemoRow))]
static GalleryLaunchOptions? ParseArgs(string[] args)
{
    try
    {
        return GalleryLaunchOptions.Parse(args);
    }
    catch (Exception ex) when (ex is ArgumentException or FormatException)
    {
        Console.Error.WriteLine(ex.Message);
        Console.Error.WriteLine(
            "usage: Gallery.Desktop [--width N] [--height N] [--frames N] [--max-fps N] " +
            "[--no-vsync] [--always-render] [--cpu] [--shot path]");
        return null;
    }
}

static int RunOffscreen(AppBuilder builder, GalleryLaunchOptions options)
{
    var app = builder.Build();
    try
    {
        var window = app.MainWindow ?? throw new InvalidOperationException("MainWindow was not set.");
        var root = window.Content ?? throw new InvalidOperationException("Window.Content was not set.");

        var size = new Size(options.Width, options.Height);
        root.Measure(size);
        root.Arrange(new Rect(Point.Zero, size));

        var theme = (window.Theme ?? app.DefaultTheme) as ElementTheme ?? new ElementTheme();
        var textPainter = app.Services.GetService<ITextPainter>() ?? new MeasuringTextPainter();
        var shotPath = options.ShotPath
                       ?? Path.Combine(Environment.CurrentDirectory, "out", "gallery-cpu.png");
        GallerySnapshot.SavePng(root, size, 1.0, shotPath, theme, textPainter);

        window.Dispose();
        return 0;
    }
    finally
    {
        (app.Services as IDisposable)?.Dispose();
    }
}

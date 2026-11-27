using Path = System.IO.Path;
using System.Collections.Generic;
using BubuUI;
using BubuUI.Elements;
using BubuUI.Platform.Windows;
using BubuUI.Rendering;
using BubuUI.Rendering.Vello;
using BubuUI.Text.Shaping;
using BubuUI.Themes.Fluent;
using Gallery;

// BubuUI 画廊验证壳（原 GalleryApp 的验证职责，2026-11-09 从 samples 移到 sandbox）：
//   --selfcheck       Fluent 控件真实消息自动化（消息 → InputRouter → 信号 → 重建 全链路）
//   --frames N        限帧运行并打印帧平均/p95
//   damage 统计        full/partial 帧数与覆盖率（avg/max），无需开关
//   --cpu             无窗口离屏链路（InputRouter 点击 + PNG 对照）
//   dotnet run --project sandbox/GalleryCheck -c Release -- --selfcheck --no-vsync

int width = 1000;
int height = 760;
int? frames = null;
string? shotPath = null;
bool vsync = true;
bool useCpu = false;
bool selfCheck = false;
bool alwaysRender = false;
bool lightTheme = false;
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
        case "--light":
            lightTheme = true;
            break;
        case "--selfcheck":
            selfCheck = true;
            frames ??= 150;
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

var theme = lightTheme ? FluentTheme.Light : FluentTheme.Dark;
var mainWindow = new MainWindow { InitialTheme = lightTheme ? ThemeVariant.Light : ThemeVariant.Dark };
mainWindow.EnsureInitialized();
var galleryRoot = mainWindow;
var gallery = mainWindow.Content!;
var page = mainWindow.Page;
var actionButton = page.LiveControls.OfType<Button>().First();
var slider = page.LiveControls.OfType<Slider>().Single();
var progress = page.LiveControls.OfType<ProgressBar>().Single();

using (galleryRoot)
{
    if (useCpu || !OperatingSystem.IsWindows())
        return RunCpu();

    using var window = new Win32Window("BubuUI · Gallery Check", width, height);
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
    }, uiRoot: galleryRoot);

    page.ApplyTheme = host.SetTheme;

    // 根滚动视图（2026-11-09 画廊重构）：交互目标可能被滚出视口，点击前先 EnsureVisible。
    var rootViewer = mainWindow.RootViewer;
    var failures = new List<string>();
    var measured = new List<double>();
    const int warmupFrames = 60;
    var step = 0;
    var fullFrames = 0;
    var partialFrames = 0;
    double partialArea = 0, maxPartialCoverage = 0;

    void EnsureVisible(Element element)
    {
        // 元素在根坐标系里的绝对 Y（含当前滚动偏移——内容按 -Offset 平移落进 FinalRect）。
        double top = element.FinalRect.Y, bottom = top + element.FinalRect.Height;
        for (var node = element.Parent; node is not null; node = node.Parent)
        {
            top += node.FinalRect.Y;
            bottom += node.FinalRect.Y;
        }
        const double margin = 16;
        var viewportH = rootViewer.ViewportHeight;
        var offset = rootViewer.VerticalOffset;
        if (bottom > viewportH)
            rootViewer.ScrollToVerticalOffset(offset + top - margin);   // 下滚：顶边贴视口上缘
        else if (top < 0)
            rootViewer.ScrollToVerticalOffset(offset + bottom - viewportH + margin); // 上滚
    }

    host.FrameTicked += (_, e) =>
    {
        if (e.Iteration == 1)
            Console.WriteLine($"first frame: {presenter.Width}x{presenter.Height} px, {gallery.Descendants().Count()} elements");

        if (e.Presented)
        {
            if (e.Damage.IsFull)
            {
                fullFrames++;
            }
            else
            {
                partialFrames++;
                var coverage = e.Damage.Coverage(e.LogicalSize);
                partialArea += coverage;
                maxPartialCoverage = Math.Max(maxPartialCoverage, coverage);
                if (selfCheck)
                    Console.WriteLine($"selfcheck: damage {e.Damage.Rects.Count} rect(s), {coverage:P2}");
            }

            if (e.Iteration > warmupFrames)
                measured.Add(e.TotalMilliseconds);
        }

        if (!selfCheck) return;

        // 用真实的 WM_LBUTTONDOWN/UP 消息驱动：覆盖 消息 → InputRouter → 信号 → 重建 全链路。
        // 每步先 EnsureVisible（滚动生效要等下几帧排列，点击留 ≥5 帧间隔）再点击。
        switch (step)
        {
            case 0 when e.Iteration >= 5:
                EnsureVisible(actionButton);
                step = 1;
                break;
            case 1 when e.Iteration >= 10:
                window.PostPointerClick(ToDevice(actionButton, window.ScaleFactor));
                step = 2;
                break;
            case 2 when e.Iteration >= 15:
                Require("button input updates status", page.Status == "Button clicked 1 time");
                slider.Value = 75;
                Require("slider updates progress", progress.Value == 75);
                window.SetClientSize(860, 600);
                step = 3;
                break;
            case 3 when e.Iteration >= 25:
                Require("surface follows resize", presenter.Width == window.ClientWidth && presenter.Height == window.ClientHeight);
                window.SetClientSize(width, height);
                step = 4;
                break;
            case 4 when e.Iteration >= 35:
                Require("surface restored after resize", presenter.Width == window.ClientWidth && presenter.Height == window.ClientHeight);
                page.ResetControls();
                Require("reset restores values", page.LiveControls.OfType<Slider>().Single().Value == 40);
                step = 5;
                break;
        }
    };

    var rendered = host.Run(frames);

    var measuredAvg = measured.Count == 0 ? 0 : measured.Average();
    var measuredP95 = measured.Count == 0 ? 0 : Percentile(measured, 0.95);
    var frameSummary = measured.Count == 0 ? "n/a" : $"{measuredAvg:F2}ms p95={measuredP95:F2}ms";
    var coverageSummary = partialFrames == 0
        ? "n/a"
        : $"{partialArea / partialFrames:P2} avg, {maxPartialCoverage:P2} max";
    Console.WriteLine($"ticks={host.Ticks} rendered={rendered} skipped={host.SkippedFrames} " +
                      $"measured={measured.Count} " +
                      $"frame={frameSummary} " +
                      $"logical={window.LogicalClientSize.Width:F0}x{window.LogicalClientSize.Height:F0} scale={window.ScaleFactor:F2}");
    Console.WriteLine($"damage: full={fullFrames} partial={partialFrames} coverage(avg/max)={coverageSummary}");
    Console.WriteLine($"state: {page.Status}");

    if (shotPath is not null && rendered > 0)
        GallerySnapshot.SavePng(gallery, window.LogicalClientSize, window.ScaleFactor, shotPath, theme, textPainter);

    if (selfCheck)
    {
        if (step < 5) failures.Add($"selfcheck stopped at step {step}/5");
        Console.WriteLine(failures.Count == 0
            ? "selfcheck: PASS"
            : $"selfcheck: FAIL ({string.Join("; ", failures)})");
        if (failures.Count > 0) return 1;
    }

    void Require(string what, bool ok)
    {
        Console.WriteLine($"selfcheck: {(ok ? "ok" : "FAIL")} {what}");
        if (!ok) failures.Add(what);
    }
}

return 0;

// ---- CPU 离屏路径（无窗口自检 / 截图对照）----

int RunCpu()
{
    var size = new Size(width, height);
    gallery.Measure(size);
    gallery.Arrange(new Rect(Point.Zero, size));

    var painter = new ElementPainter(theme, textPainter);
    painter.Prepare(gallery);
    gallery.Measure(size);
    gallery.Arrange(new Rect(Point.Zero, size));
    var input = new InputRouter(gallery);
    Click(input, actionButton);
    galleryRoot.ProcessInvalidations();
    if (page.Status != "Button clicked 1 time") throw new InvalidOperationException("Button input did not update status.");
    slider.Value = 75;
    if (progress.Value != 75) throw new InvalidOperationException("Slider did not update progress.");
    page.ResetControls();
    galleryRoot.ProcessInvalidations();
    gallery.Measure(size);
    gallery.Arrange(new Rect(Point.Zero, size));
    Console.WriteLine($"cpu: Fluent button, slider and reset passed; {page.Status}");

    var path = shotPath ?? Path.Combine(Environment.CurrentDirectory, "out", "gallery-cpu.png");
    GallerySnapshot.SavePng(gallery, size, 1.0, path, theme, textPainter);
    return 0;
}

// 点击 = 按下 + 释放（坐标按根坐标系累加父链 FinalRect）。
static void Click(InputRouter input, Element element)
{
    var point = CenterInRoot(element);
    input.HitTest(point); // 触发一次命中测试，便于调试命中目标
    input.PointerPress(point);
    input.PointerRelease(point);
}

static Point CenterInRoot(Element element)
{
    double x = element.FinalRect.X + element.FinalRect.Width / 2;
    double y = element.FinalRect.Y + element.FinalRect.Height / 2;
    for (var node = element.Parent; node is not null; node = node.Parent)
    {
        x += node.FinalRect.X;
        y += node.FinalRect.Y;
    }
    return new Point(x, y);
}

static Point ToDevice(Element element, double scale)
{
    var center = CenterInRoot(element);
    return new Point(center.X * scale, center.Y * scale);
}

static double Percentile(List<double> values, double percentile)
{
    if (values.Count == 0) return 0;
    var sorted = values.ToArray();
    Array.Sort(sorted);
    var index = Math.Clamp((int)Math.Ceiling(percentile * sorted.Length) - 1, 0, sorted.Length - 1);
    return sorted[index];
}

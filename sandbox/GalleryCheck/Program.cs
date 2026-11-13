using Path = System.IO.Path;
using System.Collections.Generic;
using EclipseUI;
using EclipseUI.Elements;
using EclipseUI.Platform.Windows;
using EclipseUI.Rendering;
using EclipseUI.Rendering.Vello;
using EclipseUI.Text.Shaping;
using Gallery;

// EclipseUI 画廊验证壳（原 GalleryApp 的验证职责，2026-11-09 从 samples 移到 sandbox）：
//   --selfcheck       六步真实消息自动化（消息 → InputRouter → 信号 → 重建 全链路）
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

var theme = lightTheme ? ElementTheme.Light : new ElementTheme();
Styling.Provider = StyleRegistry.Instance; // S-B：装配 Class 样式（类名直配 setter 序列）
StyleBootstrap.Apply(StyleRegistry.Instance); // E1：app.eustyle 编译产物（生成的 Define 序列）
// MainWindow.ui 直接生成 Window 派生类；先初始化，再读取内容与 Ref 锚点。
var mainWindow = new MainWindow();
mainWindow.EnsureInitialized();
GalleryApp.WireInteractions(mainWindow); // 应用级接线（注册伪类/读回订阅/条目工厂/启用开关）
var galleryRoot = mainWindow;
var gallery = mainWindow.Content!;
var counter = mainWindow.CounterView;
var taskList = mainWindow.TaskListView;
var addTaskButton = mainWindow.AddButton;
var themeButton = mainWindow.ThemeButton;

using (galleryRoot)
{
    if (useCpu || !OperatingSystem.IsWindows())
        return RunCpu();

    using var window = new Win32Window("EclipseUI · Gallery Check", width, height);
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

// S-A 主题令牌化：--light 起始浅色；Toggle theme 按钮换 token 表 → 段缓存失效 → 整帧重绘。
var dark = !lightTheme;
themeButton.AddHandler(Button.ClickEvent, () =>
{
    dark = !dark;
    host.SetTheme(dark ? ElementTheme.Dark : ElementTheme.Light);
});

    // 根滚动视图（2026-11-09 画廊重构）：交互目标可能被滚出视口，点击前先 EnsureVisible。
    var rootViewer = mainWindow.RootViewer;
    var checkBox = gallery.Descendants().OfType<CheckBox>().First(c => c.Label == "Button visible");
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
                EnsureVisible(checkBox);
                step = 1;
                break;
            case 1 when e.Iteration >= 10:
                window.PostPointerClick(ToDevice(checkBox, window.ScaleFactor));
                step = 2;
                break;
            case 2 when e.Iteration >= 15:
                Require("checkbox toggles showButton", counter.showButton);
                var plus = gallery.Descendants().OfType<Button>().FirstOrDefault(b => b.Label == "+1");
                Require("conditional +1 button mounted", plus is not null);
                if (plus is not null) EnsureVisible(plus);
                step = 3;
                break;
            case 3 when e.Iteration >= 20:
                var plusButton = gallery.Descendants().OfType<Button>().First(b => b.Label == "+1");
                window.PostPointerClick(ToDevice(plusButton, window.ScaleFactor));
                step = 4;
                break;
            case 4 when e.Iteration >= 25:
                Require("+1 click increments count", counter.count == 1);
                EnsureVisible(addTaskButton);
                step = 5;
                break;
            case 5 when e.Iteration >= 30:
                window.PostPointerClick(ToDevice(addTaskButton, window.ScaleFactor));
                step = 6;
                break;
            case 6 when e.Iteration >= 35:
                Require("add task appends item", taskList.tasks.Count == 4);
                window.SetClientSize(860, 600);
                step = 7;
                break;
            case 7 when e.Iteration >= 45:
                Require("surface follows resize", presenter.Width == window.ClientWidth && presenter.Height == window.ClientHeight);
                window.SetClientSize(width, height);
                step = 8;
                break;
            case 8 when e.Iteration >= 55:
                Require("surface restored after resize", presenter.Width == window.ClientWidth && presenter.Height == window.ClientHeight);
                step = 9;
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
    Console.WriteLine($"state: counter={counter.count} showButton={counter.showButton} tasks={taskList.tasks.Count}");

    if (shotPath is not null && rendered > 0)
        GallerySnapshot.SavePng(gallery, window.LogicalClientSize, window.ScaleFactor,
            host.Input.Focus.Focused, shotPath, theme, textPainter);

    if (selfCheck)
    {
        if (step < 9) failures.Add($"selfcheck stopped at step {step}/9");
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

    // 走与窗口相同的输入路径：点复选框展开 +1，再点 +1 让计数变化。
    var input = new InputRouter(gallery);
    var checkBox = gallery.Descendants().OfType<CheckBox>().First(c => c.Label == "Button visible");
    Click(input, checkBox);
    galleryRoot.ProcessInvalidations();
    Console.WriteLine($"cpu: checkbox -> showButton={counter.showButton}");

    gallery.Measure(size);
    gallery.Arrange(new Rect(Point.Zero, size));

    var button = gallery.Descendants().OfType<Button>().FirstOrDefault(b => b.Label == "+1");
    if (button is not null) Click(input, button);
    galleryRoot.ProcessInvalidations();
    Console.WriteLine($"cpu: +1 -> count={counter.count}");

    gallery.Measure(size);
    gallery.Arrange(new Rect(Point.Zero, size));

    var path = shotPath ?? Path.Combine(Environment.CurrentDirectory, "out", "gallery-cpu.png");
    GallerySnapshot.SavePng(gallery, size, 1.0, input.Focus.Focused, path, theme, textPainter);
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

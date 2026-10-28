using EclipseUI;
using EclipseUI.Elements;
using EclipseUI.Platform.Windows;
using EclipseUI.Rendering;
using EclipseUI.Rendering.Vello;
using EclipseUI.Text.Shaping;
using Samples;

// G2 出口验证样例：CounterApp + TaskList 在真实 Win32 窗口中可交互。
//   dotnet run --project samples/GalleryApp -c Release
//   dotnet run --project samples/GalleryApp -c Release -- --frames 300 --shot out/gallery-window.png
//   dotnet run --project samples/GalleryApp -c Release -- --cpu --shot out/gallery-cpu.png

int width = 1000;
int height = 760;
int? frames = null;
string? shotPath = null;
bool vsync = true;
bool useCpu = false;
bool selfCheck = false;
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

// 字体：优先西文字体，回落 CJK 字体；都没有时用只测量不绘制的降级实现。
using var shaper = TryCreateShaper();
using var harfBuzz = shaper is null ? null : new HarfBuzzTextPainter(shaper);
ITextPainter textPainter = harfBuzz is null ? new MeasuringTextPainter() : harfBuzz;
// 布局与绘制共用同一个 shaping 缓存（同一字体、同一进距）。
if (harfBuzz is not null) TextMeasurer.Current = harfBuzz;

var theme = new ElementTheme();
var scene = BuildGallery();
var gallery = scene.Root;
var counter = scene.Counter;
var taskList = scene.Tasks;
var addTaskButton = scene.AddButton;

using (counter)
using (taskList)
{
    if (useCpu || !OperatingSystem.IsWindows())
        return RunCpu();

    using var window = new Win32Window("EclipseUI · G2 Gallery (Counter + TaskList)", width, height);
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
    });

    var clicks = 0;
    var checkBox = gallery.Descendants().OfType<CheckBox>().First();
    var failures = new List<string>();
    var measured = new List<double>();
    const int warmupFrames = 60;
    var step = 0;

    host.FrameRendered += (_, e) =>
    {
        if (e.Frame == 1)
            Console.WriteLine($"first frame: {presenter.Width}x{presenter.Height} px, {gallery.Descendants().Count()} elements");
        if (e.Frame > warmupFrames) measured.Add(e.TotalMilliseconds);

        if (!selfCheck) return;

        // 用真实的 WM_LBUTTONDOWN/UP 消息驱动：覆盖 消息 → InputRouter → 信号 → 重建 全链路。
        switch (step)
        {
            case 0 when e.Frame >= 5:
                window.PostPointerClick(ToDevice(checkBox, window.ScaleFactor));
                step = 1;
                break;
            case 1 when e.Frame >= 10:
                Require("checkbox toggles showButton", counter.showButton.Value);
                var plus = gallery.Descendants().OfType<Button>().FirstOrDefault(b => b.Label == "+1");
                Require("conditional +1 button mounted", plus is not null);
                if (plus is not null) window.PostPointerClick(ToDevice(plus, window.ScaleFactor));
                step = 2;
                break;
            case 2 when e.Frame >= 15:
                Require("+1 click increments count", counter.count.Value == 1);
                window.PostPointerClick(ToDevice(addTaskButton, window.ScaleFactor));
                step = 3;
                break;
            case 3 when e.Frame >= 20:
                Require("add task appends item", taskList.tasks.Value.Count == 4);
                window.SetClientSize(860, 600);
                step = 4;
                break;
            case 4 when e.Frame >= 30:
                Require("surface follows resize", presenter.Width == window.ClientWidth && presenter.Height == window.ClientHeight);
                window.SetClientSize(width, height);
                step = 5;
                break;
            case 5 when e.Frame >= 40:
                Require("surface restored after resize", presenter.Width == window.ClientWidth && presenter.Height == window.ClientHeight);
                step = 6;
                break;
        }
    };

    // 记录交互结果，退出时打印，便于脚本化验证。
    addTaskButton.AddHandler(Button.ClickEvent, () => clicks++);
    counter.count.Subscribe(() => clicks++);

    var rendered = host.Run(frames);

    var measuredAvg = measured.Count == 0 ? host.AverageFrameMilliseconds : measured.Average();
    var measuredP95 = measured.Count == 0 ? measuredAvg : Percentile(measured, 0.95);
    Console.WriteLine($"frames={rendered} warmup={Math.Min(warmupFrames, rendered)} " +
                      $"frame={measuredAvg:F2}ms p95={measuredP95:F2}ms " +
                      $"logical={window.LogicalClientSize.Width:F0}x{window.LogicalClientSize.Height:F0} scale={window.ScaleFactor:F2}");
    Console.WriteLine($"state: counter={counter.count.Value} showButton={counter.showButton.Value} " +
                      $"tasks={taskList.tasks.Value.Count} buttonClicks={clicks}");

    if (shotPath is not null && rendered > 0)
        SavePng(gallery, window.LogicalClientSize, window.ScaleFactor, host.Input.Focus.Focused, shotPath);

    if (selfCheck)
    {
        if (step < 6) failures.Add($"selfcheck stopped at step {step}/6");
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

// ---- 场景 ----

static (StackPanel Root, Counter Counter, TaskList Tasks, Button AddButton) BuildGallery()
{
    var counter = new Counter();
    counter.Mount();
    counter.showButton.Value = false;
    SetPadding(counter.Root, 16);

    var taskList = new TaskList();
    taskList.Mount();
    SetPadding(taskList.Root, 16);
    taskList.tasks.Value = new List<TaskItem>
    {
        new TaskItem { Id = 1, Name = "Design review" },
        new TaskItem { Id = 2, Name = "Damage tracking v0" },
        new TaskItem { Id = 3, Name = "Golden image test" },
    };

    var addTaskButton = new Button { Label = "+ Add task" };
    var nextId = 4;
    addTaskButton.AddHandler(Button.ClickEvent, () =>
    {
        var tasks = new List<TaskItem>(taskList.tasks.Value)
        {
            new TaskItem { Id = nextId, Name = $"Task {nextId}" },
        };
        nextId++;
        taskList.tasks.Value = tasks;
    });

    var root = new StackPanel { Gap = 14, Padding = 20 };
    root.Children.Add(Text("EclipseUI G2 Gallery", 26));
    root.Children.Add(Text("Click the checkbox to reveal +1 · Tab / Space to drive focus · Esc to close", 12));
    root.Children.Add(counter.Root);
    root.Children.Add(addTaskButton);
    root.Children.Add(taskList.Root);
    return (root, counter, taskList, addTaskButton);
}

static TextBlock Text(string text, double fontSize)
{
    var block = new TextBlock { FontSize = fontSize };
    block.AddRun(new TextRun(text));
    return block;
}

static void SetPadding(Element element, double padding)
{
    if (element is Panel panel) panel.Padding = padding;
}

static HarfBuzzTextShaper? TryCreateShaper()
{
    var candidates = new[]
    {
        @"C:\Windows\Fonts\segoeui.ttf",
        @"C:\Windows\Fonts\msyh.ttc",
        @"C:\Windows\Fonts\arial.ttf",
    };

    foreach (var path in candidates)
    {
        if (!File.Exists(path)) continue;
        try
        {
            var shaper = new HarfBuzzTextShaper(path);
            Console.WriteLine($"font: {path}");
            return shaper;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"font failed: {path}: {ex.Message}");
        }
    }

    Console.WriteLine("font: none (measuring-only text painter)");
    return null;
}

// ---- 离屏路径（无窗口自检 / 截图对照）----

int RunCpu()
{
    var size = new Size(width, height);
    gallery.Measure(size);
    gallery.Arrange(new Rect(Point.Zero, size));

    // 走与窗口相同的输入路径：点复选框展开 +1，再点 +1 让计数变化。
    var input = new InputRouter(gallery);
    var checkBox = gallery.Descendants().OfType<CheckBox>().First();
    Click(input, checkBox);
    Console.WriteLine($"cpu: checkbox -> showButton={counter.showButton.Value}");

    gallery.Measure(size);
    gallery.Arrange(new Rect(Point.Zero, size));

    var button = gallery.Descendants().OfType<Button>().FirstOrDefault(b => b.Label == "+1");
    if (button is not null) Click(input, button);
    Console.WriteLine($"cpu: +1 -> count={counter.count.Value}");

    gallery.Measure(size);
    gallery.Arrange(new Rect(Point.Zero, size));

    var path = shotPath ?? Path.Combine(Environment.CurrentDirectory, "out", "gallery-cpu.png");
    SavePng(gallery, size, 1.0, input.Focus.Focused, path);
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

void SavePng(Element root, Size logicalSize, double scale, Element? focus, string path)
{
    var directory = Path.GetDirectoryName(Path.GetFullPath(path));
    if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

    var painter = new ElementPainter(theme, textPainter);
    var list = painter.Build(root, logicalSize, scale, focus);
    var errors = list.Validate();
    if (errors.Count > 0)
    {
        Console.Error.WriteLine($"display list invalid: {string.Join("; ", errors)}");
        return;
    }

    var widthPx = Math.Max(1, (int)Math.Round(logicalSize.Width * scale));
    var heightPx = Math.Max(1, (int)Math.Round(logicalSize.Height * scale));

    using var backend = new VelloCpuBackend(new VelloBackendOptions { UseCpu = true, ValidateDisplayList = true });
    var target = new RenderTarget(widthPx, heightPx);
    backend.Render(list, target);
    PngEncoder.Write(path, target);

    Console.WriteLine($"shot: {path} ({widthPx}x{heightPx} px, {list.Commands.Count} commands, hash={target.ContentHash():X16})");
}

using System.Diagnostics;
using EclipseUI;
using EclipseUI.Platform.Windows;
using EclipseUI.Rendering;
using EclipseUI.Rendering.Vello;
using WindowSpike;

// G2-00/01 spike：真实窗口 + Vello GPU surface 呈现。
//   dotnet run --project samples/WindowSpike -c Release
//   dotnet run --project samples/WindowSpike -c Release -- --frames 120 --shot out/window-frame.png

int width = 1100;
int height = 720;
int? frames = null;
string? shotPath = null;
bool vsync = true;
bool useCpu = false;
bool useGpuOffscreen = false;
bool selfCheck = false;
var parts = SceneParts.All;
int warmup = 30;
int maxFps = 0;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--frames" when i + 1 < args.Length:
            frames = int.Parse(args[++i]);
            break;
        case "--shot" when i + 1 < args.Length:
            shotPath = args[++i];
            break;
        case "--width" when i + 1 < args.Length:
            width = int.Parse(args[++i]);
            break;
        case "--height" when i + 1 < args.Length:
            height = int.Parse(args[++i]);
            break;
        case "--no-vsync":
            vsync = false;
            break;
        case "--parts" when i + 1 < args.Length:
            parts = ParseParts(args[++i]);
            break;
        case "--warmup" when i + 1 < args.Length:
            warmup = int.Parse(args[++i]);
            break;
        case "--max-fps" when i + 1 < args.Length:
            maxFps = int.Parse(args[++i]);
            break;
        case "--selfcheck":
            selfCheck = true;
            frames ??= 150;
            break;
        case "--cpu":
            useCpu = true;
            break;
        case "--offscreen-gpu":
            useGpuOffscreen = true;
            useCpu = true;
            break;
        default:
            Console.Error.WriteLine($"unknown argument: {args[i]}");
            return 2;
    }
}

// vsync 时默认按 60 帧限速；--no-vsync 下保持不限速以测量真实 GPU 成本。
if (maxFps == 0 && vsync) maxFps = 60;

using var scene = new DemoScene();

if (useCpu || !OperatingSystem.IsWindows())
{
    // 无窗口对照：同一场景走 CPU/GPU 离屏光栅化，产出 PNG 供像素比对。
    return RunOffscreen(scene, width, height, frames ?? 1, shotPath, parts, useGpuOffscreen);
}

using var window = new Win32Window("EclipseUI · G2 surface slice", width, height);

var pointer = Point.Zero;
var pointerInside = false;
var clicks = 0;
var lastKey = "none";

window.Resized += (_, e) =>
{
    Console.WriteLine($"resize: {e.Width}x{e.Height} px, scale={e.Scale:F2}, logical={e.LogicalSize}");
};

window.PointerMoved += (_, e) =>
{
    pointer = e.LogicalPosition;
    pointerInside = true;
};

window.PointerLeft += (_, _) => pointerInside = false;

window.PointerPressed += (_, e) =>
{
    pointer = e.LogicalPosition;
    pointerInside = true;
    clicks++;
    Console.WriteLine($"click #{clicks} at ({pointer.X:F0},{pointer.Y:F0}) logical");
};

window.KeyDown += (_, e) => lastKey = e.Key == Key.None ? $"vk{e.VirtualKey}" : e.Key.ToString();

window.CharacterTyped += (_, e) => lastKey = $"'{e.Character}'";

window.Closing += (_, _) => Console.WriteLine("window closing");

using var presenter = VelloSurfaceBackend.Create(
    window.Handle,
    Math.Max(window.ClientWidth, 1),
    Math.Max(window.ClientHeight, 1),
    new VelloSurfaceOptions { PreferVsync = vsync });

Console.WriteLine($"backend: {presenter.Name}, wgpu={presenter.BackendType}, format={presenter.Format}, present={presenter.PresentMode}");
Console.WriteLine($"caps: {presenter.Diagnostics}");
Console.WriteLine($"surface: {presenter.Width}x{presenter.Height} px, vsync={vsync}");

window.Resized += (_, e) => presenter.Resize(Math.Max(e.Width, 1), Math.Max(e.Height, 1));

// --selfcheck：程序化改尺寸，验证 WM_SIZE → presenter.Resize → 继续出帧不崩。
var selfCheckSizes = new[] { new Size(800, 600), new Size(1000, 700), new Size(640, 480), new Size(1100, 720) };
var selfCheckStep = 0;
var selfCheckFailures = new List<string>();

window.Resized += (_, e) =>
{
    if (!selfCheck || selfCheckStep == 0) return;
    if (presenter.Width != e.Width || presenter.Height != e.Height)
        selfCheckFailures.Add($"resize target {e.Width}x{e.Height} but presenter is {presenter.Width}x{presenter.Height}");
};

window.Show();

var bounds = window.ClientBoundsOnScreen();
Console.WriteLine($"client-bounds: {bounds.X:F0},{bounds.Y:F0},{bounds.Width:F0},{bounds.Height:F0}");

var clock = Stopwatch.StartNew();
var interval = Stopwatch.StartNew();
var buildTimes = new List<double>(1024);
var presentTimes = new List<double>(1024);
var acquireTimes = new List<double>(1024);
var gpuRenderTimes = new List<double>(1024);
var presentTailTimes = new List<double>(1024);
var frameIntervals = new List<double>(1024);
int rendered = 0;
bool recording = false;
var targetIntervalMs = maxFps > 0 ? 1000.0 / maxFps : 0;

while (window.PumpMessages())
{
    if (window.IsClosed) break;

    var size = window.LogicalClientSize;
    if (size.IsEmpty)
    {
        // 最小化：没有可绘制的表面尺寸。
        Thread.Sleep(16);
        continue;
    }

    var state = new FrameState(size, clock.Elapsed.TotalSeconds, pointer, pointerInside, clicks, lastKey);

    var buildStart = Stopwatch.GetTimestamp();
    var list = scene.Build(state, parts);
    var buildEnd = Stopwatch.GetTimestamp();

    presenter.Render(list);
    var renderEnd = Stopwatch.GetTimestamp();

    // 主动限帧：Fifo 下如果自由跑，每次都会错过一个 vsync（acquire 阻塞 + 提交延迟叠加）。
    // 提前 1ms 唤醒、末段自旋补足（依赖 Win32Window 的 1ms 定时器分辨率）；
    // 直接 Sleep(remaining) 在默认 ~15.6ms 量子下会整量子超调（实测墙钟只有 ~33fps）。
    // （DwmFlush 合成钟 pacing 实测过：虚拟显示链合成节奏抖动大，反而拖垮到 43.5fps，
    // 见 design/reports/target-machine-60fps-2026-11-08.md §7。）
    if (targetIntervalMs > 0)
    {
        var remaining = targetIntervalMs - interval.Elapsed.TotalMilliseconds;
        if (remaining > 2.0) Thread.Sleep((int)(remaining - 1.0));
        while (interval.Elapsed.TotalMilliseconds < targetIntervalMs) Thread.SpinWait(64);
    }

    rendered++;
    if (!recording && rendered > warmup) recording = true;
    if (recording)
    {
        var timings = presenter.LastTimings;
        buildTimes.Add(TicksToMs(buildEnd - buildStart));
        presentTimes.Add(TicksToMs(renderEnd - buildEnd));
        acquireTimes.Add(timings.AcquireMilliseconds);
        gpuRenderTimes.Add(timings.RenderMilliseconds);
        presentTailTimes.Add(timings.PresentMilliseconds);
        frameIntervals.Add(interval.Elapsed.TotalMilliseconds);
    }
    interval.Restart();

    if (rendered == 1)
        Console.WriteLine($"first frame: {list.Commands.Count} commands, {presenter.Width}x{presenter.Height} surface");

    if (selfCheck && rendered % 30 == 0 && selfCheckStep < selfCheckSizes.Length)
    {
        var target = selfCheckSizes[selfCheckStep++];
        Console.WriteLine($"selfcheck: resize -> {target.Width:F0}x{target.Height:F0}");
        window.SetClientSize((int)target.Width, (int)target.Height);
    }

    if (frames is { } limit && rendered >= limit)
        break;
}

if (frameIntervals.Count > 0)
{
    var measured = frameIntervals.Sum();
    Console.WriteLine($"frames={rendered} measured={frameIntervals.Count} warmup={warmup} " +
                      $"fps={frameIntervals.Count * 1000.0 / Math.Max(measured, 0.001):F1} " +
                      $"build={Average(buildTimes):F2}ms present={Average(presentTimes):F2}ms " +
                      $"frame={Average(frameIntervals):F2}ms p95={Percentile(frameIntervals, 0.95):F2}ms " +
                      $"max={Max(frameIntervals):F2}ms");
    Console.WriteLine($"presented={presenter.PresentedFrames}");
    Console.WriteLine($"stages: acquire={Average(acquireTimes):F2}ms render={Average(gpuRenderTimes):F2}ms present={Average(presentTailTimes):F2}ms");
}
else
{
    Console.WriteLine($"frames={rendered} (no measured frames; increase --frames above --warmup {warmup})");
}

if (selfCheck)
{
    foreach (var failure in selfCheckFailures) Console.WriteLine($"selfcheck: FAIL {failure}");
    if (selfCheckStep < selfCheckSizes.Length)
        selfCheckFailures.Add($"only {selfCheckStep}/{selfCheckSizes.Length} resizes executed");
    Console.WriteLine(selfCheckFailures.Count == 0
        ? "selfcheck: PASS"
        : $"selfcheck: FAIL ({selfCheckFailures.Count} issue(s))");
}

if (rendered > 0)
{
    // 同一状态下再走一次 CPU 光栅化，便于和窗口截图做像素比对。
    var finalState = new FrameState(window.LogicalClientSize, clock.Elapsed.TotalSeconds, pointer, pointerInside, clicks, lastKey);
    var path = shotPath ?? Path.Combine(Environment.CurrentDirectory, "out", "window-frame.png");
    SaveOffscreen(scene, finalState, path, Math.Max(presenter.Width, 1), Math.Max(presenter.Height, 1));
}

return 0;

static double TicksToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

static SceneParts ParseParts(string value)
{
    if (string.Equals(value, "all", StringComparison.OrdinalIgnoreCase)) return SceneParts.All;
    if (string.Equals(value, "none", StringComparison.OrdinalIgnoreCase)) return SceneParts.None;

    var result = SceneParts.None;
    foreach (var token in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        if (!Enum.TryParse<SceneParts>(token, ignoreCase: true, out var part))
            throw new ArgumentException($"unknown scene part: {token}");
        result |= part;
    }
    return result;
}

static double Average(List<double> values) => values.Count == 0 ? 0 : values.Average();

static double Max(List<double> values) => values.Count == 0 ? 0 : values.Max();

static double Percentile(List<double> values, double percentile)
{
    if (values.Count == 0) return 0;
    var sorted = values.ToArray();
    Array.Sort(sorted);
    var index = Math.Clamp((int)Math.Ceiling(percentile * sorted.Length) - 1, 0, sorted.Length - 1);
    return sorted[index];
}

static void SaveOffscreen(DemoScene scene, FrameState state, string path, int width, int height)
{
    var directory = Path.GetDirectoryName(Path.GetFullPath(path));
    if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

    var scaled = state with { Size = new Size(width, height) };
    var list = scene.Build(scaled);

    using var backend = new VelloCpuBackend(new VelloBackendOptions { UseCpu = true, ValidateDisplayList = true });
    var target = new RenderTarget(width, height);
    backend.Render(list, target);
    PngEncoder.Write(path, target);
    Console.WriteLine($"offscreen: {path} ({width}x{height}), hash={target.ContentHash():X16}");
}

static int RunOffscreen(DemoScene scene, int width, int height, int frames, string? path, SceneParts parts, bool gpu)
{
    using var backend = new VelloCpuBackend(new VelloBackendOptions { UseCpu = !gpu, ValidateDisplayList = true });
    var target = new RenderTarget(width, height);
    var clock = Stopwatch.StartNew();

    var warmup = Math.Min(10, frames / 2);
    var start = Stopwatch.GetTimestamp();
    for (int i = 0; i < frames; i++)
    {
        if (i == warmup) start = Stopwatch.GetTimestamp();
        var state = new FrameState(new Size(width, height), i / 60.0, Point.Zero, false, 0, "none");
        backend.Render(scene.Build(state, parts), target);
    }

    var measured = frames - warmup;
    var output = path ?? Path.Combine(Environment.CurrentDirectory, "out", "window-frame.png");
    var directory = Path.GetDirectoryName(Path.GetFullPath(output));
    if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
    PngEncoder.Write(output, target);
    Console.WriteLine($"offscreen({(gpu ? "gpu" : "cpu")}): {output} ({width}x{height}), {frames} frames, " +
                      $"{TicksToMs(Stopwatch.GetTimestamp() - start) / Math.Max(measured, 1):F2}ms/frame, hash={target.ContentHash():X16}");
    return 0;
}

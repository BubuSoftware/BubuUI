using System.Diagnostics;
using BubuUI;
using BubuUI.Rendering;
using BubuUI.Rendering.Vello;
using BubuUI.Text;
using BubuUI.Text.Shaping;

namespace RenderSpike;

/// <summary>
/// G1-08 性能 harness。分段记录：display list 构建（托管）/ 渲染（scene 构建 + FFI + 光栅化）。
/// CPU 与 GPU 各跑一遍，并比对像素一致性。
/// </summary>
public static class Bench
{
    private const int Warmup = 2;
    private static readonly int Iterations = int.Parse(Environment.GetEnvironmentVariable("RENDERSPIKE_ITERATIONS") ?? "10");

    private sealed record Scenario(string Name, int Width, int Height, int Commands, Func<DisplayList> Build);

    public static void Run(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);

        var scenarios = new[]
        {
            new Scenario("empty", 1280, 800, 0, () => new DisplayList(new Size(1280, 800))),
            new Scenario("200-elements", 1280, 800, 200, () => Elements(1280, 800, 200)),
            new Scenario("1k-glyphs", 1280, 800, 0, () => Text(1280, 800, "The quick brown fox jumps over the lazy dog. ", 22, 1000)),
            new Scenario("10k-commands", 1280, 800, 10000, () => Elements(1280, 800, 10000)),
            new Scenario("200-elements-640", 640, 400, 200, () => Elements(640, 400, 200)),
        };

        Console.WriteLine("| 场景 | 尺寸 | 命令数 | list 构建 | CPU 渲染 | CPU 总计 | GPU 渲染 | GPU 总计 | 像素一致性 |");
        Console.WriteLine("|---|---|---:|---:|---:|---:|---:|---:|---|");

        foreach (var scenario in scenarios)
        {
            var list = scenario.Build();
            var buildMs = MeasureBuild(scenario.Build);
            var (cpuMs, cpuFrame) = Measure(scenario, useCpu: true);
            var (gpuMs, gpuFrame) = Measure(scenario, useCpu: false);
            var match = Compare(cpuFrame, gpuFrame);

            Console.WriteLine(
                $"| {scenario.Name} | {scenario.Width}x{scenario.Height} | {list.Commands.Count} | " +
                $"{buildMs:F2} ms | {cpuMs:F2} ms | {buildMs + cpuMs:F2} ms | {gpuMs:F2} ms | {buildMs + gpuMs:F2} ms | {match} |");
        }

        Console.WriteLine();
        Console.WriteLine($"迭代次数 {Iterations}（预热 {Warmup}），CPU = vello_cpu，GPU = wgpu readback。");
        Console.WriteLine($"机器：{Environment.MachineName}，{Environment.OSVersion}，.NET {Environment.Version}，{Environment.ProcessorCount} 逻辑核。");
    }

    private static double MeasureBuild(Func<DisplayList> build)
    {
        for (int i = 0; i < Warmup; i++) build();
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < Iterations; i++) build();
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds / Iterations;
    }

    private static (double Milliseconds, RenderTarget Frame) Measure(Scenario scenario, bool useCpu)
    {
        try
        {
            using var backend = new VelloCpuBackend(new VelloBackendOptions { UseCpu = useCpu });
            var target = new RenderTarget(scenario.Width, scenario.Height);
            var list = scenario.Build();

            for (int i = 0; i < Warmup; i++) backend.Render(list, target);

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < Iterations; i++) backend.Render(list, target);
            sw.Stop();

            return (sw.Elapsed.TotalMilliseconds / Iterations, target);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{scenario.Name} / useCpu={useCpu} failed: {ex.Message}");
            return (double.NaN, new RenderTarget(1, 1));
        }
    }

    private static string Compare(RenderTarget a, RenderTarget b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return "n/a";
        int differing = 0, max = 0;
        for (int i = 0; i < a.Pixels.Length; i++)
        {
            int delta = Math.Abs(a.Pixels[i] - b.Pixels[i]);
            if (delta > 2) differing++;
            if (delta > max) max = delta;
        }
        return differing == 0 ? "一致" : $"{differing} 字节 >2（max {max}）";
    }

    private static DisplayList Elements(int width, int height, int count)
    {
        var list = new DisplayList(new Size(width, height));
        list.Add(new FillRect(new Rect(0, 0, width, height), new Color(18, 20, 26)));

        int columns = 20;
        double cellWidth = width / (double)columns;
        double cellHeight = 12;
        for (int i = 0; i < count; i++)
        {
            int column = i % columns;
            int row = i / columns;
            var rect = new Rect(column * cellWidth + 4, (row % 55) * cellHeight + 4, cellWidth - 8, cellHeight - 4);
            if (i % 3 == 0)
                list.Add(new FillRoundedRect(rect, 3, new Color((byte)(i % 255), (byte)((i * 7) % 255), 180)));
            else
                list.Add(new FillRect(rect, new Color((byte)((i * 13) % 255), 90, (byte)((i * 3) % 255))));
        }
        return list;
    }

    private static DisplayList Text(int width, int height, string text, double fontSize, int glyphTarget)
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf");
        if (!File.Exists(path)) return new DisplayList(new Size(width, height));

        using var shaper = new HarfBuzzTextShaper(path);
        var lines = new List<ShapedBuffer>();
        int glyphs = 0;
        while (glyphs < glyphTarget)
        {
            var shaped = shaper.Shape(text, fontSize);
            lines.Add(shaped);
            glyphs += shaped.Glyphs.Count;
        }

        var font = FontFace.Load(path);
        var list = new DisplayList(new Size(width, height));
        list.Add(new FillRect(new Rect(0, 0, width, height), new Color(250, 250, 250)));
        for (int i = 0; i < lines.Count; i++)
            list.Add(new DrawGlyphs(font, lines[i], new Point(16, 40 + (i % 24) * 30), new Color(20, 20, 20)));
        return list;
    }
}

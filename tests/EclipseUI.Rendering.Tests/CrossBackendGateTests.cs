using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using EclipseUI;
using EclipseUI.Rendering;
using EclipseUI.Rendering.Vello;
using Xunit;
using Xunit.Abstractions;

namespace EclipseUI.Rendering.Tests;

/// <summary>
/// G2：CPU/GPU 交叉一致性**门禁**（对应 render-gate-2026-11 backlog #8）。
/// 相比 SceneBuilderTests 的单场景抽测，本门禁系统化三件事：
///
///  - **场景矩阵**：按图元/特性逐项覆盖（实色 / 圆角 / 椭圆 / 路径 / 三种渐变 / 裁剪 /
///    变换 / 图层透明度与混合 / 阴影 / 图像），每项一个场景、独立定容差；
///  - **容差策略**：四级策略（Tight/Edges/Gradient/Loose），按「CPU 与 GPU 的实现差异来源」
///    分级——实色整数对齐几乎逐像素一致，抗锯齿边缘允许少量差异，渐变采样与
///    模糊/混合的实现差异最大；每场景比率超阈值即门禁失败（消息聚合全部违例）；
///  - **留痕**：无 GPU（CI 容器/无驱动）自动跳过并写入
///    TestResults/cross-backend-gate-report.txt（探测结论与跳过原因）；
///    有 GPU 时每场景一行实际 mismatch 数据（比率 / 最大通道差 / 首违点），
///    该报告同时是容差策略的校准依据。
/// 文本/字体不进矩阵：字形光栅化跨机器差异大，由 DrawGlyphs 的结构测试与画廊目检覆盖。
/// </summary>
public class CrossBackendGateTests
{
    private sealed record GatePolicy(string Label, int ChannelTolerance, double MaxMismatchRatio);

    private sealed record GateScene(string Name, int Width, int Height, Func<DisplayList> Build, GatePolicy Policy);

    private sealed record SceneResult(
        string Name, GatePolicy Policy, int ComparedPixels, int Mismatches, int MaxDelta,
        (int X, int Y, Color Cpu, Color Gpu)? FirstMismatch)
    {
        public double Ratio => ComparedPixels == 0 ? 0 : (double)Mismatches / ComparedPixels;
        public bool Passed => Ratio <= Policy.MaxMismatchRatio;
    }

    // 容差策略：channelTolerance = 单通道允许差；maxMismatchRatio = 全帧违例像素占比上限。
    private static readonly GatePolicy Tight = new("Tight", 2, 0.0005);      // 实色整数对齐：几乎逐像素一致
    private static readonly GatePolicy Edges = new("Edges", 2, 0.005);       // AA 边缘（G1 口径 0.5%）
    private static readonly GatePolicy Gradient = new("Gradient", 4, 0.02);  // 渐变采样/dithering
    private static readonly GatePolicy Loose = new("Loose", 16, 0.05);       // 模糊与混合模式

    private readonly ITestOutputHelper _output;

    public CrossBackendGateTests(ITestOutputHelper output) => _output = output;

    // -------------------------------------------------------------------
    // 场景矩阵
    // -------------------------------------------------------------------

    private static IReadOnlyList<GateScene> Scenes() => new[]
    {
        new GateScene("opaque-solids", 96, 64, OpaqueSolids, Tight),
        new GateScene("rounded-rects", 128, 64, RoundedRects, Edges),
        new GateScene("ellipses", 128, 64, Ellipses, Edges),
        new GateScene("paths", 128, 64, Paths, Edges),
        new GateScene("linear-gradient", 100, 40, LinearGradientScene, Gradient),
        new GateScene("radial-gradient", 100, 100, RadialGradientScene, Gradient),
        new GateScene("sweep-gradient", 120, 120, SweepGradientScene, Gradient),
        new GateScene("clip-stack", 128, 96, ClipStack, Edges),
        new GateScene("transforms", 128, 128, Transforms, Edges),
        new GateScene("layer-opacity", 128, 96, LayerOpacity, Edges),
        new GateScene("layer-blend", 150, 150, BlendModes, Loose),
        new GateScene("drop-shadow", 200, 200, DropShadowScene, Loose),
        new GateScene("image-1to1", 64, 64, ImageOneToOne, Tight),
        new GateScene("dashed-stroke", 128, 64, DashedStroke, Edges),
    };

    private static DisplayList OpaqueSolids() => new DisplayList(new Size(96, 64))
        .Add(new FillRect(new Rect(0, 0, 96, 64), new Color(24, 28, 38)))
        .Add(new FillRect(new Rect(8, 8, 32, 24), new Color(200, 60, 60)))
        .Add(new FillRect(new Rect(52, 16, 36, 40), new Color(60, 180, 120)))
        .Add(new FillRect(new Rect(8, 40, 32, 16), new Color(80, 120, 240)));

    private static DisplayList RoundedRects() => new DisplayList(new Size(128, 64))
        .Add(new FillRect(new Rect(0, 0, 128, 64), new Color(20, 24, 34)))
        .Add(new FillRoundedRect(new Rect(8, 8, 48, 48), 4, new Color(230, 120, 60)))
        .Add(new FillRoundedRect(new Rect(68, 8, 48, 48), 16, new Color(60, 160, 230)))
        .Add(new StrokeRect(new Rect(4, 4, 120, 56), new Color(255, 255, 255), 1));

    private static DisplayList Ellipses() => new DisplayList(new Size(128, 64))
        .Add(new FillRect(new Rect(0, 0, 128, 64), new Color(26, 30, 40)))
        .Add(new FillEllipse(new Rect(8, 8, 48, 48), new Color(120, 220, 90)))
        .Add(new FillEllipse(new Rect(68, 12, 52, 40), new Color(220, 90, 200)))
        .Add(new StrokePath(PathGeometry.Ellipse(new Rect(36, 16, 56, 32)), new Color(255, 255, 0), 2));

    private static DisplayList Paths() => new DisplayList(new Size(128, 64))
        .Add(new FillRect(new Rect(0, 0, 128, 64), new Color(30, 26, 40)))
        .Add(new FillPath(PathGeometry.Rectangle(new Rect(8, 8, 40, 40)), new Color(90, 200, 250)))
        .Add(new StrokePath(PathGeometry.Line(new Point(60, 50), new Point(120, 12)), new Color(255, 120, 120), 3))
        .Add(new PushClipPath(PathGeometry.Ellipse(new Rect(80, 8, 40, 40))))
        .Add(new FillRect(new Rect(70, 0, 60, 60), new Color(250, 220, 90)))
        .Add(new PopClip());

    private static DisplayList LinearGradientScene() => new DisplayList(new Size(100, 40))
        .Add(new FillRect(new Rect(0, 0, 100, 40), new LinearGradient(
            new Point(0, 0), new Point(100, 0),
            new[]
            {
                GradientStop.At(0, new Color(255, 0, 0)),
                GradientStop.At(1, new Color(0, 0, 255)),
            })))
        .Add(new FillRect(new Rect(10, 10, 20, 20), new Color(0, 200, 0)))
        .Add(new FillRect(new Rect(70, 10, 20, 20), new Color(255, 255, 0)));

    private static DisplayList RadialGradientScene() => new DisplayList(new Size(100, 100))
        .Add(new FillRect(new Rect(0, 0, 100, 100), new RadialGradient(
            new Point(50, 50), 50,
            new[]
            {
                GradientStop.At(0, new Color(255, 240, 200)),
                GradientStop.At(1, new Color(40, 20, 80)),
            })));

    private static DisplayList SweepGradientScene() => new DisplayList(new Size(120, 120))
        .Add(new FillRect(new Rect(0, 0, 120, 120), new SweepGradient(
            new Point(60, 60), 0, Math.PI * 2,
            new[]
            {
                GradientStop.At(0, new Color(255, 0, 0)),
                GradientStop.At(0.33, new Color(0, 255, 0)),
                GradientStop.At(0.66, new Color(0, 0, 255)),
                GradientStop.At(1, new Color(255, 0, 0)),
            })));

    private static DisplayList ClipStack() => new DisplayList(new Size(128, 96))
        .Add(new FillRect(new Rect(0, 0, 128, 96), new Color(24, 30, 42)))
        .Add(new PushClip(new Rect(8, 8, 52, 52)))
        .Add(new FillRect(new Rect(0, 0, 128, 96), new Color(70, 140, 250)))
        .Add(new PopClip())
        .Add(new PushClip(new Rect(68, 8, 52, 52)))
        .Add(new PushClip(new Rect(68, 34, 52, 54)))
        .Add(new FillRect(new Rect(0, 0, 128, 96), new Color(250, 160, 70)))
        .Add(new PopClip())
        .Add(new PopClip());

    private static DisplayList Transforms() => new DisplayList(new Size(128, 128))
        .Add(new FillRect(new Rect(0, 0, 128, 128), new Color(28, 24, 36)))
        .Add(new PushTransform(Transform.ScaleAround(1.5, 1.5, new Point(32, 32))))
        .Add(new FillRect(new Rect(12, 12, 40, 40), new Color(120, 230, 160)))
        .Add(new PopTransform())
        .Add(new PushTransform(Transform.RotationAround(Math.PI / 8, new Point(88, 64))))
        .Add(new FillRoundedRect(new Rect(68, 44, 40, 40), 6, new Color(230, 120, 220)))
        .Add(new PopTransform());

    private static DisplayList LayerOpacity() => new DisplayList(new Size(128, 96))
        .Add(new FillRect(new Rect(0, 0, 128, 96), new Color(240, 240, 240)))
        .Add(new PushLayer(0.35))
        .Add(new FillRect(new Rect(8, 8, 48, 48), new Color(200, 30, 30)))
        .Add(new PopLayer())
        .Add(new PushLayer(0.7))
        .Add(new FillRect(new Rect(68, 8, 48, 48), new Color(30, 60, 200)))
        .Add(new PopLayer())
        .Add(new PushLayer(0.5))
        .Add(new FillEllipse(new Rect(38, 50, 52, 36), new Color(30, 180, 90)))
        .Add(new PopLayer());

    private static DisplayList BlendModes() => new DisplayList(new Size(150, 150))
        .Add(new FillRect(new Rect(0, 0, 150, 150), new Color(200, 200, 210)))
        .Add(new PushLayer(0.85, LayerBlend.Multiply))
        .Add(new FillRect(new Rect(10, 10, 40, 40), new Color(250, 120, 60)))
        .Add(new PopLayer())
        .Add(new PushLayer(0.85, LayerBlend.Screen))
        .Add(new FillRect(new Rect(55, 10, 40, 40), new Color(60, 80, 250)))
        .Add(new PopLayer())
        .Add(new PushLayer(0.85, LayerBlend.Overlay))
        .Add(new FillRect(new Rect(100, 10, 40, 40), new Color(90, 220, 120)))
        .Add(new PopLayer())
        .Add(new PushLayer(0.85, LayerBlend.Difference))
        .Add(new FillRect(new Rect(32, 80, 40, 40), new Color(240, 200, 40)))
        .Add(new PopLayer());

    private static DisplayList DropShadowScene() => new DisplayList(new Size(200, 200))
        .Add(new FillRect(new Rect(0, 0, 200, 200), new Color(235, 238, 245)))
        .Add(new DropShadow(new Rect(60, 60, 80, 80), 12, 24, new Point(0, 6), new Color(0, 0, 0, 200)))
        .Add(new FillRoundedRect(new Rect(60, 60, 80, 80), 12, Color.White));

    private static DisplayList ImageOneToOne() => new DisplayList(new Size(64, 64))
        .Add(new FillRect(new Rect(0, 0, 64, 64), new Color(30, 34, 44)))
        .Add(new DrawImage(ImageHandle.Solid(24, 24, new Color(220, 80, 80)), new Rect(20, 20, 24, 24)));

    private static DisplayList DashedStroke() => new DisplayList(new Size(128, 64))
        .Add(new FillRect(new Rect(0, 0, 128, 64), new Color(24, 28, 38)))
        .Add(new StrokePath(
            PathGeometry.Line(new Point(8, 32), new Point(120, 32)),
            new Color(120, 220, 250), 3, new double[] { 8, 6 }));

    // -------------------------------------------------------------------
    // 门禁本体
    // -------------------------------------------------------------------

    [Fact]
    public void CrossBackend_AllScenes_MatchWithinTolerance()
    {
        if (!Render.TryCreateGpuBackend(out var backend))
        {
            var skipped = new List<string>
            {
                $"# CPU/GPU 交叉一致性门禁报告（{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC）",
                "GPU 后端不可用（CI 容器/无驱动）——门禁自动跳过并留痕。",
                "CPU 路径的确定性由 PixelGoldenTests/SameScene_RenderedTwice 覆盖。",
            };
            WriteReport(skipped);
            _output.WriteLine("GPU 后端不可用：门禁跳过（留痕见 " + ReportPath + "）");
            return;
        }

        using (backend)
        {
            var results = new List<SceneResult>();
            foreach (var scene in Scenes())
            {
                var list = scene.Build();
                var cpu = Render.Frame(list, scene.Width, scene.Height);
                var gpu = new RenderTarget(scene.Width, scene.Height);
                backend.Render(list, gpu);
                results.Add(Compare(scene, cpu, gpu));
            }

            WriteReport(BuildReport(results, gpuAvailable: true));

            var failures = new List<string>();
            foreach (var result in results)
            {
                _output.WriteLine(FormatResultLine(result));
                if (!result.Passed)
                    failures.Add(FormatFailure(result));
            }

            Assert.True(
                failures.Count == 0,
                $"CPU/GPU 交叉一致性门禁失败 {failures.Count}/{results.Count} 场景：\n" +
                string.Join("\n", failures) +
                $"\n完整数据：{ReportPath}");
        }
    }

    private static SceneResult Compare(GateScene scene, RenderTarget cpu, RenderTarget gpu)
    {
        int compared = 0, mismatches = 0, maxDelta = 0;
        (int, int, Color, Color)? first = null;

        for (int y = 0; y < cpu.Height; y++)
        {
            for (int x = 0; x < cpu.Width; x++)
            {
                compared++;
                var a = cpu.GetPixel(x, y);
                var b = gpu.GetPixel(x, y);
                int delta = Math.Max(
                    Math.Max(Math.Abs(a.R - b.R), Math.Abs(a.G - b.G)),
                    Math.Max(Math.Abs(a.B - b.B), Math.Abs(a.A - b.A)));
                if (delta > maxDelta) maxDelta = delta;
                if (delta <= scene.Policy.ChannelTolerance) continue;
                mismatches++;
                first ??= (x, y, a, b);
            }
        }

        return new SceneResult(scene.Name, scene.Policy, compared, mismatches, maxDelta, first);
    }

    // -------------------------------------------------------------------
    // 留痕
    // -------------------------------------------------------------------

    private static string ReportPath => Path.Combine(
        Render.RepoRoot(), "TestResults", "cross-backend-gate-report.txt");

    private static List<string> BuildReport(List<SceneResult> results, bool gpuAvailable)
    {
        var lines = new List<string>
        {
            $"# CPU/GPU 交叉一致性门禁报告（{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC）",
            $"GPU 可用：{gpuAvailable}；场景 {results.Count} 个；策略：Tight ±2/0.05%、Edges ±2/0.5%、Gradient ±4/2%、Loose ±16/5%",
            "# 场景 | 策略 | 违例像素 | 比率 | 阈值 | 最大通道差 | 结论",
        };
        foreach (var result in results)
        {
            lines.Add(string.Format(
                CultureInfo.InvariantCulture, "{0} | {1} | {2} | {3:P3} | {4:P3} | {5} | {6}",
                result.Name, result.Policy.Label, result.Mismatches, result.Ratio,
                result.Policy.MaxMismatchRatio, result.MaxDelta, result.Passed ? "PASS" : "FAIL"));
        }
        return lines;
    }

    private static string FormatResultLine(SceneResult result) =>
        $"{result.Name,-16} {result.Policy.Label,-8} mismatch {result.Ratio:P3}（{result.Mismatches}/{result.ComparedPixels}）" +
        $" maxΔ {result.MaxDelta} 阈值 {result.Policy.MaxMismatchRatio:P2} → {(result.Passed ? "PASS" : "FAIL")}";

    private static string FormatFailure(SceneResult result)
    {
        var sb = new StringBuilder();
        sb.Append($"  [{result.Name}] 策略 {result.Policy.Label}（±{result.Policy.ChannelTolerance} / {result.Policy.MaxMismatchRatio:P2}%）：").AppendLine();
        sb.Append($"    违例 {result.Mismatches}/{result.ComparedPixels}（{result.Ratio:P3}），最大通道差 {result.MaxDelta}");
        if (result.FirstMismatch is { } hit)
            sb.Append($";首违 ({hit.X},{hit.Y}) cpu {hit.Cpu} / gpu {hit.Gpu}");
        return sb.ToString();
    }

    private void WriteReport(List<string> lines)
    {
        var path = ReportPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, lines);
    }
}

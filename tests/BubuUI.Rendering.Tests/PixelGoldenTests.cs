using System.Globalization;
using System.Text;
using BubuUI;
using BubuUI.Rendering;
using BubuUI.Rendering.Vello;
using Xunit;

namespace BubuUI.Rendering.Tests;

/// <summary>
/// CPU 像素 golden 基础设施（对应 render-gate-2026-11.md §10 backlog #7）。
///
/// 设计要点：
///  - 场景全部为纯图元（矩形 / 圆角 / 椭圆 / 描边 / 渐变 / 变换 / 裁剪），无文本、无字体、无图像，
///    避免系统字体差异；CPU 后端 AntialiasingMode.Area 固定，输出跨运行可复现。
///  - 基线是仓库内**文本**文件 TestData/PixelGolden.baseline.txt，不提交二进制 PNG，
///    规避跨平台编码差异；每行「场景名=ContentHash:X16;x,y=#RRGGBBAA;...」。
///  - 双层门禁：hash 为整幅精确防漂移；语义探针（<see cref="PixelProbe"/> DSL，区域/颜色/计数断言）
///    在比对基线**之前**先验证——期望值自描述地写在场景定义里，即使基线被误重生成也能抓住结构性错误。
///  - 环境变量 BUBUUI_UPDATE_GOLDEN=1 进入「生成基线」模式，把本次实际值写入基线；
///    默认模式严格比对。基线缺失时给出可读失败消息并打印实际值，提示用该开关生成。
///  - 失败时把实际图像用 <see cref="PngEncoder"/> 写到 TestResults/（.gitignore 忽略，不进仓库）。
/// </summary>
public class PixelGoldenTests
{
    private const string UpdateEnv = "BUBUUI_UPDATE_GOLDEN";
    private const int ProbeTolerance = 2;

    private sealed record Scene(
        string Name,
        int Width,
        int Height,
        Func<DisplayList> Build,
        IReadOnlyList<(int X, int Y)> Probes,
        Action<PixelProbe>? Semantic = null);

    private sealed record BaselineEntry(
        string Name,
        string Hash,
        Dictionary<(int X, int Y), Color> Probes);

    private static readonly Size LocalSize = new(64, 64);
    private static readonly Rect LocalRect = new(24, 24, 16, 16);

    // 场景配色（与图元定义一一对应，语义探针复用）。
    private static readonly Color PrimitiveRed = new(220, 30, 30);
    private static readonly Color PrimitiveGreen = new(40, 180, 90);
    private static readonly Color PrimitiveBlue = new(40, 90, 220);
    private static readonly Color ClipPurple = new(160, 60, 200);
    private static readonly Color ClipTeal = new(0, 200, 200);
    private static readonly Color GradientGreen = new(0, 200, 0);
    private static readonly Color GradientYellow = new(255, 255, 0);

    // ---------------------------------------------------------------------
    // 固定场景（纯图元，整数对齐）
    // ---------------------------------------------------------------------

    private static IReadOnlyList<Scene> Scenes() => new[]
    {
        new Scene("solid-primitives", 64, 64, SolidPrimitives,
            new (int X, int Y)[]
            {
                (0, 0), (1, 1), (20, 20), (46, 18), (32, 46),
                (8, 8), (32, 32), (63, 63), (4, 32),
            },
            Semantic: probe => probe
                .At(20, 20, PrimitiveRed)                                          // 红矩形内部
                .At(46, 18, PrimitiveGreen)                                        // 圆角矩形内部
                .At(32, 46, PrimitiveBlue)                                         // 椭圆中心
                .At(4, 32, Color.White.WithAlpha(0.5), tolerance: 2)               // 1px 描边中心对齐路径 → 边列像素半覆盖（A≈127）
                .DominantColor(new Rect(10, 10, 20, 20), PrimitiveRed, 0.95)       // 红矩形内部区域（避开描边与边缘 AA）
                .Contains(new Rect(36, 8, 20, 20), PrimitiveGreen, minCount: 100)  // 圆角矩形区域出现大量绿
                .NotContains(new Rect(0, 0, 4, 64), PrimitiveRed)),                // 最左条带无红
        new Scene("linear-gradient", 100, 40, LinearGradientScene,
            new (int X, int Y)[]
            {
                (0, 20), (5, 20), (50, 20), (99, 20),
                (20, 20), (80, 20), (0, 0), (99, 39),
            },
            Semantic: probe => probe
                .DominantColor(new Rect(12, 12, 16, 16), GradientGreen, 0.95)      // 绿块内部
                .DominantColor(new Rect(72, 12, 16, 16), GradientYellow, 0.95)     // 黄块内部
                .CountIn(new Rect(0, 0, 2, 40), new Color(255, 0, 0), 20, 80)      // 渐变左端出现纯红（首列）
                .NotContains(new Rect(72, 12, 16, 16), new Color(255, 0, 0))),     // 黄块区域无红
        new Scene("transform-clip", 64, 64, TransformClipScene,
            new (int X, int Y)[]
            {
                (0, 0), (4, 4), (20, 12), (35, 27),
                (36, 28), (40, 40), (63, 63),
            },
            Semantic: probe => probe
                // PushClip 携带当前变换：紫矩形可见区 = (16,8)-(36,28)，收缩取内部。
                .DominantColor(new Rect(18, 10, 16, 16), ClipPurple, 0.95)
                .NotContains(new Rect(36, 28, 28, 36), ClipPurple)                 // 裁剪区外无紫
                .DominantColor(new Rect(0, 0, 8, 8), ClipTeal, 0.95)               // 弹出变换后的青色小块
                .NotContains(new Rect(0, 0, 16, 8), ClipPurple)),                  // 青块行带无紫
    };

    private static DisplayList SolidPrimitives() => new DisplayList(new Size(64, 64))
        .Add(new FillRect(new Rect(8, 8, 24, 24), new Color(220, 30, 30)))
        .Add(new FillRoundedRect(new Rect(36, 8, 20, 20), 5, new Color(40, 180, 90)))
        .Add(new FillEllipse(new Rect(8, 36, 48, 20), new Color(40, 90, 220)))
        .Add(new StrokeRect(new Rect(4, 4, 56, 56), new Color(255, 255, 255), 1));

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

    private static DisplayList TransformClipScene()
    {
        var list = new DisplayList(new Size(64, 64));
        list.Add(new PushTransform(Transform.Translation(16, 8)));
        list.Add(new PushClip(new Rect(0, 0, 20, 20)));
        list.Add(new FillRect(new Rect(0, 0, 40, 40), new Color(160, 60, 200)));
        list.Add(new PopClip());
        list.Add(new PopTransform());
        list.Add(new FillRect(new Rect(0, 0, 8, 8), new Color(0, 200, 200)));
        return list;
    }

    /// <summary>局部改动场景：全屏背景不变，仅中央小矩形换色（供 damage / retained 断言复用）。</summary>
    private static DisplayList LocalScene(Color accent) => new DisplayList(LocalSize)
        .Add(new FillRect(new Rect(0, 0, 64, 64), new Color(30, 34, 44)))
        .Add(new FillRect(LocalRect, accent));

    // ---------------------------------------------------------------------
    // 路径与基线文件
    // ---------------------------------------------------------------------

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "BubuUI.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("找不到仓库根目录（未发现 BubuUI.slnx）。");
    }

    private static string BaselinePath => Path.Combine(
        RepoRoot(), "tests", "BubuUI.Rendering.Tests", "TestData", "PixelGolden.baseline.txt");

    private static string TestResultsDir => Path.Combine(RepoRoot(), "TestResults");

    // ---------------------------------------------------------------------
    // B1：文本基线比对 + 生成开关
    // ---------------------------------------------------------------------

    [Fact]
    public void PixelGoldens_MatchBaseline()
    {
        var scenes = Scenes();
        if (Environment.GetEnvironmentVariable(UpdateEnv) == "1")
        {
            GenerateBaseline(scenes);
            return;
        }

        VerifyAgainstBaseline(scenes);
    }

    private static void GenerateBaseline(IReadOnlyList<Scene> scenes)
    {
        var lines = new List<string>
        {
            "# BubuUI CPU 像素 golden 基线（由 BUBUUI_UPDATE_GOLDEN=1 生成，请勿手工编辑）",
            "# 每行：<场景名>=<ContentHash:X16>;<x>,<y>=<#RRGGBBAA>;...（探针按 ±2 容差比对）",
        };

        foreach (var scene in scenes)
        {
            // 生成前先确认该场景确定性，避免把偶然的非确定值写进基线。
            var first = Render.Frame(scene.Build());
            var second = Render.Frame(scene.Build());
            Assert.Equal(first.ContentHash(), second.ContentHash());
            Assert.Equal(first.Pixels, second.Pixels);

            // 语义探针先验证，防止把结构性错误的图写进基线。
            if (scene.Semantic is not null)
            {
                var probe = PixelProbe.On(first, scene.Name);
                scene.Semantic(probe);
                Assert.True(
                    probe.Failures.Count == 0,
                    "生成基线前语义探针未通过（请勿把错误图像写进基线）：\n" +
                    string.Join("\n", probe.Failures));
            }

            lines.Add(FormatBaselineLine(scene, first));
        }

        var path = BaselinePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Join(Environment.NewLine, lines) + Environment.NewLine);

        var parsed = ParseBaselines(File.ReadAllLines(path));
        Assert.Equal(scenes.Count, parsed.Count);
    }

    private static void VerifyAgainstBaseline(IReadOnlyList<Scene> scenes)
    {
        var path = BaselinePath;
        Dictionary<string, BaselineEntry> baselines;
        if (!File.Exists(path))
        {
            var hint = new StringBuilder();
            hint.AppendLine($"像素 golden 基线不存在：{path}");
            hint.AppendLine("请用 BUBUUI_UPDATE_GOLDEN=1 运行本 filter 生成基线后重跑。本次实际值：");
            foreach (var scene in scenes)
            {
                var frame = Render.Frame(scene.Build());
                hint.AppendLine("  " + FormatBaselineLine(scene, frame));
                WriteActualPng(scene, frame);
            }
            Assert.Fail(hint.ToString());
            return;
        }

        baselines = ParseBaselines(File.ReadAllLines(path));

        var errors = new List<string>();
        foreach (var scene in scenes)
        {
            var frame = Render.Frame(scene.Build());
            var actualHash = frame.ContentHash().ToString("X16", CultureInfo.InvariantCulture);

            // 第一层：语义探针（区域/颜色/计数），期望值自描述，结构性错误在这里先暴露。
            if (scene.Semantic is not null)
            {
                var probe = PixelProbe.On(frame, scene.Name);
                scene.Semantic(probe);
                if (probe.Failures.Count > 0)
                {
                    var png = WriteActualPng(scene, frame);
                    errors.Add(
                        $"[{scene.Name}] 语义探针失败：\n    " + string.Join("\n    ", probe.Failures) +
                        $"\n    实际图像：{png}");
                    continue;
                }
            }

            // 第二层：基线 hash + 点快照防漂移。
            if (!baselines.TryGetValue(scene.Name, out var entry))
            {
                errors.Add($"[{scene.Name}] 基线缺少该场景（实际 hash={actualHash}）；实际图像：{WriteActualPng(scene, frame)}");
                continue;
            }

            var sceneErrors = new List<string>();
            if (!string.Equals(entry.Hash, actualHash, StringComparison.OrdinalIgnoreCase))
                sceneErrors.Add($"hash 期望 {entry.Hash} 实际 {actualHash}");

            foreach (var (x, y) in scene.Probes)
            {
                var actual = frame.GetPixel(x, y);
                if (!entry.Probes.TryGetValue((x, y), out var expected))
                {
                    sceneErrors.Add($"探针 ({x},{y}) 基线缺失（实际 {actual}）");
                    continue;
                }

                if (!ColorsClose(expected, actual))
                    sceneErrors.Add($"探针 ({x},{y}) 期望 {expected} 实际 {actual}（±{ProbeTolerance}）");
            }

            if (sceneErrors.Count > 0)
            {
                var png = WriteActualPng(scene, frame);
                errors.Add($"[{scene.Name}]\n    " + string.Join("\n    ", sceneErrors) +
                           $"\n    实际图像：{png}");
            }
        }

        Assert.True(errors.Count == 0, "像素 golden 基线比对失败：\n" + string.Join("\n", errors));
    }

    private static string FormatBaselineLine(Scene scene, RenderTarget frame)
    {
        var sb = new StringBuilder();
        sb.Append(scene.Name).Append('=')
          .Append(frame.ContentHash().ToString("X16", CultureInfo.InvariantCulture));
        foreach (var (x, y) in scene.Probes)
            sb.Append(';').Append(x).Append(',').Append(y).Append('=')
              .Append(frame.GetPixel(x, y).ToString());
        return sb.ToString();
    }

    private static Dictionary<string, BaselineEntry> ParseBaselines(IEnumerable<string> rawLines)
    {
        var result = new Dictionary<string, BaselineEntry>(StringComparer.Ordinal);
        foreach (var raw in rawLines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#"))
                continue;

            var eq = line.IndexOf('=');
            Assert.True(eq > 0, $"基线行格式错误（缺少 '='）：{line}");
            var name = line.Substring(0, eq);
            var parts = line.Substring(eq + 1).Split(';', StringSplitOptions.RemoveEmptyEntries);
            Assert.True(parts.Length >= 1, $"基线行格式错误（缺少 hash）：{line}");

            var probes = new Dictionary<(int X, int Y), Color>();
            for (int i = 1; i < parts.Length; i++)
            {
                var at = parts[i].IndexOf('=');
                Assert.True(at > 0, $"探针格式错误：{parts[i]}");
                var xy = parts[i].Substring(0, at).Split(',');
                Assert.True(xy.Length == 2, $"探针坐标格式错误：{parts[i]}");
                var x = int.Parse(xy[0], CultureInfo.InvariantCulture);
                var y = int.Parse(xy[1], CultureInfo.InvariantCulture);
                probes[(x, y)] = ParseColor(parts[i].Substring(at + 1));
            }

            result[name] = new BaselineEntry(name, parts[0], probes);
        }
        return result;
    }

    private static Color ParseColor(string text)
    {
        var hex = text.Trim().TrimStart('#');
        Assert.True(hex.Length == 8, $"颜色格式应为 #RRGGBBAA：{text}");
        return new Color(
            Convert.ToByte(hex.Substring(0, 2), 16),
            Convert.ToByte(hex.Substring(2, 2), 16),
            Convert.ToByte(hex.Substring(4, 2), 16),
            Convert.ToByte(hex.Substring(6, 2), 16));
    }

    /// <summary>与 <see cref="Render.AssertColor"/> 默认容差一致的软比较，便于聚合所有失败诊断。</summary>
    private static bool ColorsClose(Color a, Color b) =>
        Math.Abs(a.R - b.R) <= ProbeTolerance &&
        Math.Abs(a.G - b.G) <= ProbeTolerance &&
        Math.Abs(a.B - b.B) <= ProbeTolerance &&
        Math.Abs(a.A - b.A) <= ProbeTolerance;

    private static string WriteActualPng(Scene scene, RenderTarget frame)
    {
        var dir = TestResultsDir;
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"PixelGolden-actual-{scene.Name}.png");
        PngEncoder.Write(path, frame);
        return path;
    }

    // ---------------------------------------------------------------------
    // B1 硬性断言：确定性 + damage 局部改动
    // ---------------------------------------------------------------------

    [Fact]
    public void SameScene_RenderedTwice_IsDeterministic()
    {
        var scene = Scenes()[0];
        var first = Render.Frame(scene.Build());
        var second = Render.Frame(scene.Build());

        Assert.Equal(first.Pixels, second.Pixels);
        Assert.Equal(first.ContentHash(), second.ContentHash());
    }

    [Fact]
    public void Damage_LocalChange_ChangesPixelsOnlyInsideDirtyRect()
    {
        var before = Render.Frame(LocalScene(new Color(200, 40, 40)));
        var after = Render.Frame(LocalScene(new Color(40, 200, 80)));

        var damage = new FrameDamage();
        damage.Add(LocalRect);
        Assert.False(damage.IsFull);
        Assert.Single(damage.Rects);

        // 中心点旧色/新色；脏区内大面积出现新色；脏区外与上一帧逐字节一致。
        PixelProbe.On(before, "damage-before").At(32, 32, new Color(200, 40, 40)).Verify();
        PixelProbe.On(after, "damage-after")
            .At(32, 32, new Color(40, 200, 80))
            .Contains(LocalRect, new Color(40, 200, 80), minCount: 200)
            .SameAsOutside(before, LocalRect)
            .Verify();
    }

    // ---------------------------------------------------------------------
    // B2：retained 合成器断言（脏区外一致 + 与整帧重绘等价）
    // ---------------------------------------------------------------------

    [Fact]
    public void RetainedCompositor_DirtyRegionOnly_MatchesFullRedraw()
    {
        const int w = 64, h = 64;
        using var backend = new VelloCpuBackend(new VelloBackendOptions
        {
            UseCpu = true,
            ValidateDisplayList = true,
        });
        var compositor = new RetainedFrameCompositor(backend, w, h);

        var full = new FrameDamage();
        full.AddFull();
        var frame0 = compositor.Composite(LocalScene(new Color(200, 40, 40)), full);
        var before = compositor.SnapshotPixels();
        Assert.True(compositor.HasFrame);
        PixelProbe.On(frame0, "retained-frame0").At(32, 32, new Color(200, 40, 40)).Verify();

        var partial = new FrameDamage();
        partial.Add(LocalRect);
        Assert.Single(partial.Rects);
        var frame1 = compositor.Composite(LocalScene(new Color(40, 200, 80)), partial);

        // 整帧重绘参考帧。
        var reference = Render.Frame(LocalScene(new Color(40, 200, 80)));

        // 与整帧重绘逐像素等价 + 中心点新色 + 脏区外与上一帧逐字节一致。
        PixelProbe.On(frame1, "retained-partial")
            .At(32, 32, new Color(40, 200, 80))
            .SameAs(reference, new Rect(0, 0, w, h))
            .SameAsOutside(before, LocalRect)
            .Verify();

        // 空 damage：跳过整帧渲染，返回保留帧。
        var idle = new FrameDamage();
        var idleFrame = compositor.Composite(LocalScene(new Color(40, 200, 80)), idle);
        Assert.Equal(reference.ContentHash(), idleFrame.ContentHash());
    }
}

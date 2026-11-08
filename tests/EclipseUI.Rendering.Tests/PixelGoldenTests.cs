using System.Globalization;
using System.Text;
using EclipseUI;
using EclipseUI.Rendering;
using EclipseUI.Rendering.Vello;
using Xunit;

namespace EclipseUI.Rendering.Tests;

/// <summary>
/// CPU 像素 golden 基础设施（对应 render-gate-2026-11.md §10 backlog #7）。
///
/// 设计要点：
///  - 场景全部为纯图元（矩形 / 圆角 / 椭圆 / 描边 / 渐变 / 变换 / 裁剪），无文本、无字体、无图像，
///    避免系统字体差异；CPU 后端 AntialiasingMode.Area 固定，输出跨运行可复现。
///  - 基线是仓库内**文本**文件 TestData/PixelGolden.baseline.txt，不提交二进制 PNG，
///    规避跨平台编码差异；每行「场景名=ContentHash:X16;x,y=#RRGGBBAA;...」。
///  - 探针用与 <see cref="Render.AssertColor"/> 一致的 ±2 容差比对；hash 为整幅精确门禁。
///  - 环境变量 ECLIPSEUI_UPDATE_GOLDEN=1 进入「生成基线」模式，把本次实际值写入基线；
///    默认模式严格比对。基线缺失时给出可读失败消息并打印实际值，提示用该开关生成。
///  - 失败时把实际图像用 <see cref="PngEncoder"/> 写到 TestResults/（.gitignore 忽略，不进仓库）。
/// </summary>
public class PixelGoldenTests
{
    private const string UpdateEnv = "ECLIPSEUI_UPDATE_GOLDEN";
    private const int ProbeTolerance = 2;

    private sealed record Scene(
        string Name,
        int Width,
        int Height,
        Func<DisplayList> Build,
        IReadOnlyList<(int X, int Y)> Probes);

    private sealed record BaselineEntry(
        string Name,
        string Hash,
        Dictionary<(int X, int Y), Color> Probes);

    private static readonly Size LocalSize = new(64, 64);
    private static readonly Rect LocalRect = new(24, 24, 16, 16);

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
            }),
        new Scene("linear-gradient", 100, 40, LinearGradientScene,
            new (int X, int Y)[]
            {
                (0, 20), (5, 20), (50, 20), (99, 20),
                (20, 20), (80, 20), (0, 0), (99, 39),
            }),
        new Scene("transform-clip", 64, 64, TransformClipScene,
            new (int X, int Y)[]
            {
                (0, 0), (4, 4), (20, 12), (35, 27),
                (36, 28), (40, 40), (63, 63),
            }),
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
            if (File.Exists(Path.Combine(dir.FullName, "EclipseUI.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("找不到仓库根目录（未发现 EclipseUI.slnx）。");
    }

    private static string BaselinePath => Path.Combine(
        RepoRoot(), "tests", "EclipseUI.Rendering.Tests", "TestData", "PixelGolden.baseline.txt");

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
            "# EclipseUI CPU 像素 golden 基线（由 ECLIPSEUI_UPDATE_GOLDEN=1 生成，请勿手工编辑）",
            "# 每行：<场景名>=<ContentHash:X16>;<x>,<y>=<#RRGGBBAA>;...（探针按 ±2 容差比对）",
        };

        foreach (var scene in scenes)
        {
            // 生成前先确认该场景确定性，避免把偶然的非确定值写进基线。
            var first = Render.Frame(scene.Build());
            var second = Render.Frame(scene.Build());
            Assert.Equal(first.ContentHash(), second.ContentHash());
            Assert.Equal(first.Pixels, second.Pixels);
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
            hint.AppendLine("请用 ECLIPSEUI_UPDATE_GOLDEN=1 运行本 filter 生成基线后重跑。本次实际值：");
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

        // 脏区内像素确实变化，且变为预期的新颜色。
        Render.AssertColor(new Color(200, 40, 40), before.GetPixel(32, 32));
        Render.AssertColor(new Color(40, 200, 80), after.GetPixel(32, 32));

        int width = (int)LocalSize.Width, height = (int)LocalSize.Height;
        int changedInside = 0, outsideMismatch = 0, outsideCount = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                bool changed = before.Pixels[i] != after.Pixels[i] ||
                               before.Pixels[i + 1] != after.Pixels[i + 1] ||
                               before.Pixels[i + 2] != after.Pixels[i + 2] ||
                               before.Pixels[i + 3] != after.Pixels[i + 3];

                if (LocalRect.Contains(new Point(x, y)))
                {
                    if (changed) changedInside++;
                }
                else
                {
                    outsideCount++;
                    if (changed) outsideMismatch++;
                }
            }
        }

        Assert.True(changedInside > 0, "脏区内应出现像素变化");
        Assert.True(outsideCount > 0);
        Assert.Equal(0, outsideMismatch);
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
        Render.AssertColor(new Color(200, 40, 40), frame0.GetPixel(32, 32));

        var partial = new FrameDamage();
        partial.Add(LocalRect);
        Assert.Single(partial.Rects);
        var frame1 = compositor.Composite(LocalScene(new Color(40, 200, 80)), partial);

        // 整帧重绘参考帧。
        var reference = Render.Frame(LocalScene(new Color(40, 200, 80)));

        // 与整帧重绘逐像素等价。
        Assert.Equal(reference.Pixels, frame1.Pixels);
        Assert.Equal(reference.ContentHash(), frame1.ContentHash());

        // 脏区内变化符合预期。
        Render.AssertColor(new Color(40, 200, 80), frame1.GetPixel(32, 32));

        // 脏区外与上一帧逐字节完全一致。
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (LocalRect.Contains(new Point(x, y))) continue;
                int i = (y * w + x) * 4;
                for (int k = 0; k < 4; k++)
                    Assert.Equal(before[i + k], frame1.Pixels[i + k]);
            }
        }

        // 空 damage：跳过整帧渲染，返回保留帧。
        var idle = new FrameDamage();
        var idleFrame = compositor.Composite(LocalScene(new Color(40, 200, 80)), idle);
        Assert.Equal(reference.ContentHash(), idleFrame.ContentHash());
    }
}

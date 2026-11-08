using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using EclipseUI;
using EclipseUI.Rendering;
using Xunit;

namespace EclipseUI.Rendering.Tests;

/// <summary>
/// G2 像素探针 DSL：把「逐点颜色快照」升级为结构化断言——点、区域（主色/出现/禁色/计数）、
/// 全帧计数、帧间逐字节一致。期望值在测试代码里自描述（"这个区域应是红色"），
/// 不再依赖从基线抄回的 magic 点值；基线 hash 仍作整幅防漂移门禁，两者互补。
///
/// 用法：链式调用所有断言方法（都返回 this），最后 <see cref="Verify"/> 终结；
/// 所有失败聚合一次性报告（附坐标/颜色/计数诊断），避免逐断言改-跑循环。
/// 区域均按「像素中心点」采样（<see cref="Rect.Contains"/> 半开区间），整数矩形语义直观。
/// </summary>
public sealed class PixelProbe
{
    private readonly RenderTarget _frame;
    private readonly string _labelPrefix;
    private readonly List<string> _failures = new();

    private PixelProbe(RenderTarget frame, string label)
    {
        _frame = frame;
        _labelPrefix = string.IsNullOrEmpty(label) ? "" : $"[{label}] ";
    }

    public static PixelProbe On(RenderTarget frame, string label = "") => new(frame, label);

    /// <summary>已收集的失败描述（<see cref="Verify"/> 未调用时也可检查）。</summary>
    public IReadOnlyList<string> Failures => _failures;

    // -------------------------------------------------------------------
    // 点断言
    // -------------------------------------------------------------------

    /// <summary>单点颜色近似断言（±tolerance 覆盖 RGBA 四通道）。</summary>
    public PixelProbe At(int x, int y, Color expected, int tolerance = 2, string? because = null)
    {
        if (x < 0 || y < 0 || x >= _frame.Width || y >= _frame.Height)
        {
            Fail($"At({x},{y}) 越界（帧 {_frame.Width}x{_frame.Height}）");
            return this;
        }

        var actual = _frame.GetPixel(x, y);
        if (!Close(actual, expected, tolerance))
            Fail($"At({x},{y}) 期望 {expected} ±{tolerance} 实际 {actual}{Because(because)}");
        return this;
    }

    /// <summary>单点 alpha 区间断言（用于抗锯齿边缘/透明背景，颜色本身不定）。</summary>
    public PixelProbe AlphaAt(int x, int y, int min, int max, string? because = null)
    {
        if (x < 0 || y < 0 || x >= _frame.Width || y >= _frame.Height)
        {
            Fail($"AlphaAt({x},{y}) 越界（帧 {_frame.Width}x{_frame.Height}）");
            return this;
        }

        var a = _frame.GetPixel(x, y).A;
        if (a < min || a > max)
            Fail($"AlphaAt({x},{y}) 期望 A∈[{min},{max}] 实际 {a}{Because(because)}");
        return this;
    }

    // -------------------------------------------------------------------
    // 区域断言（区域内像素统计）
    // -------------------------------------------------------------------

    /// <summary>区域内 ≥minRatio 比例像素近似 expected；失败时给出主要颜色直方图诊断。</summary>
    public PixelProbe DominantColor(
        Rect region, Color expected, double minRatio = 0.95, int tolerance = 2, string? because = null)
    {
        int total = 0, matched = 0;
        var histogram = new Dictionary<Color, int>();
        ForEachPixel(region, color =>
        {
            total++;
            histogram[color] = histogram.TryGetValue(color, out var n) ? n + 1 : 1;
            if (Close(color, expected, tolerance)) matched++;
        });

        var ratio = total == 0 ? 0.0 : (double)matched / total;
        if (ratio < minRatio)
        {
            var top = string.Join(", ", TopColors(histogram, 3));
            Fail(
                $"DominantColor[{Describe(region)}] 期望 {expected} 覆盖率 ≥ {minRatio:P1}，" +
                $"实际 {ratio:P1}（{matched}/{total}）主要颜色：{top}{Because(because)}");
        }
        return this;
    }

    /// <summary>区域内出现 ≥minCount 个近似 expected 的像素。</summary>
    public PixelProbe Contains(
        Rect region, Color expected, int minCount = 1, int tolerance = 2, string? because = null)
    {
        int matched = CountMatches(region, expected, tolerance);
        if (matched < minCount)
            Fail(
                $"Contains[{Describe(region)}] 期望出现 {expected} ≥{minCount} 个，" +
                $"实际 {matched} 个{Because(because)}");
        return this;
    }

    /// <summary>区域内不出现近似 expected 的像素；失败时给出计数与首个命中位置。</summary>
    public PixelProbe NotContains(Rect region, Color expected, int tolerance = 2, string? because = null)
    {
        (int x, int y)? firstHit = null;
        int count = 0;
        ForEachPixel(region, (x, y, color) =>
        {
            if (!Close(color, expected, tolerance)) return;
            count++;
            firstHit ??= (x, y);
        });

        if (count > 0)
            Fail(
                $"NotContains[{Describe(region)}] 期望不出现 {expected}，实际 {count} 个" +
                $"（首个 ({firstHit!.Value.x},{firstHit.Value.y})）{Because(because)}");
        return this;
    }

    /// <summary>区域内近似 expected 的像素数落在 [min,max]。</summary>
    public PixelProbe CountIn(
        Rect region, Color expected, int min, int max, int tolerance = 2, string? because = null)
    {
        int matched = CountMatches(region, expected, tolerance);
        if (matched < min || matched > max)
            Fail(
                $"CountIn[{Describe(region)}] 期望 {expected} 计数 ∈ [{min},{max}]，" +
                $"实际 {matched}{Because(because)}");
        return this;
    }

    /// <summary>全帧近似 expected 的像素数落在 [min,max]。</summary>
    public PixelProbe Count(Color expected, int min, int max, int tolerance = 2, string? because = null) =>
        CountIn(new Rect(0, 0, _frame.Width, _frame.Height), expected, min, max, tolerance, because);

    // -------------------------------------------------------------------
    // 帧间断言（逐字节精确一致，用于 damage / retained 等价性）
    // -------------------------------------------------------------------

    /// <summary>帧内 region 与 other 的对应区域逐字节一致。</summary>
    public PixelProbe SameAs(RenderTarget other, Rect region, string? because = null) =>
        Compare(other, new[] { region }, outside: false, because);

    /// <summary>帧内各 region 之外的所有像素与 other 逐字节一致（脏区外不变）。</summary>
    public PixelProbe SameAsOutside(RenderTarget other, Rect region, string? because = null) =>
        Compare(other, new[] { region }, outside: true, because);

    /// <summary>帧内 regions 覆盖之外的所有像素与 other 逐字节一致（多脏区并集外不变）。</summary>
    public PixelProbe SameAsOutside(RenderTarget other, IReadOnlyList<Rect> regions, string? because = null) =>
        Compare(other, regions, outside: true, because);

    /// <summary>帧内 region 与原始 RGBA 缓冲（如 compositor 快照）逐字节一致。</summary>
    public PixelProbe SameAs(byte[] rgba, Rect region, string? because = null) =>
        Compare(rgba, new[] { region }, outside: false, because);

    /// <summary>帧内 region 之外的所有像素与原始 RGBA 缓冲逐字节一致。</summary>
    public PixelProbe SameAsOutside(byte[] rgba, Rect region, string? because = null) =>
        Compare(rgba, new[] { region }, outside: true, because);

    /// <summary>帧内 regions 覆盖之外的所有像素与原始 RGBA 缓冲逐字节一致。</summary>
    public PixelProbe SameAsOutside(byte[] rgba, IReadOnlyList<Rect> regions, string? because = null) =>
        Compare(rgba, regions, outside: true, because);

    // -------------------------------------------------------------------
    // 终结
    // -------------------------------------------------------------------

    /// <summary>聚合所有失败并一次性断言（无失败时静默通过）。</summary>
    public void Verify()
    {
        if (_failures.Count == 0) return;
        var sb = new StringBuilder();
        sb.Append(_labelPrefix).Append("像素探针失败 ").Append(_failures.Count).Append(" 项：");
        foreach (var failure in _failures)
            sb.Append("\n  - ").Append(failure);
        Assert.Fail(sb.ToString());
    }

    // -------------------------------------------------------------------
    // 内部
    // -------------------------------------------------------------------

    private void ForEachPixel(Rect region, Action<Color> visit) =>
        ForEachPixel(region, (_, _, color) => visit(color));

    private void ForEachPixel(Rect region, Action<int, int, Color> visit)
    {
        int x0 = Math.Max(0, (int)Math.Ceiling(region.Left));
        int x1 = Math.Min(_frame.Width, (int)Math.Ceiling(region.Right));
        int y0 = Math.Max(0, (int)Math.Ceiling(region.Top));
        int y1 = Math.Min(_frame.Height, (int)Math.Ceiling(region.Bottom));

        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                if (!region.Contains(new Point(x + 0.5, y + 0.5))) continue;
                visit(x, y, _frame.GetPixel(x, y));
            }
        }
    }

    private int CountMatches(Rect region, Color expected, int tolerance)
    {
        int matched = 0;
        ForEachPixel(region, color => { if (Close(color, expected, tolerance)) matched++; });
        return matched;
    }

    private PixelProbe Compare(RenderTarget other, IReadOnlyList<Rect> regions, bool outside, string? because)
    {
        if (other.Width != _frame.Width || other.Height != _frame.Height)
        {
            Fail(
                $"SameAs[{(outside ? "区域外" : "")}{Describe(regions)}] 尺寸不一致：帧 {_frame.Width}x{_frame.Height}，" +
                $"other {other.Width}x{other.Height}{Because(because)}");
            return this;
        }

        return Compare((x, y) => other.GetPixel(x, y), regions, outside, "other", because);
    }

    private PixelProbe Compare(byte[] rgba, IReadOnlyList<Rect> regions, bool outside, string? because)
    {
        if (rgba.Length != _frame.Pixels.Length)
        {
            Fail(
                $"SameAs[{(outside ? "区域外" : "")}{Describe(regions)}] 缓冲长度不一致：帧 {_frame.Pixels.Length}，" +
                $"other {rgba.Length}{Because(because)}");
            return this;
        }

        return Compare(
            (x, y) => new Color(rgba[y * _frame.Stride + x * 4], rgba[y * _frame.Stride + x * 4 + 1],
                                rgba[y * _frame.Stride + x * 4 + 2], rgba[y * _frame.Stride + x * 4 + 3]),
            regions, outside, "other", because);
    }

    private PixelProbe Compare(
        Func<int, int, Color> otherPixel, IReadOnlyList<Rect> regions, bool outside, string otherDesc, string? because)
    {
        // 区域先对齐到像素格（floor/ceil，与合成器 CopyRegion 的拷贝范围一致），
        // 避免「double 区域边界内的整像素已被拷贝、却被中心点判定为区域外」的半像素误报。
        var pixelRegions = regions.Select(PixelGrid).ToList();
        bool Inside(int x, int y)
        {
            var center = new Point(x + 0.5, y + 0.5);
            foreach (var region in pixelRegions)
            {
                if (region.Contains(center)) return true;
            }
            return false;
        }

        int compared = 0, mismatched = 0;
        (int x, int y, Color a, Color b)? firstMismatch = null;

        // 遍历全帧，按 outside 取补集；SameAs 语义是逐字节精确一致，不适用容差。
        for (int y = 0; y < _frame.Height; y++)
        {
            for (int x = 0; x < _frame.Width; x++)
            {
                if (Inside(x, y) == outside) continue; // outside=true 跳过区域内，反之亦然

                compared++;
                var a = _frame.GetPixel(x, y);
                var b = otherPixel(x, y);
                if (a == b) continue;
                mismatched++;
                firstMismatch ??= (x, y, a, b);
            }
        }

        if (mismatched > 0)
        {
            var hit = firstMismatch!.Value;
            Fail(
                $"SameAs[{(outside ? "区域外" : "")}{Describe(regions)}] 与 {otherDesc} 不一致：{mismatched}/{compared} 像素，" +
                $"首个 ({hit.x},{hit.y}) 本帧 {hit.a} / {otherDesc} {hit.b}{Because(because)}");
        }
        return this;
    }

    /// <summary>区域向外对齐到整数像素格（floor 左/上、ceil 右/下），覆盖抗锯齿边缘。</summary>
    private static Rect PixelGrid(Rect region) => new(
        Math.Floor(region.Left),
        Math.Floor(region.Top),
        Math.Ceiling(region.Right) - Math.Floor(region.Left),
        Math.Ceiling(region.Bottom) - Math.Floor(region.Top));

    private static bool Close(Color a, Color b, int tolerance) =>
        Math.Abs(a.R - b.R) <= tolerance &&
        Math.Abs(a.G - b.G) <= tolerance &&
        Math.Abs(a.B - b.B) <= tolerance &&
        Math.Abs(a.A - b.A) <= tolerance;

    private static string Describe(Rect region) =>
        $"({region.Left:0.#},{region.Top:0.#},{region.Width:0.#},{region.Height:0.#})";

    private static string Describe(IReadOnlyList<Rect> regions) =>
        regions.Count == 1 ? Describe(regions[0]) : $"{regions.Count} rects";

    private static string Because(string? because) => because is null ? "" : $"（{because}）";

    private static IEnumerable<string> TopColors(Dictionary<Color, int> histogram, int count) =>
        histogram.OrderByDescending(kv => kv.Value).Take(count)
            .Select(kv => $"{kv.Value}x{kv.Key}");

    private void Fail(string message) => _failures.Add(message);
}

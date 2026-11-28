using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace BubuUI.Compiler;

/// <summary>
/// 目录 POCO 与结构相等实现在 <c>UiCatalogModel.cs</c>（design/14 §14.5.1）；
/// 目录值由 <c>BubuUI.Compiler.Metadata.UiCatalogFactory</c> 从符号派生（§14.10.2），
/// 本文件只保留与目录无关的字面量转换与建议算法。
/// </summary>
public static class UiLiterals
{
    /// <summary>
    /// 该类型形状是否有编译期字面量转换器（design/14 §14.7.5，spec/01 §5.2）。
    /// 没有 → 字面量报 EUI023（用点提示改写 <c>{expression}</c>）；
    /// 有但格式/范围/成员名错 → 报 EUI003。转换器查找键固定为 <see cref="UiTypeShape.CanonicalKey"/>。
    /// </summary>
    public static bool HasConverter(UiTypeShape shape)
    {
        if (shape.Kind == UiTypeShapeKind.Enum) return true;
        switch (shape.CanonicalKey)
        {
            case "double":
            case "float":
            case "decimal":
            case "long":
            case "int":
            case "short":
            case "byte":
            case "bool":
            case "string":
            case "char":
            case "System.TimeSpan":
                return true;
            default:
                return false;
        }
    }

    /// <summary>把 .bui 字面量按目标属性类型在编译期转换为 C# 表达式；失败给出期望格式。</summary>
    public static bool TryConvert(UiTypeShape shape, string literal, out string? csharpExpr, out string? error)
    {
        csharpExpr = null;
        error = null;
        if (shape.Kind == UiTypeShapeKind.Enum)
            return ConvertEnum(shape.CanonicalKey, shape.EnumMembers, literal, out csharpExpr, out error);
        switch (shape.CanonicalKey)
        {
            case "double":
                return ConvertDouble(literal, suffix: "d", out csharpExpr, out error);
            case "float":
                return ConvertFloat(literal, out csharpExpr, out error);
            case "decimal":
                return ConvertDecimal(literal, out csharpExpr, out error);
            case "long":
                return ConvertInteger(literal, long.MinValue, long.MaxValue, suffix: "L", cast: null,
                    out csharpExpr, out error);
            case "int":
                return ConvertInteger(literal, int.MinValue, int.MaxValue, suffix: "", cast: null,
                    out csharpExpr, out error);
            case "short":
                return ConvertInteger(literal, short.MinValue, short.MaxValue, suffix: "", cast: "(short)",
                    out csharpExpr, out error);
            case "byte":
                return ConvertInteger(literal, byte.MinValue, byte.MaxValue, suffix: "", cast: "(byte)",
                    out csharpExpr, out error);
            case "bool":
                var flag = literal.Trim();
                if (flag == "true" || flag == "false")
                {
                    csharpExpr = flag;
                    return true;
                }
                error = $"expects 'true' or 'false', got '{literal}'";
                return false;
            case "string":
                csharpExpr = EscapeString(literal);
                return true;
            case "char":
                return ConvertChar(literal, out csharpExpr, out error);
            case "System.TimeSpan":
                return ConvertTimeSpan(literal, out csharpExpr, out error);
            default:
                error = $"unsupported literal type '{shape.CanonicalKey}'";
                return false;
            }
    }

    private static readonly TimeSpan ParserRegexTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// parser 感知版 HasConverter（design/14 §14.7.3，2026-11-12）：内建 12 类优先，其后
    /// 接受目录里该类型的声明式解析器（<c>[UiLiteralParser]</c>）。
    /// </summary>
    public static bool HasConverter(UiTypeShape shape, UiLiteralParserDef? parser) =>
        HasConverter(shape) || parser is not null;

    /// <summary>
    /// parser 感知版 TryConvert：内建命中时与单参重载完全一致；否则若目录注册了该类型的
    /// 解析器，用 BCL Regex（InvariantCulture、100ms 超时）在编译期校验格式（不执行用户
    /// 代码，R1 保留），通过即生成 <c>{ParseCall}("literal")</c> 调用。生成语句在元素构造
    /// 阶段执行一次，不逐帧解析（R2 放宽口径，spec/02 §1）。
    /// </summary>
    public static bool TryConvert(UiTypeShape shape, string literal, UiLiteralParserDef? parser,
        out string? csharpExpr, out string? error)
    {
        if (HasConverter(shape))
            return TryConvert(shape, literal, out csharpExpr, out error);

        if (parser is null)
        {
            csharpExpr = null;
            error = $"has no literal converter for '{shape.CanonicalKey}'; write an {{expression}} instead";
            return false;
        }

        if (parser.Pattern is { } pattern
            && !Regex.IsMatch(literal.Trim(), pattern, RegexOptions.CultureInvariant, ParserRegexTimeout))
        {
            csharpExpr = null;
            error = $"expects {parser.Hint ?? pattern}, got '{literal}'";
            return false;
        }

        csharpExpr = $"{parser.ParseCall}({EscapeString(literal)})";
        error = null;
        return true;
    }

    private static bool ConvertDouble(string literal, string suffix, out string? csharpExpr, out string? error)
    {
        csharpExpr = null;
        error = null;
        if (double.TryParse(literal.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
        {
            csharpExpr = d.ToString("R", CultureInfo.InvariantCulture) + suffix;
            return true;
        }
        error = $"expects a number like '8' or '8.5', got '{literal}'";
        return false;
    }

    private static bool ConvertFloat(string literal, out string? csharpExpr, out string? error)
    {
        csharpExpr = null;
        error = null;
        if (!double.TryParse(literal.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
        {
            error = $"expects a number like '8' or '8.5f', got '{literal}'";
            return false;
        }
        if (!IsFinite(d) || d > float.MaxValue || d < float.MinValue)
        {
            error = $"expects a float in [{float.MinValue:R} .. {float.MaxValue:R}], got '{literal}'";
            return false;
        }
        csharpExpr = d.ToString("R", CultureInfo.InvariantCulture) + "f";
        return true;
    }

    private static bool ConvertDecimal(string literal, out string? csharpExpr, out string? error)
    {
        csharpExpr = null;
        error = null;
        if (decimal.TryParse(literal.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
        {
            csharpExpr = d.ToString(CultureInfo.InvariantCulture) + "m";
            return true;
        }
        error = $"expects a number like '8' or '8.5m', got '{literal}'";
        return false;
    }

    private static bool ConvertInteger(
        string literal, long min, long max, string suffix, string? cast,
        out string? csharpExpr, out string? error)
    {
        csharpExpr = null;
        error = null;
        if (!long.TryParse(literal.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
        {
            error = $"expects an integer like '42', got '{literal}'";
            return false;
        }
        if (v < min || v > max)
        {
            error = $"expects an integer in [{min} .. {max}], got '{literal}'";
            return false;
        }
        var digits = v.ToString(CultureInfo.InvariantCulture) + suffix;
        csharpExpr = cast is null ? digits : $"{cast}{digits}";
        return true;
    }

    private static bool ConvertEnum(
        string canonicalKey, IReadOnlyList<string> members, string literal,
        out string? csharpExpr, out string? error)
    {
        csharpExpr = null;
        error = null;
        var trimmed = literal.Trim();
        foreach (var member in members)
        {
            if (member == trimmed)
            {
                csharpExpr = $"global::{canonicalKey}.{member}";
                return true;
            }
        }
        error = $"expects an enum member of '{canonicalKey}' (one of: {string.Join(", ", members)}), got '{literal}'";
        return false;
    }

    private static bool ConvertChar(string literal, out string? csharpExpr, out string? error)
    {
        csharpExpr = null;
        error = null;
        // char 字面量不 Trim（空格可以是目标字符）；数字/布尔/枚举/TimeSpan 才 Trim（spec/01 §5.1）。
        string s = literal;
        char c;
        if (s.Length == 1)
        {
            c = s[0];
        }
        else if (s.Length >= 2 && s[0] == '\\' && TryEscape(s, out c, out error))
        {
            // 转义形式：error 由 TryEscape 在失败时填充，走下方 else-return
        }
        else
        {
            error = $"expects a single character like 'x' or an escape like '\\n', got '{literal}'";
            return false;
        }
        csharpExpr = "'" + EscapeChar(c) + "'";
        return true;
    }

    /// <summary>解析 <c>\n</c>/<c>\r</c>/<c>\t</c>/<c>\\</c>/<c>\"</c>/<c>\'</c>/<c>\0</c>/<c>\uXXXX</c>。</summary>
    private static bool TryEscape(string s, out char c, out string? error)
    {
        c = default;
        error = null;
        switch (s)
        {
            case "\\n": c = '\n'; return true;
            case "\\r": c = '\r'; return true;
            case "\\t": c = '\t'; return true;
            case "\\\\": c = '\\'; return true;
            case "\\\"": c = '"'; return true;
            case "\\'": c = '\''; return true;
            case "\\0": c = '\0'; return true;
        }
        if (s.Length == 6 && s[1] == 'u' && IsHex(s[2]) && IsHex(s[3]) && IsHex(s[4]) && IsHex(s[5]))
        {
            c = (char)(HexValue(s[2]) << 12 | HexValue(s[3]) << 8 | HexValue(s[4]) << 4 | HexValue(s[5]));
            return true;
        }
        error = $"expects an escape like '\\n', '\\u0041'; got '{s}'";
        return false;
    }

    private static bool IsFractional(double d) =>
        double.IsNaN(d) || double.IsInfinity(d);

    private static bool IsFinite(double d) => !IsFractional(d);

    private static bool HasNonDigit(string s)
    {
        foreach (var ch in s)
            if (ch < '0' || ch > '9') return true;
        return false;
    }

    private static bool IsHex(char c) => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    private static int HexValue(char c) => c <= '9' ? c - '0' : (c | 0x20) - 'a' + 10;

    /// <summary>
    /// spec/01 §5.2：单个单位的时长（<c>250ms</c>/<c>1.5s</c>/<c>2m</c>/<c>1h</c>/<c>1d</c>）
    /// 或 <c>[-][d.]hh:mm[:ss[.fffffff]]</c>；单位只允许一个（<c>1h30m</c> 不是字面量）。
    /// 编译期换算 ticks，生成 <c>new global::System.TimeSpan({ticks}L)</c>。
    /// </summary>
    private static bool ConvertTimeSpan(string literal, out string? csharpExpr, out string? error)
    {
        csharpExpr = null;
        error = null;
        if (!TryTimeSpanTicks(literal, out var ticks, out error))
            return false;
        csharpExpr = $"new global::System.TimeSpan({ticks.ToString(CultureInfo.InvariantCulture)}L)";
        return true;
    }

    private static bool TryTimeSpanTicks(string literal, out long ticks, out string? error)
    {
        ticks = 0;
        error = null;
        var formatHint = "expects a duration with a single unit like '250ms', '1.5s', '2m', '1h', '1d' " +
            "or '[-][d.]hh:mm[:ss[.fffffff]]', got '" + literal + "'";
        var t = literal.Trim();
        var negative = false;
        if (t.StartsWith("-", StringComparison.Ordinal))
        {
            negative = true;
            t = t.Substring(1).TrimStart();
        }

        int colon = t.IndexOf(':');
        if (colon < 0)
            return TrySingleUnitTicks(t, literal, negative, out ticks, formatHint, out error);

        // [d.]hh:mm[:ss[.fffffff]]：日部分 = 冒号前最后一个 '.' 之前
        long days = 0;
        string timePart = t;
        int dot = t.LastIndexOf('.', colon);
        if (dot >= 0)
        {
            if (!long.TryParse(t.Substring(0, dot), NumberStyles.Integer, CultureInfo.InvariantCulture, out days))
            {
                error = $"expects integer days in '[-][d.]hh:mm[:ss[.fffffff]]', got '{literal}'";
                return false;
            }
            timePart = t.Substring(dot + 1);
        }
        var clockParts = timePart.Split(':');
        if (clockParts.Length is < 2 or > 3)
        {
            error = formatHint;
            return false;
        }
        if (!long.TryParse(clockParts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hours)
            || hours is < 0 or > 23
            || !long.TryParse(clockParts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes)
            || minutes is < 0 or > 59)
        {
            error = "expects hours in 00..23 and minutes in 00..59 in 'hh:mm'";
            return false;
        }
        long seconds = 0;
        decimal fraction = 0m;
        if (clockParts.Length == 3)
        {
            var secs = clockParts[2];
            int frac = secs.IndexOf('.');
            if (frac >= 0)
            {
                var fracText = secs.Substring(frac + 1);
                if (fracText.Length is 0 or > 7 || HasNonDigit(fracText))
                {
                    error = "expects at most 7 fractional digits in seconds";
                    return false;
                }
                fraction = decimal.Parse("0." + fracText, CultureInfo.InvariantCulture);
                secs = secs.Substring(0, frac);
            }
            if (!long.TryParse(secs, NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds)
                || seconds is < 0 or > 59)
            {
                error = "expects seconds in 00..59 in '[:ss[.fffffff]]'";
                return false;
            }
        }
        var total = (decimal)days * TicksPerDay
            + (decimal)(hours * 60 * 60 + minutes * 60 + seconds) * TicksPerSecond
            + fraction * TicksPerSecond;
        if (negative) total = -total;
        return ToTicks(total, literal, out ticks, out error);
    }

    private const decimal TicksPerSecond = 10_000_000m;
    private const decimal TicksPerMinute = 600_000_000m;
    private const decimal TicksPerHour = 36_000_000_000m;
    private const decimal TicksPerDay = 864_000_000_000m;

    private static bool TrySingleUnitTicks(string t, string literal, bool negative,
        out long ticks, string formatHint, out string? error)
    {
        ticks = 0;
        error = null;
        string unit;
        decimal scale;
        if (t.EndsWith("ms", StringComparison.Ordinal))
        {
            unit = "ms";
            scale = 10_000m;
        }
        else if (t.EndsWith("s", StringComparison.Ordinal))
        {
            unit = "s";
            scale = TicksPerSecond;
        }
        else if (t.EndsWith("m", StringComparison.Ordinal))
        {
            unit = "m";
            scale = TicksPerMinute;
        }
        else if (t.EndsWith("h", StringComparison.Ordinal))
        {
            unit = "h";
            scale = TicksPerHour;
        }
        else if (t.EndsWith("d", StringComparison.Ordinal))
        {
            unit = "d";
            scale = TicksPerDay;
        }
        else
        {
            error = formatHint;
            return false;
        }
        if (!decimal.TryParse(t.Substring(0, t.Length - unit.Length), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var value)
            || value < 0m)
        {
            // 单位只允许一个："1h30m" 到这里会因为前缀不是纯数字而失败
            error = formatHint;
            return false;
        }
        // spec/01 §5.2 只允许一个单位 + 一个数值（"1h30m" 不是字面量）：非纯数字前缀在 TryParse 处失败。
        return ToTicks(negative ? -(value * scale) : value * scale, literal, out ticks, out error);
    }

    private static bool ToTicks(decimal total, string literal, out long ticks, out string? error)
    {
        ticks = 0;
        error = null;
        if (total > long.MaxValue || total < long.MinValue)
        {
            error = $"duration is too large for TimeSpan: '{literal}'";
            return false;
        }
        ticks = (long)total;
        return true;
    }

    public static string EscapeString(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        bool supplementary = false;
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                // Keep supplementary characters out of generated source transport and
                // EnC user-string deltas. Recreate the scalar as one UTF-16 pair at runtime.
                supplementary = true;
                sb.Append("\" + global::System.Char.ConvertFromUtf32(")
                    .Append(char.ConvertToUtf32(s[i], s[++i]).ToString(CultureInfo.InvariantCulture))
                    .Append(") + \"");
            }
            else sb.Append(EscapeChar(s[i]));
        }
        sb.Append('"');
        return supplementary ? "(" + sb + ")" : sb.ToString();
    }

    public static string EscapeInterpolatedText(string s)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                sb.Append("{(global::System.Char.ConvertFromUtf32(")
                    .Append(char.ConvertToUtf32(s[i], s[++i]).ToString(CultureInfo.InvariantCulture)).Append("))}");
            else if (s[i] == '{' || s[i] == '}') sb.Append(s[i]).Append(s[i]);
            else sb.Append(EscapeChar(s[i]));
        }
        return sb.ToString();
    }

    /// <summary>把单个字符转成 C# 字面量转义（含 \n / \r / \t / \\ / 标点引号 / 控制字符 \uXXXX）。</summary>
    private static string EscapeChar(char ch)
    {
        switch (ch)
        {
            case '"': return "\\\"";
            case '\'': return "\\'";
            case '\\': return "\\\\";
            case '\0': return "\\0";
            case '\n': return "\\n";
            case '\r': return "\\r";
            case '\t': return "\\t";
            default:
                if (ch < 32 || char.IsSurrogate(ch)) return $"\\u{((int)ch).ToString("x4", CultureInfo.InvariantCulture)}";
                return ch.ToString();
        }
    }
}

public static class Suggest
{
    /// <summary>从候选里给最近似名（编辑距离 ≤ maxDistance），用于修复建议。</summary>
    public static string? Nearest(string input, IEnumerable<string> candidates, int maxDistance = 3)
    {
        string? best = null;
        int bestDistance = int.MaxValue;
        foreach (var candidate in candidates)
        {
            var distance = Levenshtein(input, candidate);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }
        return bestDistance <= maxDistance ? best : null;
    }

    private static int Levenshtein(string a, string b)
    {
        var dp = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) dp[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) dp[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                dp[i, j] = Math.Min(Math.Min(dp[i - 1, j] + 1, dp[i, j - 1] + 1), dp[i - 1, j - 1] + cost);
            }
        return dp[a.Length, b.Length];
    }
}

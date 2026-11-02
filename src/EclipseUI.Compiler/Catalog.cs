using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace EclipseUI.Compiler;

/// <summary>
/// 目录 POCO 与结构相等实现在 <c>UiCatalogModel.cs</c>（design/14 §14.5.1）；
/// 目录值由 <c>EclipseUI.Compiler.Metadata.UiCatalogFactory</c> 从符号派生（§14.10.2），
/// 本文件只保留与目录无关的字面量转换与建议算法。
/// </summary>
public static class UiLiterals
{
    /// <summary>把 .ui 字面量按目标属性类型在编译期转换为 C# 表达式；失败给出期望格式。</summary>
    public static bool TryConvert(string clrType, string literal, out string? csharpExpr, out string? error)
    {
        csharpExpr = null;
        error = null;
        switch (clrType)
        {
            case "double":
                if (double.TryParse(literal.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                {
                    csharpExpr = d.ToString("R", CultureInfo.InvariantCulture) + "d";
                    return true;
                }
                error = $"expects a number like '8' or '8.5', got '{literal}'";
                return false;
            case "int":
                if (int.TryParse(literal.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
                {
                    csharpExpr = i.ToString(CultureInfo.InvariantCulture);
                    return true;
                }
                error = $"expects an integer like '42', got '{literal}'";
                return false;
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
            default:
                error = $"unsupported literal type '{clrType}'";
                return false;
        }
    }

    public static string EscapeString(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (ch < 32) sb.Append("\\u").Append(((int)ch).ToString("x4"));
                    else sb.Append(ch);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
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

using System;

namespace BubuUI.Compiler;

/// <summary>
/// 词法层用到的「C# 片段平衡扫描」：从 <c>{</c>/<c>(</c> 出发找配对闭括号，
/// 字符串（普通/verbatim/raw/插值）、字符字面量、注释都按 C# 规则跳过（D11）。
///
/// 为什么不用 Roslyn tokenizer：本程序集刻意不引用 Roslyn（前端可在无 Roslyn 依赖下单独测试），
/// 而 `SyntaxFactory.ParseTokens` 对**不完整片段**（括号未配平）会产出 SkippedTokensTrivia，
/// 插值洞里的 `{}` 又是独立 token，用它做「配对括号定位」需要额外补偿；这里只做最小状态机，
/// 语义边界与 C# 词法一致即可（真正的语法/语义错误仍交 Roslyn，经 <c>#line</c> 回映）。
///
/// 公开的原因：IDE 侧（`BubuUI.LanguageServer`）判断「光标是否在 <c>{expr}</c> 内」
/// 要用同一套括号/字符串判定，否则高亮与补全对同一段文本会有两种边界理解。
/// </summary>
public static class CSharpSnippet
{
    /// <summary>
    /// 从 <paramref name="openIndex"/> 处的 <c>{</c> 或 <c>(</c> 出发，返回配对闭括号的下标；
    /// 未闭合返回 -1。<paramref name="openIndex"/> 前的字符不参与扫描。
    /// </summary>
    public static int FindMatchingClose(string text, int openIndex)
    {
        if (openIndex < 0 || openIndex >= text.Length) return -1;
        char open = text[openIndex];
        char close = open == '{' ? '}' : open == '(' ? ')' : open == '[' ? ']' : '\0';
        if (close == '\0') return -1;

        int depth = 0;
        int i = openIndex;
        while (i < text.Length)
        {
            char c = text[i];

            if (c == '/' && i + 1 < text.Length && (text[i + 1] == '/' || text[i + 1] == '*'))
            {
                i = SkipComment(text, i);
                continue;
            }

            if (c == '\'')
            {
                i = SkipCharLiteral(text, i);
                continue;
            }

            if (c == '"' || c == '@' || c == '$')
            {
                int after = SkipString(text, i);
                if (after > i)
                {
                    i = after;
                    continue;
                }
            }

            if (c == open) depth++;
            else if (c == close)
            {
                depth--;
                if (depth == 0) return i;
            }
            i++;
        }
        return -1;
    }

    /// <summary>
    /// 若 <paramref name="i"/> 处是字符串开头（<c>"</c>、<c>@"</c>、<c>$"</c>、<c>$@"</c>、<c>"""</c>、
    /// <c>$"""</c> 等），返回字符串结束后的下标；否则原样返回 <paramref name="i"/>。
    /// </summary>
    private static int SkipString(string text, int i)
    {
        int start = i;
        int j = i;
        bool verbatim = false;
        int dollars = 0;
        while (j < text.Length && (text[j] == '$' || text[j] == '@'))
        {
            if (text[j] == '$') dollars++;
            else verbatim = true;
            j++;
        }
        if (j >= text.Length || text[j] != '"') return start; // @ident / $ 单独出现：不是字符串

        int quotes = 0;
        while (j + quotes < text.Length && text[j + quotes] == '"') quotes++;

        // C# 11 raw string：三个及以上引号，内容不处理转义，闭合同样是等量引号串。
        if (quotes >= 3) return SkipRawString(text, j, quotes);

        return SkipQuotedString(text, j, verbatim, dollars > 0);
    }

    private static int SkipRawString(string text, int quoteStart, int quotes)
    {
        int i = quoteStart + quotes;
        while (i < text.Length)
        {
            if (text[i] != '"') { i++; continue; }
            int run = 0;
            while (i + run < text.Length && text[i + run] == '"') run++;
            if (run >= quotes) return i + run;
            i += run;
        }
        return text.Length;
    }

    /// <summary>普通 / verbatim / 插值字符串；插值洞内的代码递归按平衡括号跳过。</summary>
    private static int SkipQuotedString(string text, int quoteIndex, bool verbatim, bool interpolated)
    {
        int i = quoteIndex + 1;
        while (i < text.Length)
        {
            char c = text[i];

            if (interpolated && c == '{')
            {
                if (i + 1 < text.Length && text[i + 1] == '{') { i += 2; continue; } // {{
                int holeEnd = FindMatchingClose(text, i);
                if (holeEnd < 0) return text.Length;
                i = holeEnd + 1;
                continue;
            }

            if (interpolated && c == '}')
            {
                // }} 是转义字面量；孤立 } 在合法 C# 里不会出现，容忍并跳过。
                i += i + 1 < text.Length && text[i + 1] == '}' ? 2 : 1;
                continue;
            }

            if (!verbatim && c == '\\')
            {
                i += 2;
                continue;
            }

            if (c == '"')
            {
                if (verbatim && i + 1 < text.Length && text[i + 1] == '"') { i += 2; continue; } // ""
                return i + 1;
            }

            i++;
        }
        return text.Length;
    }

    private static int SkipCharLiteral(string text, int i)
    {
        int j = i + 1;
        while (j < text.Length)
        {
            if (text[j] == '\\') { j += 2; continue; }
            if (text[j] == '\'') return j + 1;
            if (text[j] == '\n') return j; // 未闭合：不跨行吞掉后续文本
            j++;
        }
        return text.Length;
    }

    private static int SkipComment(string text, int i)
    {
        if (text[i + 1] == '/')
        {
            int end = text.IndexOf('\n', i);
            return end < 0 ? text.Length : end + 1;
        }
        int close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
        return close < 0 ? text.Length : close + 2;
    }
}

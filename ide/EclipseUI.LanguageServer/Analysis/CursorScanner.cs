using EclipseUI.Compiler;

namespace EclipseUI.LanguageServer.Analysis;

/// <summary>光标所处的语法位置。补全/悬停按它分派，绝不靠正则"猜上一行"。</summary>
public enum CursorKind
{
    /// <summary>文本模式（.ui 的普通内容），没有可补全的语义。</summary>
    Text,

    /// <summary><c>&lt;But|</c>：标签名。</summary>
    TagName,

    /// <summary><c>&lt;/|</c>：结束标签名。</summary>
    EndTagName,

    /// <summary><c>&lt;Button Ga|</c>：属性名（含 <c>@bind-</c> 前缀）。</summary>
    AttributeName,

    /// <summary><c>&lt;Button Gap="|</c>：属性值。</summary>
    AttributeValue,

    /// <summary><c>{|}</c>：C# 表达式（含 <c>@if</c>/<c>@foreach</c> 的条件）。</summary>
    Expression,

    /// <summary>
    /// <c>@code {| … }</c>：块内是真实 C# 文档文本（K21），跳转定义在成员树上解析。
    /// 悬停/重命名/补全都按 C# 处理（design/18 阶段 1–2：补全走 Roslyn CompletionService）。
    /// </summary>
    CodeBlock,

    /// <summary><c>@i|</c>：指令名。</summary>
    Directive,

    /// <summary><c>@using |</c>：指令参数（命名空间）。</summary>
    DirectiveArgument,
}

/// <summary>光标外围的一个 <c>@foreach</c> 作用域（<see cref="CursorContext.OpenForeaches"/> 的元素）。</summary>
public sealed record ForeachScope
{
    /// <summary>迭代变量名（<c>@foreach (var <b>task</b> in tasks)</c> 的 task）。</summary>
    public string VarName { get; init; } = "";

    /// <summary>集合表达式原文（<c>in</c> 之后的部分），item 类型由探针推断。</summary>
    public string Collection { get; init; } = "";

    /// <summary>变量名在文档里的起点（跳转定义落回 foreach 头用）；头解析不了为 -1。</summary>
    public int NameOffset { get; init; } = -1;
}

/// <summary>
/// 解析成功的 <c>@foreach</c> 头：变量名、集合表达式与它们在文档里的区间。
/// 跳转定义落回变量名、重命名扫集合表达式里的引用，都要这组偏移。
/// </summary>
public sealed record ForeachHeader(
    string VarName,
    string Collection,
    int NameOffset,
    int CollectionStart,
    int CollectionEnd);

/// <summary>
/// 全文档收集到的一个表达式（<see cref="CursorScanner.CollectExpressions"/>）：
/// 重命名的引用扫描按它逐个探针。foreach 头不是普通表达式——整体不是合法 C#，
/// 只探测它的集合表达式区间（<see cref="ForeachHeader"/> 有精确偏移）。
/// </summary>
public sealed record CollectedExpression
{
    /// <summary>表达式正文起点（定界符后首字符）。</summary>
    public int Start { get; init; }

    /// <summary>表达式正文终点（闭定界符位置，不含）。</summary>
    public int End { get; init; }

    /// <summary>指令名（<c>if</c>/<c>foreach</c>/<c>switch</c>/<c>else if</c>）；普通 <c>{expr}</c> 为空。</summary>
    public string Directive { get; init; } = "";

    /// <summary>该表达式外围的 foreach 作用域链（外层在前）——环境构建的骨架。</summary>
    public IReadOnlyList<ForeachScope> ForeachStack { get; init; } = Array.Empty<ForeachScope>();

    /// <summary><see cref="Directive"/> 为 foreach 且头解析成功时的头信息；其余 null。</summary>
    public ForeachHeader? Header { get; init; }
}

/// <summary>
/// 光标上下文。<see cref="Prefix"/> + <see cref="TokenStart"/> 一起构成**待替换区间**：
/// 补全项显式带 textEdit，避免客户端按"单词"扩大/缩小替换范围
/// （<c>@bind-Ch</c> 的默认词边界是 <c>Ch</c>，会补成 <c>@bind-@bind-Checked</c>）。
/// </summary>
public sealed record CursorContext
{
    public CursorKind Kind { get; init; } = CursorKind.Text;

    /// <summary>当前标签名（属性名/属性值/表达式位置有效）；不在标签内为空。</summary>
    public string TagName { get; init; } = "";

    /// <summary>当前属性名（属性值/表达式位置有效）；不在属性内为空。</summary>
    public string AttributeName { get; init; } = "";

    /// <summary>光标所在 token 从 <see cref="TokenStart"/> 到光标的原文。</summary>
    public string Prefix { get; init; } = "";

    public int TokenStart { get; init; }

    /// <summary><c>@using |</c>：指令参数（命名空间）时的指令名（如 <c>using</c>）。</summary>
    public string Directive { get; init; } = "";

    /// <summary>尚未闭合的元素栈（内层在前），用于 <c>&lt;/</c> 补全。</summary>
    public IReadOnlyList<string> OpenTags { get; init; } = Array.Empty<string>();

    /// <summary>
    /// 表达式正文的起点（开定界符之后的第一个字符位置）：<c>{expr}</c> 的 expr 首、
    /// <c>@if (expr)</c> 的括号后首字符。表达式上下文恒 ≥ 0，其余为 -1。
    /// 补全/悬停用它取**完整表达式**（<see cref="Prefix"/> 只含点号成员访问词）。
    /// </summary>
    public int ExpressionStart { get; init; } = -1;

    /// <summary>
    /// 表达式正文的终点（闭定界符位置，不含）：<c>{expr|}</c> 的 <c>}</c>、<c>@if (expr)</c>
    /// 的 <c>)</c>。光标落在表达式中间时（跳转定义），没有它就只能拿到半截表达式
    /// （<c>task.Na</c>），绑不出符号。未闭合表达式为文本末尾。
    /// </summary>
    public int ExpressionEnd { get; init; } = -1;

    /// <summary>
    /// 光标外围的 <c>@foreach</c> 作用域链（外层在前）：表达式环境构建的骨架——
    /// item 类型不是这里给的（那要问 Roslyn），这里只给变量名与集合表达式原文。
    /// </summary>
    public IReadOnlyList<ForeachScope> OpenForeaches { get; init; } = Array.Empty<ForeachScope>();

    /// <summary>表达式是否写在属性值位置（<c>P={expr}</c>/<c>@bind-P={expr}</c>）。</summary>
    public bool IsAttributeExpression => Kind == CursorKind.Expression && AttributeName.Length > 0;
}

/// <summary>
/// 从文件头扫到光标，判定语法位置。为什么不用 <see cref="Lexer"/>：词法器是给
/// "完整文档 + 诊断"用的，光标处往往是**半截 token**（<c>&lt;But</c>、<c>@bind-</c>），
/// 而且补全时文档必然语法不全。这里做成一个宽容的单向状态机：只关心
/// 「模式（文本/标签/引号/表达式）与当前标签属性」，遇到不完整结构就按"继续到文件尾"处理。
///
/// 括号配对复用编译器前端的 <see cref="CSharpSnippet"/>：同一套字符串/注释/插值判定，
/// 保证 <c>{ '}' }</c>、<c>{ "}" }</c>、lambda 块体这类边界与真正的编译器一致。
/// </summary>
public static class CursorScanner
{
    public static CursorContext Scan(string text, int offset)
    {
        offset = Math.Clamp(offset, 0, text.Length);
        var open = new List<string>();
        var i = 0;
        var blockDepth = 0;
        var expectBlockBrace = false;
        var foreaches = new List<(ForeachHeader Header, int Depth)>();
        ForeachHeader? pendingForeach = null;

        while (i < text.Length)
        {
            // @if/@foreach/@switch/@else 之后紧跟的 '{' 是块定界符，不是表达式。
            if (expectBlockBrace)
            {
                int j = SkipWhitespace(text, i);
                if (j < text.Length && text[j] == '{')
                {
                    blockDepth++;
                    if (pendingForeach is not null)
                        foreaches.Add((pendingForeach, blockDepth));
                    pendingForeach = null;
                    expectBlockBrace = false;
                    i = j + 1;
                    continue;
                }
                // 头后没有块（半截输入/语法坏）：该 foreach 不进环境，不能凭空猜作用域。
                pendingForeach = null;
                expectBlockBrace = false;
            }

            char c = text[i];

            // ---- 注释：内容不参与语义，光标在内一律按文本处理 ----
            if (c == '<' && StartsWith(text, i, "<!--"))
            {
                int end = text.IndexOf("-->", i + 4, StringComparison.Ordinal);
                int stop = end < 0 ? text.Length : end + 3;
                if (offset >= i && offset <= stop) return Plain(open, offset);
                i = stop;
                continue;
            }
            if (c == '@' && i + 1 < text.Length && text[i + 1] == '*')
            {
                int end = text.IndexOf("*@", i + 2, StringComparison.Ordinal);
                int stop = end < 0 ? text.Length : end + 2;
                if (offset >= i && offset <= stop) return Plain(open, offset);
                i = stop;
                continue;
            }

            // ---- 指令 ----
            // `@` 后还没有字母（刚敲下 @、或 @ 后面直接换行/空白）：仍然是指令位置——
            // 补全就在这一刻触发，不能把它当普通文本。
            if (c == '@' && (i + 1 >= text.Length || !IsIdentStart(text[i + 1])))
            {
                if (offset >= i && offset <= i + 1)
                    return new CursorContext
                    {
                        Kind = CursorKind.Directive,
                        Prefix = "@",
                        TokenStart = i,
                        OpenTags = open,
                    };
                i++;
                continue;
            }

            if (c == '@' && i + 1 < text.Length && IsIdentStart(text[i + 1]))
            {
                int wordEnd = i + 1;
                while (wordEnd < text.Length && IsIdentChar(text[wordEnd])) wordEnd++;
                string word = text.Substring(i + 1, wordEnd - i - 1);

                // @code / @style：整块是 C#/Eustyle，不是模板上下文，直接跳过。
                // @code 块内是**真实文档文本**（K21 透传）——跳转定义要在块内解析 C#，
                // 单独给一个 Kind；@style 是 Eustyle 样式表，光标在内一律按文本。
                if (word is "code" or "style")
                {
                    int brace = SkipWhitespace(text, wordEnd);
                    if (brace < text.Length && text[brace] == '{')
                    {
                        int close = CSharpSnippet.FindMatchingClose(text, brace);
                        int stop = close < 0 ? text.Length : close + 1;
                        if (offset > i && offset < stop)
                        {
                            if (word == "code")
                            {
                                // 补全的默认替换区间：光标处的标识符前缀（不含点号——
                                // Roslyn 候选自带 item.Span，逐字回映后才是权威区间）。
                                int wordStart = offset;
                                while (wordStart > brace + 1 && IsIdentChar(text[wordStart - 1])) wordStart--;
                                return new CursorContext
                                {
                                    Kind = CursorKind.CodeBlock,
                                    TokenStart = wordStart,
                                    Prefix = text.Substring(wordStart, offset - wordStart),
                                    OpenTags = open,
                                };
                            }
                            return Plain(open, offset);
                        }
                        i = stop;
                        continue;
                    }
                }

                if (word is "if" or "foreach" or "switch")
                {
                    // 惰性构造：offset 可能已被前面的跳过区间（@code 块、注释）越过，
                    // 先判范围再取原文——offset < i 时 Substring 会得到负长度。
                    if (offset >= i && offset <= wordEnd)
                        return new CursorContext
                        {
                            Kind = CursorKind.Directive,
                            Prefix = text.Substring(i, offset - i),
                            TokenStart = i,
                            OpenTags = open,
                        };

                    int next = wordEnd;
                    int paren = SkipWhitespace(text, wordEnd);
                    if (paren < text.Length && text[paren] == '(')
                    {
                        int close = CSharpSnippet.FindMatchingClose(text, paren);
                        int condEnd = close < 0 ? text.Length : close;
                        if (offset > paren && offset <= condEnd)
                            return new CursorContext
                            {
                                Kind = CursorKind.Expression,
                                Directive = word,
                                Prefix = ExpressionPrefix(text, paren + 1, offset),
                                TokenStart = ExpressionTokenStart(text, paren + 1, offset),
                                ExpressionStart = paren + 1,
                                ExpressionEnd = condEnd,
                                OpenTags = open,
                                OpenForeaches = SnapshotForeaches(foreaches),
                            };
                        next = close < 0 ? text.Length : close + 1;
                        // @foreach 的头在块开启前先记下：`{` 真出现（expectBlockBrace 命中）才压栈。
                        pendingForeach = word == "foreach" && close >= 0
                            ? ParseForeachHeader(text, paren + 1, close - paren - 1)
                            : null;
                    }
                    else
                    {
                        pendingForeach = null;
                    }

                    expectBlockBrace = true;
                    i = next;
                    continue;
                }

                // @using |（命名空间 / 别名目标）与 @namespace |（spec/02 §2.2 的必填声明）：
                // 光标在指令名之后、行尾之前时都按「指令参数」处理，补全器按 Directive 分流。
                if (word is "using" or "namespace")
                {
                    int lineEnd = text.IndexOf('\n', i);
                    if (lineEnd < 0) lineEnd = text.Length;
                    if (offset >= wordEnd && offset <= lineEnd)
                    {
                        int argStart = SkipWhitespace(text, wordEnd);
                        return new CursorContext
                        {
                            Kind = CursorKind.DirectiveArgument,
                            Directive = word,
                            Prefix = text.Substring(argStart, Math.Max(0, Math.Min(offset, lineEnd) - argStart)),
                            TokenStart = argStart,
                            OpenTags = open,
                        };
                    }
                    i = lineEnd;
                    continue;
                }

                if (offset >= i && offset <= wordEnd)
                    return new CursorContext
                    {
                        Kind = CursorKind.Directive,
                        Prefix = text.Substring(i, offset - i),
                        TokenStart = i,
                        OpenTags = open,
                    };

                i = wordEnd;
                continue;
            }

            // ---- 标签 ----
            if (c == '<')
            {
                if (i + 1 < text.Length && text[i + 1] == '!')
                {
                    int gt = text.IndexOf('>', i);
                    i = gt < 0 ? text.Length : gt + 1;
                    continue;
                }

                if (i + 1 < text.Length && text[i + 1] == '/')
                {
                    int nameStart = i + 2;
                    int j = nameStart;
                    while (j < text.Length && IsIdentChar(text[j])) j++;
                    int gt = text.IndexOf('>', j);
                    int end = gt < 0 ? text.Length : gt + 1;
                    // offset > i：光标停在 '<' 之前仍属文本，不能算进结束标签。
                    if (offset > i && offset <= end)
                        return new CursorContext
                        {
                            Kind = CursorKind.EndTagName,
                            Prefix = text.Substring(nameStart, Math.Max(0, Math.Min(offset, j) - nameStart)),
                            TokenStart = nameStart,
                            OpenTags = open,
                        };
                    if (open.Count > 0) open.RemoveAt(open.Count - 1);
                    i = end;
                    continue;
                }

                var (next, context, pushTag) = ScanOpenTag(text, i, offset, open, foreaches);
                if (context is not null) return context;
                if (pushTag is not null) open.Add(pushTag);
                i = next;
                continue;
            }

            // ---- 文本模式下的表达式与块大括号 ----
            if (c == '{')
            {
                int close = CSharpSnippet.FindMatchingClose(text, i);
                int stop = close < 0 ? text.Length : close;
                if (offset > i && offset <= stop)
                    return new CursorContext
                    {
                        Kind = CursorKind.Expression,
                        Prefix = ExpressionPrefix(text, i + 1, offset),
                        TokenStart = ExpressionTokenStart(text, i + 1, offset),
                        ExpressionStart = i + 1,
                        ExpressionEnd = stop,
                        OpenTags = open,
                        OpenForeaches = SnapshotForeaches(foreaches),
                    };
                i = close < 0 ? text.Length : close + 1;
                continue;
            }
            if (c == '}')
            {
                if (blockDepth > 0) blockDepth--;
                // 块闭合：弹掉比当前深度更深的所有 foreach 作用域（嵌套按深度归位）。
                while (foreaches.Count > 0 && foreaches[^1].Depth > blockDepth)
                    foreaches.RemoveAt(foreaches.Count - 1);
                i++;
                continue;
            }

            // ---- 裸词（含 else 续接）----
            if (IsIdentStart(c))
            {
                int j = i;
                while (j < text.Length && IsIdentChar(text[j])) j++;
                if (text.AsSpan(i, j - i).Equals("else", StringComparison.Ordinal))
                {
                    int k = SkipWhitespace(text, j);
                    if (StartsWith(text, k, "if"))
                    {
                        k = SkipWhitespace(text, k + 2);
                        if (k < text.Length && text[k] == '(')
                        {
                            int close = CSharpSnippet.FindMatchingClose(text, k);
                            if (close < 0) { i = text.Length; continue; }
                            if (offset > k && offset <= close)
                                return new CursorContext
                                {
                                    Kind = CursorKind.Expression,
                                    Directive = "else if",
                                    Prefix = ExpressionPrefix(text, k + 1, offset),
                                    TokenStart = ExpressionTokenStart(text, k + 1, offset),
                                    ExpressionStart = k + 1,
                                    ExpressionEnd = close,
                                    OpenTags = open,
                                    OpenForeaches = SnapshotForeaches(foreaches),
                                };
                            i = close + 1;
                            expectBlockBrace = true;
                            continue;
                        }
                    }
                    expectBlockBrace = true;
                }
                i = j;
                continue;
            }

            i++;
        }

        return Plain(open, offset);
    }

    private static CursorContext Plain(List<string> open, int offset) =>
        new() { Kind = CursorKind.Text, Prefix = "", TokenStart = offset, OpenTags = open };

    private static (int Next, CursorContext? Context, string? PushTag) ScanOpenTag(
        string text, int start, int offset, List<string> open,
        List<(ForeachHeader Header, int Depth)> foreaches)
    {
        int nameStart = start + 1;
        int j = nameStart;
        while (j < text.Length && IsIdentChar(text[j])) j++;
        string tagName = text.Substring(nameStart, j - nameStart);

        if (offset > start && offset <= j)
            return (j, new CursorContext
            {
                Kind = CursorKind.TagName,
                TagName = tagName, // 完整名：跳转定义要按它查目录（Prefix 只是已敲的前缀）
                Prefix = text.Substring(nameStart, Math.Max(0, offset - nameStart)),
                TokenStart = nameStart,
                OpenTags = open,
            }, null);

        string? attribute = null;
        while (j < text.Length)
        {
            int whitespaceStart = j;
            while (j < text.Length && char.IsWhiteSpace(text[j])) j++;
            if (offset >= whitespaceStart && offset <= j)
                return (j, AttrName(tagName, attribute: "", prefix: "", tokenStart: offset, open), null);

            if (j >= text.Length) break;
            char c = text[j];

            if (c == '>') return (j + 1, null, tagName.Length > 0 ? tagName : null);
            if (c == '/' && j + 1 < text.Length && text[j + 1] == '>') return (j + 2, null, null);

            if (c == '{')
            {
                int close = CSharpSnippet.FindMatchingClose(text, j);
                int stop = close < 0 ? text.Length : close;
                if (offset > j && offset <= stop)
                    return (stop + 1, new CursorContext
                    {
                        Kind = CursorKind.Expression,
                        TagName = tagName,
                        AttributeName = attribute ?? "",
                        Prefix = ExpressionPrefix(text, j + 1, offset),
                        TokenStart = ExpressionTokenStart(text, j + 1, offset),
                        ExpressionStart = j + 1,
                        ExpressionEnd = stop,
                        OpenTags = open,
                        OpenForeaches = SnapshotForeaches(foreaches),
                    }, null);
                j = close < 0 ? text.Length : close + 1;
                continue;
            }

            if (c == '"')
            {
                int close = SkipAttributeString(text, j);
                // offset == close 是"空值"形态（Checked="|"）：光标在闭合引号上，仍算值位置。
                if (offset > j && offset <= close)
                    return (close + 1, AttrValue(tagName, attribute, text, j, offset, open), null);
                j = close + 1;
                continue;
            }

            if (c == '=')
            {
                int k = SkipWhitespace(text, j + 1);
                if (offset >= j + 1 && offset <= k)
                    return (k, AttrValue(tagName, attribute, text, j, offset, open), null);
                j = k;
                continue;
            }

            int ns = j;
            while (j < text.Length && IsAttrChar(text[j])) j++;
            if (j == ns)
            {
                j++;
                continue;
            }

            string name = text.Substring(ns, j - ns);
            if (offset >= ns && offset <= j)
                return (j, AttrName(tagName, name, text.Substring(ns, offset - ns), ns, open), null);
            attribute = name;
        }

        return (text.Length, null, null); // 未闭合的标签不压栈
    }

    private static CursorContext AttrName(
        string tagName, string attribute, string prefix, int tokenStart, List<string> open) =>
        new()
        {
            Kind = CursorKind.AttributeName,
            TagName = tagName,
            AttributeName = attribute,
            Prefix = prefix,
            TokenStart = tokenStart,
            OpenTags = open,
        };

    private static CursorContext AttrValue(
        string tagName, string? attribute, string text, int valueStart, int offset, List<string> open)
    {
        int from = valueStart + 1;                  // 引号之后
        int wordStart = ValueWordStart(text, from, offset);
        return new CursorContext
        {
            Kind = CursorKind.AttributeValue,
            TagName = tagName,
            AttributeName = attribute ?? "",
            Prefix = text.Substring(wordStart, offset - wordStart),
            TokenStart = wordStart,
            OpenTags = open,
        };
    }

    /// <summary>属性值内的"词"起点：按空白与 <c>,;:</c> 切分（<c>Style="A: 1; B: 2"</c> 逐段补全）。</summary>
    private static int ValueWordStart(string text, int from, int offset)
    {
        for (int i = offset - 1; i >= from; i--)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c) || c is ',' or ';' or ':') return i + 1;
        }
        return from;
    }

    /// <summary>表达式内的"词"起点（标识符/成员访问字符）；供客户端按前缀过滤。</summary>
    private static int ExpressionTokenStart(string text, int from, int offset)
    {
        for (int i = offset - 1; i >= from; i--)
        {
            char c = text[i];
            if (!(char.IsLetterOrDigit(c) || c is '_' or '.')) return i + 1;
        }
        return from;
    }

    private static string ExpressionPrefix(string text, int from, int offset)
    {
        int start = ExpressionTokenStart(text, from, offset);
        return text.Substring(start, offset - start);
    }

    /// <summary>把内部 foreach 栈快照成对外形态（返回后扫描继续推进，不能共享可变列表）。</summary>
    private static ForeachScope[] SnapshotForeaches(
        List<(ForeachHeader Header, int Depth)> scopes) =>
        scopes.Count == 0
            ? Array.Empty<ForeachScope>()
            : scopes.Select(static s => new ForeachScope
                {
                    VarName = s.Header.VarName,
                    Collection = s.Header.Collection,
                    NameOffset = s.Header.NameOffset,
                })
                .ToArray();

    /// <summary>
    /// 全文档收集所有表达式（重命名的引用扫描用，L3 第二片）。复用 <see cref="Scan"/>
    /// 的同一套边界规则（注释/@code/引号/括号配对）——绝不复制第二套判定。每记录一个
    /// 表达式就跳过它的内部（<see cref="CSharpSnippet.FindMatchingClose"/> 原子消费括号对，
    /// 表达式里不会再出现表达式上下文），代价 O(n)。
    /// </summary>
    public static IReadOnlyList<CollectedExpression> CollectExpressions(string text)
    {
        var results = new List<CollectedExpression>();
        var offset = 0;
        while (offset <= text.Length)
        {
            var context = Scan(text, offset);
            if (context.Kind == CursorKind.Expression && context.ExpressionStart >= 0)
            {
                var header = context.Directive == "foreach"
                    ? ParseForeachHeader(text, context.ExpressionStart,
                        context.ExpressionEnd - context.ExpressionStart)
                    : null;
                results.Add(new CollectedExpression
                {
                    Start = context.ExpressionStart,
                    End = context.ExpressionEnd,
                    Directive = context.Directive,
                    ForeachStack = context.OpenForeaches,
                    Header = header,
                });
                // 跳过表达式内部：下一次扫描从闭定界符之后开始。
                offset = context.ExpressionEnd + 1;
                continue;
            }
            if (offset >= text.Length) break;
            offset++;
        }
        return results;
    }

    /// <summary>
    /// 解析 <c>@foreach</c> 括号内文本为头信息。<c>in</c> 是 foreach 头专有关键字，取
    /// 最后一个 <c> in </c> 分割；左侧最后一段是变量名（兼容 <c>var x</c> 与 <c>Type x</c>）。
    /// 解析失败返回 null——该层不进环境，而不是猜一个错答案。变量名与集合的偏移都按
    /// 文档绝对位置给（跳转定义落回变量名、重命名扫集合引用用）。
    /// </summary>
    public static ForeachHeader? ParseForeachHeader(string text, int start, int length)
    {
        var header = text.Substring(start, length);
        var trimmed = header.Trim();
        if (trimmed.Length == 0) return null;
        int trimStart = header.Length - header.TrimStart().Length;
        int inIndex = trimmed.LastIndexOf(" in ", StringComparison.Ordinal);
        if (inIndex <= 0) return null;
        var after = trimmed.Substring(inIndex + 4);
        var collection = after.Trim();
        if (collection.Length == 0) return null;
        var left = trimmed.Substring(0, inIndex).TrimEnd();
        int lastSpace = left.LastIndexOf(' ');
        var varName = (lastSpace >= 0 ? left.Substring(lastSpace + 1) : left).Trim();
        if (varName.Length == 0 || !IsIdentStart(varName[0])) return null;

        // 变量名紧跟最后一个空格（left 已去尾空白）；集合在 " in " 之后去首尾空白。
        // 偏移都在 trimmed 坐标里算好再加 trimStart，落到原文精确对齐。
        int nameInTrimmed = lastSpace >= 0 ? lastSpace + 1 : 0;
        int collectionStart = inIndex + 4 + (after.Length - after.TrimStart().Length);
        int docStart = start + trimStart;
        return new ForeachHeader(
            varName, collection,
            docStart + nameInTrimmed,
            docStart + collectionStart,
            docStart + collectionStart + collection.Length);
    }

    private static int SkipWhitespace(string text, int i)
    {
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        return i;
    }

    /// <summary>属性字面量：反斜杠转义下一个字符（spec/01 §2.5），未闭合时停在行尾。</summary>
    private static int SkipAttributeString(string text, int quoteIndex)
    {
        int i = quoteIndex + 1;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '\\') { i += 2; continue; }
            if (c == '"') return i;
            if (c == '\n') return i - 1;
            i++;
        }
        return text.Length;
    }

    private static bool StartsWith(string text, int index, string value) =>
        index >= 0 && index + value.Length <= text.Length
        && text.AsSpan(index, value.Length).SequenceEqual(value);

    private static bool IsIdentStart(char c) => char.IsAsciiLetter(c) || c == '_';

    private static bool IsIdentChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';

    /// <summary>属性名允许的字符：<c>@bind-</c> 前缀、附加属性 <c>Owner.Name</c>、旧修饰符 <c>:</c>。</summary>
    private static bool IsAttrChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or ':' or '-' or '@';
}

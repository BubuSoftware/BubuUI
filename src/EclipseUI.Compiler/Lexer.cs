using System.Text;

namespace EclipseUI.Compiler;

public enum TokenKind
{
    TagOpen,
    EndTagOpen,
    TagClose,
    SelfClose,
    Equals,
    Identifier,
    String,
    Text,
    Expression,
    Directive,
    BlockOpen,
    BlockClose,
    Error,
    EndOfFile,
}

public sealed class Token
{
    public TokenKind Kind { get; set; }
    public string Text { get; set; } = "";
    public string? Aux { get; set; }
    public TextSpan Span { get; set; }
    public TextSpan InnerSpan { get; set; }

    public override string ToString() => $"{Kind} '{Text}'";
}

/// <summary>
/// .ui 词法分析：XML 式标记 + 文本插值 {expr} + 指令 @if/@foreach/@switch/@using/@code。
/// 表达式按平衡大括号捕获（字符串字面量感知），lambda 的 '>' 不会截断。
/// S6（K21/K22/K23）：@code 块体整块捕获（原文透传，K25）；@using 捕获命名空间（K22）；
/// else/else if 续接由 Parser 在 @if 块 '}' 消费后通过 <see cref="EnableElseContinuation"/> 精确开启
/// （K23 ③），避免把正文中的 "else" 单词误判成指令；@* *@ 与 <!-- --> 均为合法注释（K26），
/// 未闭合报错（D12）；'@{' 报错（K28）。
/// </summary>
public sealed class Lexer
{
    private readonly string _s;
    private int _i;
    private bool _inMarkup;
    private bool _nextBraceIsBlock;
    private bool _elsePending;
    private readonly StringBuilder _buf = new();
    private int _bufStart;
    private Token? _pending;

    public Lexer(string s) => _s = s;

    /// <summary>指令括号后的下一个 '{' 是块结构而非文本插值（由 Parser 调用）。</summary>
    public void SetNextBraceIsBlock() => _nextBraceIsBlock = true;

    /// <summary>
    /// Parser 在消费 @if/else-if 块的 '}' 后调用：下一次取 token 时尝试匹配 else/else if 续接
    /// （不带 @ 前缀，K23 ③）。无论匹配成败，期待只生效一次。
    /// </summary>
    public void EnableElseContinuation() => _elsePending = true;

    public Token Next()
    {
        if (_pending is not null)
        {
            var pending = _pending;
            _pending = null;
            return pending;
        }

        while (true)
        {
            if (_i >= _s.Length)
            {
                if (_buf.Length > 0) return FlushText();
                return new Token { Kind = TokenKind.EndOfFile, Text = "", Span = new TextSpan(_s.Length, 0) };
            }

            char c = _s[_i];

            if (!_inMarkup)
            {
                // 文本模式；块标志置位时跳过空白，让指令后的 '{' 直达块分支
                if (_nextBraceIsBlock && char.IsWhiteSpace(c)) { _i++; continue; }

                // else/else if 续接（K23 ③）：只在 Parser 显式开启时尝试一次
                if (_elsePending)
                {
                    _elsePending = false;
                    if (TryMatchElse(out var elseTok))
                    {
                        if (_buf.Length > 0) { _pending = elseTok; return FlushText(); }
                        return elseTok;
                    }
                    // 匹配失败：_i 已恢复，按普通文本继续
                }

                if (c == '<')
                {
                    if (_buf.Length > 0) return FlushText();
                    if (IsCommentStart())
                    {
                        var err = SkipHtmlComment();
                        if (err is not null) return err;
                        continue;
                    }
                    _inMarkup = true;
                    if (Peek(1) == '/')
                    {
                        var token = new Token { Kind = TokenKind.EndTagOpen, Text = "</", Span = new TextSpan(_i, 2) };
                        _i += 2;
                        return token;
                    }
                    var open = new Token { Kind = TokenKind.TagOpen, Text = "<", Span = new TextSpan(_i, 1) };
                    _i++;
                    return open;
                }

                if (c == '}')
                {
                    if (_buf.Length > 0) return FlushText();
                    var close = new Token { Kind = TokenKind.BlockClose, Text = "}", Span = new TextSpan(_i, 1) };
                    _i++;
                    return close;
                }

                if (c == '{' && _nextBraceIsBlock)
                {
                    _nextBraceIsBlock = false;
                    var blockOpen = new Token { Kind = TokenKind.BlockOpen, Text = "{", Span = new TextSpan(_i, 1) };
                    _i++;
                    if (_buf.Length > 0)
                    {
                        _pending = blockOpen;
                        return FlushText();
                    }
                    return blockOpen;
                }

                if (c == '@')
                {
                    // @* ... *@ 编译器丢弃注释（K23/K26）；未闭合报 EUI100（D12）
                    if (Peek(1) == '*')
                    {
                        var err = SkipRazorComment();
                        if (err is not null) return err;
                        continue;
                    }

                    var directive = ParseDirective();
                    if (directive.Kind == TokenKind.Error) return directive;
                    if (_buf.Length > 0)
                    {
                        _pending = directive;
                        return FlushText();
                    }
                    return directive;
                }

                if (c == '{')
                {
                    var expr = CaptureExpression();
                    if (_buf.Length > 0)
                    {
                        _pending = expr;
                        return FlushText();
                    }
                    return expr;
                }

                if (_buf.Length == 0) _bufStart = _i;
                _buf.Append(c);
                _i++;
                continue;
            }

            // 标记模式
            if (char.IsWhiteSpace(c)) { _i++; continue; }

            int start = _i;
            if (c == '/' && Peek(1) == '>')
            {
                _i += 2;
                _inMarkup = false;
                return new Token { Kind = TokenKind.SelfClose, Text = "/>", Span = new TextSpan(start, 2) };
            }
            if (c == '>')
            {
                _i++;
                _inMarkup = false;
                return new Token { Kind = TokenKind.TagClose, Text = ">", Span = new TextSpan(start, 1) };
            }
            if (c == '=')
            {
                _i++;
                return new Token { Kind = TokenKind.Equals, Text = "=", Span = new TextSpan(start, 1) };
            }
            if (c == '"') return ReadString(start);
            if (c == '{') return CaptureExpression();
            if (c == '@') return ReadBindAttribute(start);
            if (IsIdentStart(c)) return ReadIdentifier(start);

            _i++;
            return new Token { Kind = TokenKind.Error, Text = c.ToString(), Span = new TextSpan(start, 1) };
        }
    }

    private Token FlushText()
    {
        var token = new Token
        {
            Kind = TokenKind.Text,
            Text = _buf.ToString(),
            Span = new TextSpan(_bufStart, _i - _bufStart),
        };
        _buf.Clear();
        return token;
    }

    private bool IsCommentStart() =>
        _i + 4 <= _s.Length && _s[_i] == '<' && _s[_i + 1] == '!' && _s[_i + 2] == '-' && _s[_i + 3] == '-';

    /// <summary>
    /// 跳过 HTML 注释（K26：永久保留的合法注释）。未闭合报 EUI100（D12：
    /// 原「静默吞到文件尾」的行为废止）。
    /// </summary>
    private Token? SkipHtmlComment()
    {
        int open = _i;
        int end = _s.IndexOf("-->", _i, System.StringComparison.Ordinal);
        if (end < 0)
        {
            return new Token
            {
                Kind = TokenKind.Error,
                Text = "unterminated comment",
                Aux = "unterminated '<!--' comment; add the closing '-->'",
                Span = new TextSpan(open, _s.Length - open),
            };
        }
        _i = end + 3;
        return null;
    }

    /// <summary>跳过 @* *@ 注释（K23）。未闭合报 EUI100（D12 同口径）。</summary>
    private Token? SkipRazorComment()
    {
        int open = _i;
        _i += 2; // '@*'
        int end = _s.IndexOf("*@", _i, System.StringComparison.Ordinal);
        if (end < 0)
        {
            _i = _s.Length;
            return new Token
            {
                Kind = TokenKind.Error,
                Text = "unterminated comment",
                Aux = "unterminated '@*' comment; add the closing '*@'",
                Span = new TextSpan(open, _s.Length - open),
            };
        }
        _i = end + 2;
        return null;
    }

    private char Peek(int ahead) => _i + ahead < _s.Length ? _s[_i + ahead] : '\0';

    private Token ReadString(int start)
    {
        _i++; // 开引号
        int innerStart = _i;
        while (_i < _s.Length && _s[_i] != '"')
        {
            if (_s[_i] == '\\' && _i + 1 < _s.Length) _i++;
            _i++;
        }
        if (_i >= _s.Length)
            return new Token { Kind = TokenKind.Error, Text = "unterminated string", Span = new TextSpan(start, _s.Length - start) };
        var inner = new TextSpan(innerStart, _i - innerStart);
        var text = _s.Substring(innerStart, _i - innerStart);
        _i++; // 闭引号
        return new Token { Kind = TokenKind.String, Text = text, Span = new TextSpan(start, _i - start), InnerSpan = inner };
    }

    private static bool IsIdentStart(char c) =>
        char.IsLetter(c) || c == '_' || c == '.' || c == ':' || c == '-';

    private Token ReadIdentifier(int start)
    {
        while (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || _s[_i] == '_' || _s[_i] == '.' || _s[_i] == ':' || _s[_i] == '-'))
            _i++;
        return new Token
        {
            Kind = TokenKind.Identifier,
            Text = _s.Substring(start, _i - start),
            Span = new TextSpan(start, _i - start),
        };
    }

    /// <summary>
    /// 标记模式的 <c>@bind-P</c>：整体作为一个 Identifier 记号（spec/01 §2.3）。
    /// 前缀大小写敏感；<c>@bind-</c> 后必须紧跟名字，否则是语法错误。
    /// </summary>
    private Token ReadBindAttribute(int start)
    {
        const string prefix = "@bind-";
        if (start + prefix.Length > _s.Length ||
            string.CompareOrdinal(_s, start, prefix, 0, prefix.Length) != 0)
        {
            _i = start + 1;
            return new Token { Kind = TokenKind.Error, Text = "@", Span = new TextSpan(start, 1) };
        }

        _i = start + prefix.Length;
        if (_i >= _s.Length || !IsIdentStart(_s[_i]))
        {
            return new Token
            {
                Kind = TokenKind.Error,
                Text = prefix,
                Aux = "'@bind-' must be followed immediately by a property name",
                Span = new TextSpan(start, _i - start),
            };
        }

        return ReadIdentifier(start);
    }

    /// <summary>捕获平衡大括号内的原始 C# 表达式（字符串字面量感知）。</summary>
    private Token CaptureExpression()
    {
        int open = _i;
        _i++; // '{'
        int innerStart = _i;
        int depth = 1;
        while (_i < _s.Length)
        {
            char c = _s[_i];
            if (c == '"') { SkipStringInside('"'); continue; }
            if (c == '\'') { SkipStringInside('\''); continue; }
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    var inner = new TextSpan(innerStart, _i - innerStart);
                    var text = _s.Substring(innerStart, _i - innerStart);
                    _i++;
                    return new Token
                    {
                        Kind = TokenKind.Expression,
                        Text = text,
                        Span = new TextSpan(open, _i - open),
                        InnerSpan = inner,
                    };
                }
            }
            _i++;
        }
        return new Token { Kind = TokenKind.Error, Text = "unterminated expression", Span = new TextSpan(open, _s.Length - open) };
    }

    private void SkipStringInside(char quote)
    {
        _i++;
        while (_i < _s.Length)
        {
            if (_s[_i] == '\\') { _i += 2; continue; }
            if (_s[_i] == quote) { _i++; return; }
            _i++;
        }
    }

    /// <summary>
    /// 匹配标识符词并推进；词边界保护（后随字符不是标识符字符），失败不动位置。
    /// </summary>
    private bool MatchWord(string word)
    {
        if (_i + word.Length > _s.Length) return false;
        if (string.CompareOrdinal(_s, _i, word, 0, word.Length) != 0) return false;
        int after = _i + word.Length;
        if (after < _s.Length && (char.IsLetterOrDigit(_s[after]) || _s[after] == '_')) return false;
        _i += word.Length;
        return true;
    }

    /// <summary>跳过空白与闭合的注释；遇到未闭合注释返回 false（让正常路径报错），位置不动。</summary>
    private bool TrySkipTrivia()
    {
        while (true)
        {
            while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++;
            if (IsCommentStart())
            {
                int end = _s.IndexOf("-->", _i, System.StringComparison.Ordinal);
                if (end < 0) return false;
                _i = end + 3;
                continue;
            }
            if (_i + 2 <= _s.Length && _s[_i] == '@' && _s[_i + 1] == '*')
            {
                int end = _s.IndexOf("*@", _i + 2, System.StringComparison.Ordinal);
                if (end < 0) return false;
                _i = end + 2;
                continue;
            }
            return true;
        }
    }

    /// <summary>
    /// else/else if 续接匹配（K23 ③，不带 @）：仅在 <see cref="EnableElseContinuation"/> 开启后的
    /// 一次机会里尝试。匹配形态：else + '{'；else if + '(' 条件 ')'。失败恢复位置按普通文本处理，
    /// 避免吞掉正文里的 "else" 单词。
    /// </summary>
    private bool TryMatchElse(out Token token)
    {
        token = null!;
        int saved = _i;
        int wsStart = saved;
        if (!TrySkipTrivia()) { _i = saved; return false; }
        int elseStart = _i;
        if (!MatchWord("else")) { _i = saved; return false; }
        int afterElse = _i;
        if (!TrySkipTrivia()) { _i = saved; return false; }

        if (_i < _s.Length && _s[_i] == '{')
        {
            token = new Token
            {
                Kind = TokenKind.Directive,
                Text = "else",
                Span = new TextSpan(elseStart, 4),
            };
            _ = wsStart;
            return true; // _i 停在 '{' 前，Parser 设 SetNextBraceIsBlock 后 Advance 得 BlockOpen
        }

        if (MatchWord("if") && TrySkipTrivia() && _i < _s.Length && _s[_i] == '(')
        {
            _i++; // '('
            int innerStart = _i;
            int depth = 1;
            while (_i < _s.Length)
            {
                char c = _s[_i];
                if (c == '"') { SkipStringInside('"'); continue; }
                if (c == '\'') { SkipStringInside('\''); continue; }
                if (c == '(') depth++;
                else if (c == ')') { depth--; if (depth == 0) break; }
                _i++;
            }
            if (depth != 0) { _i = saved; return false; }
            var inner = new TextSpan(innerStart, _i - innerStart);
            _i++; // ')'
            token = new Token
            {
                Kind = TokenKind.Directive,
                Text = "else if",
                Aux = _s.Substring(innerStart, inner.Length),
                Span = new TextSpan(elseStart, _i - elseStart),
                InnerSpan = inner,
            };
            return true;
        }

        _i = saved;
        return false;
    }

    private Token ErrorToken(int start, string message, int end = -1)
    {
        if (end < 0) end = System.Math.Min(start + 2, _s.Length);
        return new Token { Kind = TokenKind.Error, Text = _s.Substring(start, System.Math.Max(1, end - start)), Aux = message, Span = new TextSpan(start, end - start) };
    }

    private Token ParseDirective()
    {
        int start = _i;
        _i++; // '@'
        int nameStart = _i;
        while (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || _s[_i] == '_'))
            _i++;
        var name = _s.Substring(nameStart, _i - nameStart);

        // '@' 后无指令名：'@{' 是 K28 明确不采纳的代码块，给出专属消息
        if (name.Length == 0)
        {
            string aux = _i < _s.Length && _s[_i] == '{'
                ? "'@{ ... }' code blocks are not supported; put statements in @code members or code-behind methods"
                : "expected a directive name after '@'";
            return ErrorToken(start, aux);
        }

        // @using {Namespace}（K22，D85）：不带分号；不做 @using(...) / @using static / 别名
        if (name == "using")
        {
            while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++;
            if (_i < _s.Length && _s[_i] == '(')
                return ErrorToken(start, "'@using (...)' is not supported; write '@using Namespace'");
            int nsBegin = _i;
            while (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || _s[_i] == '_' || _s[_i] == '.'))
                _i++;
            var ns = _s.Substring(nsBegin, _i - nsBegin);
            if (ns.Length == 0 || ns[0] == '.' || ns[ns.Length - 1] == '.' || ns.Contains(".."))
                return ErrorToken(start, "'@using' requires a namespace name: @using System.Collections.Generic");
            return new Token
            {
                Kind = TokenKind.Directive,
                Text = "using",
                Aux = ns,
                Span = new TextSpan(start, _i - start),
                InnerSpan = new TextSpan(nsBegin, _i - nsBegin),
            };
        }

        // @code { ... }（K21，D86）：块体整块捕获（字符串感知），原文透传（K25）
        if (name == "code")
        {
            int j = _i;
            while (j < _s.Length && char.IsWhiteSpace(_s[j])) j++;
            if (j >= _s.Length || _s[j] != '{')
                return ErrorToken(start, "expected '{' after @code: @code { ... }");
            _i = j + 1; // 过 '{'
            int bodyStart = _i;
            int depth = 1;
            while (_i < _s.Length)
            {
                char bc = _s[_i];
                if (bc == '"') { SkipStringInside('"'); continue; }
                if (bc == '\'') { SkipStringInside('\''); continue; }
                if (bc == '{') depth++;
                else if (bc == '}')
                {
                    depth--;
                    if (depth == 0) break;
                }
                _i++;
            }
            if (depth != 0)
                return new Token
                {
                    Kind = TokenKind.Error,
                    Text = "unterminated @code",
                    Aux = "unterminated @code block; add the closing '}'",
                    Span = new TextSpan(start, _s.Length - start),
                };
            var inner = new TextSpan(bodyStart, _i - bodyStart);
            var body = _s.Substring(bodyStart, _i - bodyStart);
            _i++; // 过 '}'
            return new Token
            {
                Kind = TokenKind.Directive,
                Text = "code",
                Aux = body,
                Span = new TextSpan(start, _i - start),
                InnerSpan = inner,
            };
        }

        int j2 = _i;
        while (j2 < _s.Length && char.IsWhiteSpace(_s[j2])) j2++;
        if (j2 < _s.Length && _s[j2] == '(')
        {
            _i = j2;
            _i++; // '('
            int innerStart = _i;
            int depth = 1;
            while (_i < _s.Length)
            {
                char c = _s[_i];
                if (c == '"') { SkipStringInside('"'); continue; }
                if (c == '\'') { SkipStringInside('\''); continue; }
                if (c == '(') depth++;
                else if (c == ')')
                {
                    depth--;
                    if (depth == 0) break;
                }
                _i++;
            }
            if (_i >= _s.Length)
                return new Token { Kind = TokenKind.Error, Text = "unterminated directive", Span = new TextSpan(start, _s.Length - start) };
            var inner = new TextSpan(innerStart, _i - innerStart);
            var aux = _s.Substring(innerStart, _i - innerStart);
            _i++; // ')'
            return new Token
            {
                Kind = TokenKind.Directive,
                Text = name,
                Aux = aux,
                Span = new TextSpan(start, _i - start),
                InnerSpan = inner,
            };
        }

        return new Token { Kind = TokenKind.Directive, Text = name, Span = new TextSpan(start, _i - start) };
    }
}

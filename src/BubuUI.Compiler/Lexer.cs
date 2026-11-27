using System.Text;

namespace BubuUI.Compiler;

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

    /// <summary>K31：<c>@using</c> 别名形态的第二参数（目标类型名）；命名空间形态为 null。</summary>
    public string? Aux2 { get; set; }
    public TextSpan Span { get; set; }
    public TextSpan InnerSpan { get; set; }

    /// <summary>K37：<c>@inject</c> 属性名的 span（类型名在 <see cref="InnerSpan"/>）。</summary>
    public TextSpan InnerSpan2 { get; set; }

    public override string ToString() => $"{Kind} '{Text}'";
}

/// <summary>
/// .bui 词法分析：XML 式标记 + 文本插值 {expr} + 指令 @if/@foreach/@switch/@using/@code。
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
            // 必须推进到文件尾：否则调用方的 Error 分支 Advance 后仍停在同一位置，
            // 解析循环会无限重报同一条 EUI100（文档型死循环，比诊断本身更糟）。
            _i = _s.Length;
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
    /// <summary>
    /// 捕获 <c>{ csharp-expression }</c>（D11）：配对由 <see cref="CSharpSnippet"/> 完成——
    /// 字符串（普通/verbatim/raw/插值）、字符字面量与注释里的 <c>{ }</c> 都不计数。
    /// </summary>
    private Token CaptureExpression()
    {
        int open = _i;
        int close = CSharpSnippet.FindMatchingClose(_s, open);
        if (close < 0)
        {
            _i = _s.Length;
            return new Token { Kind = TokenKind.Error, Text = "unterminated expression", Span = new TextSpan(open, _s.Length - open) };
        }
        var inner = new TextSpan(open + 1, close - open - 1);
        var text = _s.Substring(open + 1, close - open - 1);
        _i = close + 1;
        return new Token
        {
            Kind = TokenKind.Expression,
            Text = text,
            Span = new TextSpan(open, _i - open),
            InnerSpan = inner,
        };
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
        if (!TrySkipTrivia()) { _i = saved; return false; }
        int elseStart = _i;
        if (!MatchWord("else")) { _i = saved; return false; }
        if (!TrySkipTrivia()) { _i = saved; return false; }

        if (_i < _s.Length && _s[_i] == '{')
        {
            token = new Token
            {
                Kind = TokenKind.Directive,
                Text = "else",
                Span = new TextSpan(elseStart, 4),
            };
            return true; // _i 停在 '{' 前，Parser 设 SetNextBraceIsBlock 后 Advance 得 BlockOpen
        }

        if (MatchWord("if") && TrySkipTrivia() && _i < _s.Length && _s[_i] == '(')
        {
            int openParen = _i;
            int innerStart = openParen + 1;
            int close = CSharpSnippet.FindMatchingClose(_s, openParen);
            if (close < 0) { _i = saved; return false; }
            var inner = new TextSpan(innerStart, close - innerStart);
            _i = close + 1;
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

    /// <summary>
    /// K22 / K31：<c>@using</c> 的两种形态。命名空间导入（<c>@using BubuUI.Elements</c>）
    /// 只决定标签可见性并原样进生成文件头；别名（<c>@using Btn = ThirdParty.Controls.Button</c>）
    /// 同时产生一行 C# 别名 using 与本文件内的标签别名（<c>&lt;Btn/&gt;</c>）。
    /// 失败一律返回携带专属消息的 Error token——绝不把残余字符交回文本模式
    /// （那会报成与写法无关的「根层不允许文本」）。
    /// </summary>
    private Token ParseUsingDirective(int start)
    {
        // 只跨行内空白：跳过 '\n' 会把下一行的元素吞进指令（见 DirectiveError 的整行推进）
        while (_i < _s.Length && (_s[_i] == ' ' || _s[_i] == '\t')) _i++;
        if (_i < _s.Length && _s[_i] == '(')
            return DirectiveError(start,
                "'@using (...)' is not supported; write '@using Namespace' or '@using Alias = Namespace.Type'");

        int nameBegin = _i;
        while (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || _s[_i] == '_' || _s[_i] == '.' || _s[_i] == ':'))
            _i++;
        var head = _s.Substring(nameBegin, _i - nameBegin);

        if (head == "static")
            return DirectiveError(start,
                "'@using static' is not supported; import the namespace or alias a single control type");

        // 别名形态：@using Alias = Namespace.Type
        int eq = _i;
        while (eq < _s.Length && (_s[eq] == ' ' || _s[eq] == '\t')) eq++;
        if (eq < _s.Length && _s[eq] == '=')
        {
            if (!IsIdentifier(head))
                return DirectiveError(start,
                    "'@using' alias must be a single identifier: @using Btn = My.Controls.Button");

            _i = eq + 1;
            while (_i < _s.Length && (_s[_i] == ' ' || _s[_i] == '\t')) _i++;
            int targetBegin = _i;
            while (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || _s[_i] == '_' || _s[_i] == '.' || _s[_i] == ':'))
                _i++;
            var target = _s.Substring(targetBegin, _i - targetBegin);
            if (target.Length == 0 || !IsTypeName(target))
                return DirectiveError(start,
                    "'@using' alias requires a type name after '=': @using Btn = My.Controls.Button");
            if (_i < _s.Length && _s[_i] == '<')
                return DirectiveError(start,
                    "generic type aliases are not supported; alias a non-generic control type");
            if (!ReadDirectiveTerminator())
                return DirectiveError(start,
                    "'@using' accepts a namespace name or an alias: '@using My.Controls' / '@using Btn = My.Controls.Button'");

            return new Token
            {
                Kind = TokenKind.Directive,
                Text = "using",
                Aux = head,
                Aux2 = target,
                Span = new TextSpan(start, _i - start),
                InnerSpan = new TextSpan(targetBegin, target.Length),
            };
        }

        // 同一行还有残留（如 '@using Foo Bar'）说明写法有误，就地报错而不是留给文本模式
        if (!ReadDirectiveTerminator())
            return DirectiveError(start,
                "'@using' accepts a namespace name or an alias: '@using My.Controls' / '@using Btn = My.Controls.Button'");

        if (!IsDottedName(head))
            return DirectiveError(start,
                "'@using' requires a namespace name: @using System.Collections.Generic");

        return new Token
        {
            Kind = TokenKind.Directive,
            Text = "using",
            Aux = head,
            Span = new TextSpan(start, _i - start),
            InnerSpan = new TextSpan(nameBegin, head.Length),
        };
    }

    /// <summary>
    /// <c>@namespace Foo.Bar</c>：显式命名空间声明（spec/02 §2.2）。只接受一个点分名，
    /// 写到行尾为止；允许可选的结尾分号（与 <c>@using</c> 同口径），不接受第二个名字。
    /// </summary>
    private Token ParseNamespaceDirective(int start, string directiveName = "namespace")
    {
        while (_i < _s.Length && (_s[_i] == ' ' || _s[_i] == '\t')) _i++;
        int nameBegin = _i;
        while (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || _s[_i] == '_' || _s[_i] == '.'))
            _i++;
        var name = _s.Substring(nameBegin, _i - nameBegin);

        if (name.Length == 0)
            return DirectiveError(start,
                "'@namespace' requires a namespace name: @namespace My.App");

        if (!ReadDirectiveTerminator())
            return DirectiveError(start,
                "'@namespace' accepts a single dotted namespace name: '@namespace My.App'");

        if (!IsDottedName(name))
            return DirectiveError(start,
                "'@namespace' requires a dotted identifier name: @namespace My.App");

        return new Token
        {
            Kind = TokenKind.Directive,
            Text = directiveName,
            Aux = name,
            Span = new TextSpan(start, _i - start),
            InnerSpan = new TextSpan(nameBegin, name.Length),
        };
    }

    /// <summary>
    /// K37（spec/01 §4.18）：<c>@inject TypeName PropertyName</c>。同一行内写完、结尾分号可选
    /// （与 @using/@namespace 同口径）；类型名形态同 @using 别名目标（可带 <c>global::</c>，非泛型），
    /// 属性名是单个 C# 标识符。失败一律返回携带专属消息的 Error token 并吞掉整行
    /// （<see cref="DirectiveError"/> 同款，绝不把残余字符交回文本模式）。
    /// </summary>
    private Token ParseInjectDirective(int start)
    {
        const string usage = "'@inject' requires a type name and a property name: '@inject My.Services.ICache Cache'";

        // 只跨行内空白：跳过 '\n' 会把下一行的元素吞进指令（见 DirectiveError 的整行推进）
        while (_i < _s.Length && (_s[_i] == ' ' || _s[_i] == '\t')) _i++;
        int typeBegin = _i;
        while (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || _s[_i] == '_' || _s[_i] == '.' || _s[_i] == ':'))
            _i++;
        var type = _s.Substring(typeBegin, _i - typeBegin);
        if (type.Length == 0 || !IsTypeName(type))
            return DirectiveError(start, usage);
        if (_i < _s.Length && _s[_i] == '<')
            return DirectiveError(start,
                "generic type names are not supported in '@inject'; inject a non-generic service interface");

        // 属性名必须以至少一个空白与类型名分隔（'@inject IFoo' 单参数是常见漏写）
        if (_i >= _s.Length || (_s[_i] != ' ' && _s[_i] != '\t'))
            return DirectiveError(start, usage);
        while (_i < _s.Length && (_s[_i] == ' ' || _s[_i] == '\t')) _i++;

        int nameBegin = _i;
        while (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || _s[_i] == '_'))
            _i++;
        var name = _s.Substring(nameBegin, _i - nameBegin);
        if (!IsIdentifier(name))
            return DirectiveError(start, usage);

        if (!ReadDirectiveTerminator())
            return DirectiveError(start, usage);

        return new Token
        {
            Kind = TokenKind.Directive,
            Text = "inject",
            Aux = type,
            Aux2 = name,
            Span = new TextSpan(start, _i - start),
            InnerSpan = new TextSpan(typeBegin, type.Length),
            InnerSpan2 = new TextSpan(nameBegin, name.Length),
        };
    }

    /// <summary>Line directives accept one optional semicolon, with horizontal whitespace around it.</summary>
    private bool ReadDirectiveTerminator()
    {
        while (_i < _s.Length && (_s[_i] == ' ' || _s[_i] == '\t')) _i++;
        if (_i < _s.Length && _s[_i] == ';') _i++;
        while (_i < _s.Length && (_s[_i] == ' ' || _s[_i] == '\t')) _i++;
        return _i == _s.Length || _s[_i] is '\r' or '\n';
    }

    /// <summary>
    /// 指令的错误出口：**先把词法位置推到本行行尾**再报错。否则残余字符
    /// （<c>static System.Math</c> 的右半、泛型别名的 <c>&lt;string&gt;</c>）会落进文本/标记模式，
    /// 叠加出 EUI006、「missing &lt;/string&gt;」这类与写法无关的级联错误。
    /// </summary>
    private Token DirectiveError(int start, string message)
    {
        while (_i < _s.Length && _s[_i] != '\n') _i++;
        return ErrorToken(start, message, System.Math.Max(_i, start + 1));
    }

    /// <summary>K31：单段 C# 标识符（字母/下划线开头，仅字母数字下划线）。</summary>
    private static bool IsIdentifier(string text)
    {
        if (text.Length == 0) return false;
        if (!(char.IsLetter(text[0]) || text[0] == '_')) return false;
        foreach (var c in text)
            if (!(char.IsLetterOrDigit(c) || c == '_')) return false;
        return true;
    }

    /// <summary>K31：点分名称，每段都是标识符（命名空间，或非嵌套类型名）。</summary>
    private static bool IsDottedName(string text)
    {
        if (text.Length == 0) return false;
        foreach (var part in text.Split('.'))
            if (!IsIdentifier(part)) return false;
        return true;
    }

    /// <summary>K31：类型名，允许可选的 <c>global::</c> 前缀；嵌套类型用 <c>Outer.Inner</c>。</summary>
    private static bool IsTypeName(string text)
    {
        const string globalPrefix = "global::";
        return IsDottedName(text.StartsWith(globalPrefix, System.StringComparison.Ordinal)
            ? text.Substring(globalPrefix.Length)
            : text);
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

        // @using {Namespace}（K22，D85）与 @using Alias = Namespace.Type（K31）：
        // 都允许可选的结尾分号；不做 @using(...) / @using static / 泛型别名。
        if (name == "using")
            return ParseUsingDirective(start);

        // @namespace {Dotted.Name}（spec/02 §2.2）：显式声明生成类的命名空间。必填，
        // 且必须与同名 code-behind 的命名空间一致（生成器侧校验）。与 @using 同口径——结尾分号可选。
        if (name == "namespace")
            return ParseNamespaceDirective(start);
        if (name == "template")
            return ParseNamespaceDirective(start, "template");

        // @inject {TypeName} {PropertyName}（K37，spec/01 §4.18）：服务注入声明。单行、
        // 结尾分号可选；类型名形态与 @using 别名目标同口径（可带 global::，非泛型）。
        if (name == "inject")
            return ParseInjectDirective(start);

        // @code { ... }（K21，D86）与 @style { ... }（E1，design/17 §4.8）：块体整块捕获
        //（C# 感知的括号匹配——注释/字符串安全，样式块的 /* */ 注释同样覆盖），原文透传。
        if (name == "code" || name == "style")
        {
            int j = _i;
            while (j < _s.Length && char.IsWhiteSpace(_s[j])) j++;
            if (j >= _s.Length || _s[j] != '{')
                return ErrorToken(start, $"expected '{{' after @{name}: @{name} {{ ... }}");
            int bodyStart = j + 1;
            int close = CSharpSnippet.FindMatchingClose(_s, j);
            if (close < 0)
            {
                _i = _s.Length;
                return new Token
                {
                    Kind = TokenKind.Error,
                    Text = $"unterminated @{name}",
                    Aux = $"unterminated @{name} block; add the closing '}}'",
                    Span = new TextSpan(start, _s.Length - start),
                };
            }
            var inner = new TextSpan(bodyStart, close - bodyStart);
            var body = _s.Substring(bodyStart, close - bodyStart);
            _i = close + 1;
            return new Token
            {
                Kind = TokenKind.Directive,
                Text = name,
                Aux = body,
                Span = new TextSpan(start, _i - start),
                InnerSpan = inner,
            };
        }

        int j2 = _i;
        while (j2 < _s.Length && char.IsWhiteSpace(_s[j2])) j2++;
        if (j2 < _s.Length && _s[j2] == '(')
        {
            int innerStart = j2 + 1;
            int close = CSharpSnippet.FindMatchingClose(_s, j2);
            if (close < 0)
            {
                _i = _s.Length;
                return new Token { Kind = TokenKind.Error, Text = "unterminated directive", Span = new TextSpan(start, _s.Length - start) };
            }
            var inner = new TextSpan(innerStart, close - innerStart);
            var aux = _s.Substring(innerStart, close - innerStart);
            _i = close + 1;
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

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
/// .ui 词法分析：XML 式标记 + 文本插值 {expr} + 指令 @if/@foreach。
/// 表达式按平衡大括号捕获（字符串字面量感知），lambda 的 '>' 不会截断。
/// </summary>
public sealed class Lexer
{
    private readonly string _s;
    private int _i;
    private bool _inMarkup;
    private bool _nextBraceIsBlock;
    private readonly StringBuilder _buf = new();
    private int _bufStart;
    private Token? _pending;

    public Lexer(string s) => _s = s;

    /// <summary>指令括号后的下一个 '{' 是块结构而非文本插值（由 Parser 调用）。</summary>
    public void SetNextBraceIsBlock() => _nextBraceIsBlock = true;

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

                if (c == '<')
                {
                    if (_buf.Length > 0) return FlushText();
                    if (IsCommentStart()) { SkipComment(); continue; }
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
                    var directive = ParseDirective();
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

    private void SkipComment()
    {
        int end = _s.IndexOf("-->", _i, System.StringComparison.Ordinal);
        _i = end < 0 ? _s.Length : end + 3;
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

    private Token ParseDirective()
    {
        int start = _i;
        _i++; // '@'
        int nameStart = _i;
        while (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || _s[_i] == '_'))
            _i++;
        var name = _s.Substring(nameStart, _i - nameStart);

        int j = _i;
        while (j < _s.Length && char.IsWhiteSpace(_s[j])) j++;
        if (j < _s.Length && _s[j] == '(')
        {
            _i = j;
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

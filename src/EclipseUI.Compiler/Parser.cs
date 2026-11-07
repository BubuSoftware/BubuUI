using System;
using System.Collections.Generic;
using System.Linq;

namespace EclipseUI.Compiler;

/// <summary>递归下降解析 .ui 模板：元素（XML 式闭合）、文本插值、@if/@foreach 块。</summary>
public sealed class Parser
{
    private enum Terminator { Document, BlockEnd }

    private readonly Lexer _lexer;
    private readonly string _text;
    private readonly List<UiDiagnostic> _diags;
    private Token _tok;
    private string _lastEndTag = "";

    public Parser(string text, List<UiDiagnostic> diags)
    {
        _text = text;
        _lexer = new Lexer(text);
        _diags = diags;
        _tok = _lexer.Next();
    }

    public UiDocument ParseDocument(string fileName)
    {
        var nodes = ParseNodeList(Terminator.Document, null);
        NormalizeChildren(nodes);
        return new UiDocument(fileName, _text, nodes);
    }

    private List<UiNode> ParseNodeList(Terminator terminator, string? endTag)
    {
        var list = new List<UiNode>();
        while (true)
        {
            switch (_tok.Kind)
            {
                case TokenKind.EndOfFile:
                    if (terminator == Terminator.BlockEnd)
                        Diag(UiDiagnostics.Syntax, "unexpected end of file; missing '}'", _tok.Span);
                    else if (endTag is not null)
                        Diag(UiDiagnostics.Syntax, $"unexpected end of file; missing </{endTag}>", _tok.Span);
                    return list;

                case TokenKind.TagOpen:
                    list.Add(ParseElement());
                    break;

                case TokenKind.EndTagOpen:
                    {
                        var openSpan = _tok.Span;
                        Advance();
                        if (_tok.Kind != TokenKind.Identifier)
                        {
                            Diag(UiDiagnostics.Syntax, "expected tag name after '</'", _tok.Span);
                            return list;
                        }
                        var name = _tok.Text;
                        _lastEndTag = name;
                        Advance();
                        if (_tok.Kind != TokenKind.TagClose)
                            Diag(UiDiagnostics.Syntax, "expected '>' after closing tag name", _tok.Span);
                        else
                            Advance();

                        if (endTag is null)
                            Diag(UiDiagnostics.Syntax, $"unexpected closing tag </{name}>", openSpan);
                        else
                            return list;
                        break;
                    }

                case TokenKind.Text:
                case TokenKind.Expression:
                    list.Add(ParseTextRun());
                    break;

                case TokenKind.Directive:
                    ParseDirectiveInto(list);
                    break;

                case TokenKind.BlockClose:
                    if (terminator == Terminator.BlockEnd) return list;
                    Diag(UiDiagnostics.Syntax, "unexpected '}'", _tok.Span);
                    Advance();
                    break;

                case TokenKind.Error:
                    Diag(UiDiagnostics.Syntax, _tok.Aux ?? $"unexpected input: {_tok.Text}", _tok.Span);
                    Advance();
                    break;

                default:
                    Diag(UiDiagnostics.Syntax, $"unexpected token {_tok.Kind}", _tok.Span);
                    Advance();
                    break;
            }
        }
    }

    private UiText ParseTextRun()
    {
        var node = new UiText();
        int start = -1, end = 0;
        while (_tok.Kind == TokenKind.Text || _tok.Kind == TokenKind.Expression)
        {
            if (start < 0) start = _tok.Span.Start;
            end = _tok.Span.End;
            if (_tok.Kind == TokenKind.Text)
            {
                if (_tok.Text.Length > 0)
                    node.Segments.Add(TextSegment.Static(_tok.Text, _tok.Span));
            }
            else
            {
                node.Segments.Add(TextSegment.Expr(_tok.Text, _tok.InnerSpan));
            }
            Advance();
        }
        if (start >= 0) node.Span = TextSpan.FromBounds(start, end);
        return node;
    }

    private UiNode ParseElement()
    {
        var element = ParseElementCore();
        // 保留标签名（spec/01 §3.1 / §6.2）：`<Slot>`/`<ContentSlot>` 不是普通元素——不再依赖
        // 「目录无同名控件」的口径，解析器直接特判成槽节点；目录把占用这两个名字的标签报保留冲突。
        return element.Tag switch
        {
            "Slot" or "ContentSlot" => CreateSlotNode(element),
            _ => element,
        };
    }

    private static UiNode CreateSlotNode(UiElement element)
    {
        var nameAttr = element.Attributes.FirstOrDefault(a => a.Name == "Name" && !a.IsBind && !a.IsLegacy);
        if (element.Tag == "Slot")
        {
            var fill = new UiSlotNode { Span = element.Span, Children = element.Children };
            fill.Attributes.AddRange(element.Attributes);
            NormalizeChildren(fill.Children);
            return fill;
        }

        var host = new UiContentSlotNode { Span = element.Span, HasChildren = element.Children.Count > 0 };
        host.Attributes.AddRange(element.Attributes);
        return host;
    }

    private UiElement ParseElementCore()
    {
        var open = Advance(); // 消费 '<'
        if (_tok.Kind != TokenKind.Identifier)
        {
            Diag(UiDiagnostics.Syntax, "expected tag name after '<'", _tok.Span);
            Advance();
            return new UiElement { Tag = "?", Span = open.Span };
        }

        var element = new UiElement { Tag = _tok.Text, TagSpan = _tok.Span, Span = _tok.Span };
        Advance();

        while (true)
        {
            if (_tok.Kind == TokenKind.Identifier)
            {
                var nameTok = _tok;
                Advance();
                var attr = new UiAttribute { NameSpan = nameTok.Span };
                var raw = nameTok.Text;
                if (raw.StartsWith("@bind-", StringComparison.Ordinal))
                {
                    var name = raw.Substring("@bind-".Length);
                    int colon = name.IndexOf(':');
                    if (colon >= 0)
                    {
                        Diag(UiDiagnostics.Syntax,
                            $"'{raw}' is not an attribute name; '@bind-P' takes no legacy modifier, write @bind-{name.Substring(0, colon)}",
                            nameTok.Span);
                        name = name.Substring(0, colon);
                    }
                    attr.Name = name;
                    attr.BindForm = UiBindForm.TwoWay;
                }
                else if (raw.EndsWith(":bind", StringComparison.Ordinal))
                {
                    attr.Name = raw.Substring(0, raw.Length - 5);
                    attr.BindForm = UiBindForm.LegacyTwoWay;
                }
                else if (raw.EndsWith(":value", StringComparison.Ordinal))
                {
                    attr.Name = raw.Substring(0, raw.Length - 6);
                    attr.BindForm = UiBindForm.LegacyValue;
                }
                else
                {
                    attr.Name = raw;
                }

                if (_tok.Kind != TokenKind.Equals)
                {
                    Diag(UiDiagnostics.Syntax, $"expected '=' after attribute '{raw}'", nameTok.Span);
                    continue;
                }
                Advance(); // '='

                if (_tok.Kind == TokenKind.String)
                {
                    attr.Kind = AttrValueKind.Literal;
                    attr.Value = _tok.Text;
                    attr.ValueSpan = _tok.InnerSpan;
                    Advance();
                }
                else if (_tok.Kind == TokenKind.Expression)
                {
                    attr.Kind = AttrValueKind.Expression;
                    attr.Value = _tok.Text;
                    attr.ValueSpan = _tok.InnerSpan;
                    Advance();
                }
                else
                {
                    Diag(UiDiagnostics.Syntax, $"expected attribute value for '{raw}'", _tok.Span);
                    attr.Kind = AttrValueKind.Literal;
                    attr.Value = "";
                }
                element.Attributes.Add(attr);
                continue;
            }

            if (_tok.Kind == TokenKind.SelfClose)
            {
                Advance();
                element.SelfClosing = true;
                return element;
            }

            if (_tok.Kind == TokenKind.TagClose)
            {
                Advance();
                _lastEndTag = "";
                element.Children = ParseNodeList(Terminator.Document, element.Tag);
                NormalizeChildren(element.Children);
                if (_lastEndTag.Length == 0)
                    Diag(UiDiagnostics.Syntax, $"missing </{element.Tag}>", element.TagSpan);
                else if (!string.Equals(_lastEndTag, element.Tag, StringComparison.Ordinal))
                    Diag(UiDiagnostics.Syntax, $"mismatched closing tag: expected </{element.Tag}>, got </{_lastEndTag}>", element.TagSpan);
                return element;
            }

            if (_tok.Kind == TokenKind.Error)
            {
                Diag(UiDiagnostics.Syntax, _tok.Aux ?? $"unexpected input: {_tok.Text}", _tok.Span);
                Advance();
                continue;
            }

            Diag(UiDiagnostics.Syntax, $"unexpected token {_tok.Kind} in tag", _tok.Span);
            if (_tok.Kind == TokenKind.EndOfFile) return element;
            Advance();
        }
    }

    private void ParseDirectiveInto(List<UiNode> list)
    {
        var directive = _tok; // 不在此处 Advance：块指令要先设 lexer 标志再取下一个 token

        switch (directive.Text)
        {
            case "if":
                ParseIfInto(list, directive);
                break;
            case "foreach":
                ParseForeachInto(list, directive);
                break;
            case "switch":
                Advance();
                _diags.Add(UiDiagnostics.Error(UiDiagnostics.SwitchNotSupported,
                    "@switch is not supported yet; use @if chains", directive.Span));
                break;
            default:
                Advance();
                _diags.Add(UiDiagnostics.Error(UiDiagnostics.UnknownDirective,
                    $"unknown directive '@{directive.Text}'", directive.Span));
                break;
        }
    }

    private void ParseIfInto(List<UiNode> list, Token directive)
    {
        if (string.IsNullOrWhiteSpace(directive.Aux))
        {
            Diag(UiDiagnostics.Syntax, "@if requires a condition: @if (condition) { ... }", directive.Span);
            Advance();
            return;
        }
        _lexer.SetNextBraceIsBlock();
        Advance(); // 标志已设置：指令后的空白被跳过，_tok = BlockOpen
        if (_tok.Kind != TokenKind.BlockOpen)
        {
            Diag(UiDiagnostics.Syntax, "expected '{' after @if condition", _tok.Span);
            return;
        }
        Advance();
        var children = ParseNodeList(Terminator.BlockEnd, null);
        if (_tok.Kind == TokenKind.BlockClose) Advance();
        else Diag(UiDiagnostics.Syntax, "expected '}' to close @if block", _tok.Span);
        NormalizeChildren(children);
        list.Add(new UiIf
        {
            Condition = directive.Aux!,
            ConditionSpan = directive.InnerSpan,
            Children = children,
            Span = directive.Span,
        });
    }

    private void ParseForeachInto(List<UiNode> list, Token directive)
    {
        // Tokenizer 切片：以深度/字符串感知的扫描替代正则（spec/01 §4.11 冻结形态
        // `var item in collection`）。集合里带引号的 " in "（旧正则会误切分）与括号内的
        // `in`（如方法实参修饰符）不再撕裂集合；解构/类型化条目按冻结规则明确拒绝（EUI100）。
        var text = directive.Aux ?? "";
        var header = ScanForeachHeader(text);
        if (!header.Ok)
        {
            Diag(UiDiagnostics.Syntax, header.Reason, directive.Span);
            Advance();
            return;
        }
        _lexer.SetNextBraceIsBlock();
        Advance(); // 标志已设置：指令后的空白被跳过，_tok = BlockOpen
        if (_tok.Kind != TokenKind.BlockOpen)
        {
            Diag(UiDiagnostics.Syntax, "expected '{' after @foreach declaration", _tok.Span);
            return;
        }
        Advance();
        var children = ParseNodeList(Terminator.BlockEnd, null);
        if (_tok.Kind == TokenKind.BlockClose) Advance();
        else Diag(UiDiagnostics.Syntax, "expected '}' to close @foreach block", _tok.Span);
        NormalizeChildren(children);
        list.Add(new UiForeach
        {
            VarName = header.VarName,
            Collection = header.Collection,
            CollectionSpan = directive.InnerSpan,
            Children = children,
            Span = directive.Span,
        });
    }

    private readonly struct ForeachHeader
    {
        public bool Ok { get; init; }
        public string Reason { get; init; }
        public string VarName { get; init; }
        public string Collection { get; init; }
    }

    /// <summary>
    /// 扫描 <c>var ident in collection</c>：首个词是 <c>var</c>、简单标识符、深度为 0 且不在
    /// 字符串内的 <c>in</c> 分隔符、其后非空集合表达式。其余形态给出针对性原因（解构/类型化）。
    /// </summary>
    private static ForeachHeader ScanForeachHeader(string text)
    {
        const string generic = "@foreach requires (var item in collection) { ... }";

        var i = 0;
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;

        if (i < text.Length && text[i] == '(')
            return new ForeachHeader { Ok = false, Reason = "@foreach does not support deconstruction items; " + generic };

        var wordStart = i;
        while (i < text.Length && IsWordChar(text[i])) i++;
        var first = text.Substring(wordStart, i - wordStart);

        if (first != "var")
            return new ForeachHeader
            {
                Ok = false,
                Reason = first.Length == 0
                    ? generic
                    : $"'{first}' is a typed item; @foreach does not support typed items (use 'var'); {generic}",
            };

        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        if (i < text.Length && text[i] == '(')
            return new ForeachHeader { Ok = false, Reason = "@foreach does not support deconstruction items; " + generic };

        var nameStart = i;
        while (i < text.Length && IsWordChar(text[i])) i++;
        if (nameStart == i) return new ForeachHeader { Ok = false, Reason = generic };
        var varName = text.Substring(nameStart, i - nameStart);

        // 分隔符：深度 0、不在字符串内、有词边界的 `in`；其后非空文本即集合表达式。
        var depth = 0;
        while (i < text.Length)
        {
            switch (text[i])
            {
                case '"' or '\'':
                    i = SkipQuoted(text, i);
                    continue;
                case '(' or '[' or '{':
                    depth++;
                    break;
                case ')' or ']' or '}':
                    if (depth > 0) { depth--; break; }
                    return new ForeachHeader { Ok = false, Reason = generic }; // 集合在 'in' 前已完结
            }
            if (text[i] == 'i' && depth == 0 && IsInKeyword(text, i))
            {
                i += 2;
                while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
                if (i >= text.Length)
                    return new ForeachHeader { Ok = false, Reason = generic }; // 'in' 后无集合
                return new ForeachHeader
                {
                    Ok = true,
                    VarName = varName,
                    Collection = text.Substring(i).Trim(),
                };
            }
            i++;
        }
        return new ForeachHeader { Ok = false, Reason = generic };
    }

    private static bool PeekParen(string text, int i)
    {
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        return i < text.Length && text[i] == '(';
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static bool IsInKeyword(string text, int i) =>
        i + 2 <= text.Length &&
        text[i + 1] == 'n' &&
        (i == 0 || char.IsWhiteSpace(text[i - 1])) &&
        (i + 2 == text.Length || char.IsWhiteSpace(text[i + 2]));

    private static int SkipQuoted(string text, int i)
    {
        var quote = text[i];
        i++;
        while (i < text.Length)
        {
            if (text[i] == '\\') { i += 2; continue; }
            if (text[i] == quote) return i + 1;
            i++;
        }
        return i;
    }

    /// <summary>去头尾空白、丢弃纯空白文本节点（与 XAML/JSX 直觉一致）。</summary>
    internal static void NormalizeChildren(List<UiNode> list)
    {
        if (list.Count == 0) return;
        if (list[0] is UiText first) TrimStart(first);
        if (list[list.Count - 1] is UiText last) TrimEnd(last);
        list.RemoveAll(n => n is UiText text && IsBlank(text));
    }

    private static bool IsBlank(UiText text)
    {
        foreach (var segment in text.Segments)
            if (segment.Kind == TextSegmentKind.Expression || !string.IsNullOrWhiteSpace(segment.Text))
                return false;
        return true;
    }

    private static void TrimStart(UiText text)
    {
        if (text.Segments.Count == 0) return;
        var first = text.Segments[0];
        if (first.Kind != TextSegmentKind.Static) return;
        first.Text = first.Text.TrimStart();
    }

    private static void TrimEnd(UiText text)
    {
        if (text.Segments.Count == 0) return;
        var last = text.Segments[text.Segments.Count - 1];
        if (last.Kind != TextSegmentKind.Static) return;
        last.Text = last.Text.TrimEnd();
    }

    private Token Advance()
    {
        var current = _tok;
        _tok = _lexer.Next();
        return current;
    }

    private void Diag(string code, string message, TextSpan span)
        => _diags.Add(UiDiagnostics.Error(code, message, span));
}

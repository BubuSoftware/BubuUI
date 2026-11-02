using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

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

    private UiElement ParseElement()
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
        var match = Regex.Match(directive.Aux ?? "", @"^\s*var\s+([A-Za-z_]\w*)\s+in\s+(.+?)\s*$", RegexOptions.Singleline);
        if (!match.Success)
        {
            Diag(UiDiagnostics.Syntax, "@foreach requires (var item in collection) { ... }", directive.Span);
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
            VarName = match.Groups[1].Value,
            Collection = match.Groups[2].Value,
            CollectionSpan = directive.InnerSpan,
            Children = children,
            Span = directive.Span,
        });
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

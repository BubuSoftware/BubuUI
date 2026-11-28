using System;
using System.Collections.Generic;

namespace BubuUI.Compiler;

/// <summary>递归下降解析 .bui 模板：元素（XML 式闭合）、文本插值、@if/@foreach 块。</summary>
public sealed class Parser
{
    private enum Terminator { Document, BlockEnd }

    private readonly Lexer _lexer;
    private readonly string _text;
    private readonly List<UiDiagnostic> _diags;
    private Token _tok;
    private string _lastEndTag = "";
    private int _switchDepth;
    private string? _namespace;
    private TextSpan _namespaceSpan;
    private bool _namespaceSeen;
    private string? _templateTarget;
    private TextSpan _templateTargetSpan;

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
        return new UiDocument(fileName, _text, nodes, _namespace, _namespaceSpan)
        { TemplateTarget = _templateTarget, TemplateTargetSpan = _templateTargetSpan };
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
                    // K29 ③：@switch case 体内嵌套块里的 break 不是合法内容（块无「跳出」语义）
                    if (_tok.Kind == TokenKind.Text && _switchDepth > 0 && _tok.Text.Trim() == "break;")
                    {
                        Diag(UiDiagnostics.Syntax,
                            "'break' is not allowed inside a nested block; it belongs to the enclosing @switch case",
                            _tok.Span);
                        Advance();
                        break;
                    }
                    list.Add(ParseTextRun());
                    break;

                case TokenKind.Directive:
                    ParseDirectiveInto(list, terminator == Terminator.Document);
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

    private void ParseDirectiveInto(List<UiNode> list, bool topLevel)
    {
        var directive = _tok; // 不在此处 Advance：块指令要先设 lexer 标志再取下一个 token

        switch (directive.Text)
        {
            case "template":
                Advance();
                if (!topLevel || _templateTarget is not null)
                    Diag(UiDiagnostics.Syntax, "@template must occur once at document level", directive.Span);
                else { _templateTarget = directive.Aux; _templateTargetSpan = directive.InnerSpan; }
                break;
            // spec/02 §2.2：@namespace 必填、只能出现在顶层、只能一个。解析层只负责收集与
            // 形状校验；「缺失」与「与 code-behind 不一致」由生成器报（EUI055/EUI056），
            // 这样内存态解析（IDE 的 Tier1、单测内联模板）不会被强制。
            case "namespace":
                Advance();
                if (!topLevel)
                {
                    Diag(UiDiagnostics.Syntax,
                        "'@namespace' must be declared at the top level of the template", directive.Span);
                    break;
                }
                if (string.IsNullOrWhiteSpace(directive.Aux))
                {
                    Diag(UiDiagnostics.Syntax,
                        "'@namespace' requires a namespace name: @namespace My.App", directive.Span);
                    break;
                }
                if (_namespaceSeen)
                {
                    Diag(UiDiagnostics.Syntax,
                        "duplicate '@namespace' declaration; a template may declare it only once", directive.Span);
                    break;
                }
                _namespaceSeen = true;
                _namespace = directive.Aux!.Trim();
                _namespaceSpan = directive.InnerSpan;
                break;
            case "if":
                ParseIfInto(list, directive);
                break;
            case "foreach":
                ParseForeachInto(list, directive);
                break;
            case "switch":
                ParseSwitchInto(list, directive);
                break;
            case "using":
                Advance();
                if (directive.Aux2 is { } target)
                {
                    // K31：别名形态——Namespace 留空，避免混进 CollectImports 的可见性集合
                    list.Add(new UiUsingDirective
                    {
                        Alias = directive.Aux!.Trim(),
                        AliasTarget = target,
                        NamespaceSpan = directive.InnerSpan,
                        Span = directive.Span,
                    });
                }
                else if (string.IsNullOrWhiteSpace(directive.Aux))
                    Diag(UiDiagnostics.Syntax,
                        "'@using' requires a namespace name: @using System.Collections.Generic", directive.Span);
                else
                    list.Add(new UiUsingDirective
                    {
                        Namespace = directive.Aux!.Trim(),
                        NamespaceSpan = directive.InnerSpan,
                        Span = directive.Span,
                    });
                break;
            case "inject":
                // K37（spec/01 §4.18）：服务注入声明。位置校验与 @code/@using 同口径——
                // 非顶层由 TemplateAnalyzer.Visit 报（EUI100），这里只收集。
                Advance();
                if (directive.Aux is null || directive.Aux2 is null)
                    break; // 词法层已报专属错误
                list.Add(new UiInjectDirective
                {
                    TypeName = directive.Aux,
                    TypeNameSpan = directive.InnerSpan,
                    PropertyName = directive.Aux2,
                    PropertyNameSpan = directive.InnerSpan2,
                    Span = directive.Span,
                });
                break;
            case "code":
                Advance();
                if (directive.Aux is null)
                    Diag(UiDiagnostics.Syntax, "expected '{' after @code: @code { ... }", directive.Span);
                else
                    list.Add(new UiCodeBlock
                    {
                        Code = directive.Aux,
                        CodeSpan = directive.InnerSpan,
                        Span = directive.Span,
                    });
                break;
            case "style":
                Advance();
                if (directive.Aux is null)
                    Diag(UiDiagnostics.Syntax, "expected '{' after @style: @style { ... }", directive.Span);
                else
                    list.Add(new UiStyleBlock
                    {
                        Code = directive.Aux,
                        CodeSpan = directive.InnerSpan,
                        Span = directive.Span,
                    });
                break;
            case "else" or "else if":
                Advance();
                Diag(UiDiagnostics.Syntax,
                    $"'{directive.Text}' must directly follow an @if or else-if block", directive.Span);
                break;
            case "for" or "while" or "do":
                ParseLoopInto(list, directive);
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
        if (_tok.Kind == TokenKind.BlockClose)
        {
            // else/else if 续接（K23 ③）：在消费 '}' 取下一 token 前开启一次匹配机会
            _lexer.EnableElseContinuation();
            Advance();
        }
        else Diag(UiDiagnostics.Syntax, "expected '}' to close @if block", _tok.Span);
        NormalizeChildren(children);
        var ifNode = new UiIf
        {
            Condition = directive.Aux!,
            ConditionSpan = directive.InnerSpan,
            Children = children,
            Span = directive.Span,
        };
        ParseElseChain(ifNode);
        list.Add(ifNode);
    }

    /// <summary>
    /// else / else if 续接链（K23 ③）：词法在 <see cref="Lexer.EnableElseContinuation"/> 开启后
    /// 把紧接前一分支 '}' 的裸 <c>else</c>/<c>else if</c> 产出为指令 token；else 块结束后不再续接。
    /// </summary>
    private void ParseElseChain(UiIf ifNode)
    {
        while (_tok.Kind == TokenKind.Directive && _tok.Text is "else" or "else if")
        {
            var head = _tok;
            if (head.Text == "else if")
            {
                if (string.IsNullOrWhiteSpace(head.Aux))
                {
                    Diag(UiDiagnostics.Syntax, "else if requires a condition: else if (condition) { ... }", head.Span);
                    Advance();
                    return;
                }
                var elseIf = new UiElseIf
                {
                    Condition = head.Aux!,
                    ConditionSpan = head.InnerSpan,
                    Span = head.Span,
                };
                _lexer.SetNextBraceIsBlock();
                Advance(); // _tok = BlockOpen
                if (_tok.Kind != TokenKind.BlockOpen)
                {
                    Diag(UiDiagnostics.Syntax, "expected '{' after else if condition", _tok.Span);
                    return;
                }
                Advance();
                var children = ParseNodeList(Terminator.BlockEnd, null);
                if (_tok.Kind == TokenKind.BlockClose)
                {
                    _lexer.EnableElseContinuation();
                    Advance();
                }
                else Diag(UiDiagnostics.Syntax, "expected '}' to close else if block", _tok.Span);
                NormalizeChildren(children);
                elseIf.Children = children;
                ifNode.ElseIfs.Add(elseIf);
                continue;
            }

            // else：终局分支，之后不允许再续接
            _lexer.SetNextBraceIsBlock();
            Advance(); // _tok = BlockOpen
            if (_tok.Kind != TokenKind.BlockOpen)
            {
                Diag(UiDiagnostics.Syntax, "expected '{' after else", _tok.Span);
                return;
            }
            Advance();
            var elseChildren = ParseNodeList(Terminator.BlockEnd, null);
            if (_tok.Kind == TokenKind.BlockClose) Advance();
            else Diag(UiDiagnostics.Syntax, "expected '}' to close else block", _tok.Span);
            NormalizeChildren(elseChildren);
            ifNode.ElseChildren = elseChildren;
            return;
        }
    }

    private void ParseLoopInto(List<UiNode> list, Token directive)
    {
        if (directive.Text != "do" && string.IsNullOrWhiteSpace(directive.Aux))
        {
            Diag(UiDiagnostics.Syntax, $"@{directive.Text} requires a parenthesized header", directive.Span);
            Advance();
            return;
        }
        if (directive.Text == "do" && directive.Aux is not null)
            Diag(UiDiagnostics.Syntax, "@do takes its condition after the body: @do { ... } while (condition);", directive.Span);
        _lexer.SetNextBraceIsBlock();
        Advance();
        if (_tok.Kind != TokenKind.BlockOpen)
        {
            Diag(UiDiagnostics.Syntax, $"expected '{{' after @{directive.Text}", _tok.Span);
            return;
        }
        Advance();
        var children = ParseNodeList(Terminator.BlockEnd, null);
        var end = _tok.Span.Start + _tok.Span.Length;
        if (_tok.Kind == TokenKind.BlockClose)
        {
            if (directive.Text == "do") _lexer.EnableDoContinuation();
            Advance();
        }
        else Diag(UiDiagnostics.Syntax, $"expected '}}' to close @{directive.Text} block", _tok.Span);
        var header = directive;
        if (directive.Text == "do")
        {
            if (_tok.Kind != TokenKind.Directive || _tok.Text != "do while")
            {
                Diag(UiDiagnostics.Syntax, "expected 'while (condition);' after @do body", _tok.Span);
                return;
            }
            header = _tok;
            end = header.Span.Start + header.Span.Length;
            if (string.IsNullOrWhiteSpace(header.Aux))
                Diag(UiDiagnostics.Syntax, "@do requires a nonempty while condition", header.Span);
            Advance();
        }
        NormalizeChildren(children);
        list.Add(new UiLoop
        {
            Kind = directive.Text,
            Header = header.Aux ?? "",
            HeaderSpan = header.InnerSpan,
            Children = children,
            Span = new TextSpan(directive.Span.Start, end - directive.Span.Start),
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

    /// <summary>
    /// <c>@switch (value) { case CONST: … break; default: … break; }</c>（K23 ③/K29，D87）。
    /// case/default 头、<c>break;</c> 与 case 体文本都以裸文本形态出现在词法流里
    /// （文本模式累积到 '&lt;'/'@'/'}' 停），由 <see cref="SplitSwitchText"/> 按片段拆开；
    /// 一个 Text token 可能同时含「break; + 下一个 case 头」，不能按整段比对
    /// （K29 ③：非空 section 必须以 break; 结尾，无贯穿语义）。
    /// </summary>
    private void ParseSwitchInto(List<UiNode> list, Token directive)
    {
        if (string.IsNullOrWhiteSpace(directive.Aux))
        {
            Diag(UiDiagnostics.Syntax,
                "@switch requires a value: @switch (expression) { case value: ... break; default: ... break; }",
                directive.Span);
            Advance();
            return;
        }
        _lexer.SetNextBraceIsBlock();
        Advance(); // _tok = BlockOpen
        if (_tok.Kind != TokenKind.BlockOpen)
        {
            Diag(UiDiagnostics.Syntax, "expected '{' after @switch value", _tok.Span);
            return;
        }
        Advance();

        var switchNode = new UiSwitch { Value = directive.Aux!, ValueSpan = directive.InnerSpan, Span = directive.Span };
        bool sawDefault = false;
        UiSwitchSection? current = null;
        bool closed = false;

        _switchDepth++;
        try
        {
            while (true)
            {
                if (_tok.Kind == TokenKind.EndOfFile)
                {
                    Diag(UiDiagnostics.Syntax, "expected '}' to close @switch block", _tok.Span);
                    break;
                }
                if (_tok.Kind == TokenKind.BlockClose)
                {
                    Advance();
                    closed = true;
                    break;
                }

                // case 体顶层的裸文本：拆成「section 头 / 独立 break; / 普通文本」三类片段。
                // 词法层把 `break;` 与后面紧跟的 `case 2:` 累成同一个 Text token，
                // 所以不能要求整段 trim 后恰好是 `break;`（K29 ③，否则多分支必然落成文本节点）。
                if (_tok.Kind == TokenKind.Text)
                {
                    var pieces = SplitSwitchText(_tok.Text);
                    int baseStart = _tok.Span.Start;
                    Advance();

                    foreach (var piece in pieces)
                    {
                        var span = new TextSpan(baseStart + piece.Offset, piece.Length);
                        switch (piece.Kind)
                        {
                            case SwitchPieceKind.Case:
                            case SwitchPieceKind.Default:
                                {
                                    var section = new UiSwitchSection
                                    {
                                        IsDefault = piece.Kind == SwitchPieceKind.Default,
                                        Label = piece.Text,
                                        LabelSpan = span,
                                        HeaderSpan = new TextSpan(baseStart + piece.HeaderOffset, piece.HeaderLength),
                                    };
                                    if (section.IsDefault)
                                    {
                                        if (sawDefault)
                                            Diag(UiDiagnostics.Syntax,
                                                "duplicate 'default' section; a @switch allows at most one",
                                                section.HeaderSpan);
                                        sawDefault = true;
                                    }
                                    else
                                    {
                                        ValidateCaseLabelShape(section.Label, section.LabelSpan);
                                    }
                                    switchNode.Sections.Add(section);
                                    current = section;
                                    break;
                                }

                            case SwitchPieceKind.Break:
                                if (current is null)
                                    Diag(UiDiagnostics.Syntax,
                                        "'break;' must be inside a case or default section", span);
                                else
                                {
                                    current.HasBreak = true;
                                    current = null;
                                }
                                break;

                            default:
                                if (current is null)
                                    Diag(UiDiagnostics.Syntax,
                                        "expected 'case', 'default' or '}' in @switch body", span);
                                else
                                {
                                    var textNode = new UiText();
                                    textNode.Segments.Add(TextSegment.Static(piece.Text, span));
                                    current.Children.Add(textNode);
                                }
                                break;
                        }
                    }
                    continue;
                }

                if (current is null)
                {
                    Diag(UiDiagnostics.Syntax,
                        "expected 'case', 'default' or '}' in @switch body", _tok.Span);
                    Advance();
                    continue;
                }

                switch (_tok.Kind)
                {
                    case TokenKind.TagOpen:
                        current.Children.Add(ParseElement());
                        break;
                    case TokenKind.Text:
                    case TokenKind.Expression:
                        current.Children.Add(ParseTextRun());
                        break;
                    case TokenKind.Directive:
                        ParseDirectiveInto(current.Children, topLevel: false);
                        break;
                    default:
                        Diag(UiDiagnostics.Syntax,
                            "unexpected token in @switch case body; expected an element, a block, 'break;' or the next case",
                            _tok.Span);
                        Advance();
                        break;
                }
            }
        }
        finally
        {
            _switchDepth--;
        }

        foreach (var section in switchNode.Sections)
        {
            NormalizeChildren(section.Children);
            if (section.Children.Count > 0 && !section.HasBreak)
                Diag(UiDiagnostics.Syntax,
                    section.IsDefault
                        ? "the 'default' section must end with a 'break;' statement (write it on its own line)"
                        : $"case '{section.Label}' must end with a 'break;' statement (write it on its own line)",
                    section.HeaderSpan);
            else if (section.Children.Count == 0 && !section.HasBreak && !section.IsDefault)
                // 无贯穿语义（K29 ③）下，`case 1:` 紧跟 `case 2:` 不是 C# 的标签组，而是两个独立分支：
                // case 1 的分支体为空。静默渲染空内容属于"看似可用、运行时才炸"，显式拒绝。
                Diag(UiDiagnostics.Syntax,
                    $"case '{section.Label}' has no body and no 'break;'; a case section always starts its own body " +
                    "(multi-label sections are not supported)",
                    section.LabelSpan);
        }

        if (!closed && switchNode.Sections.Count == 0)
            Diag(UiDiagnostics.Syntax, "expected '}' to close @switch block", directive.Span);

        list.Add(switchNode);
    }

    /// <summary>K29 ②（语法层）：case 标签必须是常量形态（字面量/标识符/成员访问链/括号/正负号）。</summary>
    private void ValidateCaseLabelShape(string label, TextSpan span)
    {
        if (!IsConstantShape(label))
            _diags.Add(UiDiagnostics.Error(UiDiagnostics.CaseLabelNotConstant,
                $"case label '{label}' must be a compile-time constant (a literal, const member or enum member)",
                span));
    }

    private static bool IsConstantShape(string label)
    {
        var s = label.Trim();
        int i = 0;
        return ParseConstOperand(s, ref i) && SkipWs(s, ref i) && i == s.Length;
    }

    private static bool SkipWs(string s, ref int i)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        return true;
    }

    private static bool ParseConstOperand(string s, ref int i)
    {
        SkipWs(s, ref i);
        if (i >= s.Length) return false;
        if (s[i] == '(')
        {
            i++;
            if (!ParseConstOperand(s, ref i)) return false;
            SkipWs(s, ref i);
            if (i >= s.Length || s[i] != ')') return false;
            i++;
            return true;
        }
        if (s[i] == '-' || s[i] == '+')
        {
            i++;
            return ParseConstOperand(s, ref i);
        }
        if (s[i] == '"')
        {
            i++;
            while (i < s.Length && s[i] != '"')
            {
                if (s[i] == '\\' && i + 1 < s.Length) i++;
                i++;
            }
            if (i >= s.Length) return false;
            i++;
            return true;
        }
        if (s[i] == '\'')
        {
            i++;
            while (i < s.Length && s[i] != '\'')
            {
                if (s[i] == '\\' && i + 1 < s.Length) i++;
                i++;
            }
            if (i >= s.Length) return false;
            i++;
            return true;
        }
        if (char.IsDigit(s[i]))
        {
            while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '.')) i++;
            return true;
        }
        if (!char.IsLetter(s[i]) && s[i] != '_') return false;
        while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
        // 成员访问链（enum 成员 / const 字段）：A.B.C
        while (i < s.Length && s[i] == '.')
        {
            i++;
            if (i >= s.Length || (!char.IsLetter(s[i]) && s[i] != '_')) return false;
            while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
        }
        return true;
    }

    /// <summary>
    /// 从 case 体顶层的裸文本中拆出一个 section 头：<c>case &lt;const&gt;:</c> 或 <c>default:</c>。
    /// 字符串/括号感知；标签内不得含 &lt;、@、{、}（这些字符会在词法层断词，属已知限制）。
    /// 返回标签在 text 内的偏移（用于 span 回映）与头结束位置（':' 之后）。
    /// </summary>
    private static bool TrySplitSectionHeader(string text, int start,
        out bool isDefault, out string label, out int labelOffset, out int headStart, out int headEnd)
    {
        isDefault = false;
        label = "";
        labelOffset = -1;
        headStart = -1;
        headEnd = -1;

        int i = start;
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        if (i >= text.Length) return false;

        if (MatchWordAt(text, i, "case"))
        {
            headStart = i;
            i += 4;
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            int labelStart = i;
            int depth = 0;
            while (i < text.Length)
            {
                char c = text[i];
                if (c == '"') { i = SkipQuoted(text, i); continue; }
                if (c == '\'') { i = SkipQuoted(text, i); continue; }
                if (c == '(' || c == '[') depth++;
                else if (c == ')' || c == ']')
                {
                    if (depth == 0) return false;
                    depth--;
                }
                else if (c == ':' && depth == 0)
                {
                    var raw = text.Substring(labelStart, i - labelStart);
                    var trimmed = raw.Trim();
                    if (trimmed.Length == 0) return false;
                    label = trimmed;
                    labelOffset = labelStart + (raw.Length - raw.TrimStart().Length);
                    headEnd = i + 1;
                    return true;
                }
                else if ((c == '}' || c == '<' || c == '@' || c == '{') && depth == 0)
                {
                    return false; // 标签未闭合就遇到结构字符
                }
                i++;
            }
            return false;
        }

        if (MatchWordAt(text, i, "default"))
        {
            headStart = i;
            i += 7;
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            if (i < text.Length && text[i] == ':')
            {
                isDefault = true;
                labelOffset = i; // default 无标签；span 校验用 HeaderSpan
                headEnd = i + 1;
                return true;
            }
            return false;
        }

        return false;
    }

    private static bool MatchWordAt(string text, int i, string word)
    {
        if (i + word.Length > text.Length) return false;
        if (string.CompareOrdinal(text, i, word, 0, word.Length) != 0) return false;
        int after = i + word.Length;
        return after >= text.Length || !(char.IsLetterOrDigit(text[after]) || text[after] == '_');
    }

    private enum SwitchPieceKind { Case, Default, Break, Text }

    /// <summary>@switch 块内裸文本的一个片段（section 头 / 独立 break; / 普通文本），偏移相对该 Text token。</summary>
    private readonly struct SwitchPiece
    {
        public SwitchPieceKind Kind { get; init; }
        public string Text { get; init; }
        public int Offset { get; init; }
        public int Length { get; init; }
        public int HeaderOffset { get; init; }
        public int HeaderLength { get; init; }
    }

    /// <summary>
    /// 把 @switch 块内的裸文本拆成 section 头 / 独立 <c>break;</c> / 普通文本三类片段（K29 ③）。
    /// 词法层会把 <c>break; case 2:</c> 这类连续内容累成**同一个** Text token，
    /// 因此必须按片段解析——只认「整段等于 break;」会让多分支 switch 必然落成文本节点（EUI006）。
    /// </summary>
    private static List<SwitchPiece> SplitSwitchText(string text)
    {
        var pieces = new List<SwitchPiece>();
        int pos = 0;
        while (pos < text.Length)
        {
            if (IsLineStart(text, pos)
                && TrySplitSectionHeader(text, pos, out var isDefault, out var label,
                    out var labelOffset, out var headStart, out var headEnd))
            {
                pieces.Add(new SwitchPiece
                {
                    Kind = isDefault ? SwitchPieceKind.Default : SwitchPieceKind.Case,
                    Text = label,
                    Offset = labelOffset,
                    Length = label.Length,
                    HeaderOffset = headStart,
                    HeaderLength = headEnd - headStart,
                });
                pos = headEnd;
                continue;
            }

            if (TryConsumeBreak(text, pos, out var breakStart, out var breakEnd))
            {
                pieces.Add(new SwitchPiece
                {
                    Kind = SwitchPieceKind.Break,
                    Text = "break;",
                    Offset = breakStart,
                    Length = breakEnd - breakStart,
                });
                pos = breakEnd;
                continue;
            }

            int next = FindNextSwitchMarker(text, pos + 1);
            var chunk = text.Substring(pos, next - pos);
            if (chunk.Trim().Length > 0)
            {
                pieces.Add(new SwitchPiece
                {
                    Kind = SwitchPieceKind.Text,
                    Text = chunk,
                    Offset = pos,
                    Length = chunk.Length,
                });
            }
            pos = next;
        }
        return pieces;
    }

    /// <summary>
    /// 识别独立成行的 <c>break;</c>：前导空白后是 <c>break</c> 词、可选空白、<c>;</c>，
    /// 且该行其余部分只有空白。要求「本行无后续内容」是为了不把正文里的 "break" 当语句。
    /// </summary>
    private static bool TryConsumeBreak(string text, int start, out int breakStart, out int breakEnd)
    {
        breakStart = -1;
        breakEnd = -1;
        int i = start;
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        if (!MatchWordAt(text, i, "break")) return false;
        int j = i + 5;
        while (j < text.Length && (text[j] == ' ' || text[j] == '\t')) j++;
        if (j >= text.Length || text[j] != ';') return false;
        j++;
        for (int k = j; k < text.Length && text[k] != '\n'; k++)
        {
            if (!char.IsWhiteSpace(text[k])) return false;
        }
        breakStart = i;
        breakEnd = j;
        return true;
    }

    /// <summary>下一个可能的 section 头 / break 起点（必须位于行首）；找不到返回文本长度。</summary>
    private static int FindNextSwitchMarker(string text, int from)
    {
        for (int i = from < 0 ? 0 : from; i < text.Length; i++)
        {
            if (!IsLineStart(text, i)) continue;
            if (TrySplitSectionHeader(text, i, out _, out _, out _, out _, out _)) return i;
            if (TryConsumeBreak(text, i, out _, out _)) return i;
        }
        return text.Length;
    }

    /// <summary>位置 i 之前直到上一个换行只有空白（token 起点也算行首）。</summary>
    private static bool IsLineStart(string text, int i)
    {
        for (int k = i - 1; k >= 0; k--)
        {
            if (text[k] == '\n') return true;
            if (!char.IsWhiteSpace(text[k])) return false;
        }
        return true;
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

using EclipseUI.Compiler;

namespace EclipseUI.LanguageServer.Analysis;

/// <summary>
/// 大纲 / 面包屑：元素树 + 控制流块 + <c>@code</c>。名称取标签名（不是 CLR 全名），
/// detail 给类型短名，这样 VS Code 的 outline 里一眼能看出「这是 Button，不是组件」。
///
/// 范围（<c>range</c>）要覆盖整个元素（含子内容），而 <see cref="UiElement.Span"/> 只记标签名，
/// 所以这里按"最后一个子节点之后找结束标签"补出右边界：叶子元素就只高亮标签本身，
/// 容器元素高亮到 <c>&lt;/Tag&gt;</c>。
/// </summary>
public static class SymbolProvider
{
    public static IReadOnlyList<UiSymbol> Build(DocumentAnalysis analysis, UiCatalog catalog)
    {
        if (analysis.Ast is null) return Array.Empty<UiSymbol>();

        var symbols = new List<UiSymbol>();
        foreach (var node in analysis.Ast.Nodes) Add(node, symbols, catalog, analysis);
        return symbols;
    }

    private static void Add(UiNode node, List<UiSymbol> into, UiCatalog catalog, DocumentAnalysis analysis)
    {
        switch (node)
        {
            case UiElement element:
            {
                var def = analysis.ResolveAny(catalog, element.Tag);
                var children = new List<UiSymbol>();
                foreach (var child in element.Children) Add(child, children, catalog, analysis);

                var detail = def is null ? "unknown tag" : TypeNames.Bare(def.ClrTypeDisplayName);
                var kind = def?.Origin == UiElementOrigin.Component
                    ? UiSymbolKind.Component
                    : UiSymbolKind.Control;

                into.Add(new UiSymbol(element.Tag, detail, kind,
                    Extend(analysis.Text, element.TagSpan, "</" + element.Tag, children),
                    element.TagSpan, children));
                break;
            }

            case UiIf @if:
            {
                var children = new List<UiSymbol>();
                foreach (var child in @if.Children) Add(child, children, catalog, analysis);
                foreach (var elseIf in @if.ElseIfs)
                {
                    var branch = new List<UiSymbol>();
                    foreach (var child in elseIf.Children) Add(child, branch, catalog, analysis);
                    children.Add(new UiSymbol($"@else if ({Condense(elseIf.Condition)})", "branch",
                        UiSymbolKind.Block, elseIf.Span, elseIf.ConditionSpan, branch));
                }
                if (@if.ElseChildren is { } elseChildren)
                {
                    var branch = new List<UiSymbol>();
                    foreach (var child in elseChildren) Add(child, branch, catalog, analysis);
                    children.Add(new UiSymbol("@else", "branch", UiSymbolKind.Block, @if.Span, @if.Span, branch));
                }

                into.Add(new UiSymbol($"@if ({Condense(@if.Condition)})", "conditional", UiSymbolKind.Block,
                    Extend(analysis.Text, @if.Span, "}", children), @if.Span, children));
                break;
            }

            case UiForeach loop:
            {
                var children = new List<UiSymbol>();
                foreach (var child in loop.Children) Add(child, children, catalog, analysis);
                var name = $"@foreach (var {loop.VarName} in {Condense(loop.Collection)})";
                into.Add(new UiSymbol(name, "repeated", UiSymbolKind.Block,
                    Extend(analysis.Text, loop.Span, "}", children), loop.Span, children));
                break;
            }

            case UiSwitch @switch:
            {
                var children = new List<UiSymbol>();
                foreach (var section in @switch.Sections)
                {
                    var branch = new List<UiSymbol>();
                    foreach (var child in section.Children) Add(child, branch, catalog, analysis);
                    var label = section.IsDefault ? "default" : $"case {Condense(section.Label)}";
                    children.Add(new UiSymbol(label, "branch", UiSymbolKind.Block, section.HeaderSpan,
                        section.LabelSpan, branch));
                }
                into.Add(new UiSymbol($"@switch ({Condense(@switch.Value)})", "multi-branch", UiSymbolKind.Block,
                    Extend(analysis.Text, @switch.Span, "}", children), @switch.Span, children));
                break;
            }

            case UiCodeBlock code:
                into.Add(new UiSymbol("@code", "class members", UiSymbolKind.CodeBlock,
                    TextSpan.FromBounds(code.Span.Start, Math.Max(code.Span.End, code.CodeSpan.End + 1)),
                    code.CodeSpan, Array.Empty<UiSymbol>()));
                break;
        }
    }

    /// <summary>
    /// 右边界推定：从"最后一个子节点的末尾"往后找结束标记（<c>&lt;/Tag</c> 或 <c>}</c>）。
    /// 加一个距离上限，避免结束标记缺失时一路吃到文件尾。
    /// </summary>
    private static TextSpan Extend(string text, TextSpan fallback, string closer, IReadOnlyList<UiSymbol> children)
    {
        if (children.Count == 0) return fallback;

        int end = fallback.End;
        foreach (var child in children) end = Math.Max(end, child.Span.End);
        if (end >= text.Length) return fallback;

        int found = text.IndexOf(closer, end, StringComparison.Ordinal);
        if (found < 0 || found - end > 512) return TextSpan.FromBounds(fallback.Start, end);

        int terminator = text.IndexOf('>', found);
        if (closer == "}")
            return found < text.Length ? TextSpan.FromBounds(fallback.Start, found + 1) : fallback;

        if (terminator < 0 || terminator - found > closer.Length + 16)
            return TextSpan.FromBounds(fallback.Start, found + closer.Length);
        return TextSpan.FromBounds(fallback.Start, terminator + 1);
    }

    /// <summary>表达式压成一行并截断：outline 的标签宽度有限，换行会把树撑歪。</summary>
    private static string Condense(string expression)
    {
        var text = expression.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= 40 ? text : text.Substring(0, 37) + "…";
    }
}

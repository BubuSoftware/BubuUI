using EclipseUI.Compiler;

namespace EclipseUI.LanguageServer.Analysis;

/// <summary>
/// 悬停：把"这个名字是什么"答清楚——标签落在哪个 CLR 类型、属性是什么类型、
/// 事件要什么签名、枚举有哪些值。答案同样只来自模板目录，不猜。
/// </summary>
public static class HoverProvider
{
    public static UiHoverResult? Hover(DocumentAnalysis analysis, UiCatalog catalog, int offset)
    {
        var context = CursorScanner.Scan(analysis.Text, offset);
        switch (context.Kind)
        {
            case CursorKind.TagName:
            case CursorKind.EndTagName:
            {
                var (name, span) = WordAt(analysis.Text, context.TokenStart);
                if (name.Length == 0) return null;
                var element = analysis.ResolveAny(catalog, name);
                return element is null ? null : new UiHoverResult(span, ElementMarkdown(element));
            }

            case CursorKind.AttributeName:
            {
                if (context.AttributeName.Length == 0) return null;
                var span = AttributeSpan(analysis.Text, context, offset);
                var markdown = AttributeMarkdown(analysis, catalog, context);
                return markdown is null ? null : new UiHoverResult(span, markdown);
            }

            case CursorKind.AttributeValue:
            {
                var markdown = ValueMarkdown(analysis, catalog, context);
                if (markdown is null) return null;
                var end = analysis.Text.Length;
                return new UiHoverResult(new TextSpan(context.TokenStart, Math.Max(0, Math.Min(end, offset) - context.TokenStart)), markdown);
            }

            case CursorKind.Directive:
            {
                var (name, span) = WordAt(analysis.Text, context.TokenStart);
                var markdown = DirectiveMarkdown(name.TrimStart('@'));
                return markdown is null ? null : new UiHoverResult(span, markdown);
            }

            default:
                return null;
        }
    }

    /// <summary>从 <paramref name="start"/> 起的一个标识符/名字（含 <c>@</c> 与 <c>.</c>）。</summary>
    private static (string Name, TextSpan Span) WordAt(string text, int start)
    {
        int i = start;
        while (i < text.Length && (char.IsAsciiLetterOrDigit(text[i]) || text[i] is '_' or '@' or '.' or '-')) i++;
        return (text.Substring(start, i - start), new TextSpan(start, i - start));
    }

    private static TextSpan AttributeSpan(string text, CursorContext context, int offset)
    {
        int start = context.TokenStart;
        int end = start;
        while (end < text.Length && (char.IsAsciiLetterOrDigit(text[end]) || text[end] is '_' or '.' or ':' or '-' or '@'))
            end++;
        // 光标可能落在 token 中间：范围仍然覆盖整个属性名。
        return new TextSpan(start, Math.Max(offset - start, end - start));
    }

    private static string ElementMarkdown(UiElementDef element)
    {
        var content = element.Content switch
        {
            UiContentModel.Children => " · content: children",
            UiContentModel.Text => " · content: text",
            UiContentModel.TextRuns => " · content: text runs",
            _ => " · no content",
        };

        var lines = new List<string>
        {
            $"**{element.Tag}** — `{TypeNames.Short(element.ClrTypeDisplayName)}`",
            "",
            (element.Origin == UiElementOrigin.Component ? "component" : "control") + content,
        };

        if (element.Slots.Count > 0)
            lines.Add("slots: " + string.Join(", ", element.Slots.Keys.OrderBy(static k => k, StringComparer.Ordinal)));

        lines.Add($"properties: {element.Properties.Count} · events: {element.Events.Count}" +
                  (element.Parameters.Count > 0 ? $" · parameters: {element.Parameters.Count}" : ""));
        return string.Join("\n", lines);
    }

    private static string? AttributeMarkdown(DocumentAnalysis analysis, UiCatalog catalog, CursorContext context)
    {
        var candidate = analysis.FindAttribute(catalog, context);
        if (candidate is null) return null;

        if (candidate.Kind == CandidateAttributeKind.Event)
        {
            var element = analysis.AttributeOwner(catalog, context);
            return $"**{candidate.Name}** — event\n\nhandler: `{candidate.TypeDisplay}`\n\ndeclared on `{TypeNames.Short(candidate.DeclaringTypeName)}`" +
                   (element is null ? "" : $"\nthe handler runs with an automatic `Invalidate()`");
        }

        var kind = candidate.Kind switch
        {
            CandidateAttributeKind.Property => "property",
            CandidateAttributeKind.Parameter => "component parameter",
            CandidateAttributeKind.AttachedProperty => "attached property",
            _ => "member",
        };

        var lines = new List<string>
        {
            $"**{candidate.Name}** — {kind}",
            "",
            $"type: `{candidate.TypeDisplay}`",
            $"declared on `{TypeNames.Short(candidate.DeclaringTypeName)}`",
        };

        if (!candidate.CanWrite) lines.Add("read-only — a literal/expression cannot be assigned here (EUI019)");
        if (candidate.Context is UiParameterDef { IsContent: true }) lines.Add("content parameter — build-once, cannot be bound");
        if (candidate.Context is UiParameterDef { Required: true }) lines.Add("required — must be set at every use site (EUI027)");

        if (candidate.Shape is { Kind: UiTypeShapeKind.Enum } shape)
            lines.Add("values: " + string.Join(", ", shape.EnumMembers));

        return string.Join("\n", lines);
    }

    private static string? ValueMarkdown(DocumentAnalysis analysis, UiCatalog catalog, CursorContext context)
    {
        if (string.Equals(context.AttributeName, "Style", StringComparison.Ordinal))
        {
            var property = catalog.StyleProperties.Find(context.Prefix);
            if (property is null) return null;
            return $"**Style:{property.Name}** — " +
                   (property.Kind == StyleValueKind.Color ? "color (hex or `theme(dark, light)`)" : "number");
        }

        var candidate = analysis.FindAttribute(catalog, context);
        if (candidate?.Shape is not { } shape) return null;

        if (shape.Kind == UiTypeShapeKind.Enum)
            return $"**{candidate.Name}** — `{TypeNames.Bare(shape.CanonicalKey)}`\n\nvalues: " +
                   string.Join(", ", shape.EnumMembers);

        return $"**{candidate.Name}** — `{candidate.TypeDisplay}`";
    }

    private static string? DirectiveMarkdown(string name) => name switch
    {
        "if" => "**@if** — conditional block\n\n`@if (condition) { … }`, with `else if` / `else` continuations (no `@`).",
        "foreach" => "**@foreach** — repeated block\n\n`@foreach (var item in items) { … }`; `Key={expr}` on the item root drives reuse.",
        "switch" => "**@switch** — multi-branch block\n\n`@switch (value) { case CONST: … break; default: … break; }`",
        "using" => "**@using** — namespace import (or `@using Alias = Ns.Type`)\n\nTags resolve only inside imported namespaces (K22).",
        "code" => "**@code** — class member block\n\nMembers merge into the partial class, exactly like the code-behind file.",
        "style" => "**@style** — component style block\n\nEustyle rules scoped to this component (`{Component}__{class}`).",
        "else" => "**else** — continuation of the preceding `@if` branch (written without `@`).",
        _ => null,
    };
}

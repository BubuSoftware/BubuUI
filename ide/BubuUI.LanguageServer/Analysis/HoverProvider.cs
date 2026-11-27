using BubuUI.Compiler;
using BubuUI.Compiler.Probe;
using Microsoft.CodeAnalysis;

namespace BubuUI.LanguageServer.Analysis;

/// <summary>
/// 悬停：把"这个名字是什么"答清楚——标签落在哪个 CLR 类型、属性是什么类型、
/// 事件要什么签名、枚举有哪些值；C# 表达式交给探针推断（design/06 §6.3）。
/// 答案只来自模板目录与 Roslyn 符号，不猜。
/// </summary>
public static class HoverProvider
{
    public static UiHoverResult? Hover(DocumentAnalysis analysis, UiCatalog catalog, int offset)
    {
        var context = CursorScanner.Scan(analysis.Text, offset);
        switch (context.Kind)
        {
            case CursorKind.DirectiveArgument when context.Directive == "template":
            {
                var (name, span) = WordAt(analysis.Text, context.TokenStart);
                var element = analysis.ResolveAny(catalog, name);
                return element is null ? null : new UiHoverResult(span, ElementMarkdown(element));
            }
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

            case CursorKind.Expression:
                return ExpressionHover(analysis, catalog, context, offset);

            case CursorKind.CodeBlock:
                return CodeBlockHover(analysis, offset);

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

    /// <summary>
    /// C# 表达式悬停（design/06 §6.3）：表达式放进探针方法体问 SemanticModel，
    /// 与编译期 EUI049 同一台机器。事件处理器位置显示方法签名（方法组没有可显示的"类型"）。
    /// 探针不可用时返回 null——不猜。
    /// </summary>
    private static UiHoverResult? ExpressionHover(
        DocumentAnalysis analysis, UiCatalog catalog, CursorContext context, int offset)
    {
        var probe = analysis.Probe;
        if (probe is null || context.ExpressionStart < 0 || context.ExpressionStart >= offset) return null;

        var text = analysis.Text.Substring(context.ExpressionStart, offset - context.ExpressionStart).Trim();
        if (text.Length == 0) return null;

        var env = ExpressionEnvironment.Build(analysis.Ast!, context.OpenForeaches, probe, probe.Compilation);
        var span = new TextSpan(context.ExpressionStart, offset - context.ExpressionStart);

        if (context.IsAttributeExpression)
        {
            var candidate = analysis.FindAttribute(catalog, context);
            if (candidate?.Kind == CandidateAttributeKind.Event)
            {
                var method = probe.ResolveMethod(text, env);
                if (method is null) return null;
                return new UiHoverResult(span,
                    $"**{method.Name}** — handler method\n\n`{Signature(method)}`\n\ntarget: `{candidate.TypeDisplay}`");
            }
        }

        var type = probe.InferType(text, env);
        if (type is null) return null;

        var markdown = $"**{Display(type)}** — inferred C# type";
        if (context.IsAttributeExpression && analysis.FindAttribute(catalog, context) is { } target)
            markdown += $"\n\ntarget: `{target.TypeDisplay}`";
        return new UiHoverResult(span, markdown);
    }

    /// <summary>
    /// <c>@code</c> 块内悬停（design/18 阶段 1）：光标处符号经真实成员树绑定，
    /// 局部变量/参数/字段/属性/方法/类型分别给出类型或签名。解析不出返回 null——不猜。
    /// </summary>
    private static UiHoverResult? CodeBlockHover(DocumentAnalysis analysis, int offset)
    {
        if (EmbeddedCSharpService.CodeBlockSymbolAt(analysis, offset) is not { } hit) return null;
        var markdown = SymbolMarkdown(hit.Symbol);
        return markdown is null ? null : new UiHoverResult(hit.Span, markdown);
    }

    private static string? SymbolMarkdown(ISymbol symbol) => symbol switch
    {
        ILocalSymbol local => $"**{local.Name}** — local variable\n\ntype: `{Display(local.Type)}`",
        IParameterSymbol parameter => $"**{parameter.Name}** — parameter\n\ntype: `{Display(parameter.Type)}`",
        IFieldSymbol field => $"**{field.Name}** — field\n\ntype: `{Display(field.Type)}`",
        IPropertySymbol property => $"**{property.Name}** — property\n\ntype: `{Display(property.Type)}`",
        IEventSymbol @event => $"**{@event.Name}** — event\n\ntype: `{Display(@event.Type)}`",
        IMethodSymbol method => $"**{method.Name}** — method\n\n`{Signature(method)}`",
        INamedTypeSymbol type => $"**{type.Name}** — {type.TypeKind.ToString().ToLowerInvariant()}",
        _ => null,
    };

    /// <summary>展示格式：去掉可空修饰符（oblivious 的 code-behind 类型会被显示成 <c>string?</c>，是噪音）。</summary>
    private static readonly SymbolDisplayFormat DisplayFormat =
        SymbolDisplayFormat.MinimallyQualifiedFormat.WithMiscellaneousOptions(
            SymbolDisplayFormat.MinimallyQualifiedFormat.MiscellaneousOptions
            & ~SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    private static string Signature(IMethodSymbol method) =>
        $"{Display(method.ReturnType)} {method.Name}(" +
        string.Join(", ", method.Parameters.Select(static p => $"{Display(p.Type)} {p.Name}")) + ")";

    private static string Display(ITypeSymbol type) => type.ToDisplayString(DisplayFormat);

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
            (element.IsEmbeddable ? "control" : "UI object") + content,
        };

        if (element.Slots.Count > 0)
            lines.Add("slots: " + string.Join(", ", element.Slots.Keys.OrderBy(static k => k, StringComparer.Ordinal)));

        lines.Add($"properties: {element.Properties.Count} · events: {element.Events.Count}" +
                  (element.Parameters.Count > 0 ? $" · parameters: {element.Parameters.Count}" : ""));
        return string.Join("\n", lines);
    }

    private static string? AttributeMarkdown(DocumentAnalysis analysis, UiCatalog catalog, CursorContext context)
    {
        if (context.AttributeName == "Part") return "**Part** — named part of a control template, available through `TemplateInstance.FindPart<T>()`. Names must be unique within the template.";
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

        if (candidate.Shape is { Kind: UiTypeShapeKind.Enum } shape)
            lines.Add("values: " + string.Join(", ", shape.EnumMembers));

        // 声明式字面量解析器（design/14 §14.7.3）：给出期望格式或候选值。
        if (candidate.Shape is not null
            && catalog.FindLiteralParser(candidate.Shape.CanonicalKey) is { } parser)
        {
            lines.Add("literal — " + (parser.Hint ?? parser.Pattern)
                + (parser.Candidates.Count > 0 ? " (" + string.Join(", ", parser.Candidates) + ")" : ""));
        }

        return string.Join("\n", lines);
    }

    private static string? ValueMarkdown(DocumentAnalysis analysis, UiCatalog catalog, CursorContext context)
    {
        if (string.Equals(context.AttributeName, "Style", StringComparison.Ordinal))
        {
            var property = catalog.StyleProperties.Find(context.Prefix);
            if (property is null) return null;
            var kindText = property.Kind switch
            {
                StyleValueKind.Color => "color (hex or `theme(dark, light)`)",
                StyleValueKind.Number => "number",
                StyleValueKind.Enum when property.Candidates is { Count: > 0 } members =>
                    "enum keyword — one of: " + string.Join(", ", members),
                StyleValueKind.Enum => "enum keyword",
                _ => "string",
            };
            return $"**Style:{property.Name}** — {kindText}";
        }

        var candidate = analysis.FindAttribute(catalog, context);
        if (candidate?.Shape is not { } shape) return null;

        if (shape.Kind == UiTypeShapeKind.Enum)
            return $"**{candidate.Name}** — `{TypeNames.Bare(shape.CanonicalKey)}`\n\nvalues: " +
                   string.Join(", ", shape.EnumMembers);

        // 声明式字面量解析器（design/14 §14.7.3）：引号内 hover 给期望格式。
        if (catalog.FindLiteralParser(shape.CanonicalKey) is { } parser)
            return $"**{candidate.Name}** — `{candidate.TypeDisplay}`\n\nliteral: " +
                   (parser.Hint ?? parser.Pattern);

        return $"**{candidate.Name}** — `{candidate.TypeDisplay}`";
    }

    private static string? DirectiveMarkdown(string name) => name switch
    {
        "if" => "**@if** — conditional block\n\n`@if (condition) { … }`, with `else if` / `else` continuations (no `@`).",
        "foreach" => "**@foreach** — repeated block\n\n`@foreach (var item in items) { … }`; `Key={expr}` on the item root drives reuse.",
        "switch" => "**@switch** — multi-branch block\n\n`@switch (value) { case CONST: … break; default: … break; }`",
        "using" => "**@using** — namespace import (or `@using Alias = Ns.Type`)\n\nTags resolve only inside imported namespaces (K22).",
        "inject" => "**@inject TypeName Name** — service injection (K37)\n\nGenerates `public TypeName Name { get; private set; }`, resolved from `Application.Current.Services` at the top of `InitializeUi`; unregistered services fail fast.",
        "code" => "**@code** — class member block\n\nMembers merge into the partial class, exactly like the code-behind file.",
        "style" => "**@style** — component style block\n\nBss rules scoped to this component (`{Component}__{class}`).",
        "template" => "**@template ControlType** — reusable control template. `Owner` has the target control type; each application builds a fresh visual tree.",
        "else" => "**else** — continuation of the preceding `@if` branch (written without `@`).",
        _ => null,
    };
}

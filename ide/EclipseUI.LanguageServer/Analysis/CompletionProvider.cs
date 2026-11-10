using EclipseUI.Compiler;

namespace EclipseUI.LanguageServer.Analysis;

/// <summary>
/// 补全：全部答案来自**模板目录**（编译器同一份符号派生物），不做硬编码清单。
/// 四个位置各一套候选：标签名 / 属性名（含 <c>@bind-</c>）/ 属性值（枚举、bool、样式属性）/ 指令。
/// 表达式内部（C# 语义补全）需要 Roslyn 投影，属下一片（design/06 §6.3）。
/// </summary>
public static class CompletionProvider
{
    private static readonly string[] ReservedAttributes = { "Class", "Style", "Ref", "Key" };

    public static UiCompletionList Complete(DocumentAnalysis analysis, UiCatalog catalog, int offset)
    {
        var context = CursorScanner.Scan(analysis.Text, offset);
        var replacement = new TextSpan(context.TokenStart, Math.Max(0, offset - context.TokenStart));

        return context.Kind switch
        {
            CursorKind.TagName => Tags(analysis, catalog, replacement),
            CursorKind.EndTagName => ClosingTags(context, replacement),
            CursorKind.AttributeName => Attributes(analysis, catalog, context, replacement),
            CursorKind.AttributeValue => Values(analysis, catalog, context, replacement),
            CursorKind.Directive => Directives(replacement),
            CursorKind.DirectiveArgument => Namespaces(catalog, context, replacement),
            _ => UiCompletionList.Empty,
        };
    }

    // ---- 标签 ----

    private static UiCompletionList Tags(DocumentAnalysis analysis, UiCatalog catalog, TextSpan replacement)
    {
        var items = new List<UiCompletionItem>();
        foreach (var element in catalog.Elements.OrderBy(static e => e.Tag, StringComparer.Ordinal))
        {
            var ns = TypeNames.NamespaceOf(element.ClrTypeDisplayName);
            var imported = ns is null || analysis.Imports.Contains(ns, StringComparer.Ordinal);

            var detail = element.Origin == UiElementOrigin.Component ? "component" : "control";
            detail += element.Content switch
            {
                UiContentModel.Children => " · children",
                UiContentModel.Text => " · text",
                UiContentModel.TextRuns => " · text runs",
                _ => " · empty",
            };
            if (!imported) detail += $" · needs @using {ns}";

            items.Add(new UiCompletionItem(
                element.Tag,
                UiCompletionKind.Tag,
                detail,
                DescribeElement(element),
                SortText: imported ? "0" + element.Tag : "1" + element.Tag));
        }

        return new UiCompletionList(replacement, items);
    }

    private static UiCompletionList ClosingTags(CursorContext context, TextSpan replacement)
    {
        var items = new List<UiCompletionItem>();
        foreach (var tag in context.OpenTags.Reverse().Distinct(StringComparer.Ordinal))
            items.Add(new UiCompletionItem(tag, UiCompletionKind.Tag, "close element"));
        return new UiCompletionList(replacement, items);
    }

    // ---- 属性 ----

    private static UiCompletionList Attributes(
        DocumentAnalysis analysis, UiCatalog catalog, CursorContext context, TextSpan replacement)
    {
        var element = analysis.AttributeOwner(catalog, context);
        var prefix = context.Prefix;

        // @bind-：只列可写目标（属性/参数），事件与只读属性不参与双向绑定（EUI040）。
        if (prefix.StartsWith('@'))
            return Bindings(element, replacement);

        var items = new List<UiCompletionItem>();
        if (element is null) return new UiCompletionList(replacement, items);

        foreach (var property in element.Properties.Values.OrderBy(static p => p.Name, StringComparer.Ordinal))
        {
            items.Add(new UiCompletionItem(
                property.Name,
                UiCompletionKind.Property,
                TypeNames.Short(property.ClrTypeDisplayName),
                property.CanWrite ? null : "read-only (setter is not public) — EUI019",
                SortText: "1" + property.Name));
        }

        foreach (var @event in element.Events.Values.OrderBy(static e => e.AttributeName, StringComparer.Ordinal))
        {
            items.Add(new UiCompletionItem(
                @event.AttributeName,
                UiCompletionKind.Event,
                $"event { @event.ArgsTypeName }",
                $"handler: `{@event.ArgsTypeName} ({@event.ArgsTypeName} e)` or a lambda",
                SortText: "1" + @event.AttributeName));
        }

        foreach (var parameter in element.Parameters.Values.OrderBy(static p => p.Name, StringComparer.Ordinal))
        {
            var flags = new List<string>();
            if (parameter.Required) flags.Add("required");
            if (parameter.IsContent) flags.Add("content");
            if (parameter.IsDelegate) flags.Add($"event · {parameter.DelegateArity} args");
            items.Add(new UiCompletionItem(
                parameter.Name,
                UiCompletionKind.Parameter,
                TypeNames.Short(parameter.ClrTypeDisplayName),
                flags.Count == 0 ? "component parameter" : "component parameter · " + string.Join(", ", flags),
                SortText: "1" + parameter.Name));
        }

        foreach (var reserved in ReservedAttributes)
        {
            items.Add(new UiCompletionItem(
                reserved,
                UiCompletionKind.Keyword,
                "reserved",
                ReservedDocumentation(reserved),
                SortText: "2" + reserved));
        }

        // 附加属性：写成 Owner.Name；输入里出现 '.' 时按前缀过滤 owner。
        int dot = prefix.IndexOf('.');
        foreach (var attached in catalog.AttachedProperties)
        {
            if (dot >= 0 && !attached.OwnerTag.StartsWith(prefix.Substring(0, dot), StringComparison.Ordinal))
                continue;
            var label = $"{attached.OwnerTag}.{attached.Name}";
            items.Add(new UiCompletionItem(
                label,
                UiCompletionKind.AttachedProperty,
                "attached · " + TypeNames.Short(attached.ClrTypeDisplayName),
                $"declared on `{TypeNames.Short(attached.OwnerClrName)}` (attached property)",
                SortText: (dot >= 0 ? "0" : "3") + label));
        }

        return new UiCompletionList(replacement, items);
    }

    private static UiCompletionList Bindings(UiElementDef? element, TextSpan replacement)
    {
        var items = new List<UiCompletionItem>();
        if (element is null) return new UiCompletionList(replacement, items);

        foreach (var property in element.Properties.Values.OrderBy(static p => p.Name, StringComparer.Ordinal))
        {
            if (!property.CanWrite || ReservedAttributes.Contains(property.Name, StringComparer.Ordinal)) continue;
            items.Add(new UiCompletionItem(
                "@bind-" + property.Name,
                UiCompletionKind.Property,
                "two-way · " + TypeNames.Short(property.ClrTypeDisplayName),
                "source must be a writable field/property (EUI037)"));
        }

        foreach (var parameter in element.Parameters.Values.OrderBy(static p => p.Name, StringComparer.Ordinal))
        {
            if (!parameter.CanWrite || parameter.IsContent) continue;
            var detail = parameter.ChangedCallbackName is null
                ? "two-way needs {P}Changed (EUI040)"
                : "two-way · " + TypeNames.Short(parameter.ClrTypeDisplayName);
            items.Add(new UiCompletionItem("@bind-" + parameter.Name, UiCompletionKind.Parameter, detail));
        }

        return new UiCompletionList(replacement, items);
    }

    // ---- 属性值 ----

    private static UiCompletionList Values(
        DocumentAnalysis analysis, UiCatalog catalog, CursorContext context, TextSpan replacement)
    {
        var items = new List<UiCompletionItem>();

        if (string.Equals(context.AttributeName, "Style", StringComparison.Ordinal))
        {
            foreach (var property in catalog.StyleProperties.Properties.OrderBy(static p => p.Name, StringComparer.Ordinal))
            {
                items.Add(new UiCompletionItem(
                    property.Name,
                    UiCompletionKind.StyleProperty,
                    property.Kind == StyleValueKind.Color ? "color" : "number",
                    property.AllowsTheme ? "accepts theme(dark, light)" : null,
                    InsertText: property.Name + ": "));
            }
            return new UiCompletionList(replacement, items);
        }

        var candidate = analysis.FindAttribute(catalog, context);
        if (candidate?.Shape is not { } shape) return new UiCompletionList(replacement, items);

        if (shape.Kind == UiTypeShapeKind.Enum)
        {
            var enumName = TypeNames.Bare(shape.CanonicalKey);
            foreach (var member in shape.EnumMembers)
                items.Add(new UiCompletionItem(member, UiCompletionKind.EnumMember, enumName));
            return new UiCompletionList(replacement, items);
        }

        if (shape.CanonicalKey == "bool")
        {
            items.Add(new UiCompletionItem("true", UiCompletionKind.Keyword, "bool"));
            items.Add(new UiCompletionItem("false", UiCompletionKind.Keyword, "bool"));
        }

        return new UiCompletionList(replacement, items);
    }

    // ---- 指令与命名空间 ----

    private static UiCompletionList Directives(TextSpan replacement)
    {
        var items = new List<UiCompletionItem>
        {
            new("@if", UiCompletionKind.Directive, "conditional block", "renders the block when the condition is true",
                "@if (${1:condition}) {\n\t$0\n}", IsSnippet: true),
            new("@foreach", UiCompletionKind.Directive, "repeated block",
                "keyed by `Key={expr}` on the item root",
                "@foreach (var ${1:item} in ${2:items}) {\n\t$0\n}", IsSnippet: true),
            new("@switch", UiCompletionKind.Directive, "multi-branch block",
                "`case CONST:` sections, each ends with `break;`",
                "@switch (${1:value}) {\n\tcase ${2:1}:\n\t\t$0\n\t\tbreak;\n}", IsSnippet: true),
            new("@using", UiCompletionKind.Directive, "import a namespace",
                "tags resolve only inside imported namespaces (K22)",
                "@using ${1:EclipseUI.Elements}", IsSnippet: true),
            new("@code", UiCompletionKind.Directive, "class member block",
                "members merge into the partial class, same as code-behind",
                "@code {\n\t$0\n}", IsSnippet: true),
            new("@style", UiCompletionKind.Directive, "component style block",
                "Eustyle rules scoped to this component via `{Component}__` prefix",
                "@style {\n\t$0\n}", IsSnippet: true),
        };
        return new UiCompletionList(replacement, items);
    }

    private static UiCompletionList Namespaces(UiCatalog catalog, CursorContext context, TextSpan replacement)
    {
        var items = catalog.Elements
            .Select(e => TypeNames.NamespaceOf(e.ClrTypeDisplayName))
            .Where(static ns => ns is not null)
            .Select(static ns => ns!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static ns => ns, StringComparer.Ordinal)
            .Select(ns => new UiCompletionItem(ns, UiCompletionKind.Namespace, "namespace"))
            .ToList();
        return new UiCompletionList(replacement, items);
    }

    // ---- 文档文本 ----

    private static string DescribeElement(UiElementDef element)
    {
        var lines = new List<string>
        {
            $"`{TypeNames.Short(element.ClrTypeDisplayName)}`",
            element.Origin == UiElementOrigin.Component ? "component" : "control",
            $"properties: {element.Properties.Count} · events: {element.Events.Count}" +
            (element.Parameters.Count > 0 ? $" · parameters: {element.Parameters.Count}" : ""),
        };
        return string.Join("\n\n", lines);
    }

    private static string ReservedDocumentation(string name) => name switch
    {
        "Class" => "style class names from `.eustyle` / `@style` (EUI031 when unknown)",
        "Style" => "inline declarations: `CornerRadius: 8; FontSize: 14`",
        "Ref" => "assigns the element/component instance to a code-behind field (EUI029)",
        "Key" => "item identity for `@foreach` reuse — first element of the item template only (EUI010)",
        _ => "reserved",
    };
}

using EclipseUI.Compiler;
using EclipseUI.Compiler.Probe;
using Microsoft.CodeAnalysis;

namespace EclipseUI.LanguageServer.Analysis;

/// <summary>
/// 补全：标签/属性来自模板目录，命名空间与类型来自同一项目的 Roslyn 符号。
/// 五个位置各一套候选：标签名 / 属性名（含 <c>@bind-</c>）/ 属性值（枚举、bool、样式属性）/
/// 指令 / C# 表达式（探针投影，design/06 §6.3——与编译期 EUI049 用同一台机器）。
/// </summary>
public static class CompletionProvider
{
    private static readonly string[] ReservedAttributes = { "Class", "Style", "Ref", "Key", "Part" };

    public static UiCompletionList Complete(DocumentAnalysis analysis, UiCatalog catalog, int offset)
    {
        var context = CursorScanner.Scan(analysis.Text, offset);
        var replacement = new TextSpan(context.TokenStart, Math.Max(0, offset - context.TokenStart));

        return context.Kind switch
        {
            CursorKind.TagName => Tags(analysis, catalog, context, replacement),
            CursorKind.EndTagName => ClosingTags(context, replacement),
            CursorKind.AttributeName => Attributes(analysis, catalog, context, replacement),
            CursorKind.AttributeValue => Values(analysis, catalog, context, replacement),
            CursorKind.Expression => Expressions(analysis, catalog, context, offset),
            CursorKind.Directive => Directives(replacement),
            CursorKind.DirectiveArgument when context.Directive == "template" => TemplateTargets(analysis, catalog, context, replacement),
            CursorKind.DirectiveArgument when context.Directive == "inject" => InjectTarget(analysis, catalog, context, replacement),
            CursorKind.DirectiveArgument => Namespaces(analysis, catalog, context, replacement),
            _ => UiCompletionList.Empty,
        };
    }

    private static UiCompletionList TemplateTargets(DocumentAnalysis analysis, UiCatalog catalog, CursorContext context, TextSpan replacement)
    {
        var end = replacement.End;
        while (end < analysis.Text.Length && (char.IsLetterOrDigit(analysis.Text[end]) || analysis.Text[end] is '_' or '.')) end++;
        replacement = new(replacement.Start, end - replacement.Start);
        var aliases = analysis.Aliases.Where(a => a.Value is not null &&
            catalog.FindByTypeDisplayName(a.Value)?.Properties.ContainsKey("Template") == true &&
            a.Key.StartsWith(context.Prefix, StringComparison.OrdinalIgnoreCase))
            .Select(a => new UiCompletionItem(a.Key, UiCompletionKind.Class, a.Value, "Control template target alias."));
        var types = catalog.Elements.Where(e => e.Properties.ContainsKey("Template") && !analysis.Aliases.ContainsKey(e.Tag) &&
            e.Tag.StartsWith(context.Prefix, StringComparison.OrdinalIgnoreCase)).Select(e =>
        {
            var ns = TypeNames.NamespaceOf(e.ClrTypeDisplayName);
            var edit = ns is null ? null : UiImportService.CreateImportEdit(analysis, ns);
            return new UiCompletionItem(e.Tag, UiCompletionKind.Class, e.ClrTypeDisplayName,
                "Control template target; Owner has this type.", AdditionalEdits: edit is null ? null : new[] { edit });
        });
        return new(replacement, aliases.Concat(types).ToArray());
    }

    /// <summary>
    /// K37 <c>@inject</c> 双参数补全（spec/02 §8）：参数①是服务类型名——命名空间 + 项目符号里
    /// 的非泛型可访问类型（服务不限 UI 目录，比 <c>@using</c> 别名目标放开目录过滤）；参数②是
    /// 自由属性名——无候选，不猜。
    /// </summary>
    private static UiCompletionList InjectTarget(
        DocumentAnalysis analysis, UiCatalog catalog, CursorContext context, TextSpan replacement)
    {
        if (context.DirectiveArgIndex >= 1) return UiCompletionList.Empty;
        return Namespaces(analysis, catalog, context, replacement, serviceTypes: true);
    }

    // ---- 表达式（C# 投影，design/06 §6.3）----

    /// <summary>
    /// <c>{expr}</c> 内的 C# 补全。答案来自探针 Compilation（code-behind 源码 + @code 成员 +
    /// @foreach 变量环境），与编译期表达式诊断同源。探针建不起来（没有 runtime、code-behind
    /// 不可读）返回空——与「目录为空就跳过语义」同一口径，不猜。
    /// </summary>
    private static UiCompletionList Expressions(
        DocumentAnalysis analysis, UiCatalog catalog, CursorContext context, int offset)
    {
        var probe = analysis.Probe;
        if (probe is null || context.ExpressionStart < 0 || context.ExpressionStart >= offset)
            return UiCompletionList.Empty;

        var full = analysis.Text.Substring(context.ExpressionStart, offset - context.ExpressionStart);
        var env = ExpressionEnvironment.Build(analysis.Ast!, context.OpenForeaches, probe, probe.Compilation);

        int lastDot = full.LastIndexOf('.');
        if (lastDot >= 0)
        {
            // 成员访问：接收者类型 → 实例成员。替换区间只覆盖点号后的部分标识符
            // （tasks.Cou → "Cou"），整段 "tasks.Cou" 不能被候选整词替换掉。
            var receiver = full.Substring(0, lastDot).Trim();
            if (receiver.Length == 0) return UiCompletionList.Empty;
            var receiverType = probe.InferType(receiver, env);
            if (receiverType is null) return UiCompletionList.Empty;
            var memberStart = context.ExpressionStart + lastDot + 1;
            return new UiCompletionList(new TextSpan(memberStart, offset - memberStart),
                Members(receiverType, includeStatic: false));
        }

        // 裸前缀（或空）：@foreach 变量 + code-behind/@code 的成员（含 static——裸标识符可引用）。
        var items = new List<UiCompletionItem>();
        foreach (var (envType, name) in env)
        {
            items.Add(new UiCompletionItem(name, UiCompletionKind.Variable,
                TypeDisplay(envType) + " · foreach item", SortText: "0" + name));
        }
        if (probe.ThisType is { } self)
            items.AddRange(Members(self, includeStatic: true, sortPrefix: "1"));
        return new UiCompletionList(new TextSpan(context.TokenStart, offset - context.TokenStart), items);
    }

    /// <summary>
    /// 实例成员枚举：属性/字段/普通方法，沿基类链走到 object 为止（object 的成员不进列表，
    /// 降噪），按名去重（派生覆写先到先得），private 剔除。扩展方法不在列（v1 边界）。
    /// </summary>
    private static List<UiCompletionItem> Members(ITypeSymbol type, bool includeStatic, string sortPrefix = "1")
    {
        var items = new List<UiCompletionItem>();
        if (type.TypeKind == TypeKind.Error || type.TypeKind == TypeKind.Dynamic) return items;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in WalkMembers(type))
        {
            if (!member.CanBeReferencedByName) continue;
            if (member.DeclaredAccessibility == Accessibility.Private) continue;
            if (!includeStatic && member.IsStatic) continue;
            if (!seen.Add(member.Name)) continue;

            switch (member)
            {
                case IPropertySymbol property:
                    items.Add(new UiCompletionItem(property.Name, UiCompletionKind.Property,
                        TypeDisplay(property.Type) + StaticSuffix(property.IsStatic),
                        SortText: sortPrefix + property.Name));
                    break;
                case IFieldSymbol field:
                    items.Add(new UiCompletionItem(field.Name, UiCompletionKind.Field,
                        TypeDisplay(field.Type) + StaticSuffix(field.IsStatic),
                        SortText: sortPrefix + field.Name));
                    break;
                case IMethodSymbol method when method.MethodKind == MethodKind.Ordinary:
                    items.Add(new UiCompletionItem(method.Name, UiCompletionKind.Method,
                        Signature(method), SortText: sortPrefix + method.Name));
                    break;
            }
        }
        return items;
    }

    private static string StaticSuffix(bool isStatic) => isStatic ? " · static" : "";

    /// <summary>
    /// 展示格式：去掉可空修饰符——探针树是 #nullable enable，oblivious 的 code-behind 类型
    /// 会被显示成 <c>string?</c>，对人是噪音；类型本身的可空性语义不受影响。
    /// </summary>
    private static readonly SymbolDisplayFormat DisplayFormat =
        SymbolDisplayFormat.MinimallyQualifiedFormat.WithMiscellaneousOptions(
            SymbolDisplayFormat.MinimallyQualifiedFormat.MiscellaneousOptions
            & ~SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    private static string TypeDisplay(ITypeSymbol type) => type.ToDisplayString(DisplayFormat);

    /// <summary>沿基类链枚举成员（不含 System.Object——ToString/Equals 这些留给全局约定）。</summary>
    private static IEnumerable<ISymbol> WalkMembers(ITypeSymbol type)
    {
        for (var current = (ITypeSymbol?)type; current is not null; current = current.BaseType)
        {
            if (current.SpecialType == SpecialType.System_Object) yield break;
            foreach (var member in current.GetMembers()) yield return member;
        }
    }

    private static string Signature(IMethodSymbol method) =>
        $"{TypeDisplay(method.ReturnType)} {method.Name}(" +
        string.Join(", ", method.Parameters.Select(static p =>
            $"{TypeDisplay(p.Type)} {p.Name}")) + ")";

    // ---- 标签 ----

    private static UiCompletionList Tags(DocumentAnalysis analysis, UiCatalog catalog, CursorContext context, TextSpan replacement)
    {
        var items = new List<UiCompletionItem>();

        // §6.9.0 第 1 条：`@using Alt = Ns.Type` 是 C# 别名**也是模板标签别名**（K31），
        // 必须进标签补全。Alias 表里 target 为 null 表示别名目标非法，仍列出并标注。
        var aliases = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (alias, target) in analysis.Aliases.OrderBy(static a => a.Key, StringComparer.Ordinal))
        {
            if (target is not null && context.OpenTags.Count > 0 &&
                catalog.FindByTypeDisplayName(target) is { IsEmbeddable: false }) continue;
            if (!aliases.Add(alias)) continue;
            var detail = target is null ? "alias · unresolved target" : "alias → " + TypeNames.Short(target);
            items.Add(new UiCompletionItem(alias, UiCompletionKind.Tag, detail,
                "declared by `@using " + alias + " = ...` (K31)", SortText: "0" + alias));
        }

        foreach (var element in catalog.Elements.OrderBy(static e => e.Tag, StringComparer.Ordinal))
        {
            if (context.OpenTags.Count > 0 && !element.IsEmbeddable) continue;
            // 别名覆盖了同名标签：解析走 Aliases（ResolveVisible 优先），补全也只列一次。
            if (!aliases.Add(element.Tag)) continue;

            var ns = TypeNames.NamespaceOf(element.ClrTypeDisplayName);
            // K33：@namespace 链内的命名空间不需要 @using，补全也就不能标「needs @using」——
            // 与解析共用 IsNamespaceVisible，避免「补全说要加 using、加上又不用」。
            var imported = analysis.IsNamespaceVisible(ns);

            var detail = element.IsEmbeddable ? "control" : "UI object";
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
                SortText: imported ? "0" + element.Tag : "1" + element.Tag,
                AdditionalEdits: !imported && ns is not null &&
                    UiImportService.CreateImportEdit(analysis, ns) is { } import
                        ? new[] { import }
                        : null));
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

    /// <summary>
    /// <c>@bind-</c> 的候选：**只列能作为双向目标的东西**（spec/01 §4.5）——控件是具名
    /// <c>{P}Property</c> 的公开可写属性，组件是「可写 + 有 <c>{P}Changed</c>」的参数。
    /// 事件、内容/委托参数、只读或没有回调的参数一律不列：补全列表就是「敲了就能过编译」，
    /// 不可绑的名字由诊断（EUI040）负责解释，不在候选里充当噪声。
    /// </summary>
    private static UiCompletionList Bindings(UiElementDef? element, TextSpan replacement)
    {
        var items = new List<UiCompletionItem>();
        if (element is null) return new UiCompletionList(replacement, items);

        // 组件的双向通道只有参数（{P}Changed 回调，spec/02 §4.4.3）：它身上就算有 StyledProperty
        // 属性也不是绑定目标（分析器报「not a component parameter」），所以不列。

        {
            foreach (var property in element.Properties.Values.OrderBy(static p => p.Name, StringComparer.Ordinal))
            {
                if (!property.CanWrite || ReservedAttributes.Contains(property.Name, StringComparer.Ordinal)) continue;
                items.Add(new UiCompletionItem(
                    "@bind-" + property.Name,
                    UiCompletionKind.Property,
                    "two-way · " + TypeNames.Short(property.ClrTypeDisplayName),
                    "source must be a writable field/property (EUI037)",
                    SortText: "0" + property.Name));
            }
        }

        foreach (var parameter in element.Parameters.Values.OrderBy(static p => p.Name, StringComparer.Ordinal))
        {
            if (!parameter.CanWrite || parameter.IsContent || parameter.IsDelegate) continue;
            if (parameter.ChangedCallbackName is null) continue;   // 没有 {P}Changed → 双向不成立（EUI040）
            items.Add(new UiCompletionItem(
                "@bind-" + parameter.Name,
                UiCompletionKind.Parameter,
                "two-way · " + TypeNames.Short(parameter.ClrTypeDisplayName),
                $"writes back through `{parameter.ChangedCallbackName}`",
                SortText: "1" + parameter.Name));
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
            // 值位置（光标在 "Prop: |" 之后）：按属性形态给值候选（design/19 §9）——
            // enum 属性列成员表；string/number/color 是自由字面量，静默空表。
            if (StyleValueTarget(analysis, catalog, context) is { } valueTarget)
            {
                if (valueTarget.Kind == StyleValueKind.Enum &&
                    valueTarget.Candidates is { Count: > 0 } members)
                {
                    var enumName = TypeNames.Bare(valueTarget.EnumTypeName ?? valueTarget.Name);
                    foreach (var member in members)
                        items.Add(new UiCompletionItem(member, UiCompletionKind.EnumMember, enumName));
                }
                return new UiCompletionList(replacement, items);
            }

            foreach (var property in catalog.StyleProperties.Properties.OrderBy(static p => p.Name, StringComparer.Ordinal))
            {
                items.Add(new UiCompletionItem(
                    property.Name,
                    UiCompletionKind.StyleProperty,
                    KindLabel(property.Kind),
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

        // 声明式字面量解析器（design/14 §14.7.3）：[UiLiteralParser] Candidates 提供值候选，
        // 与 EnumMembers 同一档位；无 Candidates 的解析器类型静默返回空，不猜。
        if (catalog.FindLiteralParser(shape.CanonicalKey) is { } parser)
        {
            foreach (var value in parser.Candidates)
                items.Add(new UiCompletionItem(value, UiCompletionKind.LiteralValue,
                    parser.CanonicalKey, parser.Hint));
        }

        return new UiCompletionList(replacement, items);
    }

    /// <summary>Style 值位置的形态标签（design/19 §5.2 分派表）。</summary>
    private static string KindLabel(StyleValueKind kind) => kind switch
    {
        StyleValueKind.Color => "color",
        StyleValueKind.Number => "number",
        StyleValueKind.Enum => "enum keyword",
        _ => "string",
    };

    /// <summary>
    /// Style 属性值位置的目标属性（design/19 §9 值候选）：光标前是 <c>Prop:</c>（只差空白）
    /// 时按 Ordinal 查目录。属性名位置（没跟冒号）返回 null，调用方列属性名。
    /// </summary>
    private static StylePropertyDef? StyleValueTarget(
        DocumentAnalysis analysis, UiCatalog catalog, CursorContext context)
    {
        var text = analysis.Text;
        var i = Math.Min(context.TokenStart, text.Length);
        while (i > 0 && char.IsWhiteSpace(text[i - 1])) i--;
        if (i == 0 || text[i - 1] != ':') return null;
        i--;
        var end = i;
        while (i > 0 && (char.IsLetterOrDigit(text[i - 1]) || text[i - 1] is '_' or '-')) i--;
        return i == end ? null : catalog.StyleProperties.Find(text.Substring(i, end - i));
    }

    // ---- 指令与命名空间 ----

    private static UiCompletionList Directives(TextSpan replacement)
    {
        var items = new List<UiCompletionItem>
        {
            // spec/02 §2.2：必填，且要与同名 code-behind 的命名空间一致（EUI055/EUI056）。
            new("@namespace", UiCompletionKind.Directive, "declare this template's namespace",
                "required; the generator errors when it is missing (EUI055) or disagrees with the code-behind (EUI056)",
                "@namespace ${1:Gallery}", IsSnippet: true),
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
            new("@inject", UiCompletionKind.Directive, "inject a service into the generated class",
                "resolved from Application.Current.Services at the top of InitializeUi; unregistered services fail fast (K37)",
                "@inject ${1:TypeName} ${2:Name}", IsSnippet: true),
            new("@template", UiCompletionKind.Directive, "declare a control template factory",
                "Owner is the typed templated control; each application creates a fresh visual tree",
                "@template ${1:Button}", IsSnippet: true),
            new("@code", UiCompletionKind.Directive, "class member block",
                "members merge into the partial class, same as code-behind",
                "@code {\n\t$0\n}", IsSnippet: true),
            new("@style", UiCompletionKind.Directive, "component style block",
                "Eustyle rules scoped to this component via `{Component}__` prefix",
                "@style {\n\t$0\n}", IsSnippet: true),
        };
        return new UiCompletionList(replacement, items);
    }

    private static UiCompletionList Namespaces(
        DocumentAnalysis analysis, UiCatalog catalog, CursorContext context, TextSpan replacement,
        bool serviceTypes = false)
    {
        var items = new List<UiCompletionItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var prefix = context.Prefix;
        var globalPrefix = prefix.StartsWith("global::", StringComparison.Ordinal) ? "global::" : "";
        var lookupPrefix = prefix[globalPrefix.Length..];

        // Complete the whole qualified target, including its suffix when the cursor is in
        // the middle. The alias declaration before '=' and an existing System. stay intact.
        var end = replacement.End;
        while (end < analysis.Text.Length && IsQualifiedNameCharacter(analysis.Text[end])) end++;
        replacement = new TextSpan(replacement.Start, end - replacement.Start);

        // @namespace 补的是「本模板自己的命名空间」：已声明值 > 项目根命名空间，
        // 排在控件所在命名空间之前（spec/02 §2.2 要求它与 code-behind 一致）。
        var self = analysis.Ast?.Namespace ?? analysis.Project?.Evaluation.RootNamespace;
        if (context.Directive == "namespace" && !string.IsNullOrWhiteSpace(self))
            AddNamespace(self!, "this template", "0");

        // Namespace directives are a C# surface, so non-control namespaces must come from
        // the real compilation, even when an unrelated template error prevents emission.
        var compilation = analysis.Project?.Compilation ?? analysis.Compilation;
        if (compilation is not null)
        {
            foreach (var ns in NamespaceTree(compilation.GlobalNamespace))
                AddNamespace(ns.ToDisplayString());
            if (context.IsUsingAliasTarget || serviceTypes)
            {
                int dot = lookupPrefix.LastIndexOf('.');
                var containerName = dot < 0 ? "" : lookupPrefix[..dot];
                var container = FindNamespaceOrType(compilation.GlobalNamespace, containerName);
                if (container is not null)
                    AddTypes(container.GetTypeMembers());
                // A simple alias target may also refer to a type in the template's own
                // namespace or a namespace already imported by another directive.
                if (dot < 0)
                    foreach (var ns in analysis.EnclosingNamespaces.Concat(analysis.Imports).Distinct(StringComparer.Ordinal))
                        if (FindNamespaceOrType(compilation.GlobalNamespace, ns) is { } visible)
                            AddTypes(visible.GetTypeMembers(), allowSimplePrefix: true);
            }
        }
        else
        {
            // A standalone document can lack a compilation. Keep useful catalog results,
            // without pretending they are the complete list of C# namespaces.
            foreach (var ns in catalog.Elements.Select(e => TypeNames.NamespaceOf(e.ClrTypeDisplayName)))
                if (ns is not null) AddNamespace(ns);
        }
        return new UiCompletionList(replacement, items);

        void AddNamespace(string ns, string detail = "namespace", string sortPrefix = "1")
        {
            // A template alias can only finish at an exposed UI type (EUI048). Namespace
            // segments remain useful while typing its qualified target, but namespaces
            // with no possible UI target are not alias candidates.
            if (context.IsUsingAliasTarget && !catalog.Elements.Any(element =>
                    TypeNames.NamespaceOf(element.ClrTypeDisplayName) is { } owner
                    && (owner == ns || owner.StartsWith(ns + ".", StringComparison.Ordinal)))) return;
            if (!ns.StartsWith(lookupPrefix, StringComparison.OrdinalIgnoreCase) || !seen.Add(ns)) return;
            var name = globalPrefix + ns;
            // Plain template namespace directives accept dotted names, unlike C# type
            // alias targets which may retain global::. Match the typed spelling in the
            // list, but commit the canonical namespace so accepting repairs the directive.
            items.Add(new UiCompletionItem(name, UiCompletionKind.Namespace, detail,
                InsertText: context.IsUsingAliasTarget ? name : ns,
                SortText: sortPrefix + name, FilterText: name));
        }

        void AddTypes(IEnumerable<INamedTypeSymbol> types, bool allowSimplePrefix = false)
        {
            foreach (var type in types.OrderBy(static t => t.Name, StringComparer.Ordinal))
            {
                if (!type.CanBeReferencedByName || !compilation!.IsSymbolAccessibleWithin(type, compilation.Assembly)) continue;
                // K37：@inject 的类型名不收泛型（词法层拒绝，spec/01 §4.18）；别名目标的泛型
                // 由目录过滤兜住，这里显式同口径。
                if (serviceTypes && type.Arity != 0) continue;
                var name = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
                    SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions & ~SymbolDisplayMiscellaneousOptions.UseSpecialTypes));
                // Use the compiler's exposure catalog: deriving UiObject alone is not
                // enough for nested, abstract, generic or explicitly ignored UI types.
                // @inject 的服务类型不限 UI 目录（任何可访问类型都可能是服务）。
                if (!serviceTypes && catalog.FindByTypeDisplayName(name) is null) continue;
                if (name.StartsWith("global::", StringComparison.Ordinal)) name = name[8..];
                if (!(allowSimplePrefix ? type.Name : name).StartsWith(lookupPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                name = globalPrefix + name;
                if (!seen.Add(name)) continue;
                items.Add(new UiCompletionItem(name, UiCompletionKind.Class,
                    serviceTypes ? "service type" : "UI type alias target",
                    SortText: "0" + name));
            }
        }
    }

    private static bool IsQualifiedNameCharacter(char c) => char.IsLetterOrDigit(c) || c is '_' or '.' or ':' or '@';

    private static IEnumerable<INamespaceSymbol> NamespaceTree(INamespaceSymbol root)
    {
        foreach (var ns in root.GetNamespaceMembers().OrderBy(static n => n.Name, StringComparer.Ordinal))
        {
            yield return ns;
            foreach (var nested in NamespaceTree(ns)) yield return nested;
        }
    }

    private static INamespaceOrTypeSymbol? FindNamespaceOrType(INamespaceSymbol root, string name)
    {
        INamespaceOrTypeSymbol current = root;
        foreach (var part in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var member = current.GetMembers(part).OfType<INamespaceOrTypeSymbol>().FirstOrDefault();
            if (member is null) return null;
            current = member;
        }
        return current;
    }

    // ---- 文档文本 ----

    private static string DescribeElement(UiElementDef element)
    {
        var lines = new List<string>
        {
            $"`{TypeNames.Short(element.ClrTypeDisplayName)}`",
            element.IsEmbeddable ? "control" : "UI object",
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
        "Part" => "named visual part of a control template; resolved by TemplateInstance.FindPart<T>()",
        _ => "reserved",
    };
}

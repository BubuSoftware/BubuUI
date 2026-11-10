using System;
using System.Collections.Generic;
using System.Linq;
using EclipseUI.Compiler;
using Microsoft.CodeAnalysis;

namespace EclipseUI.Compiler.Metadata;

/// <summary>
/// 可样式属性目录的符号扫描器（design/17 §4.3「单一来源」）：从 <c>StyleBuilder</c> 的公开
/// setter 推出「允许写什么、期望什么值形态」，从 <c>StyledProperty&lt;T&gt;</c> 静态字段推出
/// 「值最终落到哪个属性对象」。Parser / 诊断 / Emitter 只读产物，不再各自手维护一份表。
///
/// 纯函数：同一个 Compilation 得到结构相等的目录，增量缓存交给生成器管道。
/// 构建不抛异常：不满足约定的候选进 <see cref="StylePropertyCatalog.Problems"/>。
/// </summary>
public static class StylePropertyCatalogFactory
{
    private const string StyleBuilderMetadataName = "EclipseUI.Rendering.StyleBuilder";
    private const string StyledPropertyGenericMetadataName = "EclipseUI.StyledProperty`1";
    private const string ColorMetadataName = "EclipseUI.Rendering.Color";
    private const string ThemeColorMetadataName = "EclipseUI.Rendering.ThemeColor";

    private static readonly SymbolDisplayFormat Qualified = SymbolDisplayFormat.FullyQualifiedFormat;

    /// <summary>字段必须带声明类型：FullyQualifiedFormat 默认不含 MemberOptions.IncludeContainingType，
    /// 直接显示字段只会得到裸名（生成代码里找不到符号）。</summary>
    private static readonly SymbolDisplayFormat QualifiedMember =
        SymbolDisplayFormat.FullyQualifiedFormat.WithMemberOptions(
            SymbolDisplayMemberOptions.IncludeContainingType);

    /// <summary>元数据声明问题统一归 EUI026（与模板目录同口径）。</summary>
    private const string ProblemCode = UiDiagnostics.MetadataDeclarationInvalid;

    public static StylePropertyCatalog Build(Compilation compilation)
    {
        if (compilation is null) throw new ArgumentNullException(nameof(compilation));

        var builder = compilation.GetTypeByMetadataName(StyleBuilderMetadataName);
        var styledProperty = compilation.GetTypeByMetadataName(StyledPropertyGenericMetadataName);
        // 没有渲染程序集（纯 C# 测试编译）→ 空目录，而不是异常
        if (builder is null || styledProperty is null) return StylePropertyCatalog.Empty;

        var color = compilation.GetTypeByMetadataName(ColorMetadataName);
        var themeColor = compilation.GetTypeByMetadataName(ThemeColorMetadataName);
        var problems = new List<UiMetadataProblem>();
        var fields = CollectPropertyFields(compilation, styledProperty);

        // 同名重载（Color / ThemeColor）合并为一条属性；声明序无关，最终按名字排序。
        var groups = new SortedDictionary<string, (StyleValueKind Kind, bool HasColor, bool HasTheme)>(StringComparer.Ordinal);
        foreach (var method in builder.GetMembers().OfType<IMethodSymbol>())
        {
            if (method.MethodKind != MethodKind.Ordinary) continue;
            if (method.DeclaredAccessibility != Accessibility.Public) continue;   // Build() 是 internal
            if (method.IsStatic || method.IsGenericMethod) continue;              // Set<T> 是通用逃生口，不是样式属性
            if (method.Parameters.Length != 1)
            {
                Problem(problems, builder, method.Name,
                    $"StyleBuilder setter '{method.Name}' must take exactly one value parameter; " +
                    "multi-value style properties need a catalog entry first");
                continue;
            }

            var parameter = method.Parameters[0].Type;
            var theme = themeColor is not null && SymbolEqualityComparer.Default.Equals(
                (parameter as INamedTypeSymbol)?.OriginalDefinition, themeColor);
            var kind = KindOf(parameter, color, themeColor);
            if (kind is null)
            {
                Problem(problems, builder, method.Name,
                    $"StyleBuilder setter '{method.Name}' has unsupported value type '{parameter.ToDisplayString()}'; " +
                    "style values are limited to Color/ThemeColor colors and double numbers");
                continue;
            }

            if (!groups.TryGetValue(method.Name, out var group))
                group = (kind.Value, false, false);
            else if (group.Kind != kind.Value)
            {
                Problem(problems, builder, method.Name,
                    $"StyleBuilder setter '{method.Name}' has overloads with conflicting value kinds");
                continue;
            }
            groups[method.Name] = (group.Kind,
                group.HasColor || (!theme && kind == StyleValueKind.Color),
                group.HasTheme || theme);
        }

        var properties = new List<StylePropertyDef>();
        // 名字即 C# 方法名，Ordinal 分组天然唯一；样式表侧严格区分大小写，无需做归一化去重。
        foreach (var pair in groups)
        {
            var name = pair.Key;
            var group = pair.Value;
            var field = ResolveField(fields, name + "Property", builder, problems);
            if (field is null) continue;
            var fieldKind = KindOf(((INamedTypeSymbol)field.Type).TypeArguments[0], color, themeColor);
            if (fieldKind is null || fieldKind != group.Kind)
            {
                Problem(problems, builder, name,
                    $"StyledProperty field '{name}Property' has value type " +
                    $"'{((INamedTypeSymbol)field.Type).TypeArguments[0].ToDisplayString()}', " +
                    $"which does not match StyleBuilder setter '{name}'");
                continue;
            }

            string? resourceField = null;
            if (group.HasTheme)
            {
                var resource = ResolveField(fields, name + "ResourceProperty", builder, problems);
                // 缺资源字段不是灾难：目录仍可用，只是该属性拒绝 theme()（解析期 EUI052）
                resourceField = resource is null ? null : Display(resource);
            }
            else if (ResolveField(fields, name + "ResourceProperty", builder, problems: null) is not null)
            {
                Problem(problems, builder, name,
                    $"StyledProperty field '{name}ResourceProperty' exists but StyleBuilder has no " +
                    $"'{name}(ThemeColor)' overload; add the overload to expose theme() in stylesheets");
            }

            properties.Add(new StylePropertyDef(
                name, group.Kind, Display(field), resourceField, Display(field.ContainingType)));
        }

        return new StylePropertyCatalog(properties, problems);
    }

    /// <summary>收集候选程序集里全部 public static 的 <c>StyledProperty&lt;T&gt;</c> 字段，按字段名分桶。</summary>
    private static Dictionary<string, List<IFieldSymbol>> CollectPropertyFields(
        Compilation compilation, INamedTypeSymbol styledProperty)
    {
        var fields = new Dictionary<string, List<IFieldSymbol>>(StringComparer.Ordinal);
        foreach (var assembly in UiCatalogFactory.CandidateAssembliesFor(compilation))
        {
            foreach (var type in UiCatalogFactory.WalkTypesFor(assembly.GlobalNamespace))
            {
                foreach (var member in type.GetMembers().OfType<IFieldSymbol>())
                {
                    if (!member.IsStatic || member.DeclaredAccessibility != Accessibility.Public) continue;
                    if (member.Type is not INamedTypeSymbol fieldType ||
                        !SymbolEqualityComparer.Default.Equals(fieldType.OriginalDefinition, styledProperty))
                        continue;
                    if (!fields.TryGetValue(member.Name, out var bucket))
                        fields[member.Name] = bucket = [];
                    bucket.Add(member);
                }
            }
        }
        return fields;
    }

    private static IFieldSymbol? ResolveField(
        IReadOnlyDictionary<string, List<IFieldSymbol>> fields, string fieldName,
        INamedTypeSymbol builder, List<UiMetadataProblem>? problems)
    {
        if (!fields.TryGetValue(fieldName, out var candidates) || candidates.Count == 0)
        {
            if (fieldName.EndsWith("Property", StringComparison.Ordinal) && problems is not null)
                Problem(problems, builder, fieldName,
                    $"no public static StyledProperty field named '{fieldName}' was found; " +
                    "a StyleBuilder setter needs a matching property field so inline Style= can be emitted");
            return null;
        }
        if (candidates.Count == 1) return candidates[0];
        if (problems is not null)
            Problem(problems, builder, fieldName,
                $"StyledProperty field '{fieldName}' is declared in {candidates.Count} assemblies; " +
                "the name is ambiguous for style emission");
        return null;
    }

    /// <summary>从 CLR 类型派生值形态（可空包装解一层）。</summary>
    private static StyleValueKind? KindOf(ITypeSymbol type, INamedTypeSymbol? color, INamedTypeSymbol? themeColor)
    {
        if (type.SpecialType == SpecialType.System_Double) return StyleValueKind.Number;
        var named = type as INamedTypeSymbol;
        if (named is { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T })
            named = named.TypeArguments[0] as INamedTypeSymbol;
        if (named is null) return null;
        if (named.SpecialType == SpecialType.System_Double) return StyleValueKind.Number;
        if (color is not null && SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, color))
            return StyleValueKind.Color;
        if (themeColor is not null && SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, themeColor))
            return StyleValueKind.Color;
        return null;
    }

    private static string Display(IFieldSymbol field) => field.ToDisplayString(QualifiedMember);

    private static string Display(INamedTypeSymbol type) => type.ToDisplayString(Qualified);

    private static void Problem(List<UiMetadataProblem> problems, INamedTypeSymbol builder,
        string member, string message) =>
        problems.Add(new UiMetadataProblem(ProblemCode, message, Display(builder), member));
}

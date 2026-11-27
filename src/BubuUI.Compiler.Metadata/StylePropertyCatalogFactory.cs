using System;
using System.Collections.Generic;
using System.Linq;
using BubuUI.Compiler;
using Microsoft.CodeAnalysis;

namespace BubuUI.Compiler.Metadata;

/// <summary>
/// 可样式属性目录的符号扫描器（design/17 §4.3「单一来源」；design/19 §4 属性自定义化）：
/// 进料口是两条通道的并集——
/// ① <b>内建</b>：从 <c>StyleBuilder</c> 的公开 setter 推出「允许写什么、期望什么值形态」，
///    从 <c>StyledProperty&lt;T&gt;</c> 静态字段推出「值最终落到哪个属性对象」；
/// ② <b>标注</b>（design/19，opt-in）：带 <c>[UiStyleProperty]</c> 的 public static readonly
///    <c>StyledProperty&lt;T&gt;</c> 字段（全部候选程序集，显式标注同时是文档——不扫全部
///    StyledProperty 字段，避免内部属性污染目录），属性名/值形态从字段符号派生。
/// Parser / 诊断 / Emitter 只读产物，不再各自手维护一份表。
///
/// 纯函数：同一个 Compilation 得到结构相等的目录，增量缓存交给生成器管道。
/// 构建不抛异常：不满足约定的候选进 <see cref="StylePropertyCatalog.Problems"/>——
/// 内建通道沿用 EUI026，标注通道报 EUI058/EUI059（design/19 §8）。
/// </summary>
public static class StylePropertyCatalogFactory
{
    private const string StyleBuilderMetadataName = "BubuUI.Rendering.StyleBuilder";
    private const string StyledPropertyGenericMetadataName = "BubuUI.StyledProperty`1";
    private const string ColorMetadataName = "BubuUI.Rendering.Color";
    private const string ThemeColorMetadataName = "BubuUI.Rendering.ThemeColor";
    private const string UiStylePropertyAttributeName = "BubuUI.UiStylePropertyAttribute";

    private static readonly SymbolDisplayFormat Qualified = SymbolDisplayFormat.FullyQualifiedFormat;

    /// <summary>字段必须带声明类型：FullyQualifiedFormat 默认不含 MemberOptions.IncludeContainingType，
    /// 直接显示字段只会得到裸名（生成代码里找不到符号）。</summary>
    private static readonly SymbolDisplayFormat QualifiedMember =
        SymbolDisplayFormat.FullyQualifiedFormat.WithMemberOptions(
            SymbolDisplayMemberOptions.IncludeContainingType);

    /// <summary>内建通道的元数据声明问题统一归 EUI026（与模板目录同口径）。</summary>
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
        var (fields, annotated) = CollectPropertyFields(compilation, styledProperty);

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
            var derived = Derive(field, color, themeColor);
            if (derived.Kind is null || derived.Kind != group.Kind)
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
                name, group.Kind, Display(field), resourceField, Display(field.ContainingType),
                IsBuiltIn: true, derived.EnumTypeName, derived.Candidates, CanAnimate(field), IsUiColor(field)));
        }

        // 标注通道（design/19 §4.3）：并集进目录，冲突规则 §7——同名即报（Error），
        // 内建胜出；两个用户声明同名则双双拒绝。
        properties.AddRange(CollectAnnotated(annotated, styledProperty, groups.Keys,
            color, themeColor, problems));

        return new StylePropertyCatalog(properties, problems);
    }

    /// <summary>
    /// 标注通道（design/19 §4.3）：收集 <c>[UiStyleProperty]</c> 字段 → 派生属性名与值形态
    /// → 冲突查重。声明不合规报 <c>EUI058</c> 不进目录、不抛异常（构建不抛异常口径）。
    /// </summary>
    private static List<StylePropertyDef> CollectAnnotated(
        IReadOnlyList<IFieldSymbol> annotated, INamedTypeSymbol styledProperty,
        IReadOnlyCollection<string> builtInNames,
        INamedTypeSymbol? color, INamedTypeSymbol? themeColor,
        List<UiMetadataProblem> problems)
    {
        if (annotated.Count == 0) return [];

        // 第一道：字段声明合规（类型闭包 / readonly / Name 解析），合规者按名字分组。
        var valid = new SortedDictionary<string, List<IFieldSymbol>>(StringComparer.Ordinal);
        foreach (var field in annotated)
        {
            var declaring = Display(field.ContainingType);
            if (!field.IsStatic || field.DeclaredAccessibility != Accessibility.Public)
            {
                Problem(problems, field, UiDiagnostics.StylePropertyDeclarationInvalid,
                    $"[UiStyleProperty] field '{field.Name}' on '{declaring}' must be public static readonly");
                continue;
            }
            if (!field.IsReadOnly)
            {
                Problem(problems, field, UiDiagnostics.StylePropertyDeclarationInvalid,
                    $"[UiStyleProperty] field '{field.Name}' on '{declaring}' must be readonly");
                continue;
            }
            if (field.Type is not INamedTypeSymbol fieldType ||
                !SymbolEqualityComparer.Default.Equals(fieldType.OriginalDefinition, styledProperty) ||
                fieldType.TypeArguments.Length != 1)
            {
                Problem(problems, field, UiDiagnostics.StylePropertyDeclarationInvalid,
                    $"[UiStyleProperty] field '{field.Name}' on '{declaring}' must be a " +
                    "StyledProperty<T> field (closed generic — T must be a real type, not a type parameter)");
                continue;
            }
            // T 含泛型形参：发射出的 s.Set<T>(…) 在宿主代码里不可解析（R3）。
            if (ContainsTypeParameter(fieldType.TypeArguments[0]))
            {
                Problem(problems, field, UiDiagnostics.StylePropertyDeclarationInvalid,
                    $"[UiStyleProperty] field '{field.Name}' on '{declaring}' has an open generic " +
                    $"value type '{fieldType.TypeArguments[0].ToDisplayString()}'; style values must be closed types");
                continue;
            }

            var name = PropertyNameOf(field);
            if (name is not { Length: > 0 })
            {
                Problem(problems, field, UiDiagnostics.StylePropertyDeclarationInvalid,
                    $"[UiStyleProperty] field '{field.Name}' on '{declaring}' resolves to an empty " +
                    "property name; name the property explicitly ([UiStyleProperty(Name = \"...\")])");
                continue;
            }
            if (!valid.TryGetValue(name, out var bucket))
                valid[name] = bucket = [];
            bucket.Add(field);
        }

        // 第二道：冲突查重（design/19 §7）——内建同名 → 用户声明败（EUI059，目录取内建）；
        // 两个用户声明同名（跨程序集/同程序集皆然）→ 双双拒绝（对齐 ResolveField 的多程序集歧义口径）。
        var result = new List<StylePropertyDef>();
        foreach (var pair in valid)
        {
            var name = pair.Key;
            var candidates = pair.Value;
            if (builtInNames.Contains(name))
            {
                Problem(problems, candidates[0], UiDiagnostics.StylePropertyNameConflict,
                    $"style property '{name}' conflicts with a built-in StyleBuilder property; " +
                    "the built-in wins and this declaration leaves the catalog — rename it " +
                    "([UiStyleProperty(Name = \"...\")])");
                continue;
            }
            if (candidates.Count > 1)
            {
                Problem(problems, candidates[0], UiDiagnostics.StylePropertyNameConflict,
                    $"style property '{name}' is declared {candidates.Count} times " +
                    $"({string.Join(", ", candidates.Select(static f => Display(f.ContainingType)).Distinct(StringComparer.Ordinal))}); " +
                    "all declarations leave the catalog — the name is ambiguous");
                continue;
            }

            var field = candidates[0];
            var derived = Derive(field, color, themeColor);
            if (derived.Kind is null)
            {
                Problem(problems, field, UiDiagnostics.StylePropertyDeclarationInvalid,
                    $"[UiStyleProperty] field '{field.Name}' has unsupported value type " +
                    $"'{((INamedTypeSymbol)field.Type).TypeArguments[0].ToDisplayString()}'; " +
                    "style values are double numbers, Color/ThemeColor colors, enums or strings");
                continue;
            }
            result.Add(new StylePropertyDef(
                name, derived.Kind.Value, Display(field), ResourceField: null, Display(field.ContainingType),
                IsBuiltIn: false, derived.EnumTypeName, derived.Candidates, CanAnimate(field), IsUiColor(field)));
        }
        return result;
    }

    /// <summary>标注属性名：显式 <c>Name</c> → 用之；否则字段名去 <c>Property</c> 后缀（去后为空 → null）。</summary>
    private static string? PropertyNameOf(IFieldSymbol field)
    {
        var attribute = FindAttribute(field);
        if (attribute is not null)
            foreach (var pair in attribute.NamedArguments)
                if (pair.Key == "Name" && pair.Value.Value is string explicitName)
                    return explicitName.Length == 0 ? null : explicitName;

        return field.Name.EndsWith("Property", StringComparison.Ordinal) &&
               field.Name.Length > "Property".Length
            ? field.Name.Substring(0, field.Name.Length - "Property".Length)
            : null;
    }

    private static AttributeData? FindAttribute(IFieldSymbol field) =>
        field.GetAttributes().FirstOrDefault(static a =>
            a.AttributeClass is { } attribute &&
            attribute.MetadataName == "UiStylePropertyAttribute" &&
            string.Equals(attribute.ContainingNamespace.ToDisplayString(), "BubuUI", StringComparison.Ordinal));

    /// <summary>T（或其泛型实参链）是否含泛型形参（open generic 不可发射）。</summary>
    private static bool ContainsTypeParameter(ITypeSymbol type)
    {
        if (type.TypeKind == TypeKind.TypeParameter) return true;
        if (type is INamedTypeSymbol named)
            return named.TypeArguments.Any(ContainsTypeParameter);
        if (type is IArrayTypeSymbol array)
            return ContainsTypeParameter(array.ElementType);
        return false;
    }

    /// <summary>收集候选程序集里全部 public static 的 <c>StyledProperty&lt;T&gt;</c> 字段：
    /// 按字段名分桶（内建字段解析用）+ 标注字段的平铺清单（标注通道用）。</summary>
    private static (Dictionary<string, List<IFieldSymbol>> ByName, List<IFieldSymbol> Annotated)
        CollectPropertyFields(Compilation compilation, INamedTypeSymbol styledProperty)
    {
        var fields = new Dictionary<string, List<IFieldSymbol>>(StringComparer.Ordinal);
        var annotated = new List<IFieldSymbol>();
        foreach (var assembly in UiCatalogFactory.CandidateAssembliesFor(compilation))
        {
            foreach (var type in UiCatalogFactory.WalkTypesFor(assembly.GlobalNamespace))
            {
                foreach (var member in type.GetMembers().OfType<IFieldSymbol>())
                {
                    // 标注收集在类型过滤之前：[UiStyleProperty] 标在非 StyledProperty 字段上
                    // 也是作者意图，要进 CollectAnnotated 报 EUI058，而不是被静默忽略。
                    if (FindAttribute(member) is not null) annotated.Add(member);
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
        return (fields, annotated);
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
        var owners = builder.GetAttributes()
            .Where(a => a.AttributeClass?.ToDisplayString() == "BubuUI.UiStyleOwnerAttribute")
            .Select(a => a.ConstructorArguments.Length == 1 ? a.ConstructorArguments[0].Value as INamedTypeSymbol : null)
            .Where(o => o is not null).ToArray();
        if (owners.Length > 0)
        {
            var owned = candidates.Where(c => owners.Any(o => SymbolEqualityComparer.Default.Equals(o, c.ContainingType))).ToArray();
            if (owned.Length == 1) return owned[0];
        }
        if (candidates.Count == 1) return candidates[0];
        // 多候选时未标注者优先：用户带 [UiStyleProperty] 的同名声明是 EUI059 冲突方
        // （用户败、内建胜），它不该让内建字段本身落入「多程序集歧义」而从目录消失。
        var builtIn = candidates.Where(static c => FindAttribute(c) is null).ToArray();
        if (builtIn.Length == 1) return builtIn[0];
        if (problems is not null)
            Problem(problems, builder, fieldName,
                $"StyledProperty field '{fieldName}' is declared in {candidates.Count} assemblies; " +
                "the name is ambiguous for style emission");
        return null;
    }

    /// <summary>值形态派生（design/19 §5.2 分派表的编译期一半）：从字段泛型实参 T 推出
    /// kind + 枚举类型名/成员表（发射期零符号依赖——enum 信息在扫描期算好存进 def）。</summary>
    private static (StyleValueKind? Kind, string? EnumTypeName, IReadOnlyList<string>? Candidates) Derive(
        IFieldSymbol field, INamedTypeSymbol? color, INamedTypeSymbol? themeColor)
    {
        var kind = KindOf(((INamedTypeSymbol)field.Type).TypeArguments[0], color, themeColor);
        if (kind is not StyleValueKind.Enum) return (kind, null, null);

        var enumType = (INamedTypeSymbol)((INamedTypeSymbol)field.Type).TypeArguments[0];
        if (enumType.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
            enumType = (INamedTypeSymbol)enumType.TypeArguments[0];
        var members = enumType.GetMembers()
            .OfType<IFieldSymbol>()
            .Where(static f => f.IsConst)
            .Select(static f => f.Name)
            .OrderBy(static n => n, StringComparer.Ordinal)
            .ToArray();
        return (StyleValueKind.Enum, Display(enumType), members);
    }

    /// <summary>从 CLR 类型派生值形态（可空包装解一层）。design/19 §5.2 扩展：
    /// 枚举 → keyword、string → string；其余仍只认 double / Color / ThemeColor。</summary>
    private static StyleValueKind? KindOf(ITypeSymbol type, INamedTypeSymbol? color, INamedTypeSymbol? themeColor)
    {
        if (type.SpecialType == SpecialType.System_Double) return StyleValueKind.Number;
        if (type.ToDisplayString() == "BubuUI.UiColor") return StyleValueKind.Color;
        var named = type as INamedTypeSymbol;
        if (named is { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T })
            named = named.TypeArguments[0] as INamedTypeSymbol;
        if (named is null) return null;
        if (named.SpecialType == SpecialType.System_Double) return StyleValueKind.Number;
        if (color is not null && SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, color))
            return StyleValueKind.Color;
        if (themeColor is not null && SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, themeColor))
            return StyleValueKind.Color;
        if (named.TypeKind == TypeKind.Enum) return StyleValueKind.Enum;
        if (named.SpecialType == SpecialType.System_String) return StyleValueKind.Text;
        return null;
    }

    private static string Display(IFieldSymbol field) => field.ToDisplayString(QualifiedMember);

    private static bool IsUiColor(IFieldSymbol field) => ((INamedTypeSymbol)field.Type).TypeArguments[0].ToDisplayString() == "BubuUI.UiColor";

    private static bool CanAnimate(IFieldSymbol field)
    {
        if (!field.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == "BubuUI.UiAnimatableAttribute")) return false;
        var type = ((INamedTypeSymbol)field.Type).TypeArguments[0];
        if (type.SpecialType == SpecialType.System_Double || type.ToDisplayString() == "BubuUI.UiColor") return true;
        return type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable &&
            (nullable.TypeArguments[0].ToDisplayString() == "BubuUI.Rendering.Color" ||
                nullable.TypeArguments[0].SpecialType == SpecialType.System_Double);
    }

    private static string Display(INamedTypeSymbol type) => type.ToDisplayString(Qualified);

    private static void Problem(List<UiMetadataProblem> problems, INamedTypeSymbol builder,
        string member, string message) =>
        problems.Add(new UiMetadataProblem(ProblemCode, message, Display(builder), member));

    /// <summary>标注通道问题：定位到字段声明类型（EUI058/EUI059 自带源定位出口）。</summary>
    private static void Problem(List<UiMetadataProblem> problems, IFieldSymbol field,
        string code, string message) =>
        problems.Add(new UiMetadataProblem(code, message, Display(field.ContainingType), field.Name));
}

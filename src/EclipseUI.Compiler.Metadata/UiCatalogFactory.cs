using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace EclipseUI.Compiler.Metadata;

/// <summary>
/// 从 <see cref="Compilation"/> 的符号派生模板目录（design/14 §14.10.2）。
/// 纯函数：同一个 Compilation 得到结构相等的目录，缓存交给增量管道（§14.10.3）。
/// 构建不抛异常：不满足声明规则的候选进 <c>Problems</c> / <c>HiddenTags</c>（§14.5.4）。
/// </summary>
public static class UiCatalogFactory
{
    /// <summary>程序集过滤锚点：只扫源程序集与直接引用运行时的程序集。</summary>
    public const string RuntimeAssemblyName = "EclipseUI.Runtime";

    // 锚点一律按 metadata name 判定，不按程序集名匹配（design/14 §14.4.6）
    private const string ElementMetadataName = "EclipseUI.Element";
    private const string ComponentMetadataName = "EclipseUI.Component";
    private const string RoutedEventMetadataName = "EclipseUI.RoutedEvent";
    private const string StyledPropertyGenericMetadataName = "EclipseUI.StyledProperty`1";
    private const string AttachedPropertyGenericMetadataName = "EclipseUI.AttachedProperty`1";
    private const string TextContentHostMetadataName = "EclipseUI.ITextContentHost";
    private const string SignalGenericMetadataName = "EclipseUI.Signal`1";

    private const string UiContentAttributeName = "EclipseUI.UiContentAttribute";
    private const string UiIgnoreAttributeName = "EclipseUI.UiIgnoreAttribute";
    private const string UiParameterAttributeName = "EclipseUI.UiParameterAttribute";
    private const string UiSlotAttributeName = "EclipseUI.UiSlotAttribute";

    /// <summary>运行时只有 <c>RoutedEventArgs</c> 一种事件实参形状；typed args 属 G4（design/14 §14.4.3）。</summary>
    internal const string DefaultEventArgsType = "global::EclipseUI.RoutedEventArgs";

    internal static readonly SymbolDisplayFormat Qualified = SymbolDisplayFormat.FullyQualifiedFormat;

    /// <summary>
    /// R4「全 global:: 限定」在原生类型上的补齐：FullyQualifiedFormat 含 UseSpecialTypes，
    /// 会把内置类型打印成关键字（int/string?），而生成代码与 spec/02 §4.3.3/§4.4.3 的冻结文本
    /// 要求 global::System.Int32/global::System.String?。改为去掉 UseSpecialTypes 的派生格式，
    /// 让 Roslyn 符号显示引擎按符号结构直接产出限定名（泛型/数组/可空/元组由符号递归处理，
    /// 不做任何文本后处理——原 <c>SpecialTypeWord</c> 正则逐词替换已移除）。
    /// 只作用于参数/委托显示（组件推送与 Comparer 文本）；fully-qualified 用户类型不含裸
    /// 关键字记号，不受影响。
    /// </summary>
    internal static readonly SymbolDisplayFormat QualifiedFrozen =
        SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
            SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions
                & ~SymbolDisplayMiscellaneousOptions.UseSpecialTypes);


    /// <summary>保留属性名（design/01 §4.13）；控件暴露它们会与语法冲突 → 元数据问题。</summary>
    private static readonly string[] ReservedNames = { "Key", "Ref", "Class", "Style" };

    public static UiCatalog Build(Compilation compilation)
    {
        if (compilation is null) throw new ArgumentNullException(nameof(compilation));
        return new Scanner(compilation).Build();
    }

    /// <summary>扫描口径复用出口：样式属性目录与标签目录共用同一套候选程序集/类型遍历规则。</summary>
    internal static IEnumerable<IAssemblySymbol> CandidateAssembliesFor(Compilation compilation) =>
        Scanner.CandidateAssemblies(compilation);

    internal static IEnumerable<INamedTypeSymbol> WalkTypesFor(INamespaceSymbol ns) => Scanner.WalkTypes(ns);

    private sealed class Scanner
    {
        private readonly Compilation _compilation;
        private readonly INamedTypeSymbol? _element;
        private readonly INamedTypeSymbol? _component;
        private readonly INamedTypeSymbol? _routedEvent;
        private readonly INamedTypeSymbol? _styledPropertyGeneric;
        private readonly INamedTypeSymbol? _attachedPropertyGeneric;
        private readonly INamedTypeSymbol? _textContentHost;
        private readonly INamedTypeSymbol? _signalGeneric;
        private readonly INamedTypeSymbol? _nullableGeneric;
        private readonly INamedTypeSymbol? _elementReadOnlyList;
        private readonly INamedTypeSymbol? _spanGeneric;
        private readonly INamedTypeSymbol? _readOnlySpanGeneric;

        public Scanner(Compilation compilation)
        {
            _compilation = compilation;
            _element = compilation.GetTypeByMetadataName(ElementMetadataName);
            _component = compilation.GetTypeByMetadataName(ComponentMetadataName);
            _routedEvent = compilation.GetTypeByMetadataName(RoutedEventMetadataName);
            _styledPropertyGeneric = compilation.GetTypeByMetadataName(StyledPropertyGenericMetadataName);
            _attachedPropertyGeneric = compilation.GetTypeByMetadataName(AttachedPropertyGenericMetadataName);
            _textContentHost = compilation.GetTypeByMetadataName(TextContentHostMetadataName);
            _signalGeneric = compilation.GetTypeByMetadataName(SignalGenericMetadataName);
            _nullableGeneric = compilation.GetTypeByMetadataName("System.Nullable`1");
            _elementReadOnlyList = compilation.GetTypeByMetadataName("System.Collections.Generic.IReadOnlyList`1");
            _spanGeneric = compilation.GetTypeByMetadataName("System.Span`1");
            _readOnlySpanGeneric = compilation.GetTypeByMetadataName("System.ReadOnlySpan`1");
        }

        public UiCatalog Build()
        {
            var exposed = new List<UiElementDef>();
            var hidden = new List<UiHiddenTag>();
            var problems = new List<UiMetadataProblem>();
            var attached = new List<UiAttachedPropDef>();
            var attachedSeen = new Dictionary<(string Owner, string Name), string>();

            // 样式属性表面与标签表面同一次符号扫描产出（design/17 §4.3 单一来源）
            var styleProperties = StylePropertyCatalogFactory.Build(_compilation);

            // 没有运行时锚点的编译（例如纯 C# 测试项目）得到空标签目录，而不是异常
            if (_element is null && _component is null)
                return new UiCatalog(exposed, hidden, problems, styleProperties: styleProperties);

            foreach (var assembly in CandidateAssemblies(_compilation))
            {
                // C5：来源标记——EUI019/020/021 使用点口径只对引用程序集成立（design/14 §14.5.4）；
                // 源项目控件的声明错误归 EUI026，用点仍落 EUI002（design/14 §14.9.1 v2 口径）。
                var fromReferencedAssembly =
                    !SymbolEqualityComparer.Default.Equals(assembly, _compilation.Assembly);
                foreach (var type in WalkTypes(assembly.GlobalNamespace))
                {
                    if (IsStaticEventHost(type))
                    {
                        problems.Add(Problem(UiDiagnostics.MetadataDeclarationInvalid, type, null,
                            $"'{Display(type)}' is a static host class declaring RoutedEvent fields; declare them on an Element-derived type"));
                        continue;
                    }

                    // G4：附加属性宿主（design/14 §14.5.5）。与标签表面独立：宿主不要求 Element 派生、
                    // 不要求可实例化（静态类/抽象类都可以做宿主），只要求顶层、public 可见、无 [UiIgnore]。
                    CollectAttachedProperties(type, attached, attachedSeen, problems);

                    var origin = OriginOf(type);
                    if (origin is null) continue;
                    if (!IsPubliclyVisible(type)) continue;              // 不可见 → 使用点 EUI001
                    if (HasIgnore(type)) continue;                       // 完全退出模板表面
                    if (!CanExpose(type, out var reasonCode, out var reason))
                    {
                        hidden.Add(new UiHiddenTag(type.Name, Display(type), reasonCode, reason));
                        continue;
                    }

                    // C6：`<Slot>`/`<ContentSlot>` 是解析器特判的保留标签名（spec/01 §3.1）——
                    // 目录不再「同名控件让位」，而是把占用者挡在面板外并记声明问题。
                    var exposedTag = type.Name;
                    if (exposedTag is "Slot" or "ContentSlot")
                    {
                        hidden.Add(new UiHiddenTag(exposedTag, Display(type), UiDiagnostics.StyleAttributeInvalid,
                            $"'{exposedTag}' is a reserved template tag (named slots, spec/01 §4.15); " +
                            "rename the type"));
                        problems.Add(Problem(UiDiagnostics.StyleAttributeInvalid, type, null,
                            $"'{Display(type)}' registers the reserved template tag '{exposedTag}' (named slots); " +
                            "rename the type"));
                        continue;
                    }

                    exposed.Add(Describe(type, origin.Value, fromReferencedAssembly));
                }
            }

            return new UiCatalog(exposed, hidden, problems, attached, styleProperties);
        }

        // ---- 扫描范围 ----

        /// <summary>候选程序集：源程序集 + 直接引用运行时的引用程序集。
        /// internal 供 <see cref="StylePropertyCatalogFactory"/> 复用同一扫描口径。</summary>
        internal static IEnumerable<IAssemblySymbol> CandidateAssemblies(Compilation compilation)
        {
            yield return compilation.Assembly;
            foreach (var reference in compilation.References)
            {
                if (compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol assembly) continue;
                if (SymbolEqualityComparer.Default.Equals(assembly, compilation.Assembly)) continue;
                if (!ReferencesRuntime(assembly)) continue;
                yield return assembly;
            }
        }

        internal static bool ReferencesRuntime(IAssemblySymbol assembly)
        {
            if (assembly.Identity.Name == RuntimeAssemblyName) return true;
            foreach (var module in assembly.Modules)
                foreach (var identity in module.ReferencedAssemblies)
                    if (identity.Name == RuntimeAssemblyName) return true;
            return false;
        }

        /// <summary>命名空间/嵌套类型全遍历（internal 供样式属性目录复用）。</summary>
        internal static IEnumerable<INamedTypeSymbol> WalkTypes(INamespaceSymbol namespaceSymbol)
        {
            foreach (var member in namespaceSymbol.GetMembers())
            {
                if (member is INamespaceSymbol child)
                {
                    foreach (var nested in WalkTypes(child)) yield return nested;
                }
                else if (member is INamedTypeSymbol type)
                {
                    yield return type;
                    foreach (var nested in WalkTypeMembers(type)) yield return nested;
                }
            }
        }

        private static IEnumerable<INamedTypeSymbol> WalkTypeMembers(INamedTypeSymbol type)
        {
            foreach (var nested in type.GetTypeMembers())
            {
                yield return nested;
                foreach (var deeper in WalkTypeMembers(nested)) yield return deeper;
            }
        }

        // ---- 暴露判定 ----

        private UiElementOrigin? OriginOf(INamedTypeSymbol type)
        {
            if (type.TypeKind != TypeKind.Class) return null;
            for (var current = type; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
            {
                if (_component is not null && SymbolEqualityComparer.Default.Equals(current, _component))
                    return UiElementOrigin.Component;
                if (_element is not null && SymbolEqualityComparer.Default.Equals(current, _element))
                    return UiElementOrigin.Control;
            }
            return null;
        }

        private bool IsPubliclyVisible(INamedTypeSymbol type)
        {
            for (var current = type; current is not null; current = current.ContainingType)
                if (current.DeclaredAccessibility != Accessibility.Public)
                    return false;
            return true;
        }

        private bool IsStaticEventHost(INamedTypeSymbol type)
        {
            if (!type.IsStatic || _routedEvent is null) return false;
            return type.GetMembers().OfType<IFieldSymbol>().Any(field => IsRoutedEventType(field.Type));
        }

        private bool IsRoutedEventType(ITypeSymbol type)
            => _routedEvent is not null && SymbolEqualityComparer.Default.Equals(type, _routedEvent);

        private bool IsStyledPropertyOf(ITypeSymbol type, out ITypeSymbol? valueType)
        {
            valueType = null;
            if (_styledPropertyGeneric is null) return false;
            if (type is not INamedTypeSymbol named || named.Arity != 1) return false;
            if (!SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, _styledPropertyGeneric)) return false;
            valueType = named.TypeArguments[0];
            return true;
        }

        private bool IsAttachedPropertyOf(ITypeSymbol type, out ITypeSymbol? valueType)
        {
            valueType = null;
            if (_attachedPropertyGeneric is null) return false;
            if (type is not INamedTypeSymbol named || named.Arity != 1) return false;
            if (!SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, _attachedPropertyGeneric)) return false;
            valueType = named.TypeArguments[0];
            return true;
        }

        /// <summary>
        /// G4：收集一个类型上的附加属性声明（design/14 §14.5.5）。候选 = <c>public static readonly
        /// AttachedProperty&lt;T&gt; XxxProperty</c> 字段 + 配对的 <c>public static Get/Set</c> 方法；
        /// 宿主要求顶层、public 可见、无 [UiIgnore]。嵌套宿主 v1 静默跳过（OwnerTag 歧义风险）。
        /// 同 (OwnerTag, Name) 冲突报 <c>EUI026</c> 家族问题，先声明者胜出。
        /// </summary>
        private void CollectAttachedProperties(
            INamedTypeSymbol type,
            List<UiAttachedPropDef> result,
            Dictionary<(string Owner, string Name), string> seen,
            List<UiMetadataProblem> problems)
        {
            if (type.ContainingType is not null) return;            // 嵌套宿主不收集（v1）
            if (!IsPubliclyVisible(type)) return;                   // 不可见 → 使用点按未知处理
            if (HasIgnore(type)) return;                            // [UiIgnore] 退出附加属性表面

            foreach (var field in type.GetMembers().OfType<IFieldSymbol>())
            {
                if (!field.IsStatic || !field.IsReadOnly) continue;
                if (field.DeclaredAccessibility != Accessibility.Public) continue;
                if (HasIgnore(field)) continue;
                if (!IsAttachedPropertyOf(field.Type, out var valueType) || valueType is null) continue;

                var propName = field.Name.EndsWith("Property", StringComparison.Ordinal)
                    && field.Name.Length > "Property".Length
                        ? field.Name.Substring(0, field.Name.Length - "Property".Length)
                        : null;
                if (propName is null)
                {
                    problems.Add(Problem(UiDiagnostics.PropertyNotBindable, type, field.Name,
                        $"'{field.Name}' must be named 'XxxProperty' to become the attached property 'Xxx'"));
                    continue;
                }

                var setter = FindAttachedAccessor(type, "Set" + propName, parameters: 2);
                var getter = FindAttachedAccessor(type, "Get" + propName, parameters: 1);
                if (setter is null || getter is null)
                {
                    problems.Add(Problem(UiDiagnostics.PropertyNotBindable, type, field.Name,
                        $"'{field.Name}' needs a public static pair 'Get{propName}(Element)' / 'Set{propName}(Element, {Display(valueType)})'"));
                    continue;
                }
                // 目录按字段泛型实参转换字面量并生成调用；访问器形状不一致时在使用点才会爆 CS1503，
                // 声明期先拦住，保证目录可信（生成器与探针不做二次类型检查，§2.4 规则 8）。
                if (!SymbolEqualityComparer.Default.Equals(setter.Parameters[1].Type, valueType)
                    || !SymbolEqualityComparer.Default.Equals(getter.ReturnType, valueType))
                {
                    problems.Add(Problem(UiDiagnostics.PropertyNotBindable, type, field.Name,
                        $"'Get{propName}'/'Set{propName}' must use the attached property value type {Display(valueType)}"));
                    continue;
                }

                var key = (type.Name, propName);
                if (seen.TryGetValue(key, out var existing))
                {
                    problems.Add(Problem(UiDiagnostics.MetadataDeclarationInvalid, type, field.Name,
                        $"attached property '{type.Name}.{propName}' is already declared by '{existing}'; the first declaration wins"));
                    continue;
                }
                seen[key] = Display(type);
                result.Add(new UiAttachedPropDef(
                    type.Name,
                    propName,
                    Display(type),
                    setter.Name,
                    Display(valueType),
                    Shape(valueType)));
            }
        }

        private static IMethodSymbol? FindAttachedAccessor(INamedTypeSymbol type, string name, int parameters)
            => type.GetMembers(name).OfType<IMethodSymbol>().FirstOrDefault(m =>
                m.IsStatic
                && m.DeclaredAccessibility == Accessibility.Public
                && m.Parameters.Length == parameters);

        private static bool CanExpose(INamedTypeSymbol type, out string reasonCode, out string reason)
        {
            if (type.ContainingType is not null)
            {
                reasonCode = UiDiagnostics.TagNotExposed;
                reason = "nested types are not exposed as template tags";
                return false;
            }
            if (type.IsAbstract)
            {
                reasonCode = UiDiagnostics.TagNotExposed;
                reason = "the type is abstract";
                return false;
            }
            if (type.Arity > 0)
            {
                reasonCode = UiDiagnostics.GenericTagNotSupported;
                reason = "generic template tags are not supported yet";
                return false;
            }
            if (!HasPublicParameterlessConstructor(type))
            {
                reasonCode = UiDiagnostics.NoPublicParameterlessConstructor;
                reason = "the type has no public parameterless constructor";
                return false;
            }
            reasonCode = "";
            reason = "";
            return true;
        }

        private static bool HasPublicParameterlessConstructor(INamedTypeSymbol type)
            => type.InstanceConstructors.Any(c => c.DeclaredAccessibility == Accessibility.Public && c.Parameters.Length == 0);

        // ---- 成员描述 ----

        private UiElementDef Describe(INamedTypeSymbol type, UiElementOrigin origin, bool fromReferencedAssembly)
        {
            var problems = new List<UiMetadataProblem>();
            var properties = CollectProperties(type, problems);
            var events = CollectEvents(type, problems);
            var content = origin == UiElementOrigin.Component
                ? RejectClassLevelContentOnComponent(type, problems)
                : ResolveContent(type, problems);
            var parameters = new Dictionary<string, UiParameterDef>(StringComparer.Ordinal);
            var slots = new Dictionary<string, UiSlotDef>(StringComparer.Ordinal);
            if (origin == UiElementOrigin.Component)
                CollectComponentMembers(type, parameters, slots, problems);

            if (origin == UiElementOrigin.Component && parameters.Values.Count(p => p.IsContent) > 1)
                problems.Add(Problem(UiDiagnostics.MetadataDeclarationInvalid, type, null,
                    "a component declares more than one default content parameter ([UiContent] on a parameter); keep exactly one"));

            foreach (var name in properties.Keys)
            {
                if (Array.IndexOf(ReservedNames, name) >= 0)
                    problems.Add(Problem(UiDiagnostics.MetadataDeclarationInvalid, type, name,
                        $"'{name}' is a reserved template attribute name and must not be exposed as a property"));
                else if (events.ContainsKey(EventAttributeName(name)))
                    problems.Add(Problem(UiDiagnostics.MetadataDeclarationInvalid, type, name,
                        $"property '{name}' collides with event '{EventAttributeName(name)}'; the event wins at the use site"));
            }

            return new UiElementDef(
                type.Name,
                Display(type),
                origin,
                content.Item1,
                content.Item2,
                Sorted(properties),
                Sorted(events),
                Sorted(parameters),
                Sorted(slots),
                problems,
                fromReferencedAssembly);
        }

        private Dictionary<string, UiPropDef> CollectProperties(INamedTypeSymbol type, List<UiMetadataProblem> problems)
        {
            var result = new Dictionary<string, UiPropDef>(StringComparer.Ordinal);
            for (var current = type; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
            {
                foreach (var member in current.GetMembers().OfType<IPropertySymbol>())
                {
                    if (member.IsStatic || member.IsIndexer) continue;
                    if (member.DeclaredAccessibility != Accessibility.Public) continue;
                    if (member.GetMethod is null || member.SetMethod is null) continue;
                    if (member.GetMethod.DeclaredAccessibility != Accessibility.Public) continue;
                    // init/required 的 setter 不能被生成代码赋值（spec/02 §4.4 用普通赋值），也不参与暴露
                    if (member.SetMethod.DeclaredAccessibility != Accessibility.Public
                        || member.SetMethod.IsInitOnly || member.IsRequired) continue;
                    if (HasIgnore(member) || result.ContainsKey(member.Name)) continue;

                    var field = FindField(type, member.Name + "Property");
                    if (field is null) continue;                       // 普通 CLR 属性不暴露（design/14 §14.4.2）
                    if (field.DeclaredAccessibility != Accessibility.Public
                        || !field.IsStatic || !field.IsReadOnly)
                    {
                        problems.Add(Problem(UiDiagnostics.PropertyNotBindable, current, member.Name,
                            $"'{member.Name}Property' must be a public static readonly StyledProperty field"));
                        continue;
                    }
                    if (!IsStyledPropertyOf(field.Type, out var valueType) || valueType is null)
                    {
                        problems.Add(Problem(UiDiagnostics.PropertyNotBindable, current, member.Name,
                            $"'{member.Name}Property' is not a StyledProperty<T>"));
                        continue;
                    }
                    if (!SymbolEqualityComparer.Default.Equals(valueType, member.Type))
                    {
                        problems.Add(Problem(UiDiagnostics.PropertyNotBindable, current, member.Name,
                            $"'{member.Name}Property' is StyledProperty<{Display(valueType)}> but '{member.Name}' is {Display(member.Type)}"));
                        continue;
                    }

                    result[member.Name] = new UiPropDef(
                        member.Name,
                        Display(member.Type),
                        Display(member.ContainingType),
                        member.Name + "Property",
                        CanWrite: true,
                        Shape(member.Type));
                }
            }
            return result;
        }

        private Dictionary<string, UiEventDef> CollectEvents(INamedTypeSymbol type, List<UiMetadataProblem> problems)
        {
            var result = new Dictionary<string, UiEventDef>(StringComparer.Ordinal);
            for (var current = type; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
            {
                foreach (var member in current.GetMembers().OfType<IFieldSymbol>())
                {
                    if (!member.IsStatic || !member.IsReadOnly) continue;
                    if (member.DeclaredAccessibility != Accessibility.Public) continue;
                    if (HasIgnore(member)) continue;
                    if (!IsRoutedEventType(member.Type))
                    {
                        if (member.Name.EndsWith("Event", StringComparison.Ordinal))
                            problems.Add(Problem(UiDiagnostics.EventFieldMissing, current, member.Name,
                                $"'{member.Name}' ends with 'Event' but its type is {Display(member.Type)}, not EclipseUI.RoutedEvent"));
                        continue;
                    }
                    if (!member.Name.EndsWith("Event", StringComparison.Ordinal) || member.Name.Length <= "Event".Length) continue;

                    var eventName = member.Name.Substring(0, member.Name.Length - "Event".Length);
                    var attributeName = "On" + eventName;
                    if (result.ContainsKey(attributeName)) continue;
                    result[attributeName] = new UiEventDef(
                        attributeName,
                        eventName,
                        Display(member.ContainingType),
                        member.Name,
                        DefaultEventArgsType);
                }
            }
            return result;
        }

        private (UiContentModel Model, string? Target) ResolveContent(INamedTypeSymbol type, List<UiMetadataProblem> problems)
        {
            for (var current = type; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
            {
                var attribute = FindAttribute(current, UiContentAttributeName);
                if (attribute is null) continue;

                var declaredKind = attribute.ConstructorArguments.Length == 1
                    ? attribute.ConstructorArguments[0].Value as int?
                    : (int?)UiContentModel.Children;
                if (declaredKind is null || !Enum.IsDefined(typeof(UiContentModel), declaredKind.Value))
                {
                    // 前向兼容：新版本运行时的未知枚举值降级为 None，不崩溃（design/14 §14.5.4）
                    problems.Add(Problem(UiDiagnostics.MetadataDeclarationInvalid, current, null,
                        $"[UiContent] declares unknown kind '{declaredKind?.ToString() ?? "<null>"}'; treated as None"));
                    return (UiContentModel.None, null);
                }

                var model = (UiContentModel)declaredKind.Value;
                var target = attribute.NamedArguments.FirstOrDefault(a => a.Key == "Target").Value.Value as string;
                return ValidateContent(model, target, current, problems);
            }
            return (UiContentModel.None, null);
        }

        private (UiContentModel, string?) ValidateContent(
            UiContentModel model, string? target, INamedTypeSymbol declaringType, List<UiMetadataProblem> problems)
        {
            switch (model)
            {
                case UiContentModel.None:
                    return (UiContentModel.None, null);

                case UiContentModel.Children:
                {
                    var name = target ?? "Children";
                    var property = FindProperty(declaringType, name);
                    if (property is null || !HasAddMethod(property.Type, name))
                    {
                        problems.Add(Problem(UiDiagnostics.ContentTargetInvalid, declaringType, name,
                            $"content target '{name}' must be an accessible collection with Add(Element)"));
                        return (UiContentModel.None, null);
                    }
                    return (UiContentModel.Children, name);
                }

                case UiContentModel.Text:
                {
                    if (target is null)
                    {
                        problems.Add(Problem(UiDiagnostics.ContentTargetInvalid, declaringType, null,
                            "[UiContent(Text)] must name its string target with Target"));
                        return (UiContentModel.None, null);
                    }
                    var property = FindProperty(declaringType, target);
                    if (property is null || property.Type.SpecialType != SpecialType.System_String)
                    {
                        problems.Add(Problem(UiDiagnostics.ContentTargetInvalid, declaringType, target,
                            $"content target '{target}' must be a public string property"));
                        return (UiContentModel.None, null);
                    }
                    return (UiContentModel.Text, target);
                }

                default:
                {
                    if (_textContentHost is null || !ImplementsInterface(declaringType, _textContentHost))
                    {
                        problems.Add(Problem(UiDiagnostics.ContentTargetInvalid, declaringType, null,
                            "[UiContent(TextRuns)] requires the type to implement EclipseUI.ITextContentHost"));
                        return (UiContentModel.None, null);
                    }
                    return (UiContentModel.TextRuns, null);
                }
            }
        }

        private void CollectComponentMembers(
            INamedTypeSymbol type,
            Dictionary<string, UiParameterDef> parameters,
            Dictionary<string, UiSlotDef> slots,
            List<UiMetadataProblem> problems)
        {
            // 基链自己走（Roslyn 的 GetAttributes 不把特性带进 override，也不含继承的类型特性）。
            // 同名成员按派生优先，但只有派生侧**显式写了模板声明或 [UiIgnore]** 才算覆盖：
            // 否则 override 会把基类的参数/插槽静默丢掉。重复处理同一声明会把它误报成
            // 重复插槽声明，并让基类定义覆盖派生定义（Required/形状以派生声明为准）。
            var seenMembers = new HashSet<string>(StringComparer.Ordinal);
            for (var current = type; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
            {
                foreach (var member in current.GetMembers().OfType<IPropertySymbol>())
                {
                    if (member.IsStatic || member.IsIndexer) continue;
                    var parameter = FindAttribute(member, UiParameterAttributeName);
                    var slot = FindAttribute(member, UiSlotAttributeName);
                    var contentMark = FindAttribute(member, UiContentAttributeName);
                    // design/14 §14.6.1「参数属性不得是 [UiIgnore]」：忽略优先，成员退出表面并记问题。
                    // 同时占用名字——派生侧的 [UiIgnore] 也是基类同名模板声明的显式退出。
                    if (HasIgnore(member))
                    {
                        // 显式 [UiIgnore] 占用这个名字：基类同名的模板声明随之退出表面。
                        seenMembers.Add(member.Name);
                        if (parameter is not null || slot is not null || contentMark is not null)
                            problems.Add(Problem(UiDiagnostics.ParameterDeclarationInvalid, current, member.Name,
                                $"'{member.Name}' is both [UiIgnore] and a template parameter/slot/content; [UiIgnore] wins and the member leaves the surface"));
                        continue;
                    }

                    if (parameter is null && slot is null && contentMark is null) continue;
                    if (!seenMembers.Add(member.Name)) continue;

                    if (parameter is not null)
                    {
                        var writable = member.DeclaredAccessibility == Accessibility.Public
                            && member.GetMethod?.DeclaredAccessibility == Accessibility.Public
                            && member.SetMethod?.DeclaredAccessibility == Accessibility.Public
                            && member.SetMethod?.IsInitOnly == false
                            && !member.IsRequired;
                        if (!writable)
                            problems.Add(Problem(UiDiagnostics.ParameterDeclarationInvalid, current, member.Name,
                                $"parameter '{member.Name}' must be a public property with a public setter; use [UiParameter(Required = true)] instead of the C# required modifier"));

                        var required = parameter.NamedArguments.FirstOrDefault(a => a.Key == "Required").Value.Value as bool? ?? false;
                        var arity = 0;
                        if (member.Type.TypeKind == TypeKind.Delegate)
                        {
                            // EUI028：委托参数必须返回 void、无 ref/out/in（spec/02 §4.6.2 规则 6）。
                            if (!ValidDelegateShape(member.Type, out arity, out var reason))
                                problems.Add(Problem(UiDiagnostics.ParameterDeclarationInvalid, current, member.Name,
                                    $"delegate parameter '{member.Name}' {reason}"));
                        }
                        else if (InvalidParameterType(member.Type))
                        {
                            // EUI028：ref struct / 指针 / Span<T> 不能做 EqualityComparer<T>（spec/02 §4.3.3 规则 7）。
                            problems.Add(Problem(UiDiagnostics.ParameterDeclarationInvalid, current, member.Name,
                                $"parameter '{member.Name}' has type {Display(member.Type)}; parameters cannot be ref structs, " +
                                "pointers or Span<T> (generated code uses EqualityComparer<T>.Default)"));
                        }

                        parameters[member.Name] = new UiParameterDef(
                            member.Name,
                            DisplayQualified(member.Type),
                            Shape(member.Type),
                            CanWrite: writable,
                            Required: required,
                            IsDelegate: member.Type.TypeKind == TypeKind.Delegate,
                            DelegateArity: arity,
                            IsContent: false,
                            ContentIsMultiple: false,
                            ChangedCallbackName: null);
                    }

                    if (slot is not null)
                    {
                        if (parameter is not null)
                        {
                            // [UiParameter] 胜出：参数 Only 表面（design/14 §14.6.1）；槽属性退出表面并记问题。
                            problems.Add(Problem(UiDiagnostics.ParameterDeclarationInvalid, current, member.Name,
                                $"'{member.Name}' is both [UiParameter] and [UiSlot]; the parameter wins and " +
                                "the slot leaves the template surface"));
                        }
                        else
                        {
                            var slotName = slot.ConstructorArguments.Length == 1 ? slot.ConstructorArguments[0].Value as string : null;
                            if (string.IsNullOrEmpty(slotName))
                            {
                                problems.Add(Problem(UiDiagnostics.ParameterDeclarationInvalid, current, member.Name,
                                    "[UiSlot] must name the slot"));
                            }
                            else if (slots.ContainsKey(slotName!))
                            {
                                problems.Add(Problem(UiDiagnostics.ParameterDeclarationInvalid, current, member.Name,
                                    $"a slot named '{slotName}' is already declared on this component; slot names must be unique"));
                            }
                            else if (IsElementListType(member.Type))
                            {
                                // IReadOnlyList<Element> → 多根填充拼接；Element（含 ?）→ 单根取首个（§4.12.5）。
                                slots[slotName!] = new UiSlotDef(slotName!, member.Name, DisplayQualified(member.Type), UiSlotCardinality.Multiple);
                            }
                            else if (IsElementType(member.Type))
                            {
                                slots[slotName!] = new UiSlotDef(slotName!, member.Name, DisplayQualified(member.Type), UiSlotCardinality.Single);
                            }
                            else
                            {
                                problems.Add(Problem(UiDiagnostics.ContentTargetInvalid, current, member.Name,
                                    $"slot '{member.Name}' has type {Display(member.Type)}; [UiSlot] properties must be " +
                                    $"Element? (single root) or IReadOnlyList<Element> (multiple roots)"));
                            }
                        }
                    }
                    else if (contentMark is not null)
                    {
                        // 默认内容参数（design/14 §14.6.3 / spec/02 §4.12.3）：
                        // IReadOnlyList<Element> → 多根；Element（含 ?）→ 单根；其余 → EUI021。
                        if (IsElementListType(member.Type))
                        {
                            parameters[member.Name] = ContentParameter(member, multiple: true);
                        }
                        else if (IsElementType(member.Type))
                        {
                            parameters[member.Name] = ContentParameter(member, multiple: false);
                        }
                        else
                        {
                            problems.Add(Problem(UiDiagnostics.ContentTargetInvalid, current, member.Name,
                                $"content parameter '{member.Name}' has type {Display(member.Type)}; " +
                                "[UiContent] parameters must be Element? (single root) or IReadOnlyList<Element> (multiple roots)"));
                        }
                    }
                }
            }

            // 双向参数的回调形状：{P}Changed 必须是同名参数上的委托（design/14 §14.6.1）
            var names = parameters.Keys.ToList();
            foreach (var name in names)
            {
                var changedName = name + "Changed";
                if (parameters.TryGetValue(changedName, out var changed) && changed.IsDelegate)
                    parameters[name] = parameters[name] with { ChangedCallbackName = changedName };
            }
        }

        /// <summary>EUI028：委托参数必须返回 void、不含 ref/out/in（spec/02 §4.6.2 规则 6）。</summary>
        private bool ValidDelegateShape(ITypeSymbol type, out int arity, out string? reason)
        {
            arity = 0;
            reason = null;
            foreach (var member in type.GetMembers("Invoke"))
            {
                if (member is not IMethodSymbol invoke) continue;
                if (!invoke.ReturnsVoid)
                {
                    reason = $"must return void, not '{Display(invoke.ReturnType)}'";
                    return false;
                }
                foreach (var p in invoke.Parameters)
                {
                    if (p.RefKind != RefKind.None)
                    {
                        reason = $"must not use {p.RefKind.ToString().ToLowerInvariant()} modifiers";
                        return false;
                    }
                }
                arity = invoke.Parameters.Length;
                return true;
            }
            reason = "has no Invoke method";
            return false;
        }

        private bool InvalidParameterType(ITypeSymbol type)
        {
            if (type.TypeKind == TypeKind.Pointer) return true;
            if (type.IsRefLikeType) return true;
            var key = type is INamedTypeSymbol named &&
                SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, _spanGeneric) ||
                type is INamedTypeSymbol named2 &&
                SymbolEqualityComparer.Default.Equals(named2.OriginalDefinition, _readOnlySpanGeneric);
            return key;
        }

        private bool IsElementListType(ITypeSymbol type)
        {
            if (type is not INamedTypeSymbol named) return false;
            var def = named.OriginalDefinition;
            if (_elementReadOnlyList is null || !SymbolEqualityComparer.Default.Equals(def, _elementReadOnlyList))
                return false;
            return named.TypeArguments.Length == 1 &&
                SymbolEqualityComparer.Default.Equals(named.TypeArguments[0], _element);
        }

        private bool IsElementType(ITypeSymbol type)
        {
            var unwrapped = type;
            if (unwrapped is INamedTypeSymbol nullable &&
                SymbolEqualityComparer.Default.Equals(nullable.OriginalDefinition, _nullableGeneric))
                unwrapped = nullable.TypeArguments[0];
            return SymbolEqualityComparer.Default.Equals(unwrapped, _element);
        }

        private UiParameterDef ContentParameter(IPropertySymbol member, bool multiple) => new(
            member.Name, DisplayQualified(member.Type), Shape(member.Type),
            CanWrite: member.DeclaredAccessibility == Accessibility.Public
                && member.SetMethod?.DeclaredAccessibility == Accessibility.Public,
            Required: false, IsDelegate: false, DelegateArity: 0,
            IsContent: true, ContentIsMultiple: multiple, ChangedCallbackName: null);

        /// <summary>组件类级 [UiContent] 非法：默认内容必须标在参数属性上（design/14 §14.6.3）。</summary>
        private (UiContentModel, string?) RejectClassLevelContentOnComponent(INamedTypeSymbol type, List<UiMetadataProblem> problems)
        {
            for (var current = type; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
            {
                if (FindAttribute(current, UiContentAttributeName) is not null)
                {
                    problems.Add(Problem(UiDiagnostics.MetadataDeclarationInvalid, current, null,
                        "[UiContent] on a component type is invalid; mark a [UiParameter] property with [UiContent] " +
                        "(type Element? or IReadOnlyList<Element>) to accept default content"));
                    return (UiContentModel.None, null);
                }
            }
            return (UiContentModel.None, null);
        }

        // ---- 成员查找 ----

        // 标签名恒等于类型短名（`[UiTag]` 已于 2026-11-09 退役，见 design/14 §14.4.1 与 K32）：
        // 重名消歧改由模板侧 `@using Alias = Ns.Type` 完成，控件作者无需也无法在类型上改名。

        private static string EventAttributeName(string propertyName) => "On" + propertyName;

        private static IFieldSymbol? FindField(INamedTypeSymbol type, string name)
        {
            for (var current = type; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
            {
                foreach (var member in current.GetMembers(name))
                    if (member is IFieldSymbol field && field.IsStatic) return field;
            }
            return null;
        }

        private static IPropertySymbol? FindProperty(INamedTypeSymbol type, string name)
        {
            for (var current = type; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
            {
                foreach (var member in current.GetMembers(name))
                    if (member is IPropertySymbol property && !property.IsStatic
                        && property.DeclaredAccessibility == Accessibility.Public
                        && property.GetMethod is not null)
                        return property;
            }
            return null;
        }

        private bool HasAddMethod(ITypeSymbol targetType, string targetName)
        {
            if (_element is null) return false;
            for (var current = targetType as INamedTypeSymbol; current is not null; current = current.BaseType)
            {
                foreach (var member in current.GetMembers("Add").OfType<IMethodSymbol>())
                {
                    if (member.Parameters.Length != 1) continue;
                    if (SymbolEqualityComparer.Default.Equals(member.Parameters[0].Type, _element)) return true;
                }
            }
            return false;
        }

        private static bool ImplementsInterface(INamedTypeSymbol type, INamedTypeSymbol interfaceType)
            => type.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, interfaceType));

        private static AttributeData? FindAttribute(ISymbol symbol, string attributeMetadataName)
            => symbol.GetAttributes().FirstOrDefault(a => a.AttributeClass?.MetadataName != null
                && FullMetadataName(a.AttributeClass) == attributeMetadataName);

        private static bool HasIgnore(ISymbol symbol) => FindAttribute(symbol, UiIgnoreAttributeName) is not null;

        private static string FullMetadataName(INamedTypeSymbol type)
        {
            var ns = type.ContainingNamespace;
            var name = ns is null || ns.IsGlobalNamespace ? type.MetadataName : $"{ns.ToDisplayString()}.{type.MetadataName}";
            var containing = type.ContainingType;
            return containing is null ? name : $"{FullMetadataName(containing)}/{type.MetadataName}";
        }

        // ---- 类型形状 ----

        private UiTypeShape Shape(ITypeSymbol type)
        {
            var key = CanonicalKey(type);
            if (type is INamedTypeSymbol named && named.TypeKind == TypeKind.Enum)
            {
                var members = named.GetMembers()
                    .OfType<IFieldSymbol>()
                    .Where(f => f.IsConst)
                    .Select(f => f.Name)
                    .OrderBy(n => n, StringComparer.Ordinal)
                    .ToImmutableArray();
                return new UiTypeShape(UiTypeShapeKind.Enum, key, members);
            }
            if (type.TypeKind == TypeKind.Delegate)
                return new UiTypeShape(UiTypeShapeKind.Delegate, key, ImmutableArray<string>.Empty);
            if (_signalGeneric is not null && type is INamedTypeSymbol signal
                && SymbolEqualityComparer.Default.Equals(signal.OriginalDefinition, _signalGeneric))
                return new UiTypeShape(UiTypeShapeKind.Signal, key, ImmutableArray<string>.Empty);
            if (IsPrimitiveKey(key))
                return new UiTypeShape(UiTypeShapeKind.Primitive, key, ImmutableArray<string>.Empty);
            return new UiTypeShape(UiTypeShapeKind.Other, key, ImmutableArray<string>.Empty);
        }

        /// <summary>
        /// 查表键：内置类型用 C# 关键字名（与 <c>UiLiterals.TryConvert</c> 的键一致），
        /// 其余用去掉 <c>global::</c> 与可空标注的限定全名（design/14 §14.5.1）。
        /// </summary>
        private static string CanonicalKey(ITypeSymbol type)
        {
            switch (type.SpecialType)
            {
                case SpecialType.System_Double: return "double";
                case SpecialType.System_Single: return "float";
                case SpecialType.System_Int32: return "int";
                case SpecialType.System_Int64: return "long";
                case SpecialType.System_Byte: return "byte";
                case SpecialType.System_Char: return "char";
                case SpecialType.System_Boolean: return "bool";
                case SpecialType.System_String: return "string";
                case SpecialType.System_Object: return "object";
                case SpecialType.System_Decimal: return "decimal";
                case SpecialType.System_Int16: return "short";
                default: break;
            }
            var display = Display(type);
            if (display.StartsWith("global::", StringComparison.Ordinal))
                display = display.Substring("global::".Length);
            return display.TrimEnd('?');
        }

        private static bool IsPrimitiveKey(string canonicalKey)
        {
            switch (canonicalKey)
            {
                case "double":
                case "float":
                case "int":
                case "long":
                case "byte":
                case "char":
                case "bool":
                case "string":
                case "object":
                case "decimal":
                case "short":
                    return true;
                default:
                    return false;
            }
        }

        // ---- 小工具 ----

        private static string Display(ISymbol symbol) => symbol.ToDisplayString(Qualified);

    /// <summary>参数/委托文本的显示：特殊类型由 Roslyn 符号显示引擎直接按 R4 产出 global::System.*，零文本后处理。</summary>
    private static string DisplayQualified(ISymbol symbol) => symbol.ToDisplayString(QualifiedFrozen);

        private static UiMetadataProblem Problem(string code, INamedTypeSymbol declaringType, string? memberName, string message)
            => new UiMetadataProblem(code, message, Display(declaringType), memberName,
                Locate(declaringType, memberName));

        /// <summary>
        /// D74 ②：声明处诊断的位置载体。成员优先（属性/方法/特性的标识符 span），否则退回类型声明；
        /// 引用程序集符号没有 IsInSource 的 Location → null，它们仍只在使用点暴露（design/14 §14.9.2）。
        /// 只存纯数据，不把 Location/SyntaxTree 带进增量缓存。
        /// </summary>
        private static UiSourceLocation? Locate(INamedTypeSymbol declaringType, string? memberName)
        {
            var target = (ISymbol)declaringType;
            if (memberName is not null)
                target = declaringType.GetMembers(memberName).FirstOrDefault() ?? target;
            var location = target.Locations.FirstOrDefault(static l => l.IsInSource);
            if (location is null) return null;
            var line = location.GetLineSpan();
            return new UiSourceLocation(
                line.Path, location.SourceSpan.Start, location.SourceSpan.Length,
                line.StartLinePosition.Line, line.StartLinePosition.Character,
                line.EndLinePosition.Line, line.EndLinePosition.Character);
        }

        private static IReadOnlyDictionary<string, T> Sorted<T>(Dictionary<string, T> source)
        {
            var result = new SortedDictionary<string, T>(StringComparer.Ordinal);
            foreach (var pair in source) result[pair.Key] = pair.Value;
            return result;
        }
    }
}

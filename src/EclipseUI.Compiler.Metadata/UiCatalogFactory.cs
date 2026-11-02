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
    private const string TextContentHostMetadataName = "EclipseUI.ITextContentHost";
    private const string SignalGenericMetadataName = "EclipseUI.Signal`1";

    private const string UiTagAttributeName = "EclipseUI.UiTagAttribute";
    private const string UiContentAttributeName = "EclipseUI.UiContentAttribute";
    private const string UiIgnoreAttributeName = "EclipseUI.UiIgnoreAttribute";
    private const string UiParameterAttributeName = "EclipseUI.UiParameterAttribute";
    private const string UiSlotAttributeName = "EclipseUI.UiSlotAttribute";

    /// <summary>运行时只有 <c>RoutedEventArgs</c> 一种事件实参形状；typed args 属 G4（design/14 §14.4.3）。</summary>
    internal const string DefaultEventArgsType = "global::EclipseUI.RoutedEventArgs";

    internal static readonly SymbolDisplayFormat Qualified = SymbolDisplayFormat.FullyQualifiedFormat;

    /// <summary>保留属性名（design/01 §4.13）；控件暴露它们会与语法冲突 → 元数据问题。</summary>
    private static readonly string[] ReservedNames = { "Key", "Ref", "Class", "Style" };

    public static UiCatalog Build(Compilation compilation)
    {
        if (compilation is null) throw new ArgumentNullException(nameof(compilation));
        return new Scanner(compilation).Build();
    }

    private sealed class Scanner
    {
        private readonly Compilation _compilation;
        private readonly INamedTypeSymbol? _element;
        private readonly INamedTypeSymbol? _component;
        private readonly INamedTypeSymbol? _routedEvent;
        private readonly INamedTypeSymbol? _styledPropertyGeneric;
        private readonly INamedTypeSymbol? _textContentHost;
        private readonly INamedTypeSymbol? _signalGeneric;

        public Scanner(Compilation compilation)
        {
            _compilation = compilation;
            _element = compilation.GetTypeByMetadataName(ElementMetadataName);
            _component = compilation.GetTypeByMetadataName(ComponentMetadataName);
            _routedEvent = compilation.GetTypeByMetadataName(RoutedEventMetadataName);
            _styledPropertyGeneric = compilation.GetTypeByMetadataName(StyledPropertyGenericMetadataName);
            _textContentHost = compilation.GetTypeByMetadataName(TextContentHostMetadataName);
            _signalGeneric = compilation.GetTypeByMetadataName(SignalGenericMetadataName);
        }

        public UiCatalog Build()
        {
            var exposed = new List<UiElementDef>();
            var hidden = new List<UiHiddenTag>();
            var problems = new List<UiMetadataProblem>();

            // 没有运行时锚点的编译（例如纯 C# 测试项目）得到空目录，而不是异常
            if (_element is null && _component is null)
                return new UiCatalog(exposed, hidden, problems);

            foreach (var assembly in CandidateAssemblies())
            {
                foreach (var type in WalkTypes(assembly.GlobalNamespace))
                {
                    if (IsStaticEventHost(type))
                    {
                        problems.Add(new UiMetadataProblem(
                            UiDiagnostics.MetadataDeclarationInvalid,
                            $"'{Display(type)}' is a static host class declaring RoutedEvent fields; declare them on an Element-derived type",
                            Display(type),
                            null));
                        continue;
                    }

                    var origin = OriginOf(type);
                    if (origin is null) continue;
                    if (!IsPubliclyVisible(type)) continue;              // 不可见 → 使用点 EUI001
                    if (HasIgnore(type)) continue;                       // 完全退出模板表面
                    if (!CanExpose(type, out var reasonCode, out var reason))
                    {
                        hidden.Add(new UiHiddenTag(TagOf(type), Display(type), reasonCode, reason));
                        continue;
                    }

                    exposed.Add(Describe(type, origin.Value));
                }
            }

            return new UiCatalog(exposed, hidden, problems);
        }

        // ---- 扫描范围 ----

        private IEnumerable<IAssemblySymbol> CandidateAssemblies()
        {
            yield return _compilation.Assembly;
            foreach (var reference in _compilation.References)
            {
                if (_compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol assembly) continue;
                if (SymbolEqualityComparer.Default.Equals(assembly, _compilation.Assembly)) continue;
                if (!ReferencesRuntime(assembly)) continue;
                yield return assembly;
            }
        }

        private static bool ReferencesRuntime(IAssemblySymbol assembly)
        {
            if (assembly.Identity.Name == RuntimeAssemblyName) return true;
            foreach (var module in assembly.Modules)
                foreach (var identity in module.ReferencedAssemblies)
                    if (identity.Name == RuntimeAssemblyName) return true;
            return false;
        }

        private static IEnumerable<INamedTypeSymbol> WalkTypes(INamespaceSymbol namespaceSymbol)
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

        private UiElementDef Describe(INamedTypeSymbol type, UiElementOrigin origin)
        {
            var problems = new List<UiMetadataProblem>();
            var properties = CollectProperties(type, problems);
            var events = CollectEvents(type, problems);
            var content = ResolveContent(type, problems);
            var parameters = new Dictionary<string, UiParameterDef>(StringComparer.Ordinal);
            var slots = new Dictionary<string, UiSlotDef>(StringComparer.Ordinal);
            if (origin == UiElementOrigin.Component)
                CollectComponentMembers(type, parameters, slots, problems);

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
                TagOf(type),
                Display(type),
                origin,
                content.Model,
                content.Target,
                Sorted(properties),
                Sorted(events),
                Sorted(parameters),
                Sorted(slots),
                problems);
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
            for (var current = type; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
            {
                foreach (var member in current.GetMembers().OfType<IPropertySymbol>())
                {
                    if (member.IsStatic || member.IsIndexer) continue;
                    var parameter = FindAttribute(member, UiParameterAttributeName);
                    var slot = FindAttribute(member, UiSlotAttributeName);
                    if (parameter is null && slot is null) continue;
                    // design/14 §14.6.1「参数属性不得是 [UiIgnore]」：忽略优先，成员退出表面并记问题
                    if (HasIgnore(member))
                    {
                        if (parameter is not null || slot is not null)
                            problems.Add(Problem(UiDiagnostics.ParameterDeclarationInvalid, current, member.Name,
                                $"'{member.Name}' is both [UiIgnore] and a template parameter/slot; [UiIgnore] wins and the member leaves the surface"));
                        continue;
                    }

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
                        parameters[member.Name] = new UiParameterDef(
                            member.Name,
                            Display(member.Type),
                            CanWrite: writable,
                            Required: required,
                            IsDelegate: member.Type.TypeKind == TypeKind.Delegate,
                            ChangedCallbackName: null);
                    }

                    if (slot is not null)
                    {
                        var slotName = slot.ConstructorArguments.Length == 1 ? slot.ConstructorArguments[0].Value as string : null;
                        if (string.IsNullOrEmpty(slotName))
                        {
                            problems.Add(Problem(UiDiagnostics.MetadataDeclarationInvalid, current, member.Name,
                                "[UiSlot] must name the slot"));
                        }
                        else
                        {
                            slots[slotName!] = new UiSlotDef(slotName!, member.Name, Display(member.Type), UiSlotCardinality.Single);
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

        // ---- 名字与查找 ----

        private string TagOf(INamedTypeSymbol type)
        {
            var attribute = FindAttribute(type, UiTagAttributeName);
            if (attribute is null) return type.Name;
            var name = attribute.ConstructorArguments.Length == 1 ? attribute.ConstructorArguments[0].Value as string : null;
            return IsValidTag(name) ? name! : type.Name;
        }

        private static bool IsValidTag(string? name)
            => !string.IsNullOrEmpty(name)
               && name!.IndexOf('.') < 0
               && name.IndexOf('`') < 0
               && name.IndexOf('<') < 0
               && char.IsLetter(name![0]);

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
                    return true;
                default:
                    return false;
            }
        }

        // ---- 小工具 ----

        private static string Display(ISymbol symbol) => symbol.ToDisplayString(Qualified);

        private static UiMetadataProblem Problem(string code, INamedTypeSymbol declaringType, string? memberName, string message)
            => new UiMetadataProblem(code, message, Display(declaringType), memberName);

        private static IReadOnlyDictionary<string, T> Sorted<T>(Dictionary<string, T> source)
        {
            var result = new SortedDictionary<string, T>(StringComparer.Ordinal);
            foreach (var pair in source) result[pair.Key] = pair.Value;
            return result;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using MsTextSpan = Microsoft.CodeAnalysis.Text.TextSpan;
using UiSpan = EclipseUI.Compiler.TextSpan;
using CompilerSeverity = EclipseUI.Compiler.DiagnosticSeverity;
using RoslynSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;
using EclipseUI.Compiler.Metadata;

namespace EclipseUI.Compiler.Generator;

/// <summary>
/// .ui → C# 增量源生成器：编译期类型检查（表达式原样进入生成代码，由 Roslyn 检查并经
/// #line 映射回 .ui）、@foreach item 类型推断、@bind 源可写性校验、定向更新代码发射。
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class TemplateGenerator : IIncrementalGenerator
{
    private const string UiSlotAttributeName = "EclipseUI.UiSlotAttribute";
    private const string UiParameterAttributeName = "EclipseUI.UiParameterAttribute";
    private const string UiIgnoreAttributeName = "EclipseUI.UiIgnoreAttribute";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var uiFiles = context.AdditionalTextsProvider
            .Where(static file => Path.GetExtension(file.Path) is ".ui")
            .Select(static (file, ct) => new UiFileInput(file.Path, file.GetText(ct)?.ToString() ?? ""));

        // 模板表面每轮从符号重建（design/14 §14.10.3：派生目录是唯一事实源，不允许手写缓存）。
        // UiCatalog 逐字段实现结构相等，所以控件 API 没变时下游仍按值命中缓存、不重生成代码。
        var catalogProvider = context.CompilationProvider
            .Select(static (compilation, _) => UiCatalogFactory.Build(compilation));

        // D74 ②：目录级诊断出口（design/14 §14.9.1）。挂在 catalogProvider 而不是逐文件管道，
        // 保证同一条元数据问题无论项目里有多少个 .ui 文件都只报一次。
        context.RegisterSourceOutput(catalogProvider, static (spc, catalog) =>
            ReportCatalogProblems(spc, catalog));

        // 逐文件语义事实（code-behind 表面 + 探针）：输入挂 Compilation 故每轮重建，但输出是值相等
        // record——无关改动下值不变，下游逐文件 SourceOutput 按值命中 Cached/Unchanged
        // （design/14 §14.10.3/§14.11.2 的验收口径）。不再 Collect 成批：单文件变化只重跑该文件。
        var perFile = uiFiles.Combine(catalogProvider).Combine(context.CompilationProvider)
            .Select(static (pair, _) => (
                Input: pair.Left.Left,
                Catalog: pair.Left.Right,
                Facts: ExtractFacts(pair.Left.Left, pair.Left.Right, pair.Right)))
            // 命名节点让逐文件生成在 TrackedSteps 里可独立观测：D74 ② 的目录级诊断出口
            // 是第二个 SourceOutput，不能把所有 "SourceOutput" 混成一组断言（§14.10.3）。
            .WithTrackingName("PerFileGeneration");
        context.RegisterSourceOutput(perFile, static (spc, data) =>
            ExecuteFile(spc, data.Input, data.Catalog, data.Facts));
    }

    private static UiFileFacts ExtractFacts(UiFileInput input, UiCatalog catalog, Compilation compilation)
    {
        var className = Path.GetFileNameWithoutExtension(input.Path);
        var validName = SyntaxFacts.IsValidIdentifier(className);
        var codeBehindDiags = new List<UiDiagnostic>();
        var type = validName ? FindCodeBehind(compilation, className) : null;
        if (validName)
        {
            if (type is null)
            {
                codeBehindDiags.Add(UiDiagnostics.Error(UiDiagnostics.CodeBehindMissing,
                    $"no code-behind class '{className}' found; add a partial class with that name", UiSpan.Zero));
            }
            else if (type.ContainingType is not null)
            {
                codeBehindDiags.Add(UiDiagnostics.Error(UiDiagnostics.CodeBehindNested,
                    $"code-behind class '{className}' must be top-level", UiSpan.Zero));
            }
            else if (!IsPartial(type))
            {
                codeBehindDiags.Add(UiDiagnostics.Error(UiDiagnostics.CodeBehindNotPartial,
                    $"code-behind class '{className}' must be declared partial", UiSpan.Zero));
            }
        }

        // D57/EUI045（spec/02 §10）：code-behind 与生成成员/基类成员冲突。
        if (type is not null)
            ProbeCodeBehindConflicts(type, codeBehindDiags);

        // 自身组件（spec/02 §4.12.5）：code-behind 符号直接派生插槽表——目录里没有
        // code-behind 类的 def（基类由生成注入，源码不派生 Component），这是唯一权威来源。
        var own = ResolveOwnComponent(type, compilation);
        var slots = own is null
            ? ImmutableArray<UiSlotDef>.Empty
            : own.Value.Slots.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Value).ToImmutableArray();

        // AST 不可值相等、不能跨缓存边界：这里解析只为喂探针，发射阶段再解析一次（前端纯函数，同文同果）。
        var doc = FrontEnd.Parse(input.Text, input.Path, new List<UiDiagnostic>());
        // 探针读 Analyze 回写在 AST 上的 PropDef/ParamDef/ResolvedDef，必须先分析一遍（诊断丢弃，发射阶段重报）。
        FrontEnd.Analyze(doc, catalog, new List<UiDiagnostic>(),
            own is null ? null : slots.ToDictionary(slot => slot.Name, StringComparer.Ordinal),
            own?.DisplayName);
        var probeDiags = new List<UiDiagnostic>();
        var itemTypes = type is null
            ? ImmutableArray<ItemTypeFact>.Empty
            : ProbeSemantics(doc, type, compilation, probeDiags)
                .Select(kv => new ItemTypeFact(kv.Key.CollectionSpan.Start, kv.Value))
                .OrderBy(fact => fact.SpanStart)
                .ToImmutableArray();

        return new UiFileFacts(
            type is null ? "internal" : AccessibilityOf(type),
            type is null ? "" : NamespaceName(type),
            own?.DisplayName ?? "",
            slots,
            codeBehindDiags.ToImmutableArray(),
            probeDiags.ToImmutableArray(),
            itemTypes);
    }

    private static void ExecuteFile(SourceProductionContext spc, UiFileInput input,
        UiCatalog catalog, UiFileFacts facts)
    {
        var sourceText = SourceText.From(input.Text);
        var className = Path.GetFileNameWithoutExtension(input.Path);
        var diags = new List<UiDiagnostic>();

        if (!SyntaxFacts.IsValidIdentifier(className))
        {
            Report(spc, input.Path, sourceText, UiDiagnostics.Error(UiDiagnostics.InvalidClassName,
                $"'{className}' is not a valid class name; rename the .ui file", UiSpan.Zero));
            return;
        }

        var doc = FrontEnd.Parse(input.Text, input.Path, diags);
        diags.AddRange(facts.CodeBehindDiags);

        var ownSlots = facts.DisplayName.Length == 0
            ? null
            : facts.Slots.ToDictionary(slot => slot.Name, StringComparer.Ordinal);
        FrontEnd.Analyze(doc, catalog, diags, ownSlots, facts.DisplayName.Length == 0 ? null : facts.DisplayName);
        diags.AddRange(facts.ProbeDiags);

        bool hasErrors = false;
        foreach (var diag in diags)
        {
            Report(spc, input.Path, sourceText, diag);
            // EUI045 只报冲突，不阻止生成器输出（spec/02 §10：生成器不改名、仍按本文生成，
            // 错误诊断本身已阻断构建）。其它错误诊断维持「不输出」的既有口径。
            if (diag.IsError && diag.Code != UiDiagnostics.CodeBehindConflict) hasErrors = true;
        }
        if (hasErrors) return;

        var code = new Emitter(doc,
            facts.Accessibility, facts.Namespace, className,
            facts.ItemTypes.ToDictionary(fact => fact.SpanStart, fact => fact.Type),
            input.Path, ownSlots).Emit();

        spc.AddSource($"{className}.{StableHash(input.Path)}.g.cs", code);
    }

    /// <summary>
    /// D74 ②：源项目元数据声明问题的编译期出口（design/14 §14.5.4/§14.9.2）。
    /// 只有带源声明位置的 problem 才在这里报——引用程序集没有语法位置，
    /// 仍由 D74 ① 的使用点 EUI019/EUI020/EUI021 暴露。诊断不阻断 .ui 的生成输出，
    /// 与 EUI045 口径一致：Error 本身已阻断构建，生成器不改目录形状。
    /// </summary>
    private static void ReportCatalogProblems(SourceProductionContext spc, UiCatalog catalog)
    {
        // 类型级问题进 catalog.Problems（静态宿主、保留标签），成员级问题进
        // UiElementDef.Problems（属性/事件/内容/参数）——两处都要有出口。
        var problems = catalog.Problems
            .Concat(catalog.Elements.SelectMany(static element => element.Problems))
            .Distinct();
        foreach (var problem in problems)
        {
            if (problem.Source is null) continue;
            // 源项目控件的属性/事件/内容元数据问题统一归 EUI026（design/14 §14.9.2）；
            // EUI026/EUI028/EUI031 本就是声明处/目录侧的码，保持原样。
            var code = problem.Code is UiDiagnostics.PropertyNotBindable
                    or UiDiagnostics.EventFieldMissing
                    or UiDiagnostics.ContentTargetInvalid
                ? UiDiagnostics.MetadataDeclarationInvalid
                : problem.Code;
            Report(spc, "", EmptySourceText,
                UiDiagnostics.Error(code, problem.Message, UiSpan.Zero, problem.Source));
        }
    }

    private static readonly SourceText EmptySourceText = SourceText.From("");

    private static string StableHash(string path)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(path));
        var sb = new StringBuilder(16);
        for (int i = 0; i < 8; i++) sb.Append(bytes[i].ToString("x2"));
        return sb.ToString();
    }

    private static INamedTypeSymbol? FindCodeBehind(Compilation compilation, string name)
    {
        foreach (var symbol in compilation.GetSymbolsWithName(name, SymbolFilter.Type))
            if (symbol is INamedTypeSymbol type && type.TypeKind == TypeKind.Class)
                return type;
        return null;
    }

    /// <summary>
    /// D57/EUI045（spec/02 §10）：code-behind 与生成成员冲突。生成器不改名、仍按契约输出；
    /// 诊断落在 code-behind 的源声明，错误诊断本身阻断构建。
    /// 五类触发：Build/Render 声明、生成成员保留名、非 Component 基类、隐藏生成代码依赖成员。
    /// </summary>
    private static void ProbeCodeBehindConflicts(INamedTypeSymbol type, List<UiDiagnostic> diags)
    {
        foreach (var member in type.GetMembers())
        {
            var name = member.Name;
            string? reason = null;
            if (member is IMethodSymbol { MethodKind: MethodKind.Ordinary } && name is "Build" or "Render")
                reason = $"code-behind must not declare '{name}()'; the generator emits it";
            else if (IsGeneratedMemberName(name))
                reason = $"member '{name}' collides with a generated member name";
            else if (GeneratedDependencyNames.Contains(name))
                reason = $"member '{name}' hides a Component member used by generated code";
            if (reason is not null)
                diags.Add(Conflict(reason, member.Locations.FirstOrDefault(static l => l.IsInSource)));
        }

        foreach (var reference in type.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax() is not TypeDeclarationSyntax declaration || declaration.BaseList is null)
                continue;
            foreach (var baseType in declaration.BaseList.Types)
            {
                var text = baseType.Type.ToString();
                if (!IsComponentBase(text))
                    diags.Add(Conflict(
                        $"code-behind base '{text}' must be omitted or exactly Component",
                        baseType.GetLocation()));
            }
        }
    }

    private static readonly HashSet<string> GeneratedDependencyNames = new(StringComparer.Ordinal)
    {
        "Invalidate", "Mount", "Track", "TrackContent", "AddDisposable",
    };

    private static bool IsComponentBase(string text) => text is
        "Component" or "EclipseUI.Component" or "global::EclipseUI.Component";

    /// <summary>__el{N}/__cmp{N}/__blk{N}/__run{N}/__bind{N}/U{N}/R{N} 是生成成员保留名。</summary>
    private static bool IsGeneratedMemberName(string name)
    {
        string[] prefixes = { "__el", "__cmp", "__blk", "__run", "__bind", "U", "R" };
        foreach (var prefix in prefixes)
            if (name.StartsWith(prefix, StringComparison.Ordinal) &&
                name.Length > prefix.Length &&
                name.Skip(prefix.Length).All(char.IsDigit))
                return true;
        return false;
    }

    private static UiDiagnostic Conflict(string message, Location? location)
    {
        UiSourceLocation? source = null;
        if (location is { IsInSource: true })
        {
            var line = location.GetLineSpan();
            source = new UiSourceLocation(
                line.Path, location.SourceSpan.Start, location.SourceSpan.Length,
                line.StartLinePosition.Line, line.StartLinePosition.Character,
                line.EndLinePosition.Line, line.EndLinePosition.Character);
        }
        return UiDiagnostics.Error(UiDiagnostics.CodeBehindConflict, message, UiSpan.Zero, source);
    }

    /// <summary>
    /// code-behind 类的插槽表（spec/02 §4.12.5）：沿基类链读 [UiSlot] 属性。
    /// [UiParameter] 胜出（参数占表面，槽退出，与目录 EUI028 口径一致）；[UiIgnore] 胜出；
    /// 类型只接受 <c>Element?</c>（单根）与 <c>IReadOnlyList&lt;Element&gt;</c>（多根），
    /// 其余不进表（声明期问题归目录 EUI021 族）。
    /// </summary>
    private static (string DisplayName, IReadOnlyDictionary<string, UiSlotDef> Slots)? ResolveOwnComponent(
        INamedTypeSymbol? type, Compilation compilation)
    {
        if (type is null) return null;

        var element = compilation.GetTypeByMetadataName("EclipseUI.Element");
        var elementList = compilation.GetTypeByMetadataName("System.Collections.Generic.IReadOnlyList`1");
        var nullable = compilation.GetTypeByMetadataName("System.Nullable`1");
        var slots = new Dictionary<string, UiSlotDef>(StringComparer.Ordinal);
        for (var current = type; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
        {
            foreach (var member in current.GetMembers().OfType<IPropertySymbol>())
            {
                if (member.IsStatic || member.IsIndexer) continue;
                var attributes = member.GetAttributes().ToList();
                if (attributes.Any(a => a.AttributeClass?.ToDisplayString() == UiIgnoreAttributeName)) continue;
                var slot = attributes.FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == UiSlotAttributeName);
                if (slot is null) continue;
                // 参数胜出（catalog 口径："[UiParameter] and [UiSlot]; the parameter wins"）
                if (attributes.Any(a => a.AttributeClass?.ToDisplayString() == UiParameterAttributeName)) continue;

                var name = slot.ConstructorArguments.Length == 1 &&
                    slot.ConstructorArguments[0].Value is string slotName ? slotName : null;
                if (string.IsNullOrEmpty(name) || slots.ContainsKey(name!)) continue;
                if (CardinalityOf(member.Type, element, elementList, nullable) is not { } cardinality) continue;

                slots[name!] = new UiSlotDef(
                    name!, member.Name, member.Type.ToDisplayString(UiCatalogFactory.Qualified), cardinality);
            }
        }
        return (type.ToDisplayString(UiCatalogFactory.Qualified), slots);
    }

    private static UiSlotCardinality? CardinalityOf(
        ITypeSymbol type, ITypeSymbol? element, ITypeSymbol? elementList, ITypeSymbol? nullable)
    {
        if (element is null) return null;
        if (elementList is not null && type is INamedTypeSymbol named &&
            SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, elementList) &&
            named.TypeArguments.Length == 1 &&
            SymbolEqualityComparer.Default.Equals(named.TypeArguments[0], element))
            return UiSlotCardinality.Multiple;
        var unwrapped = type;
        if (unwrapped is INamedTypeSymbol wrapper && nullable is not null &&
            SymbolEqualityComparer.Default.Equals(wrapper.OriginalDefinition, nullable))
            unwrapped = wrapper.TypeArguments[0];
        return SymbolEqualityComparer.Default.Equals(unwrapped, element)
            ? UiSlotCardinality.Single
            : null;
    }

    private static bool IsPartial(INamedTypeSymbol type)
    {
        foreach (var reference in type.DeclaringSyntaxReferences)
            if (reference.GetSyntax() is TypeDeclarationSyntax declaration &&
                declaration.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)))
                return true;
        return false;
    }

    private static string AccessibilityOf(INamedTypeSymbol type) =>
        type.DeclaredAccessibility == Accessibility.Public ? "public" : "internal";

    private static string NamespaceName(INamedTypeSymbol type) =>
        type.ContainingNamespace.IsGlobalNamespace ? "" : type.ContainingNamespace.ToDisplayString();

    private static ITypeSymbol? ElementTypeOf(ITypeSymbol type, Compilation compilation)
    {
        if (type is IArrayTypeSymbol array) return array.ElementType;
        var enumerable = compilation.GetTypeByMetadataName("System.Collections.Generic.IEnumerable`1");
        if (enumerable is null) return null;
        if (type is INamedTypeSymbol named &&
            SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, enumerable))
            return named.TypeArguments[0];
        foreach (var iface in type.AllInterfaces)
            if (SymbolEqualityComparer.Default.Equals(iface.OriginalDefinition, enumerable))
                return iface.TypeArguments[0];
        return null;
    }

    /// <summary>
    /// 逐块推断 item 类型，并校验每个 <c>@bind</c> 源：两者都把表达式放进合成 partial 方法体，
    /// 借 code-behind 的符号上下文让 Roslyn 回答；外层 item 以局部变量注入，支持嵌套作用域。
    /// </summary>
    private static Dictionary<UiForeach, string> ProbeSemantics(
        UiDocument doc, INamedTypeSymbol type, Compilation compilation, List<UiDiagnostic> diags)
    {
        var itemTypes = new Dictionary<UiForeach, string>();
        var probe = new TemplateProbe(compilation, type);
        var env = new List<(ITypeSymbol Type, string Name)>();
        Walk(doc.Nodes, env);
        return itemTypes;

        void Walk(IEnumerable<UiNode> nodes, List<(ITypeSymbol Type, string Name)> scope)
        {
            foreach (var node in nodes)
            {
                switch (node)
                {
                    case UiElement element:
                        foreach (var attr in element.Attributes)
                        {
                            ValidateBindSource(attr, probe, scope, diags);
                            ValidateRefTarget(element, attr, probe, scope, diags);
                        }
                        Walk(element.Children, scope);
                        break;
                    case UiSlotNode fill:
                        // 插槽填充的内容是组件 Build 的一部分：@foreach item 推断与 @bind 校验
                        // 必须穿透（EUI007/EUI037 与内容上下文同口径）
                        Walk(fill.Children, scope);
                        break;
                    case UiIf ifNode:
                        Walk(ifNode.Children, scope);
                        break;
                    case UiForeach foreachNode:
                        var collectionType = probe.InferType(foreachNode.Collection, scope);
                        var itemType = collectionType is null ? null : ElementTypeOf(collectionType, compilation);
                        if (itemType is null)
                        {
                            diags.Add(UiDiagnostics.Error(UiDiagnostics.ForEachItemTypeUnknown,
                                $"cannot infer the item type of '{foreachNode.Collection.Trim()}'; " +
                                "the collection must be an IEnumerable<T> (array, List<T>, dictionary, LINQ result or custom implementation)",
                                foreachNode.CollectionSpan));
                            break;
                        }

                        itemTypes[foreachNode] = itemType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                        var inner = new List<(ITypeSymbol Type, string Name)>(scope)
                        {
                            (itemType, foreachNode.VarName.Length > 0 ? foreachNode.VarName : "item"),
                        };
                        Walk(foreachNode.Children, inner);
                        break;
                }
            }
        }
    }

    /// <summary>
    /// <c>@bind-P={expr}</c> 的源必须是可写左值（spec/01 §4.5）：句法白名单先拒绝调用/索引器/
    /// <c>?.</c>/<c>??</c>/转换/new/lambda，再用赋值探针取 C# 的可写性与类型结论。
    /// 目标非法（EUI040）与非表达式值（EUI037）已由分析器报错，这里不重复报。
    /// </summary>
    private static void ValidateBindSource(UiAttribute attr, TemplateProbe probe,
        IReadOnlyList<(ITypeSymbol Type, string Name)> env, List<UiDiagnostic> diags)
    {
        // 控件目标看 PropDef；组件目标看 ParamDef（spec/02 §4.4.3 回调由父推送实现，源校验同规则）。
        string? clrType = null;
        if (attr.PropDef is { CanWrite: true }) clrType = attr.PropDef.ClrTypeDisplayName;
        else if (attr.ParamDef is { CanWrite: true, IsDelegate: false, IsContent: false } p) clrType = p.ClrTypeDisplayName;
        if (!attr.IsBind || clrType is null) return;
        if (attr.Kind != AttrValueKind.Expression || string.IsNullOrWhiteSpace(attr.Value)) return;

        var expression = attr.Value.Trim();
        if (!probe.IsWritableSource(expression, clrType, env, out var reason))
            diags.Add(UiDiagnostics.Error(UiDiagnostics.BindSourceInvalid,
                $"@bind-{attr.Name} source '{expression}' is not writable: {reason}", attr.ValueSpan));
    }

    /// <summary>
    /// <c>Ref={field}</c> 的语义级校验（spec/01 §4.3：简单标识符、字段可写）：
    /// 字段缺失（CS0103）、类型不兼容（CS0029）、只读/静态/标注 const 的一律拒绝。
    /// 语法级（非简单标识符）已由分析器报 EUI029，这里不重复。
    /// </summary>
    private static void ValidateRefTarget(UiElement element, UiAttribute attr, TemplateProbe probe,
        IReadOnlyList<(ITypeSymbol Type, string Name)> env, List<UiDiagnostic> diags)
    {
        if (attr.Name != "Ref" || attr.RefTarget is null || element.ResolvedDef is null) return;
        if (!probe.IsRefTarget(attr.RefTarget, element.ResolvedDef.ClrTypeDisplayName, env, out var reason))
            diags.Add(UiDiagnostics.Error(UiDiagnostics.RefTargetInvalid,
                $"Ref target '{attr.RefTarget}' is invalid: {reason}", attr.ValueSpan));
    }

    private sealed class TemplateProbe
    {
        private readonly Compilation _compilation;
        private readonly INamedTypeSymbol _type;
        private int _counter;

        public TemplateProbe(Compilation compilation, INamedTypeSymbol type)
        {
            _compilation = compilation;
            _type = type;
        }

        public ITypeSymbol? InferType(string expression, IReadOnlyList<(ITypeSymbol Type, string Name)> env)
        {
            var tree = ProbeTree("var __value = (" + expression + ");", env);
            var augmented = _compilation.AddSyntaxTrees(tree);
            var model = augmented.GetSemanticModel(tree);
            var method = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().LastOrDefault();
            if (method is null) return null;
            var declarator = method.DescendantNodes().OfType<VariableDeclaratorSyntax>()
                .FirstOrDefault(v => v.Identifier.ValueText == "__value");
            if (declarator is null) return null;
            if (model.GetDeclaredSymbol(declarator) is not ILocalSymbol local) return null;
            return local.Type.TypeKind == TypeKind.Error || local.Type is IDynamicTypeSymbol ? null : local.Type;
        }

        /// <summary>把 <c>{expr} = __bindValue</c> 交给 C# 判定：落在赋值语句内的错误即左值/类型问题。</summary>
        public bool IsWritableSource(string expression, string targetClrType,
            IReadOnlyList<(ITypeSymbol Type, string Name)> env, out string? reason)
        {
            if (!IsLvalueShape(SyntaxFactory.ParseExpression(expression), out var rootName))
            {
                reason = "it must be a writable field or property of the component or of the item " +
                    "(not a method call, indexer, '?.', '??', cast, 'new' or lambda)";
                return false;
            }

            // 值类型 item 是副本：写它的成员既不报错也传不回去，必须静态拒绝（spec/01 §4.5）。
            if (rootName is not null)
            {
                foreach (var (envType, name) in env)
                {
                    if (name != rootName || !envType.IsValueType) continue;
                    reason = $"the @foreach item '{name}' is a value type ({envType.Name}), " +
                        $"so '{expression}' would write into a copy";
                    return false;
                }
            }

            var tree = ProbeTree($"{expression} = __bindValue;", env,
                $"        {ClrTypeOf(targetClrType)} __bindValue = default!;");
            var augmented = _compilation.AddSyntaxTrees(tree);
            var model = augmented.GetSemanticModel(tree);
            var assignment = tree.GetRoot().DescendantNodes().OfType<AssignmentExpressionSyntax>().FirstOrDefault();
            if (assignment is null)
            {
                reason = "it is not an assignment target";
                return false;
            }

            switch (model.GetSymbolInfo(assignment.Left).Symbol)
            {
                case IFieldSymbol { IsStatic: true }:
                    reason = "a static field cannot be written back";
                    return false;
                case IFieldSymbol { IsConst: true }:
                    reason = "a constant cannot be written back";
                    return false;
                case IPropertySymbol { IsStatic: true }:
                    reason = "a static property cannot be written back";
                    return false;
            }

            foreach (var diagnostic in model.GetDiagnostics())
            {
                if (diagnostic.Severity != Microsoft.CodeAnalysis.DiagnosticSeverity.Error) continue;
                if (!assignment.Span.Contains(diagnostic.Location.SourceSpan)) continue;
                reason = $"{diagnostic.GetMessage()} ({diagnostic.Id})";
                return false;
            }

            reason = null;
            return true;
        }

        /// <summary>
        /// <c>Ref={field}</c> 用赋值探针取 C# 结论：把 <c>{target} = __refValue</c>（__refValue 为
        /// 元素/组件实例类型）放进合成方法体。缺失（CS0103）/类型不符（CS0029）/只读（CS0191）由
        /// 赋值语句内的 CS 错误回答（与 <see cref="IsWritableSource"/> 同法）；编译干净后再要求
        /// 目标是 code-behind 的非静态实例字段（static/const/readonly 兜底，防语义合法但形态非法）。
        /// </summary>
        public bool IsRefTarget(string target, string valueClrType,
            IReadOnlyList<(ITypeSymbol Type, string Name)> env, out string? reason)
        {
            if (!IsLvalueShape(SyntaxFactory.ParseExpression(target), out _))
            {
                reason = "it must be a simple identifier";
                return false;
            }

            // Ref 目标不允许落在 @foreach item 名上：真实生成代码里 item 是迭代变量，不可赋值。
            foreach (var (_, name) in env)
            {
                if (name != target) continue;
                reason = $"'{target}' is the @foreach item; the Ref target must be an instance field " +
                    "declared on the code-behind";
                return false;
            }

            var tree = ProbeTree($"{target} = __refValue;", env,
                $"        {ClrTypeOf(valueClrType)} __refValue = default!;");
            var augmented = _compilation.AddSyntaxTrees(tree);
            var model = augmented.GetSemanticModel(tree);
            var assignment = tree.GetRoot().DescendantNodes().OfType<AssignmentExpressionSyntax>().FirstOrDefault();
            if (assignment is null)
            {
                reason = "it is not an assignment target";
                return false;
            }

            // C# 诊断优先：缺失/类型不符/readonly 在 bound 阶段就把左值标记为失败，Symbol 会变 null，
            // 所以必须先问诊断再问符号（readonly 实测 CS0191 + null symbol）。
            foreach (var diagnostic in model.GetDiagnostics())
            {
                if (diagnostic.Severity != Microsoft.CodeAnalysis.DiagnosticSeverity.Error) continue;
                if (!assignment.Span.Contains(diagnostic.Location.SourceSpan)) continue;
                reason = $"{diagnostic.GetMessage()} ({diagnostic.Id})";
                return false;
            }

            if (model.GetSymbolInfo(assignment.Left).Symbol is not IFieldSymbol field)
            {
                reason = "the Ref target must be an instance field of the code-behind class";
                return false;
            }
            if (field.IsStatic)
            {
                reason = $"'{field.Name}' is a static field and cannot hold a template instance";
                return false;
            }
            if (field.IsConst)
            {
                reason = $"'{field.Name}' is a constant";
                return false;
            }
            if (field.IsReadOnly)
            {
                reason = $"'{field.Name}' is readonly and can only be assigned in a constructor";
                return false;
            }

            reason = null;
            return true;
        }

        /// <summary>句法白名单：标识符 / this / base，以及它们之间的简单成员访问链（可加括号）。</summary>
        private static bool IsLvalueShape(ExpressionSyntax expression, out string? rootName)
        {
            rootName = null;
            while (true)
            {
                if (expression is ParenthesizedExpressionSyntax paren)
                {
                    expression = paren.Expression;
                    continue;
                }
                if (expression is IdentifierNameSyntax identifier)
                {
                    rootName = identifier.Identifier.ValueText;
                    return true;
                }
                if (expression is ThisExpressionSyntax or BaseExpressionSyntax) return true;
                if (expression is MemberAccessExpressionSyntax access &&
                    access.IsKind(SyntaxKind.SimpleMemberAccessExpression))
                {
                    expression = access.Expression;
                    continue;
                }
                return false;
            }
        }

        /// <summary>目录里的 CLR 类型名 → 探针里可用的全限定类型。</summary>
        private static string ClrTypeOf(string clrType) => clrType switch
        {
            "bool" => "global::System.Boolean",
            "byte" => "global::System.Byte",
            "char" => "global::System.Char",
            "double" => "global::System.Double",
            "float" => "global::System.Single",
            "int" => "global::System.Int32",
            "long" => "global::System.Int64",
            "object" => "global::System.Object",
            "string" => "global::System.String",
            _ => clrType.StartsWith("global::", StringComparison.Ordinal) ? clrType : "global::" + clrType,
        };

        /// <summary>合成探针方法：与 code-behind 同命名空间的 partial 类，item 以局部变量注入。</summary>
        private SyntaxTree ProbeTree(string statement, IReadOnlyList<(ITypeSymbol Type, string Name)> env,
            string? preamble = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine("#nullable enable");
            if (!_type.ContainingNamespace.IsGlobalNamespace)
            {
                sb.Append("namespace ").Append(_type.ContainingNamespace.ToDisplayString()).AppendLine(";");
                sb.AppendLine();
            }
            sb.Append(_type.DeclaredAccessibility == Accessibility.Public ? "public" : "internal")
                .Append(" partial class ").Append(_type.Name).AppendLine();
            sb.AppendLine("{");
            sb.Append("    private void __EclipseUiProbe").Append(_counter++).AppendLine("()");
            sb.AppendLine("    {");
            foreach (var (envType, name) in env)
                sb.Append("        ")
                    .Append(envType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
                    .Append(' ').Append(name).AppendLine(" = default!;");
            if (preamble is not null) sb.AppendLine(preamble);
            sb.Append("        ").Append(statement).AppendLine(";");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            // 探针树必须与编译单元共享解析选项，否则 AddSyntaxTrees 拒绝混合特征。
            var parseOptions = _compilation.SyntaxTrees.FirstOrDefault()?.Options as CSharpParseOptions
                ?? CSharpParseOptions.Default;
            return CSharpSyntaxTree.ParseText(sb.ToString(), parseOptions);
        }
    }

    private static void Report(SourceProductionContext spc, string path, SourceText text, UiDiagnostic diag)
    {
        Location location;
        if (diag.Source is { } source)
        {
            // C# 源声明：用纯数据重建外部文件位置，不把 SyntaxTree 带进增量缓存。
            // 内存编译的语法树没有 path（测试桩），Location.Create 不接受空路径，退回无位置诊断。
            var sourceSpan = new MsTextSpan(source.Start, Math.Max(0, source.Length));
            location = source.Path.Length == 0
                ? Location.None
                : Location.Create(source.Path, sourceSpan, new LinePositionSpan(
                    new LinePosition(source.StartLine, source.StartCharacter),
                    new LinePosition(source.EndLine, source.EndCharacter)));
        }
        else
        {
            var span = new MsTextSpan(diag.Span.Start, Math.Max(0, diag.Span.Length));
            location = Location.Create(path, span, text.Lines.GetLinePositionSpan(span));
        }
        var descriptor = new DiagnosticDescriptor(
            diag.Code, diag.Code, "{0}", "EclipseUI",
            diag.Severity switch
            {
                CompilerSeverity.Error => RoslynSeverity.Error,
                CompilerSeverity.Warning => RoslynSeverity.Warning,
                _ => RoslynSeverity.Info,
            },
            isEnabledByDefault: true);
        spc.ReportDiagnostic(Diagnostic.Create(descriptor, location, diag.Message));
    }
}

internal sealed record UiFileInput(string Path, string Text);

internal sealed record ItemTypeFact(int SpanStart, string Type);

/// <summary>逐文件语义事实：值相等是增量缓存的前提（ImmutableArray 默认引用相等，必须逐元素比）。</summary>
internal sealed record UiFileFacts(
    string Accessibility,
    string Namespace,
    string DisplayName,
    ImmutableArray<UiSlotDef> Slots,
    ImmutableArray<UiDiagnostic> CodeBehindDiags,
    ImmutableArray<UiDiagnostic> ProbeDiags,
    ImmutableArray<ItemTypeFact> ItemTypes)
{
    public bool Equals(UiFileFacts? other) =>
        other is not null &&
        Accessibility == other.Accessibility &&
        Namespace == other.Namespace &&
        DisplayName == other.DisplayName &&
        Slots.SequenceEqual(other.Slots) &&
        CodeBehindDiags.SequenceEqual(other.CodeBehindDiags) &&
        ProbeDiags.SequenceEqual(other.ProbeDiags) &&
        ItemTypes.SequenceEqual(other.ItemTypes);

    public override int GetHashCode()
    {
        var hash = 17;
        hash = Add(hash, Accessibility);
        hash = Add(hash, Namespace);
        hash = Add(hash, DisplayName);
        foreach (var slot in Slots) hash = Add(hash, slot);
        foreach (var diag in CodeBehindDiags) hash = Add(hash, diag);
        foreach (var diag in ProbeDiags) hash = Add(hash, diag);
        foreach (var fact in ItemTypes) hash = Add(hash, fact);
        return hash;

        // netstandard2.0 没有 System.HashCode，与 CatalogValueEq 同口径累加。
        static int Add(int h, object? v) => unchecked(h * 397 + (v?.GetHashCode() ?? 0));
    }
}

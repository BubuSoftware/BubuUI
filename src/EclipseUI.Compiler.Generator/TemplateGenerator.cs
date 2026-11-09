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
        // K23 ④ 单文件形态（无 .cs）的命名空间归属对齐 Razor：取项目根命名空间
        // （MSBuild 标准管道 build_property.rootnamespace；测试桩等拿不到时落 global）。
        var rootNamespace = context.AnalyzerConfigOptionsProvider
            .Select(static (provider, _) =>
                provider.GlobalOptions.TryGetValue("build_property.rootnamespace", out var value)
                    ? value ?? "" : "");
        var perFile = uiFiles.Combine(catalogProvider).Combine(context.CompilationProvider)
            .Combine(rootNamespace)
            .Select(static (pair, _) => (
                Input: pair.Left.Left.Left,
                Catalog: pair.Left.Left.Right,
                RootNamespace: pair.Right,
                Facts: ExtractFacts(pair.Left.Left.Left, pair.Left.Left.Right, pair.Left.Right, pair.Right)))
            // 命名节点让逐文件生成在 TrackedSteps 里可独立观测：D74 ② 的目录级诊断出口
            // 是第二个 SourceOutput，不能把所有 "SourceOutput" 混成一组断言（§14.10.3）。
            .WithTrackingName("PerFileGeneration");
        context.RegisterSourceOutput(perFile, static (spc, data) =>
            ExecuteFile(spc, data.Input, data.Catalog, data.Facts));

        // E1（design/17 §4.1）：.eustyle → StyleBootstrap.g.cs。样式表文件数少且全局单源，
        // Collect 成批处理（一个 Apply 出口、AppBuilder.UseStyles 直连）；诊断逐文件定位，
        // 任一文件存在 Error 短路整个产物（与 .ui 管道口径一致）。
        var styleSheets = context.AdditionalTextsProvider
            .Where(static file => Path.GetExtension(file.Path) == ".eustyle")
            .Select(static (file, ct) => new UiFileInput(file.Path, file.GetText(ct)?.ToString() ?? ""))
            .Collect()
            .Combine(rootNamespace);
        context.RegisterSourceOutput(styleSheets, static (spc, data) =>
            ExecuteStylesheet(spc, data.Left, data.Right));
    }

    private static UiFileFacts ExtractFacts(UiFileInput input, UiCatalog catalog, Compilation compilation,
        string rootNamespace)
    {
        var className = Path.GetFileNameWithoutExtension(input.Path);
        var validName = SyntaxFacts.IsValidIdentifier(className);
        var codeBehindDiags = new List<UiDiagnostic>();

        // S6：先解析文档——EUI005 放宽（K23 ④：@code 或同名 partial 至少其一）、@using 收集（K22）
        // 与 @code 成员收集（K21）都以 AST 为准。
        var parseDiags = new List<UiDiagnostic>();
        var doc = FrontEnd.Parse(input.Text, input.Path, parseDiags);
        var usings = CollectUsingLines(doc.Nodes);
        var codeBlocks = doc.Nodes.OfType<UiCodeBlock>().ToList();

        var type = validName ? FindCodeBehind(compilation, className) : null;
        if (validName)
        {
            if (type is null && codeBlocks.Count == 0)
            {
                // K23 ④ / K25：EUI005 放宽为「@code 或同名 partial 至少其一」
                codeBehindDiags.Add(UiDiagnostics.Error(UiDiagnostics.CodeBehindMissing,
                    $"no code-behind class '{className}' found and the template has no @code block; " +
                    "add a partial class with that name or declare members in @code", UiSpan.Zero));
            }
            else if (type is not null && type.ContainingType is not null)
            {
                codeBehindDiags.Add(UiDiagnostics.Error(UiDiagnostics.CodeBehindNested,
                    $"code-behind class '{className}' must be top-level", UiSpan.Zero));
            }
            else if (type is not null && !IsPartial(type))
            {
                codeBehindDiags.Add(UiDiagnostics.Error(UiDiagnostics.CodeBehindNotPartial,
                    $"code-behind class '{className}' must be declared partial", UiSpan.Zero));
            }
        }

        // D57/EUI045（spec/02 §10）：code-behind 与生成成员/基类成员冲突。
        if (type is not null)
            ProbeCodeBehindConflicts(type, codeBehindDiags);

        // K25：EUI045 扩展到 @code 成员——撞生成保留名（Build/Render/U{N}/R{N}/__el{N}/…）
        // 报在 .ui 的 @code 块内成员位置。@code ↔ 同名 .cs 的冲突由 C# partial 规则兜底，不在这里报。
        if (codeBlocks.Count > 0)
            ProbeCodeMemberConflicts(codeBlocks, codeBehindDiags);

        // 自身组件（spec/02 §4.12.5）：code-behind 符号直接派生插槽表——目录里没有
        // code-behind 类的 def（基类由生成注入，源码不派生 Component），这是唯一权威来源。
        var own = ResolveOwnComponent(type, compilation);
        var slots = own is null
            ? ImmutableArray<UiSlotDef>.Empty
            : own.Value.Slots.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Value).ToImmutableArray();

        // 探针读 Analyze 回写在 AST 上的 PropDef/ParamDef/ResolvedDef，必须先分析一遍（诊断丢弃，发射阶段重报）。
        FrontEnd.Analyze(doc, catalog, new List<UiDiagnostic>(),
            own is null ? null : slots.ToDictionary(slot => slot.Name, StringComparer.Ordinal),
            own?.DisplayName);

        // R1/K21：先全量收集全部 @code 成员，合成 partial 类语法树加入探针 Compilation——
        // 否则「模板在上、@code 在下」会让前置 {expr} 集体误报 EUI007/EUI024/EUI029/EUI037。
        // K23 ④：无 code-behind 时类壳归属项目根命名空间（Razor 口径），探针树与成员树必须
        // 用同一命名空间，否则 partial 类合并不了、探针看不到 @code 成员。
        var effectiveNamespace = type is null ? SanitizeNamespace(rootNamespace) : NamespaceName(type);
        var accessibility = type is null ? "public" : AccessibilityOf(type);
        var memberTrees = BuildCodeMemberTrees(codeBlocks, type, className, effectiveNamespace, usings, compilation);
        var probe = new TemplateProbe(compilation, type, className, effectiveNamespace, memberTrees, usings);
        var probeDiags = new List<UiDiagnostic>();
        var switchTypes = new List<SwitchTypeFact>();
        if (type is not null || codeBlocks.Count > 0)
        {
            var itemTypes = ProbeSemantics(doc, probe, compilation, probeDiags, switchTypes)
                .Select(kv => new ItemTypeFact(kv.Key.CollectionSpan.Start, kv.Value))
                .OrderBy(fact => fact.SpanStart)
                .ToImmutableArray();

            return new UiFileFacts(
                accessibility,
                effectiveNamespace,
                own?.DisplayName ?? "",
                slots,
                codeBehindDiags.ToImmutableArray(),
                probeDiags.ToImmutableArray(),
                itemTypes,
                switchTypes.OrderBy(fact => fact.SpanStart).ToImmutableArray());
        }

        return new UiFileFacts(
            "internal", "", "", slots,
            codeBehindDiags.ToImmutableArray(),
            probeDiags.ToImmutableArray(),
            ImmutableArray<ItemTypeFact>.Empty,
            ImmutableArray<SwitchTypeFact>.Empty);
    }

    /// <summary>K22/K31：根层 @using 的 using 子句正文（命名空间 + 别名，按文档顺序去重）。</summary>
    private static ImmutableArray<string> CollectUsingLines(IEnumerable<UiNode> nodes) =>
        TemplateAnalyzer.CollectUsingLines(nodes).ToImmutableArray();

    /// <summary>
    /// K21/K25：@code 成员与生成保留名的冲突（EUI045）。对块体原文做轻量成员名提取
    /// （字段/属性/方法/嵌套类型声明的标识符），撞保留名报在 .ui 的成员 span；
    /// 内容粒度的其余校验（语句合法性、与 .cs 的重复）由探针 Compilation 与 C# 兜底。
    /// </summary>
    private static void ProbeCodeMemberConflicts(IReadOnlyList<UiCodeBlock> blocks, List<UiDiagnostic> diags)
    {
        foreach (var block in blocks)
        {
            foreach (var (name, nameOffset, nameLength) in ExtractMemberNames(block.Code))
            {
                string? reason = name is "Build" or "Render"
                    ? $"@code must not declare '{name}()'; the generator emits it"
                    : IsGeneratedMemberName(name)
                        ? $"@code member '{name}' collides with a generated member name"
                        : null;
                if (reason is null) continue;
                var span = new UiSpan(block.CodeSpan.Start + nameOffset, nameLength);
                diags.Add(UiDiagnostics.Error(UiDiagnostics.CodeBehindConflict, reason, span));
            }
        }
    }

    /// <summary>
    /// 提取 @code 块体内的成员名（语法级扫描，不完整代码也尽力）：字段/属性/方法/嵌套类型
    /// 声明位的标识符。返回 (名字, 在块体原文内的偏移, 长度)。
    /// </summary>
    private static IEnumerable<(string Name, int Offset, int Length)> ExtractMemberNames(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(
            "class __EuiProbe\n{\n" + code + "\n}");
        var root = tree.GetCompilationUnitRoot();
        var container = root.DescendantNodes().OfType<TypeDeclarationSyntax>().First();
        foreach (var member in container.Members)
        {
            string? name = member switch
            {
                FieldDeclarationSyntax field => field.Declaration.Variables.FirstOrDefault()?.Identifier.ValueText,
                PropertyDeclarationSyntax property => property.Identifier.ValueText,
                MethodDeclarationSyntax method => method.Identifier.ValueText,
                TypeDeclarationSyntax nested => nested.Identifier.ValueText,
                EventDeclarationSyntax or EventFieldDeclarationSyntax => null,
                _ => null,
            };
            if (string.IsNullOrEmpty(name)) continue;
            MsTextSpan? csSpan = member switch
            {
                FieldDeclarationSyntax field => field.Declaration.Variables.FirstOrDefault()?.Identifier.GetLocation().SourceSpan,
                PropertyDeclarationSyntax property => property.Identifier.GetLocation().SourceSpan,
                MethodDeclarationSyntax method => method.Identifier.GetLocation().SourceSpan,
                TypeDeclarationSyntax nested => nested.Identifier.GetLocation().SourceSpan,
                _ => null,
            };
            if (csSpan is null) continue;
            // 合成前缀 "class __EuiProbe\n{\n" 的长度
            const int prefixLength = 20;
            var offset = csSpan.Value.Start - prefixLength;
            if (offset < 0 || offset + csSpan.Value.Length > code.Length) continue;
            yield return (name!, offset, csSpan.Value.Length);
        }
    }

    /// <summary>
    /// R1/K21：把全部 @code 块合成一棵 partial 类语法树（含模板 @using 的 using 行），
    /// 供探针 Compilation 注入——保证后置块成员对前置表达式同样可见。
    /// 命名空间由调用方给定（code-behind 的命名空间，或单文件形态的项目根命名空间）。
    /// </summary>
    private static ImmutableArray<SyntaxTree> BuildCodeMemberTrees(
        IReadOnlyList<UiCodeBlock> blocks, INamedTypeSymbol? type, string className,
        string namespaceName, ImmutableArray<string> usings, Compilation compilation)
    {
        if (blocks.Count == 0) return ImmutableArray<SyntaxTree>.Empty;
        var sb = new StringBuilder();
        sb.AppendLine("#nullable enable");
        foreach (var ns in usings)
            sb.AppendLine($"using {ns};");
        if (namespaceName.Length > 0)
            sb.Append("namespace ").AppendLine(namespaceName + ";");
        var accessibility = type is null || type.DeclaredAccessibility == Accessibility.Public ? "public" : "internal";
        sb.Append(accessibility).Append(" partial class ").AppendLine(type?.Name ?? className);
        sb.AppendLine("{");
        foreach (var block in blocks)
            sb.AppendLine(block.Code);
        sb.AppendLine("}");

        var parseOptions = compilation.SyntaxTrees.FirstOrDefault()?.Options as CSharpParseOptions
            ?? CSharpParseOptions.Default;
        return ImmutableArray.Create<SyntaxTree>(CSharpSyntaxTree.ParseText(sb.ToString(), parseOptions));
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

        // E1（design/17 §4.8）：@style 组件块——Eustyle 解析，类名加组件前缀后交 Emitter
        // 在组件初始化注册。块内诊断 span 经 CodeSpan 偏移映射回 .ui 绝对位置；样式错误
        // 是 Error（短路生成，与 .ui 管道口径一致），健康块的规则仍进产物。
        var styleRules = new List<EustyleRule>();
        foreach (var block in doc.Nodes.OfType<UiStyleBlock>())
        {
            var blockDiags = new List<UiDiagnostic>();
            var sheet = Eustyle.Parse(block.Code, input.Path, blockDiags, block.CodeSpan.Start);
            diags.AddRange(blockDiags);
            if (blockDiags.Exists(static d => d.IsError)) continue;
            foreach (var rule in sheet.Rules)
                styleRules.Add(new EustyleRule(className + "__" + rule.ClassName, rule.NameSpan, rule.Setters));
        }

        bool hasErrors = false;
        foreach (var diag in diags)
        {
            Report(spc, input.Path, sourceText, diag);
            // EUI045 只报冲突，不阻止生成器输出（spec/02 §10：生成器不改名、仍按本文生成，
            // 错误诊断本身已阻断构建）。其它错误诊断维持「不输出」的既有口径。
            if (diag.IsError && diag.Code != UiDiagnostics.CodeBehindConflict) hasErrors = true;
        }
        if (hasErrors) return;

        // K22/K21/K29：@using、@code 块与 @switch 值类型随发射交给 Emitter
        // （doc 在这里重解析，与 ExtractFacts 的解析同输入同结果）。
        var code = new Emitter(doc,
            facts.Accessibility, facts.Namespace, className,
            facts.ItemTypes.ToDictionary(fact => fact.SpanStart, fact => fact.Type),
            input.Path, ownSlots,
            facts.SwitchTypes.ToDictionary(fact => fact.SpanStart, fact => fact.Type),
            TemplateAnalyzer.CollectUsingLines(doc.Nodes),
            doc.Nodes.OfType<UiCodeBlock>().ToList(),
            styleRules).Emit();

        spc.AddSource($"{className}.{StableHash(input.Path)}.g.cs", code);
    }

    /// <summary>
    /// E1（design/17 §4.1）：.eustyle 编译产物——全部文件的规则按文件路径序合并为一个
    /// <c>StyleBootstrap.Apply(StyleRegistry)</c> 静态入口（AppBuilder.UseStyles 方法组直连）。
    /// 命名空间取项目根命名空间（与单文件组件同口径）。诊断逐文件报告；任一文件存在
    /// Error 短路生成。同名类跨文件重复：保持「后注册覆盖先注册」运行时语义，顺序由路径排序固定。
    /// </summary>
    private static void ExecuteStylesheet(SourceProductionContext spc,
        ImmutableArray<UiFileInput> files, string rootNamespace)
    {
        if (files.IsEmpty) return;

        var ordered = files.OrderBy(static f => f.Path, StringComparer.Ordinal).ToList();
        var sheets = new List<EustyleStylesheet>();
        var hasErrors = false;
        foreach (var file in ordered)
        {
            var text = SourceText.From(file.Text);
            var diags = new List<UiDiagnostic>();
            var sheet = Eustyle.Parse(file.Text, file.Path, diags);
            foreach (var diag in diags)
            {
                Report(spc, file.Path, text, diag);
                if (diag.IsError) hasErrors = true;
            }
            sheets.Add(sheet);
        }
        if (hasErrors) return;

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated> 由 EclipseUI 样式表编译器生成；请勿手动修改。 </auto-generated>");
        sb.AppendLine("#nullable enable");
        var ns = SanitizeNamespace(rootNamespace);
        if (ns.Length > 0)
        {
            sb.Append("namespace ").Append(ns).AppendLine(";");
            sb.AppendLine();
        }
        sb.AppendLine("/// <summary>.eustyle 编译产物（E1）：把样式表类注册进 StyleRegistry——");
        sb.AppendLine("/// AppBuilder.UseStyles 方法组直连本方法，宿主启动期调用一次。</summary>");
        sb.AppendLine("public static class StyleBootstrap");
        sb.AppendLine("{");
        sb.AppendLine("    public static void Apply(global::EclipseUI.Rendering.StyleRegistry registry)");
        sb.AppendLine("    {");
        foreach (var sheet in sheets)
            sb.Append(EustyleGenerator.EmitDefines(sheet, "registry"));
        sb.AppendLine("    }");
        sb.AppendLine("}");
        spc.AddSource("StyleBootstrap.g.cs", sb.ToString());
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
            if (member is IMethodSymbol { MethodKind: MethodKind.Ordinary } &&
                name is "Build" or "Render" or "__RegisterStyles")
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

    /// <summary>__el{N}/__cmp{N}/__blk{N}/__run{N}/__bind{N}/__eui*/U{N}/R{N} 是生成成员保留名。</summary>
    private static bool IsGeneratedMemberName(string name)
    {
        string[] prefixes = { "__el", "__cmp", "__blk", "__run", "__bind", "__eui", "U", "R" };
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

    /// <summary>
    /// K23 ④：项目根命名空间仅在每段都是合法标识符时采用（MSBuild 可能给出空串或
    /// 带点的占位值）；不合法落 global 命名空间，交由生成代码的可见性自然成立。
    /// </summary>
    private static string SanitizeNamespace(string rootNamespace) =>
        string.IsNullOrWhiteSpace(rootNamespace) ? "" :
        rootNamespace.Split('.').All(SyntaxFacts.IsValidIdentifier) ? rootNamespace : "";

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
    /// 逐块推断 item 类型与 @switch selector 类型，并校验每个 <c>@bind</c> 源：都把表达式放进
    /// 合成 partial 方法体，借探针 Compilation（含 @code 成员合成树，R1）的符号上下文让 Roslyn 回答；
    /// 外层 item 以局部变量注入，支持嵌套作用域。
    /// </summary>
    private static Dictionary<UiForeach, string> ProbeSemantics(
        UiDocument doc, TemplateProbe probe, Compilation compilation, List<UiDiagnostic> diags,
        List<SwitchTypeFact> switchTypes)
    {
        var itemTypes = new Dictionary<UiForeach, string>();
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
                            ValidateEventHandlerSignature(attr, probe, scope, diags);
                            ValidateExpressionValue(attr, probe, scope, diags);
                        }
                        Walk(element.Children, scope);
                        break;
                    case UiText text:
                        foreach (var segment in text.Segments)
                        {
                            if (segment.Kind != TextSegmentKind.Expression) continue;
                            ProbeExpression(segment.Text, segment.Span, probe, scope, diags);
                        }
                        break;
                    case UiSlotNode fill:
                        // 插槽填充的内容是组件 Build 的一部分：@foreach item 推断与 @bind 校验
                        // 必须穿透（EUI007/EUI037 与内容上下文同口径）
                        Walk(fill.Children, scope);
                        break;
                    case UiIf ifNode:
                        ProbeExpression(ifNode.Condition, ifNode.ConditionSpan, probe, scope, diags);
                        Walk(ifNode.Children, scope);
                        foreach (var elseIf in ifNode.ElseIfs)
                        {
                            ProbeExpression(elseIf.Condition, elseIf.ConditionSpan, probe, scope, diags);
                            Walk(elseIf.Children, scope);
                        }
                        if (ifNode.ElseChildren is not null) Walk(ifNode.ElseChildren, scope);
                        break;
                    case UiSwitch switchNode:
                        ProbeSwitch(switchNode, probe, compilation, scope, diags, switchTypes);
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
    /// @switch 的探针（K29）：推断 selector 类型（SwitchBlock&lt;T&gt; 的 T，失败静默交 C#）；
    /// 对每个 case 标签做编译期常量校验（EUI047 语义级——const 声明探针；语法层形态校验已在
    /// Parser 完成）。符号未解析/表达式内部错误静默，交 C# 经 #line 兜底。
    /// </summary>
    private static void ProbeSwitch(UiSwitch switchNode, TemplateProbe probe, Compilation compilation,
        IReadOnlyList<(ITypeSymbol Type, string Name)> scope, List<UiDiagnostic> diags,
        List<SwitchTypeFact> switchTypes)
    {
        var valueType = probe.InferType(switchNode.Value, scope);
        if (valueType is not null && valueType.TypeKind != TypeKind.Error && valueType is not IDynamicTypeSymbol)
            switchTypes.Add(new SwitchTypeFact(
                switchNode.ValueSpan.Start, valueType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));

        foreach (var section in switchNode.Sections)
        {
            if (section.IsDefault || valueType is null) continue;
            if (!probe.IsConstant(section.Label, valueType, scope, out var reason))
            {
                diags.Add(UiDiagnostics.Error(UiDiagnostics.CaseLabelNotConstant,
                    $"case label '{section.Label}' is not a compile-time constant: {reason}",
                    section.LabelSpan));
            }
        }
    }

    /// <summary>
    /// EUI049：普通表达式属性值（非 bind/事件/Ref——各有专属探针；目标未知 EUI002 已报的不重复）
    /// 做语义探针编译，句内 CS 错误与「表达式 → 目标类型」转换错误在使用点直接报。
    /// </summary>
    private static void ValidateExpressionValue(UiAttribute attr, TemplateProbe probe,
        IReadOnlyList<(ITypeSymbol Type, string Name)> env, List<UiDiagnostic> diags)
    {
        if (attr.Kind != AttrValueKind.Expression || attr.IsBind) return;
        if (attr.EventDef is not null || attr.RefTarget is not null) return;
        var targetClrType =
            attr.PropDef?.ClrTypeDisplayName ?? attr.ParamDef?.ClrTypeDisplayName ?? attr.AttachedDef?.ClrTypeDisplayName;
        if (targetClrType is null) return;
        ProbeExpression(attr.Value, attr.ValueSpan, probe, env, diags, targetClrType);
    }

    /// <summary>EUI049 共用入口：表达式原文放探针编译，句内 CS 错误逐条升级为模板诊断。</summary>
    private static void ProbeExpression(string expression, TextSpan span, TemplateProbe probe,
        IReadOnlyList<(ITypeSymbol Type, string Name)> env, List<UiDiagnostic> diags,
        string? targetClrType = null)
    {
        var trimmed = expression.Trim();
        if (trimmed.Length == 0) return;
        foreach (var error in probe.ProbeExpressionErrors(trimmed, env, targetClrType))
            diags.Add(UiDiagnostics.Warning(UiDiagnostics.ExpressionSemantic,
                $"template expression '{trimmed}' does not compile against the code-behind: {error}",
                span));
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

    /// <summary>
    /// EUI024（spec/02 §4.6.1 规则 7、spec/01 §6.2）：把处理器表达式放进 code-behind 的
    /// 符号上下文，让 Roslyn 判定它能否转换为生成代码将要 cast 的目标委托类型。
    /// 触发面刻意收窄：只有「表达式自身语义干净 + 委托转换确定失败」才报；符号未解析、
    /// 方法重载失败、dynamic、以及其它表达式内错误一律返回「静态不可判定」，交给 C#
    /// 经 #line 兜底（§2.4 规则 8：生成器不得重复报告 C# 类型错误）。
    /// 字面量/@bind 由 EUI004/EUI040 负责，这里不重复报。
    /// </summary>
    private static void ValidateEventHandlerSignature(UiAttribute attr, TemplateProbe probe,
        IReadOnlyList<(ITypeSymbol Type, string Name)> env, List<UiDiagnostic> diags)
    {
        string? delegateType = null;
        if (attr.EventDef is not null)
            delegateType = Emitter.EventHandlerType(attr.Value);   // 与生成代码同一结论
        else if (attr.ParamDef is { IsDelegate: true } p)
            delegateType = p.ClrTypeDisplayName;                   // 组件委托目标类型来自目录
        if (delegateType is null) return;
        if (attr.IsBind || attr.Kind != AttrValueKind.Expression || string.IsNullOrWhiteSpace(attr.Value)) return;

        var compatible = probe.IsHandlerCompatible(attr.Value, delegateType, env, out var reason);
        if (compatible is not false) return;   // true = 兼容；null = 不可静态判定，C# 兜底

        diags.Add(UiDiagnostics.Error(UiDiagnostics.EventHandlerSignature,
            $"event handler '{attr.Value.Trim()}' does not match the delegate '{delegateType}': {reason}",
            attr.ValueSpan));
    }

    /// <summary>
    /// 探针：把表达式放进合成 partial 方法体，借 Roslyn 的符号上下文回答类型/可写性/
    /// 委托转换/常量性。R1/K21：构造时注入全部 @code 成员合成树（含模板 using 行），
    /// 每次 Augment 时一并加入派生 Compilation——保证后置 @code 成员对前置 {expr} 同样可见。
    /// </summary>
    private sealed class TemplateProbe
    {
        private readonly Compilation _compilation;
        private readonly INamedTypeSymbol? _type;
        private readonly string _className;
        private readonly string _namespace;
        private readonly ImmutableArray<SyntaxTree> _memberTrees;
        private readonly ImmutableArray<string> _usings;
        private int _counter;

        public TemplateProbe(Compilation compilation, INamedTypeSymbol? type, string className,
            string namespaceName, ImmutableArray<SyntaxTree> memberTrees, ImmutableArray<string> usings)
        {
            _compilation = compilation;
            _type = type;
            _className = className;
            _namespace = namespaceName;
            _memberTrees = memberTrees;
            _usings = usings;
        }

        /// <summary>
        /// 派生 Compilation：@code 成员合成树（K21，通常为空——多数模板没有 @code）+ 探针树。
        /// 同一棵 memberTree 实例加入多次派生 Compilation 是安全的（树不可变）。
        /// </summary>
        private Compilation Augment(SyntaxTree tree)
        {
            var result = _memberTrees.IsEmpty ? _compilation : _compilation.AddSyntaxTrees(_memberTrees);
            return result.AddSyntaxTrees(tree);
        }

        public ITypeSymbol? InferType(string expression, IReadOnlyList<(ITypeSymbol Type, string Name)> env)
        {
            var tree = ProbeTree("var __value = (" + expression + ");", env);
            var augmented = Augment(tree);
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
            var augmented = Augment(tree);
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
            var augmented = Augment(tree);
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

        /// <summary>
        /// 「委托转换」类 C# 错误：这些说明表达式本身可解析，但签/形态与目标委托不匹配。
        /// 其它错误（CS0103 未解析、CS1501/CS1503 表达式内部调用失败等）一律视为
        /// 静态不可判定，交回生成代码经 #line 由 C# 报告。
        /// </summary>
        private static readonly HashSet<string> DelegateConversionErrors = new(StringComparer.Ordinal)
        {
            "CS0029", "CS0030", "CS0123", "CS1593",
            "CS1660", "CS1661", "CS1678", "CS0411", "CS0428", "CS0119",
        };

        /// <summary>
        /// true = 可转换为目标委托；false = 确定不匹配（可报 EUI024）；
        /// null = 静态不可判定（符号/上下文不足），由 C# 经 #line 兜底。
        /// 复用 <see cref="ProbeTree"/>：与 EUI037/EUI029 同一套合成 partial 方法体机制。
        /// </summary>
        public bool? IsHandlerCompatible(string expression, string delegateType,
            IReadOnlyList<(ITypeSymbol Type, string Name)> env, out string? reason)
        {
            reason = null;
            expression = expression.Trim();
            if (expression.Length == 0) return null;

            var target = ClrTypeOf(delegateType);
            var tree = ProbeTree("var __handler = (" + target + ")(" + expression + ")", env);
            var model = Augment(tree).GetSemanticModel(tree);
            var initializer = tree.GetRoot().DescendantNodes().OfType<VariableDeclaratorSyntax>()
                .FirstOrDefault(v => v.Identifier.ValueText == "__handler")?.Initializer?.Value;
            if (initializer is not CastExpressionSyntax cast) return null;
            var inner = cast.Expression;

            // (1) 表达式自身必须语义干净。方法组转换失败时 Symbol 可能为 null 但候选非空，
            //     因此只把「零候选」当作未解析；歧义/重载失败留给下面的错误分类处理。
            foreach (var name in inner.DescendantNodesAndSelf().OfType<SimpleNameSyntax>())
            {
                var info = model.GetSymbolInfo(name);
                if (info.Symbol is null && info.CandidateSymbols.Length == 0) return null;
                if (info.Symbol is IErrorTypeSymbol) return null;
            }
            if (model.GetTypeInfo(inner).Type is IErrorTypeSymbol or IDynamicTypeSymbol) return null;

            // (2) 落在 cast 内的 Error 必须全部是委托转换类；否则说明表达式本身有问题。
            string? failure = null;
            foreach (var diagnostic in model.GetDiagnostics())
            {
                if (diagnostic.Severity != RoslynSeverity.Error) continue;
                if (!cast.Span.Contains(diagnostic.Location.SourceSpan)) continue;
                if (!DelegateConversionErrors.Contains(diagnostic.Id)) return null;
                failure ??= $"{diagnostic.GetMessage()} ({diagnostic.Id})";
            }

            if (failure is null) return true;
            reason = failure;
            return false;
        }

        /// <summary>
        /// EUI049（表达式语义分析最小闭环）：把模板表达式放进合成方法体做探针编译，
        /// 落在表达式本体 span 内的 CS 错误逐条返回（消息 + 代码）——拼错成员（CS0103）、
        /// 类型不匹配（CS0029/CS0266）等在使用点直接报。带 <paramref name="targetClrType"/> 时
        /// 先声明目标类型的局部变量再赋值（生成代码同形态：左值 = 表达式），类型转换错误一并捕获；
        /// 无目标类型（条件/文本段）用 var 探针。脚手架自身的句外错误忽略；
        /// 生成代码仍经 #line 由 C# 兜底（同一错误两处可见是探针校验的既定权衡）。
        /// </summary>
        public IReadOnlyList<string> ProbeExpressionErrors(string expression,
            IReadOnlyList<(ITypeSymbol Type, string Name)> env, string? targetClrType = null)
        {
            SyntaxTree tree;
            if (targetClrType is null)
            {
                tree = ProbeTree("var __value = (" + expression + ");", env);
            }
            else
            {
                // 与生成代码同形态：目标局部 = 表达式。错误过滤锚定赋值右侧。
                tree = ProbeTree(
                    ClrTypeOf(targetClrType) + " __value = default!; __value = (" + expression + ");", env);
            }

            var model = Augment(tree).GetSemanticModel(tree);
            var valueNode = targetClrType is null
                ? tree.GetRoot().DescendantNodes().OfType<VariableDeclaratorSyntax>()
                    .FirstOrDefault(v => v.Identifier.ValueText == "__value")?.Initializer?.Value
                : tree.GetRoot().DescendantNodes().OfType<AssignmentExpressionSyntax>()
                    .FirstOrDefault()?.Right;
            if (valueNode is null) return Array.Empty<string>();

            var errors = new List<string>();
            foreach (var diagnostic in model.GetDiagnostics())
            {
                if (diagnostic.Severity != RoslynSeverity.Error) continue;
                if (!valueNode.Span.Contains(diagnostic.Location.SourceSpan)) continue;
                errors.Add($"{diagnostic.GetMessage()} ({diagnostic.Id})");
            }
            return errors;
        }

        /// <summary>
        /// K29/EUI047：case 标签编译期常量校验——把 <c>const T __caseKey = expr</c> 放进探针，
        /// 只有 CS0133（"must be constant"）落在声明上才判非常量；类型不匹配、符号未解析等
        /// 其它错误静默返回 true，交生成代码经 #line 由 C# 兜底（§2.4 规则 8：不重复报 C# 错）。
        /// </summary>
        public bool IsConstant(string expression, ITypeSymbol targetType,
            IReadOnlyList<(ITypeSymbol Type, string Name)> env, out string? reason)
        {
            reason = null;
            var tree = ProbeTree(
                "const " + ClrTypeOf(targetType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)) +
                " __caseKey = " + expression, env);
            var model = Augment(tree).GetSemanticModel(tree);
            var declarator = tree.GetRoot().DescendantNodes().OfType<VariableDeclaratorSyntax>()
                .FirstOrDefault(v => v.Identifier.ValueText == "__caseKey");
            if (declarator?.Initializer is null) return true;
            foreach (var diagnostic in model.GetDiagnostics())
            {
                if (diagnostic.Id != "CS0133" ||
                    diagnostic.Severity != RoslynSeverity.Error ||
                    !declarator.Span.Contains(diagnostic.Location.SourceSpan)) continue;
                reason = "use a literal, a const field, an enum member or a constant expression";
                return false;
            }
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

        /// <summary>
        /// 合成探针方法：与 code-behind（或 @code 类壳，K23 ④）同命名空间的 partial 类，
        /// item 以局部变量注入。模板 @using 逐行输出——using 是语法树局部的，成员合成树里的
        /// using 行帮不到本树的表达式，两边都要有。
        /// </summary>
        private SyntaxTree ProbeTree(string statement, IReadOnlyList<(ITypeSymbol Type, string Name)> env,
            string? preamble = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine("#nullable enable");
            foreach (var ns in _usings)
                sb.AppendLine($"using {ns};");
            if (_namespace.Length > 0)
            {
                sb.Append("namespace ").Append(_namespace).AppendLine(";");
                sb.AppendLine();
            }
            sb.Append(_type is null || _type.DeclaredAccessibility != Accessibility.Public ? "internal" : "public")
                .Append(" partial class ").Append(_type?.Name ?? _className).AppendLine();
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

/// <summary>@switch selector 的值类型事实（K29）：ValueSpan.Start → 全限定类型名。</summary>
internal sealed record SwitchTypeFact(int SpanStart, string Type);

/// <summary>逐文件语义事实：值相等是增量缓存的前提（ImmutableArray 默认引用相等，必须逐元素比）。</summary>
internal sealed record UiFileFacts(
    string Accessibility,
    string Namespace,
    string DisplayName,
    ImmutableArray<UiSlotDef> Slots,
    ImmutableArray<UiDiagnostic> CodeBehindDiags,
    ImmutableArray<UiDiagnostic> ProbeDiags,
    ImmutableArray<ItemTypeFact> ItemTypes,
    ImmutableArray<SwitchTypeFact> SwitchTypes)
{
    public bool Equals(UiFileFacts? other) =>
        other is not null &&
        Accessibility == other.Accessibility &&
        Namespace == other.Namespace &&
        DisplayName == other.DisplayName &&
        Slots.SequenceEqual(other.Slots) &&
        CodeBehindDiags.SequenceEqual(other.CodeBehindDiags) &&
        ProbeDiags.SequenceEqual(other.ProbeDiags) &&
        ItemTypes.SequenceEqual(other.ItemTypes) &&
        SwitchTypes.SequenceEqual(other.SwitchTypes);

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
        foreach (var fact in SwitchTypes) hash = Add(hash, fact);
        return hash;

        // netstandard2.0 没有 System.HashCode，与 CatalogValueEq 同口径累加。
        static int Add(int h, object? v) => unchecked(h * 397 + (v?.GetHashCode() ?? 0));
    }
}

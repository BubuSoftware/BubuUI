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
using UiSpan = BubuUI.Compiler.TextSpan;
using CompilerSeverity = BubuUI.Compiler.DiagnosticSeverity;
using RoslynSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;
using BubuUI.Compiler.Metadata;
using BubuUI.Compiler.Probe;

namespace BubuUI.Compiler.Generator;

/// <summary>
/// .bui → C# 增量源生成器：编译期类型检查（表达式原样进入生成代码，由 Roslyn 检查并经
/// #line 映射回 .bui）、@foreach item 类型推断、@bind 源可写性校验、定向更新代码发射。
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class TemplateGenerator : IIncrementalGenerator
{
    private const string UiSlotAttributeName = "BubuUI.UiSlotAttribute";
    private const string UiIgnoreAttributeName = "BubuUI.UiIgnoreAttribute";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var uiFiles = context.AdditionalTextsProvider
            .Where(static file => Path.GetExtension(file.Path) is ".bui")
            .Select(static (file, ct) => new UiFileInput(file.Path, file.GetText(ct)?.ToString() ?? ""));

        // spec/02 §2.7模板类型声明：每个 .bui 文件本身就是一条组件声明——标签 = 文件名，
        // 命名空间 = 该文件声明的 @namespace。目录建在输入编译上、看不到自己的产物（.g.cs），
        // 这份清单就是「.bui 也是组件来源」的唯一凭据，同时消掉 IDE（生成后编译）与编译器
        // （输入编译）之间的目录分叉。
        // 清单只有 (Tag, @namespace) 两项：改模板正文不改变目录值，下游仍按值命中缓存
        // （§14.10.3）。清单本身也逐项比，文件增删或 @namespace 改动才让目录重建。
        var templateTypes = context.AdditionalTextsProvider
            .Where(static file => Path.GetExtension(file.Path) is ".bui")
            .Select(static (file, ct) => UiTemplateScanner.FromFile(
                file.Path, file.GetText(ct)?.ToString() ?? ""))
            .Collect()
            .Select(static (declarations, _) => UiTemplateDeclarations.Build(declarations))
            .WithTrackingName("TemplateTypes");

        // 模板表面每轮从符号重建（design/14 §14.10.3：派生目录是唯一事实源，不允许手写缓存）。
        // UiCatalog 逐字段实现结构相等，所以控件 API 没变时下游仍按值命中缓存、不重生成代码。
        var catalogProvider = templateTypes
            .Combine(context.CompilationProvider)
            .Select(static (pair, _) => UiCatalogFactory.Build(pair.Right, pair.Left.Items))
            .WithTrackingName("TemplateCatalog");

        // D74 ②：目录级诊断出口（design/14 §14.9.1）。挂在 catalogProvider 而不是逐文件管道，
        // 保证同一条元数据问题无论项目里有多少个 .bui 文件都只报一次。
        context.RegisterSourceOutput(catalogProvider, static (spc, catalog) =>
            ReportCatalogProblems(spc, catalog));

        // design/17 §4.3：样式属性目录级诊断出口（与标签目录同口径，一次编译只报一次）。
        // 问题来自引用程序集符号（多数宿主不编译 Rendering 源码），没有源位置，故用 Location.None。
        context.RegisterSourceOutput(catalogProvider, static (spc, catalog) =>
            ReportStylePropertyProblems(spc, catalog.StyleProperties));

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
            .Combine(rootNamespace).Combine(templateTypes)
            .Select(static (pair, _) => (
                Input: pair.Left.Left.Left.Left,
                Catalog: pair.Left.Left.Left.Right,
                RootNamespace: pair.Left.Right,
                Facts: ExtractFacts(pair.Left.Left.Left.Left, pair.Left.Left.Left.Right,
                    pair.Left.Left.Right, pair.Left.Right, pair.Right.Items)))
            // 命名节点让逐文件生成在 TrackedSteps 里可独立观测：D74 ② 的目录级诊断出口
            // 是第二个 SourceOutput，不能把所有 "SourceOutput" 混成一组断言（§14.10.3）。
            .WithTrackingName("PerFileGeneration");

        // E5（design/17 §3.2）：注册制伪类名目录——.bss 与 .bui @style 块的 @states 声明并集
        // （全局掩码命名空间，注册名不做类名式前缀）。两级收集：声明先成目录（预扫，便宜），
        // 再进样式类目录管道供跨文件注册名引用的编译期校验（声明序无关）。
        var styleStates = context.AdditionalTextsProvider
            .Where(static file => Path.GetExtension(file.Path) is ".bss" or ".bui")
            .Select(static (file, ct) => new UiFileInput(file.Path, file.GetText(ct)?.ToString() ?? ""))
            .Select(static (input, _) => ExtractStyleStates(input))
            .Collect()
            .Select(static (facts, _) => StyleStatesCatalog.Build(facts))
            .WithTrackingName("StyleStatesCatalog");

        // E5（design/17 §5/§8）：宿主手写 C# Define("name", …) 的类名名单——与样式表类求
        // 交集报 EUI054（Warning，不短路；运行时「后注册覆盖」语义不变）。扫描语法树取
        // 字符串字面量实参（注释/非字符串不算）；值相等缓存随 compilation 失效。
        var handwrittenDefines = context.CompilationProvider
            .Select(static (compilation, _) => ExtractHandwrittenDefines(compilation))
            .WithTrackingName("HandwrittenDefines");

        // E1（design/17 §4.1）：.bss → StyleBootstrap.g.cs。样式表文件数少且全局单源，
        // Collect 成批处理（一个 Apply 出口、AppBuilder.UseStyles 直连）；诊断逐文件定位，
        // 任一文件存在 Error 短路整个产物（与 .bui 管道口径一致）。
        // E5：Combine 样式目录（跨文件 @states 引用校验）与手写 Define 名单（EUI054 双通道）。
        var styleSheets = context.AdditionalTextsProvider
            .Where(static file => Path.GetExtension(file.Path) == ".bss")
            .Select(static (file, ct) => new UiFileInput(file.Path, file.GetText(ct)?.ToString() ?? ""))
            .Collect()
            .Combine(rootNamespace)
            .Combine(styleStates)
            .Combine(handwrittenDefines)
            .Combine(catalogProvider);
        context.RegisterSourceOutput(styleSheets, static (spc, data) => ExecuteStylesheet(spc,
            data.Left.Left.Left.Left, data.Left.Left.Left.Right, data.Left.Left.Right,
            data.Left.Right, data.Right.StyleProperties));

        var controlThemes = styleSheets.Combine(templateTypes).Combine(context.CompilationProvider);
        context.RegisterSourceOutput(controlThemes, static (spc, data) =>
        {
            var sheets = data.Left.Left;
            var compilation = UiCatalogFactory.WithTemplateTypes(data.Right, data.Left.Right.Items);
            var emitter = new ControlThemeEmitter(sheets.Right, data.Left.Right.Items, compilation);
            var documents = sheets.Left.Left.Left.Left.OrderBy(f => f.Path, StringComparer.Ordinal)
                .Select(f => (File: f, Doc: ControlThemeSyntax.Parse(f.Text, new List<UiDiagnostic>()))).ToArray();
            if (!documents.Any(d => d.Doc.Nodes.Count > 0)) return;
            foreach (var document in documents) emitter.Declare(document.Doc);
            var failed = false;
            foreach (var document in documents)
                foreach (var diagnostic in emitter.Emit(document.Doc))
                { Report(spc, document.File.Path, SourceText.From(document.File.Text), diagnostic); failed |= diagnostic.IsError; }
            if (failed) return;
            var ns = SanitizeNamespace(sheets.Left.Left.Left.Right);
            spc.AddSource("ControlThemes.g.cs", "// <auto-generated/>\n#nullable enable\n" +
                (ns.Length == 0 ? "" : "namespace " + ns + ";\n") +
                "public static partial class StyleBootstrap {\n" + emitter.Fields +
                "static partial void ApplyControlThemes(global::BubuUI.Rendering.StyleRegistry registry) {\nif (!registry.InstallModule(typeof(StyleBootstrap))) return;\n" + emitter.Apply + "}\n}\n");
        });

        // E2（design/17 §5）：全局样式类名目录——.bss 的类名 + 各 .bui 的 @style 块类名
        // （组件前缀 {TypeName}__）。产出是值相等 record，combine 进逐文件管道供
        // Class="…" 编译期校验（EUI031 转正）。目录管道不报诊断——解析错误由各自主管道
        // 报告（.bss → ExecuteStylesheet，@style → ExecuteFile），此处按「有错即空」处理。
        var styleClasses = context.AdditionalTextsProvider
            .Where(static file => Path.GetExtension(file.Path) is ".bss" or ".bui")
            .Select(static (file, ct) => new UiFileInput(file.Path, file.GetText(ct)?.ToString() ?? ""))
            .Combine(styleStates)
            .Combine(catalogProvider)
            .Select(static (pair, _) => ExtractStyleClasses(pair.Left.Left, pair.Left.Right,
                pair.Right.StyleProperties))
            .Collect()
            .Select(static (facts, _) => StyleClassCatalog.Build(facts))
            .WithTrackingName("StyleClassCatalog");
        var perFileWithStyles = perFile.Combine(styleClasses);
        context.RegisterSourceOutput(perFileWithStyles, static (spc, data) =>
            ExecuteFile(spc, data.Left.Input, data.Left.Catalog, data.Left.Facts, data.Right));
    }

    private static UiFileFacts ExtractFacts(UiFileInput input, UiCatalog catalog, Compilation compilation,
        string rootNamespace, IReadOnlyList<UiTemplateDeclaration> declarations)
    {
        var className = Path.GetFileNameWithoutExtension(input.Path);
        var validName = SyntaxFacts.IsValidIdentifier(className);
        var codeBehindDiags = new List<UiDiagnostic>();

        // S6：先解析文档——EUI005 放宽（K23 ④：@code 或同名 partial 至少其一）、@using 收集（K22）
        // 与 @code 成员收集（K21）都以 AST 为准。
        var parseDiags = new List<UiDiagnostic>();
        var doc = FrontEnd.Parse(input.Text, input.Path, parseDiags);
        if (doc.TemplateTarget is not null) className += "__TemplateView";
        var usings = CollectUsingLines(doc.Nodes);
        var codeBlocks = doc.Nodes.OfType<UiCodeBlock>().ToList();
        FrontEnd.Analyze(doc, catalog, new List<UiDiagnostic>());
        var rootDef = doc.Nodes.OfType<UiElement>().FirstOrDefault()?.ResolvedDef;

        var type = validName ? FindCodeBehind(compilation, className) : null;
        if (validName && type is not null)
        {
            if (type.ContainingType is not null)
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

        // spec/02 §2.7：不再要求「同名 .cs 或 @code 至少其一」。.bui 是独立组件来源——
        // 没有 code-behind、没有 @code 的纯展示组件完全合法（EUI005 停用）。

        // spec/02 §2.2：@namespace 是必填契约——缺失（EUI055）与「与 code-behind 不一致」
        // （EUI056）都在这里报。解析层不报，保证内存态解析（IDE Tier1 / 单测内联模板）不被强制。
        // 没有保留值：`global` 只是普通名字。处于全局命名空间的 code-behind 无法与任何声明相等
        // （C# 写不出 global namespace 声明），按 EUI056 提示把它搬进命名空间。
        var declaredNamespace = doc.Namespace;
        if (declaredNamespace is null)
        {
            codeBehindDiags.Add(UiDiagnostics.Error(UiDiagnostics.NamespaceMissing,
                $"'{className}.bui' must declare its namespace: add '@namespace " +
                $"{SuggestedNamespace(type, rootNamespace)}' (spec/02 §2.2)", UiSpan.Zero));
        }
        else if (type is not null &&
                 !string.Equals(NamespaceName(type), declaredNamespace, StringComparison.Ordinal))
        {
            var codeBehindNamespace = NamespaceName(type);
            codeBehindDiags.Add(UiDiagnostics.Error(UiDiagnostics.NamespaceMismatch,
                codeBehindNamespace.Length == 0
                    ? $"code-behind class '{className}' is in the global namespace, but '@namespace " +
                      $"{declaredNamespace}' is declared; move '{className}' into a namespace — a template " +
                      "always generates into a named namespace (spec/02 §2.2)"
                    : $"'@namespace {declaredNamespace}' does not match code-behind class '{className}' " +
                      $"(declared in '{codeBehindNamespace}')",
                doc.NamespaceSpan));
        }

        // D57/EUI045（spec/02 §10）：code-behind 与生成成员/基类成员冲突。
        // design/20：基类口径按窗口/普通模板分流（EUI063）。
        if (type is not null)
            ProbeCodeBehindConflicts(type, codeBehindDiags, rootDef?.ClrTypeDisplayName, compilation);

        // K25：EUI045 扩展到 @code 成员——撞生成保留名（InitializeUi/Render/U{N}/R{N}/__el{N}/…）
        // 报在 .bui 的 @code 块内成员位置。@code ↔ 同名 .cs 的冲突由 C# partial 规则兜底，不在这里报。
        // @code 与 code-behind 统一进入符号视图，均可声明对外属性和插槽。
        if (codeBlocks.Count > 0)
            ProbeCodeMemberConflicts(codeBlocks, codeBehindDiags);

        // K37：@inject 属性名与生成保留名/生成依赖成员/@code 成员/code-behind 成员的冲突（EUI045）。
        // 与 @code 冲突同哲学：进 codeBehindDiags（不短路生成），由 C# 编译给权威错误。
        // 同一模板重复属性名由分析器 EUI065 在语法层报，这里不重复。
        var injects = doc.Nodes.OfType<UiInjectDirective>().ToList();
        if (injects.Count > 0)
            ProbeInjectConflicts(injects, codeBlocks, type, codeBehindDiags);

        // 自身组件（spec/02 §4.12.5）：code-behind 符号直接派生插槽表——目录里没有
        // 根类型和 @code 成员在生成前补入同一符号视图，供目录与探针共同使用。
        var symbolCompilation = UiCatalogFactory.WithTemplateTypes(compilation, declarations);
        var symbolType = declaredNamespace is not null
            ? symbolCompilation.GetTypeByMetadataName(declaredNamespace + "." + className) : type;
        var own = ResolveOwnTemplate(symbolType, symbolCompilation);
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
        var effectiveNamespace = declaredNamespace
            ?? (type is null ? SanitizeNamespace(rootNamespace) : NamespaceName(type));
        var accessibility = type is null ? "public" : AccessibilityOf(type);
        var probe = new ExpressionProbe(symbolCompilation, symbolType, className, effectiveNamespace, usings);
        var probeDiags = new List<UiDiagnostic>();
        var switchTypes = new List<SwitchTypeFact>();
        var classKinds = new List<ClassKindFact>();
        var loopLocals = new List<LoopLocalFact>();
        if (symbolType is not null || codeBlocks.Count > 0)
        {
            var itemTypes = ProbeSemantics(doc, probe, symbolCompilation, probeDiags, switchTypes, classKinds, loopLocals)
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
                switchTypes.OrderBy(fact => fact.SpanStart).ToImmutableArray(),
                classKinds.OrderBy(fact => fact.SpanStart).ToImmutableArray(),
                loopLocals.ToImmutableArray());
        }

        // 无 code-behind、无 @code：没有成员作用域可探针，但类壳仍要落到模板声明的命名空间
        // ——纯 .bui 组件（spec/02 §2.7）就是走这条路生成的，之前这里的空串会让它掉进全局命名空间。
        return new UiFileFacts(
            accessibility, effectiveNamespace, "", slots,
            codeBehindDiags.ToImmutableArray(),
            probeDiags.ToImmutableArray(),
            ImmutableArray<ItemTypeFact>.Empty,
            ImmutableArray<SwitchTypeFact>.Empty,
            ImmutableArray<ClassKindFact>.Empty, ImmutableArray<LoopLocalFact>.Empty);
    }

    /// <summary>K22/K31：根层 @using 的 using 子句正文（命名空间 + 别名，按文档顺序去重）。</summary>
    private static ImmutableArray<string> CollectUsingLines(IEnumerable<UiNode> nodes) =>
        TemplateAnalyzer.CollectUsingLines(nodes).ToImmutableArray();

    /// <summary>
    /// K21/K25：@code 成员与生成保留名的冲突（EUI045）。对块体原文做轻量成员名提取
    /// （字段/属性/方法/嵌套类型声明的标识符），撞保留名报在 .bui 的成员 span；
    /// 内容粒度的其余校验（语句合法性、与 .cs 的重复）由探针 Compilation 与 C# 兜底。
    ///
    /// @code 属性和 code-behind 使用同一符号视图，不区分声明来源。
    /// </summary>
    private static void ProbeCodeMemberConflicts(IReadOnlyList<UiCodeBlock> blocks,
        List<UiDiagnostic> diags)
    {
        foreach (var block in blocks)
        {
            foreach (var (name, nameOffset, nameLength) in ExtractMemberNames(block.Code))
            {
                string? reason = name is "InitializeUi" or "Render"
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
    /// K37：@inject 属性名冲突（EUI045，spec/01 §4.18）——撞生成保留名（InitializeUi/Render/
    /// U{N}/R{N}/__el{N}/…）、生成依赖成员（Invalidate/…）、@code 成员或 code-behind 成员时，
    /// 报在 .bui 的 @inject 属性名 span。与 <see cref="ProbeCodeMemberConflicts"/> 同哲学：
    /// 进 codeBehindDiags（不短路生成），由 C# 编译给权威错误。同一模板重复属性名由分析器
    /// EUI065 在语法层报，这里不重复。
    /// </summary>
    private static void ProbeInjectConflicts(IReadOnlyList<UiInjectDirective> injects,
        IReadOnlyList<UiCodeBlock> blocks, INamedTypeSymbol? type, List<UiDiagnostic> diags)
    {
        // @code 成员名与 code-behind 成员名合成占用集——@inject 属性在同一个 partial 类里生成，
        // 与任何一侧同名都是 CS0102/CS0111 的必然冲突。
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var block in blocks)
            foreach (var (name, _, _) in ExtractMemberNames(block.Code))
                taken.Add(name);
        if (type is not null)
            foreach (var member in type.GetMembers())
                taken.Add(member.Name);

        foreach (var inject in injects)
        {
            var name = inject.PropertyName;
            string? reason = name is "InitializeUi" or "Render" or "__RegisterStyles"
                ? $"'@inject' property '{name}' collides with a generated method name"
                : IsGeneratedMemberName(name)
                    ? $"'@inject' property '{name}' collides with a generated member name"
                    : GeneratedDependencyNames.Contains(name)
                        ? $"'@inject' property '{name}' hides a UiObject member used by generated code"
                        : taken.Contains(name)
                            ? $"'@inject' property '{name}' collides with a member declared in @code or code-behind"
                            : null;
            if (reason is null) continue;
            diags.Add(UiDiagnostics.Error(UiDiagnostics.CodeBehindConflict, reason, inject.PropertyNameSpan));
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

    private static void ExecuteFile(SourceProductionContext spc, UiFileInput input,
        UiCatalog catalog, UiFileFacts facts, StyleClassCatalog styleClasses)
    {
        var sourceText = SourceText.From(input.Text);
        var className = Path.GetFileNameWithoutExtension(input.Path);
        var diags = new List<UiDiagnostic>();

        if (!SyntaxFacts.IsValidIdentifier(className))
        {
            Report(spc, input.Path, sourceText, UiDiagnostics.Error(UiDiagnostics.InvalidClassName,
                $"'{className}' is not a valid class name; rename the .bui file", UiSpan.Zero));
            return;
        }

        var doc = FrontEnd.Parse(input.Text, input.Path, diags);
        diags.AddRange(facts.CodeBehindDiags);

        // E2（design/17 §4.8①）：先解析本文件 @style 块——产物除 styleRules 外还提供
        // 裸名 → 注册名映射（Probe__demo），供 Class="…" 的「先本文件、再全局」解析序。
        // E4（design/17 §4.5）：嵌套塌缩——派生类带前缀注册（Probe__panel__button），
        // 塌缩名进裸名映射（模板 Class="panel__button" 闭环校验），注入规则同前缀化。
        var styleRules = new List<BssRule>();
        var localStyleClasses = new Dictionary<string, string>(StringComparer.Ordinal);
        var localInjects = new List<InjectRuleData>();
        var localStates = new List<BssStateDecl>();
        foreach (var block in doc.Nodes.OfType<UiStyleBlock>())
        {
            var blockDiags = new List<UiDiagnostic>();
            // E5：跨文件注册名（@states 全局并集）兜底校验——本文件声明已在解析器预扫闭环。
            var sheet = Bss.Parse(block.Code, input.Path, blockDiags, catalog.StyleProperties,
                block.CodeSpan.Start, styleClasses.States);
            diags.AddRange(blockDiags);
            if (blockDiags.Exists(static d => d.IsError)) continue;
            var collapsed = BssCollapse.Flatten(sheet, diags);
            foreach (var rule in collapsed.Rules)
            {
                var registered = className + "__" + rule.ClassName;
                styleRules.Add(new BssRule(registered, rule.NameSpan, rule.Setters,
                    Array.Empty<BssNested>()));
                // 前块健康即可用；同名裸名取先声明（与运行时「后注册覆盖」口径不同——
                // 编译期闭环要确定性，声明序后者胜留给运行时重叠注册的既有语义）
                if (!localStyleClasses.ContainsKey(rule.ClassName))
                    localStyleClasses[rule.ClassName] = registered;
            }
            foreach (var state in collapsed.States)
                if (localStates.All(s => s.Name != state.Name))
                    localStates.Add(state);
            foreach (var inj in collapsed.Injects)
            {
                // 嵌套类约束先按本块裸名解析（Class="…" 的「先本文件」序同款）；
                // 未命中的约束保持裸名（指全局 .bss 类——注册名即裸名）。
                var chain = new List<InjectSegData>();
                foreach (var seg in inj.Chain)
                    chain.Add(new InjectSegData(seg.DirectChild, seg.TypeName,
                        seg.ClassConstraint is { } c && localStyleClasses.TryGetValue(c, out var reg)
                            ? reg : seg.ClassConstraint));
                localInjects.Add(new InjectRuleData(
                    className + "__" + inj.RootClass, chain, className + "__" + inj.DerivedName));
            }
        }

        var ownSlots = facts.DisplayName.Length == 0
            ? null
            : facts.Slots.ToDictionary(slot => slot.Name, StringComparer.Ordinal);
        // E2：样式目录传真实集合（空目录 = 项目无样式类 → Class= 一律报未知；宽松 null 仅供探针）。
        FrontEnd.Analyze(doc, catalog, diags, ownSlots,
            facts.DisplayName.Length == 0 ? null : facts.DisplayName,
            doc.TemplateTarget is null ? new HashSet<string>(styleClasses.ClassNames, StringComparer.Ordinal) : null,
            localStyleClasses);
        diags.AddRange(facts.ProbeDiags);

        // E2：探针推断的 Class={expr} 集合形态按 ValueSpan.Start 定位回写（与 ItemTypes 同款跨 doc 模式）。
        if (facts.ClassKinds.Length > 0)
        {
            var kindsByStart = facts.ClassKinds.ToDictionary(fact => fact.SpanStart, fact => fact.Kind);
            foreach (var element in WalkElements(doc.Nodes))
                foreach (var attr in element.Attributes)
                    if (attr.Name == "Class" && kindsByStart.TryGetValue(attr.ValueSpan.Start, out var kind))
                        attr.ClassListKind = kind;
        }

        // E4（design/17 §4.5-2）：嵌套塌缩注入——按目录注入规则对模板静态子树匹配
        // （含 @foreach/@if 分支内的模板），命中元素回写 InjectedClasses（发射排 ClassNames
        // 之后 → 派生类胜）。表达式 Class 拿不到编译期类列表，注入跳过（§4.5-3 明示边界）。
        var injectRules = new List<InjectRuleData>(styleClasses.Injects);
        injectRules.AddRange(localInjects);
        ApplyInjectRules(doc, injectRules);

        bool hasErrors = false;
        foreach (var diag in diags)
        {
            Report(spc, input.Path, sourceText, diag);
            // Source member conflicts retain generated code for C# diagnostics. Structural errors
            // (including a foreach variable using generated names) must prevent invalid output.
            if (diag.IsError && (diag.Code != UiDiagnostics.CodeBehindConflict ||
                                 !facts.CodeBehindDiags.Contains(diag))) hasErrors = true;
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
            styleRules,
            // E5：本文件 @style 块的 @states 声明——InitializeUi 首行的 __RegisterStyles 先注册状态位
            localStates,
            // design/17 §4.3：Style= 内联发射的目标字段来自同一份符号派生目录
            catalog.StyleProperties,
            facts.LoopLocals.GroupBy(f => f.SpanStart).ToDictionary(g => g.Key,
                g => (IReadOnlyList<(string Type, string Name)>)g.Select(f => (f.Type, f.Name)).ToArray())).Emit();

        var hintName = $"{className}.{StableHash(input.Path)}.g.cs";
        // design/18 §5.2：IDE 旁路窗口开着时，把本轮发射记录的映射与 hint name 关联
        // （build 路径 IsActive=false，SetHintName 直接返回，零开销）。
        IdeSourceMappingSideband.SetHintName(input.Path, hintName);
        spc.AddSource(hintName, SourceText.From(code, Encoding.UTF8));
    }

    /// <summary>
    /// E2/E4/E5：单文件的样式来源提取——.bss 取塌缩后的全部类名（根 + 派生）与注入规则；
    /// .bui 只取 @style 块（类名加 <c>{TypeName}__</c> 前缀——塌缩派生名与注入规则同前缀化，
    /// 嵌套类约束按本块裸名先解析）。诊断丢弃（各自主管道报告），任一解析 Error 则该文件
    /// 产出空集——主管道 Error 已短路生成，半成品目录没有意义。
    /// E5：<paramref name="externalStates"/> 是全局 @states 目录（跨文件注册名引用的兜底校验）。
    /// </summary>
    private static StyleSourceFact ExtractStyleClasses(UiFileInput input, StyleStatesCatalog externalStates,
        StylePropertyCatalog properties)
    {
        var extension = Path.GetExtension(input.Path);
        if (extension == ".bss")
        {
            var diags = new List<UiDiagnostic>();
            var sheet = Bss.Parse(input.Text, input.Path, diags, properties, 0, externalStates.Names);
            if (diags.Exists(static d => d.IsError))
                return new StyleSourceFact(input.Path,
                    ImmutableArray<StyleClassRef>.Empty, ImmutableArray<InjectRuleData>.Empty,
                    ImmutableArray<string>.Empty);
            var collapsed = BssCollapse.Flatten(sheet, new List<UiDiagnostic>());
            return new StyleSourceFact(input.Path,
                collapsed.Rules.Select(static r => new StyleClassRef(r.ClassName, r.NameSpan.Start))
                    .Concat(ControlThemeSyntax.Classes(ControlThemeSyntax.Parse(input.Text, new List<UiDiagnostic>())).Select(c => new StyleClassRef(c, 0)))
                    .ToImmutableArray(),
                collapsed.Injects.Select(static i => new InjectRuleData(
                    i.RootClass,
                    i.Chain.Select(static s2 => new InjectSegData(s2.DirectChild, s2.TypeName, s2.ClassConstraint))
                        .ToImmutableArray(),
                    i.DerivedName)).ToImmutableArray(),
                collapsed.States.Select(static s => s.Name).ToImmutableArray());
        }

        // .bui：@style 块类名。组件名 = 文件名（ExecuteFile 同口径）。
        var className = Path.GetFileNameWithoutExtension(input.Path);
        if (!SyntaxFacts.IsValidIdentifier(className))
            return new StyleSourceFact(input.Path,
                ImmutableArray<StyleClassRef>.Empty, ImmutableArray<InjectRuleData>.Empty,
                ImmutableArray<string>.Empty);
        var parseDiags = new List<UiDiagnostic>();
        var doc = FrontEnd.Parse(input.Text, input.Path, parseDiags);
        var names = new List<string>();
        var injects = new List<InjectRuleData>();
        var states = new List<string>();
        foreach (var block in doc.Nodes.OfType<UiStyleBlock>())
        {
            var blockDiags = new List<UiDiagnostic>();
            var sheet = Bss.Parse(block.Code, input.Path, blockDiags, properties,
                block.CodeSpan.Start, externalStates.Names);
            if (blockDiags.Exists(static d => d.IsError)) continue;
            var collapsed = BssCollapse.Flatten(sheet, new List<UiDiagnostic>());
            var localNames = new HashSet<string>(collapsed.Rules.Select(static r => r.ClassName), StringComparer.Ordinal);
            names.AddRange(collapsed.Rules.Select(static r => r.ClassName));
            states.AddRange(collapsed.States.Select(static s => s.Name));
            foreach (var inj in collapsed.Injects)
            {
                injects.Add(new InjectRuleData(
                    className + "__" + inj.RootClass,
                    inj.Chain.Select(seg => new InjectSegData(
                        seg.DirectChild, seg.TypeName,
                        seg.ClassConstraint is { } c && localNames.Contains(c)
                            ? className + "__" + c : seg.ClassConstraint)).ToImmutableArray(),
                    className + "__" + inj.DerivedName));
            }
        }
        return new StyleSourceFact(input.Path,
            names.Select(n => new StyleClassRef(className + "__" + n, 0)).ToImmutableArray(),
            injects.ToImmutableArray(),
            states.Distinct(StringComparer.Ordinal).ToImmutableArray());
    }

    /// <summary>
    /// E5 第一级收集：单文件的 @states 声明名单（预扫——不做完整解析，声明序无关）。
    /// .bss 直接预扫全文；.bui 解析后逐 @style 块预扫。诊断丢弃（正式管道重报）。
    /// </summary>
    private static StyleStatesFact ExtractStyleStates(UiFileInput input)
    {
        var extension = Path.GetExtension(input.Path);
        if (extension == ".bss")
            return new StyleStatesFact(input.Path,
                Bss.CollectStates(input.Text, input.Path, new List<UiDiagnostic>())
                    .Select(static s => s.Name).ToImmutableArray());

        var className = Path.GetFileNameWithoutExtension(input.Path);
        if (!SyntaxFacts.IsValidIdentifier(className))
            return new StyleStatesFact(input.Path, ImmutableArray<string>.Empty);
        var doc = FrontEnd.Parse(input.Text, input.Path, new List<UiDiagnostic>());
        var names = new List<string>();
        foreach (var block in doc.Nodes.OfType<UiStyleBlock>())
            names.AddRange(Bss.CollectStates(block.Code, input.Path, new List<UiDiagnostic>(),
                block.CodeSpan.Start).Select(static s => s.Name));
        return new StyleStatesFact(input.Path, names.Distinct(StringComparer.Ordinal).ToImmutableArray());
    }

    /// <summary>
    /// E4 注入匹配（design/17 §4.5-2）：对挂根类的元素按链段匹配静态子树。直接子不穿透
    /// 插槽填充（插槽内容运行时挂进组件内部，不是视觉直接子）；后裔匹配穿透全部块结构
    /// （@foreach/@if/@switch 编译期已知——正是「无引擎获得结构表达力」的关键）。
    /// </summary>
    private static void ApplyInjectRules(UiDocument doc, IReadOnlyList<InjectRuleData> injects)
    {
        if (injects.Count == 0) return;
        foreach (var element in WalkElements(doc.Nodes))
        {
            // 根类匹配按解析后的注册名（Analyzer 已把本文件 @style 裸名映射成注册名）。
            var classAttr = element.Attributes.FirstOrDefault(static a => a.Name == "Class");
            if (classAttr?.ClassNames is not IReadOnlyList<string> names) continue; // 表达式/无目录 → 跳过
            foreach (var rule in injects)
                if (IndexOfName(names, rule.RootClass) >= 0)
                    MatchChain(element, rule, 0);
        }
    }

    private static void MatchChain(UiElement element, InjectRuleData rule, int index)
    {
        var seg = rule.Chain[index];
        var candidates = seg.DirectChild
            ? DirectChildElements(element.Children)
            : WalkElements(element.Children);
        foreach (var child in candidates)
        {
            if (!SegMatches(child, seg)) continue;
            AddInject(child, rule.DerivedName);
            if (index + 1 < rule.Chain.Count)
                MatchChain(child, rule, index + 1);
        }
    }

    /// <summary>
    /// 直接子元素枚举：穿透 @if/@foreach/@switch 块（块内模板编译期已知、运行时就是直接子），
    /// 不穿透插槽填充（插槽内容运行时挂进组件内部，不是视觉直接子）。
    /// </summary>
    private static IEnumerable<UiElement> DirectChildElements(IReadOnlyList<UiNode> children)
    {
        foreach (var node in children)
        {
            switch (node)
            {
                case UiElement element:
                    yield return element;
                    break;
                case UiIf ifNode:
                    foreach (var e in DirectChildElements(ifNode.Children)) yield return e;
                    foreach (var elseIf in ifNode.ElseIfs)
                        foreach (var e in DirectChildElements(elseIf.Children)) yield return e;
                    if (ifNode.ElseChildren is not null)
                        foreach (var e in DirectChildElements(ifNode.ElseChildren)) yield return e;
                    break;
                case UiLoop loop:
                    foreach (var e in DirectChildElements(loop.Children)) yield return e;
                    break;
                case UiForeach foreachNode:
                    foreach (var e in DirectChildElements(foreachNode.Children)) yield return e;
                    break;
                case UiSwitch switchNode:
                    foreach (var section in switchNode.Sections)
                        foreach (var e in DirectChildElements(section.Children)) yield return e;
                    break;
            }
        }
    }

    private static bool SegMatches(UiElement element, InjectSegData seg)
    {
        if (seg.TypeName is { } type &&
            !string.Equals(BssCollapse.Kebab(element.Tag), type, StringComparison.Ordinal))
            return false;
        if (seg.ClassConstraint is not { } constraint) return true;
        // 类约束按注册名比较：元素解析后的 ClassNames ∪ 已注入的派生名（注入可作后续链段的约束）。
        var classAttr = element.Attributes.FirstOrDefault(static a => a.Name == "Class");
        if (classAttr?.ClassNames is { } names && IndexOfName(names, constraint) >= 0) return true;
        return element.InjectedClasses is { } existing && existing.Contains(constraint);
    }

    private static void AddInject(UiElement element, string derived)
    {
        var list = element.InjectedClasses ??= new List<string>();
        if (!list.Contains(derived)) list.Add(derived);
    }

    private static int IndexOfName(IReadOnlyList<string> names, string name)
    {
        for (var i = 0; i < names.Count; i++)
            if (string.Equals(names[i], name, StringComparison.Ordinal)) return i;
        return -1;
    }

    /// <summary>E2：深度遍历模板树的全部元素节点（含 @if/@foreach/@switch 块与插槽填充内容）。</summary>
    private static IEnumerable<UiElement> WalkElements(IEnumerable<UiNode> nodes)
    {
        foreach (var node in nodes)
        {
            switch (node)
            {
                case UiElement element:
                    yield return element;
                    foreach (var descendant in WalkElements(element.Children))
                        yield return descendant;
                    break;
                case UiIf ifNode:
                    foreach (var descendant in WalkElements(ifNode.Children))
                        yield return descendant;
                    foreach (var elseIf in ifNode.ElseIfs)
                        foreach (var d in WalkElements(elseIf.Children)) yield return d;
                    if (ifNode.ElseChildren is not null)
                        foreach (var d in WalkElements(ifNode.ElseChildren)) yield return d;
                    break;
                case UiLoop loop:
                    foreach (var descendant in WalkElements(loop.Children)) yield return descendant;
                    break;
                case UiForeach foreachNode:
                    foreach (var descendant in WalkElements(foreachNode.Children))
                        yield return descendant;
                    break;
                case UiSwitch switchNode:
                    foreach (var section in switchNode.Sections)
                        foreach (var descendant in WalkElements(section.Children))
                            yield return descendant;
                    break;
                case UiSlotNode fill:
                    foreach (var descendant in WalkElements(fill.Children))
                        yield return descendant;
                    break;
            }
        }
    }

    /// <summary>
    /// E1（design/17 §4.1）：.bss 编译产物——全部文件的规则按文件路径序合并为一个
    /// <c>StyleBootstrap.Apply(StyleRegistry)</c> 静态入口（AppBuilder.UseStyles 方法组直连）。
    /// 命名空间取项目根命名空间（与单文件组件同口径）。诊断逐文件报告；任一文件存在
    /// Error 短路生成。同名类跨文件重复：保持「后注册覆盖先注册」运行时语义，顺序由路径排序固定。
    /// E5：<paramref name="statesCatalog"/> 是全局 @states 目录（跨文件注册名引用校验）；
    /// Apply 顶部发射全部文件的注册序列（幂等 Register）。
    /// E5/EUI054：<paramref name="handwritten"/> 是宿主手写 Define 的类名名单——交集 Warning。
    /// </summary>
    private static void ExecuteStylesheet(SourceProductionContext spc,
        ImmutableArray<UiFileInput> files, string rootNamespace,
        StyleStatesCatalog statesCatalog, ImmutableArray<HandwrittenDefine> handwritten,
        StylePropertyCatalog properties)
    {
        if (files.IsEmpty) return;

        var ordered = files.OrderBy(static f => f.Path, StringComparer.Ordinal).ToList();
        var collapsedSheets = new List<BssCollapse.Collapsed>();
        var hasErrors = false;
        foreach (var file in ordered)
        {
            var text = SourceText.From(file.Text);
            var diags = new List<UiDiagnostic>();
            var sheet = Bss.Parse(file.Text, file.Path, diags, properties, 0, statesCatalog.Names);
            // E4：嵌套塌缩——派生类进 Define 序列，命名冲突 EUI053 进同一诊断流（Error 短路）。
            var collapsed = BssCollapse.Flatten(sheet, diags);
            foreach (var diag in diags)
            {
                Report(spc, file.Path, text, diag);
                if (diag.IsError) hasErrors = true;
            }
            collapsedSheets.Add(collapsed);

            // E5/EUI054：双通道冲突（Warning 不短路）——样式表类名也出现在宿主手写
            // Define("name", …) 时报告；运行时「后注册覆盖」既有语义不变。
            var handwrittenNames = new HashSet<string>(
                handwritten.Select(static d => d.Name), StringComparer.Ordinal);
            foreach (var rule in collapsed.Rules)
            {
                if (!handwrittenNames.Contains(rule.ClassName)) continue;
                Report(spc, file.Path, text, UiDiagnostics.Warning(
                    UiDiagnostics.StyleDualChannelConflict,
                    $"style class '{rule.ClassName}' is also registered by a handwritten Define(...) call " +
                    "in C#; the runtime keeps the last registration — stylesheets should be the single " +
                    "source of truth (EUI054)", rule.NameSpan));
            }
        }
        if (hasErrors) return;

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated> 由 BubuUI 样式表编译器生成；请勿手动修改。 </auto-generated>");
        sb.AppendLine("#nullable enable");
        var ns = SanitizeNamespace(rootNamespace);
        if (ns.Length > 0)
        {
            sb.Append("namespace ").Append(ns).AppendLine(";");
            sb.AppendLine();
        }
        sb.AppendLine("/// <summary>.bss 编译产物（E1）：把样式表类注册进 StyleRegistry——");
        sb.AppendLine("/// AppBuilder.UseStyles 方法组直连本方法，宿主启动期调用一次。</summary>");
        sb.AppendLine("public static partial class StyleBootstrap");
        sb.AppendLine("{");
        sb.AppendLine("    static partial void ApplyControlThemes(global::BubuUI.Rendering.StyleRegistry registry);");
        sb.AppendLine("    public static void Apply(global::BubuUI.Rendering.StyleRegistry registry)");
        sb.AppendLine("    {");
        sb.AppendLine("        ApplyControlThemes(registry);");
        foreach (var collapsed in collapsedSheets)
        {
            // E5：@states 声明先注册（幂等）——变体链的 Register 实参本就自足，此处保证
            // 未被变体引用的声明位也完成注册（宿主可 GetMask 查询句柄）。
            sb.Append(BssGenerator.EmitStateRegisters(collapsed.States));
            sb.Append(BssGenerator.EmitDefines(collapsed.Rules, "registry", properties));
        }
        sb.AppendLine("    }");
        sb.AppendLine("}");
        spc.AddSource("StyleBootstrap.g.cs", sb.ToString());
    }

    /// <summary>
    /// E5/EUI054：宿主手写 C# <c>Define("name", …)</c> 的类名名单——遍历宿主语法树，
    /// 取方法名 Define 且首实参为字符串字面量的调用（注释/非字符串实参不算）。
    /// 误报（同名自定义方法）容忍：诊断是 Warning。
    /// </summary>
    private static ImmutableArray<HandwrittenDefine> ExtractHandwrittenDefines(Compilation compilation)
    {
        var names = new List<HandwrittenDefine>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tree in compilation.SyntaxTrees)
        {
            if (tree.GetRoot() is not CompilationUnitSyntax root) continue;
            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (invocation.Expression is not MemberAccessExpressionSyntax member ||
                    !member.Name.Identifier.ValueText.Equals("Define", StringComparison.Ordinal) ||
                    invocation.ArgumentList.Arguments.Count == 0 ||
                    invocation.ArgumentList.Arguments[0].Expression is not LiteralExpressionSyntax literal ||
                    !literal.IsKind(SyntaxKind.StringLiteralExpression) ||
                    literal.Token.Value is not string name ||
                    !seen.Add(name))
                    continue;
                names.Add(new HandwrittenDefine(name));
            }
        }
        return names.ToImmutableArray();
    }

    /// <summary>
    /// D74 ②：源项目元数据声明问题的编译期出口（design/14 §14.5.4/§14.9.2）。
    /// 只有带源声明位置的 problem 才在这里报——引用程序集没有语法位置，
    /// 仍由 D74 ① 的使用点 EUI019/EUI020/EUI021 暴露。诊断不阻断 .bui 的生成输出，
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

    /// <summary>
    /// design/17 §4.3：样式属性目录的声明问题出口。问题多数来自引用程序集
    /// （Rendering.dll）的符号，没有源声明位置，统一用 Location.None 报 EUI026，
    /// 保证「加了 StyleBuilder setter 却漏了 StyledProperty 字段」这类漂移在构建期可见。
    /// </summary>
    private static void ReportStylePropertyProblems(SourceProductionContext spc, StylePropertyCatalog properties)
    {
        foreach (var problem in properties.Problems)
        {
            var descriptor = new DiagnosticDescriptor(problem.Code, problem.Code, "{0}", "BubuUI",
                RoslynSeverity.Error, isEnabledByDefault: true);
            spc.ReportDiagnostic(Diagnostic.Create(descriptor, Location.None, problem.Message));
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
    /// 触发：InitializeUi/Render 声明、生成成员保留名、非法基类、隐藏生成代码依赖成员。
    /// 显式基类必须与根标签解析得到的 CLR 类型一致。
    /// </summary>
    private static void ProbeCodeBehindConflicts(INamedTypeSymbol type, List<UiDiagnostic> diags,
        string? rootTypeDisplay, Compilation compilation)
    {
        foreach (var member in type.GetMembers())
        {
            var name = member.Name;
            string? reason = null;
            if (member is IMethodSymbol { MethodKind: MethodKind.Ordinary } &&
                name is "InitializeUi" or "Render" or "__RegisterStyles")
                reason = $"code-behind must not declare '{name}()'; the generator emits it";
            else if (IsGeneratedMemberName(name))
                reason = $"member '{name}' collides with a generated member name";
            else if (GeneratedDependencyNames.Contains(name))
                reason = $"member '{name}' hides a UiObject member used by generated code";
            if (reason is not null)
                diags.Add(Conflict(reason, member.Locations.FirstOrDefault(static l => l.IsInSource)));
        }

        // 验证全部 partial 声明上的显式基类。
        foreach (var reference in type.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax() is not TypeDeclarationSyntax declaration || declaration.BaseList is null)
                continue;
            foreach (var baseType in declaration.BaseList.Types)
            {
                var text = baseType.Type.ToString();
                if (rootTypeDisplay is not null)
                {
                    var model = compilation.GetSemanticModel(baseType.SyntaxTree);
                    var explicitType = model.GetTypeInfo(baseType.Type).Type;
                    if (explicitType?.TypeKind == TypeKind.Interface) continue;
                    var expected = rootTypeDisplay.Replace("global::", "");
                    var actual = explicitType?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                        .Replace("global::", "");
                    if (!string.Equals(actual, expected, StringComparison.Ordinal))
                        diags.Add(Conflict($"code-behind base '{text}' must match the root type '{rootTypeDisplay}'",
                            baseType.GetLocation()));
                }

            }
        }
    }

    private static readonly HashSet<string> GeneratedDependencyNames = new(StringComparer.Ordinal)
    {
        "Invalidate", "EnsureInitialized", "Track", "ProcessInvalidations",
    };

    /// <summary>__el{N}/__blk{N}/__run{N}/__bind{N}/__eui*/U{N}/R{N} 是生成成员保留名。</summary>
    private static bool IsGeneratedMemberName(string name)
    {
        if (name.StartsWith("__eui", StringComparison.Ordinal)) return true;
        string[] prefixes = { "__el", "__blk", "__run", "__bind", "__eui", "U", "R" };
        foreach (var prefix in prefixes)
            if (name.StartsWith(prefix, StringComparison.Ordinal) &&
                name.Length > prefix.Length &&
                name.Skip(prefix.Length).All(char.IsDigit))
                return true;
        return false;
    }

    private static UiDiagnostic Conflict(string message, Location? location) =>
        SourceConflict(UiDiagnostics.CodeBehindConflict, message, location);

    /// <summary>落在 code-behind 源声明上的诊断（design/18 口径）：Location → 纯数据 UiSourceLocation，.bui 侧 span 置零。</summary>
    private static UiDiagnostic SourceConflict(string code, string message, Location? location)
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
        return UiDiagnostics.Error(code, message, UiSpan.Zero, source);
    }

    /// <summary>
    /// code-behind 类的插槽表（spec/02 §4.12.5）：沿基类链读 [UiSlot] 属性。
    /// [UiIgnore] 标记的属性不参与插槽表；
    /// 类型只接受 <c>Element?</c>（单根）与 <c>IReadOnlyList&lt;Element&gt;</c>（多根），
    /// 其余不进表（声明期问题归目录 EUI021 族）。
    /// </summary>
    private static (string DisplayName, IReadOnlyDictionary<string, UiSlotDef> Slots)? ResolveOwnTemplate(
        INamedTypeSymbol? type, Compilation compilation)
    {
        if (type is null) return null;

        var element = compilation.GetTypeByMetadataName("BubuUI.Element");
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

    /// <summary>EUI055 的修复建议：优先 code-behind 的命名空间，其次项目根命名空间。</summary>
    private static string SuggestedNamespace(INamedTypeSymbol? type, string rootNamespace)
    {
        var fromType = type is null ? "" : NamespaceName(type);
        if (fromType.Length > 0) return fromType;
        var sanitized = SanitizeNamespace(rootNamespace);
        return sanitized.Length > 0 ? sanitized : "My.App";
    }

    /// <summary>
    /// K23 ④：项目根命名空间仅在每段都是合法标识符时采用（MSBuild 可能给出空串或
    /// 带点的占位值）；不合法落 global 命名空间，交由生成代码的可见性自然成立。
    /// </summary>
    private static string SanitizeNamespace(string rootNamespace) =>
        string.IsNullOrWhiteSpace(rootNamespace) ? "" :
        rootNamespace.Split('.').All(SyntaxFacts.IsValidIdentifier) ? rootNamespace : "";

    /// <summary>
    /// 逐块推断 item 类型与 @switch selector 类型，并校验每个 <c>@bind</c> 源：都把表达式放进
    /// 合成 partial 方法体，借探针 Compilation（含 @code 成员合成树，R1）的符号上下文让 Roslyn 回答；
    /// 外层 item 以局部变量注入，支持嵌套作用域。
    /// </summary>
    private static Dictionary<UiForeach, string> ProbeSemantics(
        UiDocument doc, ExpressionProbe probe, Compilation compilation, List<UiDiagnostic> diags,
        List<SwitchTypeFact> switchTypes, List<ClassKindFact> classKinds, List<LoopLocalFact> loopLocals)
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
                            ValidateClassExpression(attr, probe, scope, diags, classKinds);
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
                        // 插槽填充的内容是初始化内容的一部分：@foreach item 推断与 @bind 校验
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
                        foreach (var section in switchNode.Sections) Walk(section.Children, scope);
                        break;
                    case UiLoop loop:
                        var loopProbe = probe.ProbeLoop(loop.Kind, loop.Header, scope);
                        foreach (var error in loopProbe.Errors)
                            diags.Add(UiDiagnostics.Error(UiDiagnostics.Syntax, error, loop.HeaderSpan));
                        foreach (var local in loopProbe.Locals)
                        {
                            if (local.Name.StartsWith("__eui", StringComparison.Ordinal))
                                diags.Add(UiDiagnostics.Error(UiDiagnostics.CodeBehindConflict,
                                    "loop locals cannot use the reserved '__eui' prefix", loop.HeaderSpan));
                            if (local.Type.IsRefLikeType || local.Type.IsAnonymousType)
                                diags.Add(UiDiagnostics.Error(UiDiagnostics.Syntax,
                                    "loop locals must have a named, capturable type", loop.HeaderSpan));
                            loopLocals.Add(new LoopLocalFact(loop.HeaderSpan.Start,
                                local.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), local.Name));
                        }
                        var loopScope = new List<(ITypeSymbol Type, string Name)>(scope);
                        loopScope.AddRange(loopProbe.Locals);
                        Walk(loop.Children, loopScope);
                        break;
                    case UiForeach foreachNode:
                        var collectionType = probe.InferType(foreachNode.Collection, scope);
                        var itemType = collectionType is null ? null : ExpressionProbe.ItemTypeOf(collectionType, compilation);
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
    private static void ProbeSwitch(UiSwitch switchNode, ExpressionProbe probe, Compilation compilation,
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
    private static void ValidateExpressionValue(UiAttribute attr, ExpressionProbe probe,
        IReadOnlyList<(ITypeSymbol Type, string Name)> env, List<UiDiagnostic> diags)
    {
        if (attr.Kind != AttrValueKind.Expression || attr.IsBind) return;
        if (attr.EventDef is not null || attr.RefTarget is not null) return;
        var targetClrType =
            attr.PropDef?.ClrTypeDisplayName ?? attr.ParamDef?.ClrTypeDisplayName ?? attr.AttachedDef?.ClrTypeDisplayName;
        if (targetClrType is null) return;
        ProbeExpression(attr.Value, attr.ValueSpan, probe, env, diags, targetClrType);
    }

    /// <summary>
    /// E2：Class={expr} 的形态探针（design/17 §4.8）——string（空白分隔类名）为默认形态，
    /// IEnumerable&lt;string&gt; 记入 <paramref name="classKinds"/> 供发射 ToArray；
    /// 确定类型且两者皆非的报 EUI031。推断失败/dynamic 不报（C# 经 #line 兜底）。
    /// </summary>
    private static void ValidateClassExpression(UiAttribute attr, ExpressionProbe probe,
        IReadOnlyList<(ITypeSymbol Type, string Name)> env, List<UiDiagnostic> diags,
        List<ClassKindFact> classKinds)
    {
        if (attr.Name != "Class" || attr.IsBind || attr.Kind != AttrValueKind.Expression) return;

        var type = probe.InferType(attr.Value, env);
        if (type is null || type.TypeKind == TypeKind.Error || type is IDynamicTypeSymbol) return;
        if (type.SpecialType == SpecialType.System_String) return; // 默认形态：Split
        if (IsStringEnumerable(type))
        {
            classKinds.Add(new ClassKindFact(attr.ValueSpan.Start, UiClassListKind.Collection));
            return;
        }

        diags.Add(UiDiagnostics.Error(UiDiagnostics.StyleAttributeInvalid,
            $"Class={{…}} expression must be a string (space-separated class names) or a string collection; " +
            $"'{attr.Value.Trim()}' is '{type.ToDisplayString()}'", attr.ValueSpan));
    }

    private static bool IsStringEnumerable(ITypeSymbol type)
    {
        foreach (var iface in type.AllInterfaces)
        {
            if (iface.OriginalDefinition.SpecialType != SpecialType.System_Collections_Generic_IEnumerable_T) continue;
            if (iface.TypeArguments.Length == 1 &&
                iface.TypeArguments[0].SpecialType == SpecialType.System_String)
                return true;
        }
        return false;
    }

    /// <summary>EUI049 共用入口：表达式原文放探针编译，句内 CS 错误逐条升级为模板诊断。</summary>
    private static void ProbeExpression(string expression, TextSpan span, ExpressionProbe probe,
        IReadOnlyList<(ITypeSymbol Type, string Name)> env, List<UiDiagnostic> diags,
        string? targetClrType = null)
    {        var trimmed = expression.Trim();
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
    private static void ValidateBindSource(UiAttribute attr, ExpressionProbe probe,
        IReadOnlyList<(ITypeSymbol Type, string Name)> env, List<UiDiagnostic> diags)
    {
        // 控件目标看 PropDef；组件目标看 ParamDef（spec/02 §4.4.3 回调由父推送实现，源校验同规则）。
        // 组件还要有双向通道（{P}Changed）才继续：否则 EUI040 已经说过目标不可绑，再报源不可写
        // 只是同一处写法的第二个错，与控件侧「目标不在表面就早退」保持同一口径。
        string? clrType = null;
        if (attr.PropDef is { CanWrite: true }) clrType = attr.PropDef.ClrTypeDisplayName;
        else if (attr.ParamDef is { CanWrite: true, IsDelegate: false, IsContent: false, ChangedCallbackName: not null } p)
            clrType = p.ClrTypeDisplayName;
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
    private static void ValidateRefTarget(UiElement element, UiAttribute attr, ExpressionProbe probe,
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
    private static void ValidateEventHandlerSignature(UiAttribute attr, ExpressionProbe probe,
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
            diag.Code, diag.Code, "{0}", "BubuUI",
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

/// <summary>E5/EUI054：宿主手写 C# Define 调用的类名（值相等供增量缓存；只留名字——报告位置在样式表侧）。</summary>
internal sealed record HandwrittenDefine(string Name);

/// <summary>E2/E4：单文件提取的样式来源（路径 + 有序类名引用（含 span——EUI054 定位）+
/// 注入规则 + @states 声明；值相等供增量缓存）。</summary>
internal sealed record StyleSourceFact(
    string Path, ImmutableArray<StyleClassRef> Classes, ImmutableArray<InjectRuleData> Injects,
    ImmutableArray<string> States)
{
    public bool Equals(StyleSourceFact? other) =>
        other is not null && Path == other.Path && Classes.SequenceEqual(other.Classes) &&
        Injects.SequenceEqual(other.Injects) && States.SequenceEqual(other.States);

    public override int GetHashCode() =>
        unchecked(Path.GetHashCode() * 397 + Classes.Length * 31 + Injects.Length * 17 + States.Length);
}

/// <summary>E5：样式类引用（名 + 名字原文起点——EUI054 Warning 的样式表侧定位；值相等）。</summary>
internal sealed record StyleClassRef(string Name, int NameStart);

/// <summary>E5：单文件提取的 @states 声明名单（值相等供增量缓存；诊断丢弃——正式管道重报）。</summary>
internal sealed record StyleStatesFact(string Path, ImmutableArray<string> Names)
{
    public bool Equals(StyleStatesFact? other) =>
        other is not null && Path == other.Path && Names.SequenceEqual(other.Names);

    public override int GetHashCode() => unchecked(Path.GetHashCode() * 397 + Names.Length);
}

/// <summary>E4：注入链段的纯数据形态（值相等；诊断 span 不进缓存数据）。</summary>
internal sealed record InjectSegData(bool DirectChild, string? TypeName, string? ClassConstraint);

/// <summary>
/// E4：注入规则的纯数据形态（值相等）——挂 <see cref="RootClass"/> 的元素的静态子树中，
/// 按 <see cref="Chain"/> 匹配到的元素追加 <see cref="DerivedName"/> 类（排原类之后）。
/// RootClass 与 ClassConstraint 一律是**注册名**（.bss 裸名 = 注册名；@style 块由
/// ExtractStyleClasses/ExecuteFile 前缀化并按「先本文件」序解析约束）。
/// </summary>
internal sealed record InjectRuleData(string RootClass, IReadOnlyList<InjectSegData> Chain, string DerivedName)
{
    public bool Equals(InjectRuleData? other) =>
        other is not null && RootClass == other.RootClass && DerivedName == other.DerivedName &&
        Chain.SequenceEqual(other.Chain);

    public override int GetHashCode() =>
        unchecked(RootClass.GetHashCode() * 397 + DerivedName.GetHashCode() * 31 + Chain.Count);
}

/// <summary>
/// E2/E4/E5：全局样式目录（类名有序去重集 + 注入规则集 + @states 声明并集）。
/// 值相等是增量缓存前提：任何样式文件变化都会重建，但内容不变时下游逐文件管道按值命中。
/// </summary>
internal sealed record StyleClassCatalog(
    ImmutableArray<string> ClassNames, ImmutableArray<InjectRuleData> Injects, ImmutableArray<string> States)
{
    public static StyleClassCatalog Build(ImmutableArray<StyleSourceFact> facts) => new(
        facts.SelectMany(static f => f.Classes)
            .Select(static c => c.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static n => n, StringComparer.Ordinal)
            .ToImmutableArray(),
        facts.SelectMany(static f => f.Injects).ToImmutableArray(),
        facts.SelectMany(static f => f.States)
            .Distinct(StringComparer.Ordinal)
            .ToImmutableArray());

    public bool Contains(string className) => ClassNames.IndexOf(className, StringComparer.Ordinal) >= 0;

    public bool Equals(StyleClassCatalog? other) =>
        other is not null && ClassNames.SequenceEqual(other.ClassNames, StringComparer.Ordinal) &&
        Injects.SequenceEqual(other.Injects) && States.SequenceEqual(other.States, StringComparer.Ordinal);

    public override int GetHashCode() => ClassNames.Length * 31 + Injects.Length * 17 + States.Length;
}

/// <summary>E5：全局 @states 注册名目录（第一级收集——声明序无关，跨文件引用校验的兜底集）。</summary>
internal sealed record StyleStatesCatalog(ImmutableArray<string> Names)
{
    public static StyleStatesCatalog Build(ImmutableArray<StyleStatesFact> facts) => new(
        facts.SelectMany(static f => f.Names)
            .Distinct(StringComparer.Ordinal)
            .ToImmutableArray());

    public bool Equals(StyleStatesCatalog? other) =>
        other is not null && Names.SequenceEqual(other.Names, StringComparer.Ordinal);

    public override int GetHashCode() => Names.Length;
}

/// <summary>E2：Class={expr} 的集合形态事实（ValueSpan.Start → 形态；String 形态为默认不记录）。</summary>
internal sealed record ClassKindFact(int SpanStart, UiClassListKind Kind);

internal sealed record LoopLocalFact(int SpanStart, string Type, string Name);

internal sealed record ItemTypeFact(int SpanStart, string Type);

/// <summary>@switch selector 的值类型事实（K29）：ValueSpan.Start → 全限定类型名。</summary>
internal sealed record SwitchTypeFact(int SpanStart, string Type);

/// <summary>
/// spec/02 §2.7：项目里全部 <c>.bui</c> 文件构成的组件声明清单（模板类型声明）。
/// 逐元素值相等——清单只随文件增删或 <c>@namespace</c> 改动变化，改模板正文不会让目录重建
/// （design/14 §14.10.3）；排序让文件枚举顺序不参与比较。
/// </summary>
internal sealed record UiTemplateDeclarations(ImmutableArray<UiTemplateDeclaration> Items)
{
    public static UiTemplateDeclarations Build(ImmutableArray<UiTemplateDeclaration?> declarations) => new(
        declarations.Where(static d => d is not null)
            .Select(static d => d!)
            .OrderBy(static d => d.Tag, StringComparer.Ordinal)
            .ThenBy(static d => d.Namespace ?? "", StringComparer.Ordinal)
            .ToImmutableArray());

    public bool Equals(UiTemplateDeclarations? other) =>
        other is not null && Items.SequenceEqual(other.Items);

    public override int GetHashCode()
    {
        var hash = 17;
        foreach (var item in Items) hash = unchecked(hash * 397 + item.GetHashCode());
        return hash;
    }
}

/// <summary>逐文件语义事实：值相等是增量缓存的前提（ImmutableArray 默认引用相等，必须逐元素比）。</summary>
internal sealed record UiFileFacts(
    string Accessibility,
    string Namespace,
    string DisplayName,
    ImmutableArray<UiSlotDef> Slots,
    ImmutableArray<UiDiagnostic> CodeBehindDiags,
    ImmutableArray<UiDiagnostic> ProbeDiags,
    ImmutableArray<ItemTypeFact> ItemTypes,
    ImmutableArray<SwitchTypeFact> SwitchTypes,
    ImmutableArray<ClassKindFact> ClassKinds,
    ImmutableArray<LoopLocalFact> LoopLocals)
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
        SwitchTypes.SequenceEqual(other.SwitchTypes) &&
        ClassKinds.SequenceEqual(other.ClassKinds) &&
        LoopLocals.SequenceEqual(other.LoopLocals);

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
        hash = Add(hash, ClassKinds.Length);
        foreach (var fact in LoopLocals) hash = Add(hash, fact);
        return hash;

        // netstandard2.0 没有 System.HashCode，与 CatalogValueEq 同口径累加。
        static int Add(int h, object? v) => unchecked(h * 397 + (v?.GetHashCode() ?? 0));
    }
}

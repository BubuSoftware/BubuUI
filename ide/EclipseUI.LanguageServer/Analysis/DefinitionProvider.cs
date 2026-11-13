using System.Text.RegularExpressions;
using EclipseUI.Compiler;
using EclipseUI.Compiler.Probe;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EclipseUI.LanguageServer.Analysis;

/// <summary>
/// 跳转定义（design/06 §6.3 L3）：<c>{expr}</c> 内的标识符 → 探针符号反查
/// （<see cref="ExpressionProbe.Locate"/>，与悬停/补全同一台机器）；<c>@code</code> 块内
/// 的 C# → 成员树语义模型；标签名 → 目录里的 CLR 类型反查工作区源码。C# 岛与标签/属性
/// 共用统一落点链（<see cref="SymbolDefinition"/>）：本文档（foreach 头变量名、@code 块内
/// 声明——<see cref="DocumentAnalysis.TryMapMemberSpanToUi"/> 回映）、兄弟 <c>{Name}.cs</c>
/// （spec/02 §2.2 文件契约）、任意工作区 .cs（类型/成员声明）、跨项目源码、元数据反编译。
/// 找不到真实落点返回 null 而不是错位置（design/06 的"不猜"口径）。
/// </summary>
public static class DefinitionProvider
{
    /// <summary>工作区 .cs 扫描上限（与重命名同一口径：超过就不找，不给不可靠的跳转）。</summary>
    private const int MaxWorkspaceFiles = RenameProvider.MaxWorkspaceFiles;

    /// <summary>标签类型声明匹配：class/record/struct/interface + 类型名（词边界）。</summary>
    private static readonly Regex TypeDeclarationRegex = new(
        @"\b(class|record|struct|interface)\s+(?<name>\w+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static UiDefinition? Definition(
        DocumentAnalysis analysis, int offset, string? workspaceRoot = null,
        UiCatalog? catalog = null, Compilation? compilation = null)
    {
        var context = CursorScanner.Scan(analysis.Text, offset);
        return context.Kind switch
        {
            CursorKind.Expression => ExpressionDefinition(analysis, context, offset),
            CursorKind.CodeBlock => CodeBlockDefinition(analysis, offset),

            // 特殊属性（Analyzer.IsReservedName，目录外）：Ref/Class/Style 有自己的语义落点。
            // Ref={ident} 跳 code-behind/@code 字段；Class="x" 跳 @style 块或 .eustyle 里的
            // 类规则；Style="P: v" 的属性名跳 CLR 属性注册。先于通用属性分支（它们不在目录）。
            CursorKind.AttributeName when context.AttributeName is "Ref" =>
                RefDefinition(analysis, context),
            CursorKind.AttributeName when context.AttributeName is "Class" =>
                StyleClassDefinition(analysis, catalog, context),
            CursorKind.AttributeValue when context.AttributeName is "Class" && catalog is not null =>
                StyleClassDefinition(analysis, catalog, context),
            CursorKind.AttributeValue when context.AttributeName is "Style" && catalog is not null =>
                StyleDeclarationDefinition(analysis, catalog, context, compilation),

            // L6 属性转定义（§6.9.0 第 4 条）：属性 / 事件 / 组件参数 / 附加属性都按
            // 目录里的 CLR 成员名在项目 Compilation 上做符号查询，落点是成员声明。
            CursorKind.AttributeName when context.AttributeName.Length > 0 && catalog is not null =>
                AttributeDefinition(analysis, catalog, context, compilation),

            // Prefix 只是已敲的前缀（补全场景），跳转必须用完整标签名。
            // 开标签与闭标签（</Tag>）落点一致：都按完整标签名查目录里的 CLR 类型。
            CursorKind.TagName or CursorKind.EndTagName when context.TagName.Length > 0 && catalog is not null =>
                TagDefinition(analysis, catalog, context.TagName, workspaceRoot, compilation),
            _ => null,
        };
    }

    // ---- {expr} 内的标识符（L3 第一片主体） ----

    private static UiDefinition? ExpressionDefinition(DocumentAnalysis analysis, CursorContext context, int offset)
    {
        if (context.ExpressionStart < 0) return null;

        var probe = analysis.Probe;
        if (probe is null) return null;

        // 表达式要取**完整的**（ExpressionEnd）：光标在 token 中间时（task.Na|me），
        // 按 offset 截断会得到 task.Na——绑不出符号。
        //
        // @foreach 头特判：头整体（"var row in rows"）不是合法 C#，探针解析必败。
        // 拆开看——光标落在集合段才探（"rows"），变量名光标就是定义本身，无从跳。
        // 集合段的外层环境是 OpenForeaches（本 foreach 还没压栈，语义正确）。
        string expression;
        int cursor;
        if (context.Directive == "foreach")
        {
            var header = CursorScanner.ParseForeachHeader(
                analysis.Text, context.ExpressionStart, context.ExpressionEnd - context.ExpressionStart);
            if (header is null) return null;
            if (offset < header.CollectionStart || offset > header.CollectionEnd) return null;
            expression = header.Collection;
            cursor = offset - header.CollectionStart;
        }
        else
        {
            var end = context.ExpressionEnd < 0 ? offset : context.ExpressionEnd;
            if (end <= context.ExpressionStart) return null;
            expression = analysis.Text.Substring(context.ExpressionStart, end - context.ExpressionStart);
            cursor = Math.Clamp(offset - context.ExpressionStart, 0, expression.Length);
        }

        var env = ExpressionEnvironment.Build(analysis.Ast!, context.OpenForeaches, probe, probe.Compilation);
        var symbol = probe.Locate(expression, cursor, env);
        if (symbol is null) return null;

        // 1) @foreach 迭代变量：定义就在本文档（foreach 头里的变量名）。
        if (symbol is ILocalSymbol local)
        {
            var scope = context.OpenForeaches.FirstOrDefault(
                f => f.VarName == local.Name && f.NameOffset >= 0);
            if (scope is not null)
                return new UiDefinition(null, new TextSpan(scope.NameOffset, scope.VarName.Length), local.Name);
        }

        return SymbolDefinition(analysis, symbol);
    }

    // ---- @code 块内的 C#（L3 第三片） ----

    /// <summary>
    /// <c>@code</code> 块内是真实文档文本：把 .ui 偏移映射到成员树（块体逐字透传，K21），
    /// 在**派生编译**（探针编译 + 成员树，与重命名同一做法）的语义模型里绑定符号，
    /// 声明落点复用统一三路（本文档回映 / 兄弟 .cs / 元数据 null）。
    /// </summary>
    private static UiDefinition? CodeBlockDefinition(DocumentAnalysis analysis, int offset)
    {
        // 先触碰 Probe（触发 BuildProbe，顺带初始化 MemberTree）——MemberTree 是懒字段。
        var probe = analysis.Probe;
        if (probe is null) return null;
        var memberTree = analysis.MemberTree;
        if (memberTree is null || analysis.Ast is null) return null;

        var block = analysis.Ast.Nodes.OfType<UiCodeBlock>().FirstOrDefault(
            b => offset >= b.CodeSpan.Start && offset <= b.CodeSpan.Start + b.CodeSpan.Length);
        if (block is null) return null;

        // design/18 §5.2：项目模式走统一映射（发射时记录）；符号视图按块体原文定位。
        int bodyStart;
        if (analysis.GeneratedDocument is { } generated && ReferenceEquals(generated.Tree, memberTree))
        {
            if (!generated.Mappings.TryMapToGenerated(block.CodeSpan, out var mapped)) return null;
            bodyStart = mapped.Start;
        }
        else
        {
            bodyStart = memberTree.ToString().IndexOf(block.Code, StringComparison.Ordinal);
            if (bodyStart < 0) return null;
        }
        var treeOffset = bodyStart + (offset - block.CodeSpan.Start);

        // 项目模式下 @code 生成树已在项目 Compilation 里（L3），重复 AddSyntaxTrees 会让
        // partial 类成员翻倍；独立文档同样已将成员树放进统一编译，不再重复添加。
        var augmented = probe.Compilation;
        var model = augmented.GetSemanticModel(memberTree);

        var token = memberTree.GetRoot().FindToken(treeOffset);
        var symbol = token.Parent switch
        {
            // SimpleNameSyntax 涵盖 IdentifierNameSyntax 与 GenericNameSyntax：点 List<TaskItem>
            // 的 List 时 token.Parent 是 GenericNameSyntax（不是 IdentifierNameSyntax），符号挂在它上。
            SimpleNameSyntax name => model.GetSymbolInfo(name).Symbol
                ?? model.GetDeclaredSymbol(name),
            // 预定义类型关键字（int/bool/string…）：GetSymbolInfo 给底层 BCL 类型（Int32/Boolean/String），
            // 走统一三路反编译。不接这支它们会落 _ => null（用户实测「找不到实现」）。
            PredefinedTypeSyntax predefined => model.GetSymbolInfo(predefined).Symbol,
            VariableDeclaratorSyntax variable => model.GetDeclaredSymbol(variable),
            ParameterSyntax parameter => model.GetDeclaredSymbol(parameter),
            MethodDeclarationSyntax method => model.GetDeclaredSymbol(method),
            PropertyDeclarationSyntax property => model.GetDeclaredSymbol(property),
            LocalDeclarationStatementSyntax local => local.Declaration.Variables.Count == 1
                ? model.GetDeclaredSymbol(local.Declaration.Variables[0])
                : null,
            SingleVariableDesignationSyntax designation => model.GetDeclaredSymbol(designation),
            TypeDeclarationSyntax type => model.GetDeclaredSymbol(type),
            _ => null,
        };
        if (symbol is null) return null;

        var declared = symbol is IMethodSymbol methodSymbol ? methodSymbol.PartialDefinitionPart ?? methodSymbol.OriginalDefinition : symbol;
        // 先按定义部分（partial 方法的声明处）落点，再退回光标处符号本身（点在名字上至少定位/高亮）；
        // 两者都走统一三路：本文档回映 / 任意真实源码文件 / 元数据反编译。
        return SymbolDefinition(analysis, declared) ?? SymbolDefinition(analysis, symbol);
    }

    // ---- 属性 / 事件 / 组件参数 / 附加属性（L6，§6.9.0 第 4 条） ----

    // ---- 特殊属性：Ref / Class / Style（目录外的保留属性，Analyzer.IsReservedName） ----

    /// <summary>
    /// <c>Ref=</c> 属性名上的 F12：值恒为 <c>{ident}</c>（spec/01 §4.3），落点与点进花括号
    /// 内部完全一致——复用 <c>{expr}</c> 的符号反查（code-behind / @code 字段声明）。
    /// </summary>
    private static UiDefinition? RefDefinition(DocumentAnalysis analysis, CursorContext context)
    {
        var attr = FindAttributeAt(analysis, context.TokenStart, "Ref");
        if (attr is null || attr.Kind != AttrValueKind.Expression || attr.ValueSpan.Length == 0)
            return null;

        var expression = new CursorContext
        {
            Kind = CursorKind.Expression,
            TagName = context.TagName,
            AttributeName = "Ref",
            ExpressionStart = attr.ValueSpan.Start,
            ExpressionEnd = attr.ValueSpan.Start + attr.ValueSpan.Length,
            OpenForeaches = context.OpenForeaches,
            OpenTags = context.OpenTags,
        };
        return ExpressionDefinition(analysis, expression, attr.ValueSpan.Start);
    }

    /// <summary>
    /// <c>Class="…"</c> 的 F12：类名跳它的**定义处**。解析序与分析器一致（E2）：
    /// ① 本文件 <c>@style</c> 块的类规则（<see cref="Eustyle"/> 解析，NameSpan 经
    /// CodeSpan.Start 偏移已是 .ui 绝对 span，回跳本文档）；② 项目 <c>.eustyle</c>
    /// 全局样式表（AdditionalFiles 命中，跳文件内类名 span）。
    /// <c>StyleRegistry.Define</c> 在 C# 里定义的全局类没有稳定落点，返回 null 不猜。
    /// </summary>
    private static UiDefinition? StyleClassDefinition(
        DocumentAnalysis analysis, UiCatalog? catalog, CursorContext context)
    {
        if (analysis.Ast is null) return null;
        var properties = catalog?.StyleProperties ?? StylePropertyCatalog.Empty;

        // 光标在属性名上时取属性值里的类名序列；光标在值 token 上时取光标下的完整词
        // （TokenStart 只是补全前缀起点——curser 在词中间时 Prefix 是半截，跳转必须全名）。
        string className;
        if (context.Kind == CursorKind.AttributeValue)
        {
            className = TokenAt(analysis, context);
        }
        else
        {
            var attr = FindAttributeAt(analysis, context.TokenStart, "Class");
            if (attr is null || attr.Kind != AttrValueKind.Literal) return null;
            var names = attr.Value.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            // 光标在 Class= 本身：只有一个类名才跳，多个时不猜取哪一个。
            if (names.Length != 1) return null;
            className = names[0];
        }
        if (className.Length == 0) return null;

        foreach (var block in analysis.Ast.Nodes.OfType<UiStyleBlock>())
        {
            var rule = FindClassRule(block.Code, block.CodeSpan.Start, className, properties);
            if (rule is not null)
                return new UiDefinition(null, rule.NameSpan, className);
        }

        var project = analysis.Project;
        if (project is not null)
        {
            foreach (var file in project.Evaluation.AdditionalFiles)
            {
                if (!file.EndsWith(".eustyle", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    if (!File.Exists(file)) continue;
                    var text = File.ReadAllText(file);
                    var rule = FindClassRule(text, 0, className, properties);
                    if (rule is not null)
                        return new UiDefinition(Path.GetFullPath(file), rule.NameSpan, className, text);
                }
                catch (Exception)
                {
                    // 盘上读不了（被锁/被删）：跳过该来源，不终止。
                }
            }
        }
        return null;
    }

    /// <summary>
    /// <c>Style="P: v"</c> 值里的属性名 F12：跳该样式属性的**唯一事实源**——目录
    /// <see cref="StylePropertyDef"/> 记着 StyledProperty 字段全限定名与声明类型
    /// （含 StyleBuilder setter，如 CornerRadius），仍是符号查询。未知名（EUI051）与
    /// 值 token（18 / #FFF）返回 null，不猜。
    /// </summary>
    private static UiDefinition? StyleDeclarationDefinition(
        DocumentAnalysis analysis, UiCatalog catalog, CursorContext context, Compilation? compilation)
    {
        if (compilation is null) return null;
        var property = TokenAt(analysis, context);
        if (property.Length == 0 || !IsIdentifier(property)) return null;

        var def = catalog.StyleProperties.Find(property);
        if (def is null || def.PropertyField.Length == 0) return null;

        var fieldName = def.PropertyField[(def.PropertyField.LastIndexOf('.') + 1)..];
        if (fieldName.Length == 0) return null;
        var typeNames = new[] { def.DeclaringTypeName };
        var names = new[] { fieldName };

        foreach (var typeName in typeNames)
        {
            var type = FindTypeSymbol(compilation, typeName);
            if (type is null) continue;
            var member = FindMember(type, names);
            if (member is null) continue;
            var source = member.Locations.FirstOrDefault(static l => l.IsInSource);
            if (source is not null)
                return FromSourceLocation(analysis, source, member.Name)
                       ?? FromLocation(analysis, source, member.Name);
        }

        // 框架类型是元数据引用时：仓库里以源码编译同一类型的项目仍是符号查询；
        // 都不可达才反编译（字段名来自目录，是真实成员，反编译有意义）。
        var workspace = WorkspaceSourceDefinition(analysis, typeNames, names);
        if (workspace is not null) return workspace;
        return DecompileProvider.Definition(analysis, typeNames[0], names[0]);
    }

    /// <summary>光标下 token 的完整原文：TokenStart 是补全前缀起点，光标在词中间时 Prefix 是半截。</summary>
    private static string TokenAt(DocumentAnalysis analysis, CursorContext context)
    {
        var text = analysis.Text;
        var start = context.TokenStart;
        if (start < 0 || start >= text.Length) return "";
        var end = start;
        while (end < text.Length && (char.IsAsciiLetterOrDigit(text[end]) || text[end] is '_' or '-')) end++;
        return text[start..end];
    }

    /// <summary>样式表里的类规则：诊断丢弃（IDE 只借 span），spanOffset 保证 span 是宿主绝对坐标。
    /// 属性目录用真实目录（Empty 会让含未知属性的整条规则被解析器丢弃）。</summary>
    private static EustyleRule? FindClassRule(
        string stylesheet, int spanOffset, string className, StylePropertyCatalog properties)
    {
        var sheet = Eustyle.Parse(stylesheet, "", new List<UiDiagnostic>(),
            properties, spanOffset);
        return sheet.Rules.FirstOrDefault(r =>
            string.Equals(r.ClassName, className, StringComparison.Ordinal));
    }

    private static bool IsIdentifier(string text) =>
        char.IsAsciiLetter(text[0]) && text.All(static c => char.IsAsciiLetterOrDigit(c) || c == '_');

    /// <summary>按属性名 span 覆盖光标定位 AST 属性节点（递归遍历元素树，含 if/foreach/switch/slot）。</summary>
    private static UiAttribute? FindAttributeAt(DocumentAnalysis analysis, int offset, string name)
    {
        if (analysis.Ast is null) return null;
        foreach (var attr in WalkAttributes(analysis.Ast.Nodes))
        {
            if (attr.Name == name && attr.NameSpan.Start <= offset && offset <= attr.NameSpan.End)
                return attr;
        }
        return null;
    }

    private static IEnumerable<UiAttribute> WalkAttributes(IReadOnlyList<UiNode> nodes)
    {
        foreach (var node in nodes)
        {
            switch (node)
            {
                case UiElement element:
                    foreach (var attr in element.Attributes) yield return attr;
                    foreach (var attr in WalkAttributes(element.Children)) yield return attr;
                    break;
                case UiSlotNode slot:
                    foreach (var attr in slot.Attributes) yield return attr;
                    foreach (var attr in WalkAttributes(slot.Children)) yield return attr;
                    break;
                case UiContentSlotNode contentSlot:
                    foreach (var attr in contentSlot.Attributes) yield return attr;
                    break;
                case UiIf uiIf:
                    foreach (var attr in WalkAttributes(uiIf.Children)) yield return attr;
                    foreach (var elseIf in uiIf.ElseIfs)
                        foreach (var attr in WalkAttributes(elseIf.Children)) yield return attr;
                    if (uiIf.ElseChildren is not null)
                        foreach (var attr in WalkAttributes(uiIf.ElseChildren)) yield return attr;
                    break;
                case UiForeach uiForeach:
                    foreach (var attr in WalkAttributes(uiForeach.Children)) yield return attr;
                    break;
                case UiSwitch uiSwitch:
                    foreach (var section in uiSwitch.Sections)
                        foreach (var attr in WalkAttributes(section.Children)) yield return attr;
                    break;
            }
        }
    }

    /// <summary>
    /// F12 落在**成员**而非类型。目录已带够信息（UiPropDef.DeclaringTypeName / Name /
    /// PropertyFieldName，UiEventDef.EventName / FieldName，UiAttachedPropDef.SetterMethod），
    /// 所以只做类型符号查询再按名字取成员符号的 Locations；不扩正则去认成员声明
    /// （§6.9.0：那等于把第二事实源做得更深）。
    ///
    /// 三段落点：① 当前项目 Compilation 里的源符号 → 真实文件；② 当前项目只把该类型当
    /// 元数据引用（框架控件）→ 工作区里以源码编译同一类型的项目，仍是符号查询；
    /// ③ 都没有 → 成员级反编译兜底。
    /// </summary>
    private static UiDefinition? AttributeDefinition(
        DocumentAnalysis analysis, UiCatalog catalog, CursorContext context, Compilation? compilation)
    {
        var candidate = analysis.FindAttribute(catalog, context);
        if (candidate is null || compilation is null) return null;
        var owner = analysis.AttributeOwner(catalog, context);
        var typeNames = AttributeTypeNames(candidate, owner);
        if (typeNames.Count == 0) return null;

        var names = MemberNames(candidate);
        foreach (var typeName in typeNames)
        {
            var type = FindTypeSymbol(compilation, typeName);
            if (type is null) continue;
            var member = FindMember(type, names);
            if (member is null) continue;
            var source = member.Locations.FirstOrDefault(static l => l.IsInSource);
            if (source is null) continue;
            // 源码可达：真实文件（或 .ui 组件文件）；@code 内声明回映到本文档。
            return FromSourceLocation(analysis, source, member.Name)
                   ?? FromLocation(analysis, source, member.Name);
        }

        // 框架类型：当前项目是 dll 元数据引用，符号没有源码位置；仓库里另有源码项目。
        var workspace = WorkspaceSourceDefinition(analysis, typeNames, names);
        if (workspace is not null) return workspace;

        return DecompileProvider.Definition(analysis, typeNames[0], names.FirstOrDefault());
    }

    /// <summary>
    /// 候选声明类型：目录记的 DeclaringTypeName 优先（继承属性指向基类），其次元素自身类型；
    /// 附加属性用 OwnerClrName。全部是目录里的 CLR 全名，不做文本推断。
    /// </summary>
    private static IReadOnlyList<string> AttributeTypeNames(CandidateAttribute candidate, UiElementDef? owner)
    {
        var result = new List<string>(2);
        void Add(string? name)
        {
            if (!string.IsNullOrEmpty(name) && !result.Contains(name, StringComparer.Ordinal)) result.Add(name);
        }
        if (candidate.Context is UiAttachedPropDef attached) Add(attached.OwnerClrName);
        else
        {
            Add(candidate.DeclaringTypeName);
            Add(owner?.ClrTypeDisplayName);
        }
        return result;
    }

    /// <summary>按候选名找成员符号；继承成员沿 BaseType 上溯（不是只在当前类型上找）。</summary>
    private static ISymbol? FindMember(INamedTypeSymbol type, IReadOnlyList<string> names)
    {
        for (var current = type; current is not null; current = current.BaseType)
            foreach (var name in names)
            {
                if (name.Length == 0) continue;
                var member = current.GetMembers(name).FirstOrDefault();
                if (member is not null) return member;
            }
        return null;
    }

    /// <summary>
    /// L6 工作区源码回退：逐份项目 Compilation 用**元数据名**查同一类型，只有该类型在这份
    /// 编译里是源符号（Locations.IsInSource）才认；成员同样按符号名取声明位置。
    /// 命中不了就返回 null，交给反编译——不猜位置。
    /// </summary>
    private static UiDefinition? WorkspaceSourceDefinition(
        DocumentAnalysis analysis, IReadOnlyList<string> typeNames, IReadOnlyList<string>? memberNames)
    {
        var workspace = analysis.Workspace;
        if (workspace is null) return null;

        foreach (var typeName in typeNames)
        {
            var metadataName = MetadataName(typeName);
            if (metadataName.Length == 0) continue;
            foreach (var type in workspace.FindSourceTypes(metadataName))
            {
                var typeSource = type.Locations.FirstOrDefault(static l => l.IsInSource);
                if (typeSource is null) continue;

                if (memberNames is null)
                {
                    var hit = FromSourceLocation(analysis, typeSource, type.Name);
                    if (hit is not null) return hit;
                    continue;
                }

                var member = FindMember(type, memberNames);
                if (member is null) continue;
                var memberSource = member.Locations.FirstOrDefault(static l => l.IsInSource);
                if (memberSource is null) continue;
                var definition = FromSourceLocation(analysis, memberSource, member.Name);
                if (definition is not null) return definition;
            }
        }
        return null;
    }

    /// <summary>目录记录 → 候选 CLR 成员名，按"属性本身优先、StyledProperty 字段次选"排序。</summary>
    private static IReadOnlyList<string> MemberNames(CandidateAttribute candidate) =>
        candidate.Context switch
        {
            UiPropDef property => new[] { property.Name, property.PropertyFieldName },
            UiEventDef @event => new[] { @event.EventName, @event.FieldName, @event.AttributeName },
            UiParameterDef parameter => new[] { parameter.Name, parameter.ChangedCallbackName ?? "" },
            UiAttachedPropDef attached => new[]
            {
                attached.Name + "Property", attached.SetterMethod, attached.Name,
            },
            _ => new[] { candidate.Name },
        };

    /// <summary>目录里的 <c>global::Ns.Type</c> → Roslyn 元数据名。</summary>
    private static string MetadataName(string displayName) =>
        displayName.StartsWith("global::", StringComparison.Ordinal)
            ? displayName["global::".Length..]
            : displayName;

    /// <summary>目录里的 CLR 全名 → Roslyn 类型符号。</summary>
    private static INamedTypeSymbol? FindTypeSymbol(Compilation compilation, string displayName)
    {
        var full = MetadataName(displayName);
        return full.Length == 0 ? null : compilation.GetTypeByMetadataName(full);
    }

    /// <summary>
    /// 源码声明位置 → 落点。生成树中的作者代码按所属模板的映射表回到真实 .ui；
    /// 生成类声明没有对应的作者 span，回到 <c>{Component}.ui</c> 起始。
    /// </summary>
    private static UiDefinition? FromSourceLocation(DocumentAnalysis analysis, Location location, string name)
    {
        var tree = location.SourceTree;
        if (tree is null) return null;
        // Component parameters may be declared in another template's @code block.
        // Its generated tree has no disk file and cannot use the current document's
        // member mapping. Select the mapping belonging to the declaration's tree.
        if (analysis.Project is { } project)
        {
            foreach (var (uiPath, generated) in project.GeneratedDocuments)
            {
                if (!ReferenceEquals(generated.Tree, tree)) continue;
                if (!generated.Mappings.TryMapToOriginal(NameSpan(location), out var sourceSpan)) break;
                string? text = null;
                if (analysis.Workspace is { } workspace && workspace.TryBuffer(uiPath, out var buffer))
                    text = buffer;
                else if (File.Exists(uiPath))
                    text = File.ReadAllText(uiPath);
                return text is null ? null : new UiDefinition(Path.GetFullPath(uiPath), sourceSpan, name, text);
            }
        }
        var path = tree.FilePath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            var ui = analysis.Project?.Evaluation.AdditionalFiles.FirstOrDefault(f =>
                f.EndsWith(".ui", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Path.GetFileNameWithoutExtension(f), name, StringComparison.OrdinalIgnoreCase));
            if (ui is null || !File.Exists(ui)) return null;
            return new UiDefinition(Path.GetFullPath(ui), new TextSpan(0, 0), name, File.ReadAllText(ui));
        }
        return new UiDefinition(Path.GetFullPath(path), NameSpan(location), name, tree.GetText().ToString());
    }

    // ---- 标签名（L3 第四片） ----

    /// <summary>
    /// 标签名 → 目录里的 CLR 类型 → **源码扫描**找类型声明。落点优先级：
    /// 兄弟 code-behind（零 IO）→ 工作区 <c>{TypeName}.ui</c> 组件文件（跳文件起始）→
    /// 工作区 .cs → **框架兜底**（EclipseUI.* 类型——内置标签
    /// 的源码在 EclipseUI 仓库里，samples 这类子目录工作区够不着，从工作区向上找仓库根
    /// （<c>.git</c> / sln 标记）再扫）。多个命中优先"文件名 == 类型名"（组件惯例，spec/02）。
    /// 找不到就 null，不猜。
    /// </summary>
    private static UiDefinition? TagDefinition(DocumentAnalysis analysis, UiCatalog catalog, string tag,
        string? workspaceRoot, Compilation? compilation)
    {
        var element = analysis.ResolveAny(catalog, tag) ?? analysis.ResolveVisible(catalog, tag);
        if (element is null) return null;

        var displayName = element.ClrTypeDisplayName;

        // L6（§6.9.3）：项目 Compilation 的符号查询是标签跳转的唯一事实源——
        // GetTypeByMetadataName → Locations.FirstOrDefault(IsInSource) → 真实源码文件。
        // 正则扫文本（同名取第一个会跳错命名空间）只在没有项目 Compilation 的独立文档路径保留。
        if (analysis.Project is not null && compilation is not null)
        {
            var symbol = FindTypeSymbol(compilation, displayName);
            if (symbol is not null)
            {
                var source = symbol.Locations.FirstOrDefault(static l => l.IsInSource);
                if (source is not null)
                {
                    var hit = FromSourceLocation(analysis, source, symbol.Name);
                    if (hit is not null) return hit;
                }
            }
            // 框架控件在当前项目里是元数据引用（符号无源码位置），但仓库里另有以源码
            // 编译它的项目：仍是符号查询，不回退到正则扫文本。
            var workspaceSource = WorkspaceSourceDefinition(analysis, new[] { displayName }, null);
            if (workspaceSource is not null) return workspaceSource;
            return DecompileProvider.Definition(analysis, displayName);
        }

        var typeName = displayName[(displayName.LastIndexOf('.') + 1)..];
        if (typeName.Length == 0) return null;

        // 1) 兄弟 code-behind（已在内存里，零 IO）。
        if (analysis.CodeBehindText is not null && analysis.CodeBehindPath is not null)
        {
            var sibling = FindTypeDeclaration(analysis.CodeBehindText, typeName, analysis.CodeBehindPath);
            if (sibling is not null) return sibling;
        }

        // 1.5) 工作区 .ui 组件文件：项目里用 .ui 定义的控件（TaskList.ui 等）——
        //      组件的"定义"就是整个 .ui 文件，跳文件起始（用户口径：引用项目的控件直接跳文件）。
        if (workspaceRoot is not null && Directory.Exists(workspaceRoot))
        {
            var uiFile = FindUiComponentFile(workspaceRoot, typeName);
            if (uiFile is not null) return uiFile;
        }

        // 2) 工作区 .cs 扫描（引用的项目：控件源码就在工程里）。
        var workspace = workspaceRoot is not null && Directory.Exists(workspaceRoot)
            ? ScanForType(workspaceRoot, typeName)
            : null;
        if (workspace is not null) return workspace;

        // 3) 框架类型兜底：内置标签的声明在 EclipseUI 仓库源码里，不在 samples/Gallery
        //    这类子目录工作区里——向上找仓库根再扫。只对 EclipseUI.* 类型做（用户自己的
        //    标签继续以工作区为准，不越界）。
        if (workspaceRoot is not null && IsFrameworkType(displayName))
        {
            var repositoryRoot = FindRepositoryRoot(workspaceRoot);
            var repository = repositoryRoot is null ? null : ScanForType(repositoryRoot, typeName);
            if (repository is not null) return repository;
        }

        // 4) 反编译兜底（dll / nuget 引用：源码不可达，只剩元数据——ILSpy 引擎出 C#，
        //    挂 eui-decompiled: 虚拟文档，客户端内容提供器取文本）。
        return DecompileProvider.Definition(analysis, displayName);
    }

    /// <summary>框架类型（EclipseUI.* 程序集，目录名带 <c>global::</c> 前缀）。</summary>
    private static bool IsFrameworkType(string displayName) =>
        displayName.StartsWith("global::EclipseUI.", StringComparison.Ordinal)
        || displayName.StartsWith("EclipseUI.", StringComparison.Ordinal);

    /// <summary>从工作区向上找仓库根（<c>.git</c> 目录或 <c>*.sln*</c> 标记）。</summary>
    private static string? FindRepositoryRoot(string workspaceRoot)
    {
        for (var current = new DirectoryInfo(workspaceRoot); current is not null; current = current.Parent)
        {
            if (!LooksLikeRepositoryRoot(current.FullName)) continue;
            // 仓库根找到了但类型没找到就到此为止：不再向上（防止一路扫到盘根）。
            return current.FullName;
        }
        return null;
    }

    private static bool LooksLikeRepositoryRoot(string directory)
    {
        try
        {
            return Directory.Exists(Path.Combine(directory, ".git"))
                || Directory.EnumerateFiles(directory, "*.sln*").Any();
        }
        catch (Exception)
        {
            // 不可读的祖先目录（权限/挂载点）：当它不是仓库根，继续向上。
            return false;
        }
    }

    /// <summary>
    /// 工作区里的 <c>{TypeName}.ui</c> 组件文件（spec/02：文件即组件）。栈式遍历（跳 bin/obj），
    /// 存在即命中。跳转落在文件起始（span 0,0）；<see cref="UiDefinition.Text"/> 带上原文，
    /// 协议层的行位换算与偏移同源。
    /// </summary>
    private static UiDefinition? FindUiComponentFile(string workspaceRoot, string typeName)
    {
        var pending = new Stack<string>();
        pending.Push(workspaceRoot);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(directory))
                {
                    if (RenameProvider.SkippedDirectories.Contains(Path.GetFileName(sub))) continue;
                    pending.Push(sub);
                }
                var file = Directory.EnumerateFiles(directory, typeName + ".ui").FirstOrDefault();
                if (file is null) continue;
                var text = File.ReadAllText(file);
                return new UiDefinition(file, new TextSpan(0, 0), typeName, text);
            }
            catch (Exception)
            {
                // 权限/并发删除：跳过该目录，不终止整个扫描。
            }
        }
        return null;
    }

    /// <summary>目录树里找类型声明：文件名 == 类型名优先，否则按扫描顺序取第一个。</summary>
    private static UiDefinition? ScanForType(string root, string typeName)
    {
        UiDefinition? byOrder = null;
        UiDefinition? byFileName = null;
        var files = EnumerateWorkspaceFiles(root);
        if (files is null) return null;

        foreach (var file in files)
        {
            string text;
            try { text = File.ReadAllText(file); } catch (Exception) { continue; }
            var match = FindTypeDeclaration(text, typeName, file);
            if (match is null) continue;
            byOrder ??= match;
            if (string.Equals(Path.GetFileNameWithoutExtension(file), typeName, StringComparison.Ordinal))
                byFileName ??= match;
        }

        return byFileName ?? byOrder;
    }
    /// <summary>单个文本里的类型声明（词边界正则）。命中多个同名声明取第一个。
    /// <see cref="UiDefinition.Text"/> 带上原文：协议层换行位与偏移同源，不会错位。</summary>
    private static UiDefinition? FindTypeDeclaration(string text, string typeName, string path)
    {
        var span = FindTypeDeclarationSpan(text, typeName);
        return span is null ? null : new UiDefinition(path, span.Value, typeName, text);
    }

    /// <summary>类型声明的名字区间（词边界正则，第一个命中）；反编译文本同用。</summary>
    internal static TextSpan? FindTypeDeclarationSpan(string text, string typeName)
    {
        foreach (Match match in TypeDeclarationRegex.Matches(text))
        {
            if (!string.Equals(match.Groups["name"].Value, typeName, StringComparison.Ordinal)) continue;
            var group = match.Groups["name"];
            return new TextSpan(group.Index, group.Length);
        }
        return null;
    }

    /// <summary>工作区 .cs 枚举（跳过 bin/obj 等，上限 <see cref="MaxWorkspaceFiles"/>）。</summary>
    private static List<string>? EnumerateWorkspaceFiles(string workspaceRoot)
    {
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(workspaceRoot);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(directory))
                {
                    if (RenameProvider.SkippedDirectories.Contains(Path.GetFileName(sub))) continue;
                    pending.Push(sub);
                }
                foreach (var file in Directory.EnumerateFiles(directory, "*.cs"))
                {
                    files.Add(file);
                    if (files.Count > MaxWorkspaceFiles) return null;
                }
            }
            catch (Exception)
            {
                // 权限/并发删除：跳过该目录，不终止整个扫描。
            }
        }
        return files;
    }

    // ---- 公共：符号声明位置 → 落点三路 ----

    /// <summary>
    /// C# 岛（<c>{expr}</c> / <c>@code</c>）符号的统一落点，与标签/属性跳转同一口径：
    /// ① 有源码位置 → 本文档三路（兄弟 code-behind / @code 回映，<see cref="FromLocation"/>）
    /// 再任意真实源码文件（<see cref="FromSourceLocation"/>）；② 无源码位置 = 元数据符号
    /// （引用程序集里的类型/成员）→ 跨项目源码（<see cref="WorkspaceSourceDefinition"/>）
    /// 再反编译（<see cref="DecompileProvider"/>）。局部/参数恒有源码位置，走①映不回即 null，
    /// 不会落到②拿 ContainingType 误跳。找不到真实位置就 null，不猜（design/06 §6.3）。
    /// </summary>
    private static UiDefinition? SymbolDefinition(DocumentAnalysis analysis, ISymbol symbol)
    {
        var source = symbol.Locations.FirstOrDefault(static l => l.IsInSource);
        if (source is not null)
            return FromLocation(analysis, source, symbol.Name)
                   ?? FromSourceLocation(analysis, source, symbol.Name);

        var type = (symbol as INamedTypeSymbol ?? symbol.ContainingType)?.OriginalDefinition;
        if (type is null) return null;
        var displayName = MetadataFullName(type);
        if (displayName.Length == 0) return null;
        var memberName = symbol is INamedTypeSymbol ? null : symbol.Name;

        var workspace = WorkspaceSourceDefinition(analysis, new[] { displayName },
            memberName is null ? null : new[] { memberName });
        return workspace ?? DecompileProvider.Definition(analysis, displayName, memberName);
    }

    /// <summary>
    /// 类型符号 → Roslyn 元数据全名（<c>GetTypeByMetadataName</c> 口径）：命名空间点分 +
    /// 嵌套用 <c>+</c> 连接 + 泛型 arity 后缀。<see cref="INamedTypeSymbol.MetadataName"/> 已含
    /// <c>`1</c> 但不含命名空间与外层类型，需自行拼（实测 <c>N.Outer`1+Inner</c> 可往返）。
    /// </summary>
    private static string MetadataFullName(INamedTypeSymbol type)
    {
        var parts = new Stack<string>();
        for (INamedTypeSymbol? current = type; current is not null; current = current.ContainingType)
            parts.Push(current.MetadataName);
        var typeName = string.Join("+", parts);
        var ns = type.ContainingNamespace;
        return ns is null || ns.IsGlobalNamespace ? typeName : ns.ToDisplayString() + "." + typeName;
    }

    private static UiDefinition? FromLocation(DocumentAnalysis analysis, Location? location, string name)
    {
        if (location?.SourceTree is null) return null;

        // 兄弟 code-behind：跳 .cs。
        if (analysis.IsCodeBehindTree(location.SourceTree))
        {
            if (analysis.CodeBehindPath is null) return null;
            return new UiDefinition(analysis.CodeBehindPath, NameSpan(location), name);
        }

        // @code 成员：声明写在 .ui 自己的 @code 块里——真实文本，回映过去。
        if (analysis.TryMapMemberSpanToUi(NameSpan(location), out var uiSpan))
            return new UiDefinition(null, uiSpan, name);

        return null;
    }

    /// <summary>声明节点的名字区间（导航落在标识符上，而不是整个属性/方法声明）。</summary>
    private static TextSpan NameSpan(Location location)
    {
        var node = location.SourceTree!.GetRoot().FindNode(location.SourceSpan);
        TextSpan Of(Microsoft.CodeAnalysis.Text.TextSpan span) => new(span.Start, span.Length);
        return node switch
        {
            PropertyDeclarationSyntax property => Of(property.Identifier.Span),
            MethodDeclarationSyntax method => Of(method.Identifier.Span),
            FieldDeclarationSyntax field => field.Declaration.Variables.FirstOrDefault() is { } variable
                ? Of(variable.Identifier.Span)
                : Of(node.Span),
            VariableDeclaratorSyntax variable => Of(variable.Identifier.Span),
            EventDeclarationSyntax @event => Of(@event.Identifier.Span),
            TypeDeclarationSyntax type => Of(type.Identifier.Span),
            ParameterSyntax parameter => Of(parameter.Identifier.Span),
            _ => Of(node.Span),
        };
    }
}

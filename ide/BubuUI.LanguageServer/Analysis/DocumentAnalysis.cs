using System.Collections.Immutable;
using BubuUI.Compiler;
using BubuUI.Compiler.Metadata;
using BubuUI.Compiler.Probe;
using BubuUI.LanguageServer.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BubuUI.LanguageServer.Analysis;

/// <summary>
/// 打开文档的全文镜像。v1 只做**全文同步**（<c>TextDocumentSyncKind.Full</c>）：
/// .bui 文件普遍在几 KB，全量重解析比维护增量编辑映射可靠得多，
/// 也不会出现"编辑器增量与解析器位置错位"这类最难查的 bug。
/// </summary>
public sealed class DocumentStore
{
    public sealed record Document(string Uri, string FileName, string Text, int Version);

    private readonly Dictionary<string, Document> _documents = new(StringComparer.Ordinal);

    public void Open(string uri, string text, int version) =>
        _documents[uri] = Create(uri, text, version);

    public bool Change(string uri, string text, int version)
    {
        if (!_documents.ContainsKey(uri)) return false;
        _documents[uri] = Create(uri, text, version);
        return true;
    }

    public void Close(string uri) => _documents.Remove(uri);

    public bool TryGet(string uri, out Document document) => _documents.TryGetValue(uri, out document!);

    public IReadOnlyCollection<Document> All => _documents.Values;

    private static Document Create(string uri, string text, int version)
    {
        var path = UriToPath(uri);
        var fileName = Path.GetFileName(path);
        return new Document(uri, string.IsNullOrEmpty(fileName) ? "Template.bui" : fileName, text, version);
    }

    /// <summary>
    /// <c>file:///d%3A/…</c> → 本地路径；非 file 协议原样返回（测试里常用 <c>untitled:</c>）。
    ///
    /// **不能用 <see cref="Uri.LocalPath"/>**：VS Code 发来的 URI 把盘符冒号编码成
    /// <c>%3A</c>（<c>file:///d%3A/repo/x.bui</c>）。一旦路径里有转义序列，.NET 就不再把
    /// 它当 Windows 本地路径，<c>LocalPath</c> 返回 <c>/d:/repo/x.bui</c>——前导斜杠 + 正
    /// 斜杠。再经过 <see cref="Path.GetFullPath(string)"/> 就变成一个永远匹配不到项目
    /// <c>AdditionalFiles</c> 的路径，于是文档「不属于任何项目」，补全/诊断/跳转全空。
    /// （2026-11-11 实测：手写 <c>file:///D:/…</c> 的脚本全绿、真实编辑器全废，就是这个分支。）
    ///
    /// 所以自己解一遍：先按 URI 语义解转义，再按盘符/UNC 还原 Windows 路径；
    /// macOS/Linux 本地绝对路径保留 '/'，供文件系统和项目归属判断使用。
    /// </summary>
    public static string UriToPath(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || !parsed.IsFile)
            return uri;

        var path = Uri.UnescapeDataString(parsed.AbsolutePath);
        if (path.Length == 0) return uri;

        // UNC：file://server/share/x → \\server\share\x（Host 非空且不是 localhost）
        if (parsed.Host.Length > 0 && !string.Equals(parsed.Host, "localhost", StringComparison.OrdinalIgnoreCase))
            return @"\\" + parsed.Host + path.Replace('/', '\\');

        // 盘符路径可能来自其他平台上的编辑器，仍按 Windows 语义解析。
        // 本机 Unix 路径则不能替换分隔符，否则文件名和项目归属都会损坏。
        if (path[0] == '/' && path.Length >= 3 && char.IsAsciiLetter(path[1]) && path[2] == ':')
            path = path[1..];
        var windowsDrive = path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':';
        return OperatingSystem.IsWindows() || windowsDrive ? path.Replace('/', '\\') : path;
    }

    public static string PathToUri(string path) =>
        new Uri(Path.GetFullPath(path)).AbsoluteUri;
}

/// <summary>
/// 一个 <c>.bui</c> 文档的分析结果：AST + 诊断 + 本文件的 <c>@using</c> 可见性信息。
/// 诊断走与编译期同一条前端（<see cref="FrontEnd"/>），所以 IDE 红波浪线和
/// <c>dotnet build</c> 报的是同一批 EUI 码、同一批 span。
/// </summary>
public sealed class DocumentAnalysis
{
    private readonly Compilation? _baseCompilation;
    private readonly ProjectSnapshot? _project;
    private readonly ProjectWorkspace? _workspace;
    private readonly string? _codeBehind;
    private readonly string? _codeBehindPath;
    private readonly string? _documentPath;
    private SyntaxTree? _codeBehindTree;
    private SyntaxTree? _memberTree;
    private ExpressionProbe? _probe;
    private bool _probeFailed;

    private DocumentAnalysis(
        string text,
        PositionMap positions,
        UiDocument? ast,
        IReadOnlyList<UiDiagnostic> diagnostics,
        IReadOnlyList<string> imports,
        IReadOnlyList<string> enclosingNamespaces,
        IReadOnlyDictionary<string, string?> aliases,
        string? internalError,
        Compilation? baseCompilation,
        string? codeBehind,
        string? codeBehindPath,
        ProjectSnapshot? project,
        ProjectWorkspace? workspace,
        string? documentPath)
    {
        Text = text;
        Positions = positions;
        Ast = ast;
        Diagnostics = diagnostics;
        Imports = imports;
        EnclosingNamespaces = enclosingNamespaces;
        Aliases = aliases;
        InternalError = internalError;
        _baseCompilation = baseCompilation;
        _codeBehind = codeBehind;
        _codeBehindPath = codeBehindPath;
        _project = project;
        _workspace = workspace;
        _documentPath = documentPath;
    }

    public string Text { get; }

    public PositionMap Positions { get; }

    public UiDocument? Ast { get; }

    public IReadOnlyList<UiDiagnostic> Diagnostics { get; }

    /// <summary>本文件 <c>@using</c> 导入的命名空间（标签可见性过滤，K22）。</summary>
    public IReadOnlyList<string> Imports { get; }

    /// <summary>
    /// <c>@namespace</c> 的 C# 名称查找链，最内层在前（K33 / D111）。这一链**不需要 @using**
    /// 就可见，且按 C# 规则优先于 <see cref="Imports"/>；未声明 <c>@namespace</c> 时为空。
    /// </summary>
    public IReadOnlyList<string> EnclosingNamespaces { get; }

    /// <summary>
    /// 命名空间对标签是否可见（K22 + K33）：全局命名空间恒可见，<c>@namespace</c> 链内的命名空间
    /// 隐式可见，其余必须已 <c>@using</c> 导入。补全的 <c>needs @using</c> 与解析共用这一条判定。
    /// </summary>
    public bool IsNamespaceVisible(string? ns) =>
        ns is null
        || Imports.Contains(ns, StringComparer.Ordinal)
        || EnclosingNamespaces.Contains(ns, StringComparer.Ordinal);

    /// <summary><c>@using Alias = Ns.Type</c> 的别名表（K31）；非法别名值为 null。</summary>
    public IReadOnlyDictionary<string, string?> Aliases { get; }

    /// <summary>前端自身抛异常时的消息（正常路径恒为 null）。</summary>
    public string? InternalError { get; }

    /// <summary>code-behind 兄弟文件的磁盘路径（spec/02 §2.2）；内存态分析（测试）为 null。</summary>
    public string? CodeBehindPath => _codeBehindPath;

    /// <summary>code-behind 源码原文（与探针编译里那棵树逐字对应）；跳转定义换算行位用。</summary>
    public string? CodeBehindText => _codeBehind;

    /// <summary>
    /// code-behind 兄弟文件的语法树（已在探针编译里，可直接取语义模型）。
    /// 重命名扫成员声明与引用用；无 code-behind 时 null。
    /// </summary>
    public SyntaxTree? CodeBehindTree => _codeBehindTree;

    /// <summary>
    /// <c>@code</c> 块合成出的成员树（块体原文透传，K21）。重命名扫 <c>@code</c> 内的
    /// 成员引用用：块体在树文本里逐字出现，<c>IndexOf</c> 定位后加 <see cref="Compiler.TextSpan"/>
    /// 偏移即回映到 .bui。无 <c>@code</c> 时 null；该树已在统一探针编译中。
    /// </summary>
    public SyntaxTree? MemberTree => _memberTree;

    public static DocumentAnalysis Create(string text, string fileName, UiCatalog catalog,
        Compilation? baseCompilation = null, string? codeBehindText = null, string? codeBehindPath = null,
        ProjectSnapshot? project = null, ProjectWorkspace? workspace = null, string? documentPath = null)
    {
        var positions = new PositionMap(text);
        var diagnostics = new List<UiDiagnostic>();

        UiDocument? ast;
        try
        {
            ast = FrontEnd.Parse(text, fileName, diagnostics);
        }
        catch (Exception ex)
        {
            return new DocumentAnalysis(text, positions, null,
                diagnostics.ToArray(), Array.Empty<string>(), Array.Empty<string>(),
                new Dictionary<string, string?>(), ex.Message, baseCompilation, codeBehindText, codeBehindPath, project, workspace, documentPath);
        }

        var imports = new List<string>();
        var aliases = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var node in ast.Nodes)
        {
            if (node is not UiUsingDirective directive) continue;
            if (directive.Alias is { Length: > 0 } alias)
            {
                aliases[alias] = directive.AliasTarget.Length == 0
                    ? null
                    : ToDisplayName(directive.AliasTarget);
            }
            else if (directive.Namespace.Length > 0 && !imports.Contains(directive.Namespace, StringComparer.Ordinal))
            {
                imports.Add(directive.Namespace);
            }
        }

        string? internalError = null;
        // 目录为空（没找到 Runtime）时不做语义检查：否则每个标签都会报 EUI001，
        // 把"没配好 IDE"伪装成"代码全错"。语法诊断照报。
        if (catalog.Elements.Count > 0)
        {
            try
            {
                FrontEnd.Analyze(ast, catalog, diagnostics);
            }
            catch (Exception ex)
            {
                internalError = ex.Message;
            }
        }

        return new DocumentAnalysis(text, positions, ast, diagnostics.ToArray(), imports,
            TemplateAnalyzer.EnclosingNamespaces(ast.Namespace), aliases,
            internalError, baseCompilation, codeBehindText, codeBehindPath, project, workspace, documentPath);
    }

    /// <summary>
    /// 表达式探针（design/06 §6.3）：懒建、每文档缓存。项目编译或独立文档的统一符号视图
    /// 包含根继承、code-behind 与 @code；通过同一个 ExpressionProbe 查询。
    /// 找不到或解析不了时停用表达式能力，保留语法和目录服务。
    /// </summary>
    public ExpressionProbe? Probe
    {
        get
        {
            if (_probe is null && !_probeFailed) _probe = BuildProbe();
            return _probe;
        }
    }

    /// <summary>
    /// 这棵语法树是不是 code-behind 兄弟文件的原文（跳转定义的"可跳"判据）：
    /// 只有它带真实磁盘路径；<c>@code</c> 成员树是合成的，声明位置没有可去的文件，
    /// 符号落在那里就返回 null 而不是给出错位置。
    /// </summary>
    public bool IsCodeBehindTree(SyntaxTree tree) =>
        ReferenceEquals(tree, _codeBehindTree)
        || (_project is not null && _codeBehindPath is not null
            && string.Equals(tree.FilePath, _codeBehindPath, StringComparison.OrdinalIgnoreCase));

    /// <summary>当前分析所依附的项目快照；null = 独立程序集目录 / 内存态分析。</summary>
    public ProjectSnapshot? Project => _project;

    /// <summary>The current project/reference symbols, available even while template emission fails.</summary>
    public Compilation? Compilation => _project?.Compilation ?? _baseCompilation;

    /// <summary>工作区内的全部项目快照（L6 跨项目源码符号查询用）。</summary>
    public ProjectWorkspace? Workspace => _workspace;

    /// <summary>
    /// 本 .bui 的生成文档（design/18 §5.2）：项目模式且发射时记下了旁路映射才有；
    /// 独立程序集目录 / 内存态分析为 null。键按完整路径归一（与 GeneratorHost 同口径）。
    /// </summary>
    public GeneratedDocument? GeneratedDocument =>
        _project is not null && _documentPath is not null && _documentPath.Length > 0
        && Path.IsPathRooted(_documentPath)
        && _project.GeneratedDocuments.TryGetValue(Path.GetFullPath(_documentPath), out var document)
            ? document
            : null;

    /// <summary>统一双向映射服务（编辑类功能唯一入口）；无生成文档时 null。</summary>
    public DocumentMappingService? Mappings => GeneratedDocument?.Mappings;

    /// <summary>
    /// <c>@code</code> 合成树里的 span → .bui 文档偏移。块体在树文本里逐字透传（K21），
    /// <see cref="string.IndexOf(string, StringComparison)"/> 定位所在块后按偏移差回映——
    /// 与重命名的引用回映（RenameProvider）同一公式。span 不落在任何块体内返回 false。
    /// 跳转定义用它把 @code 成员声明落到 .bui 的真实文本上（而不是"合成树没有可去文件"）。
    /// </summary>
    public bool TryMapMemberSpanToUi(TextSpan span, out TextSpan uiSpan)
    {
        uiSpan = default;
        if (_memberTree is null || Ast is null) return false;
        // design/18 §5.2：项目模式优先走统一映射服务（发射时记录，不做文本搜索）。
        // 映射表就是这棵树的权威坐标；映不回即 false，不再退回 IndexOf 猜。
        if (GeneratedDocument is { } generated && ReferenceEquals(generated.Tree, _memberTree))
            return generated.Mappings.TryMapToOriginal(span, out uiSpan);
        var treeText = _memberTree.ToString();
        foreach (var block in Ast.Nodes.OfType<UiCodeBlock>())
        {
            var bodyStart = treeText.IndexOf(block.Code, StringComparison.Ordinal);
            if (bodyStart < 0) continue;
            if (span.Start < bodyStart || span.Start + span.Length > bodyStart + block.Code.Length) continue;
            uiSpan = new TextSpan(block.CodeSpan.Start + (span.Start - bodyStart), span.Length);
            return true;
        }
        return false;
    }

    private ExpressionProbe? BuildProbe()
    {
        if (_baseCompilation is null || Ast is null)
        {
            _probeFailed = true;
            return null;
        }

        try
        {
            var usings = TemplateAnalyzer.CollectUsingLines(Ast.Nodes).ToImmutableArray();
            var compilation = _project?.Compilation ?? _baseCompilation;
            var probeClassName = Ast.ClassName + (Ast.TemplateTarget is null ? "" : "__TemplateView");

            // ---- 项目模式（L2/L3）：只查符号，不加树 ----
            // 项目 Compilation 已含 code-behind 源码、@code 生成树、.bui 派生组件。
            // 再 AddSyntaxTrees 会造成 partial 类成员重复（CS0111）与类型歧义。
            if (_project is not null)
            {
                var tree = FindProbeTree(compilation, probeClassName, _codeBehindPath);
                var projectType = tree is null ? null : FindTypeSymbol(compilation, tree, probeClassName);
                var projectNamespace = projectType?.ContainingNamespace is { IsGlobalNamespace: false } ns
                    ? ns.ToDisplayString()
                    : "";
                _codeBehindTree = _codeBehindPath is null
                    ? null
                    : compilation.SyntaxTrees.FirstOrDefault(t =>
                        string.Equals(t.FilePath, _codeBehindPath, StringComparison.OrdinalIgnoreCase));
                // @code 块体在生成树里逐字透传（Emitter.EmitCodeBlock）——用它做 span 回映，
                // 但绝不把它再次加进编译。
                _memberTree = FindGeneratedCodeTree(_project.GeneratedTrees, Ast);
                return new ExpressionProbe(compilation, projectType, probeClassName, projectNamespace, usings);
            }

            // 独立文档也使用与编译器相同的根类型符号视图。code-behind 是该输入编译的普通源码，
            // @code 和根继承只由 WithTemplateTypes 一次性补全，不再另造成员树。
            INamedTypeSymbol? sourceType = null;
            if (!string.IsNullOrEmpty(_codeBehind))
            {
                var parseOptions = (_baseCompilation.SyntaxTrees.FirstOrDefault()?.Options as CSharpParseOptions)
                    ?? CSharpParseOptions.Default;
                var codeBehindTree = CSharpSyntaxTree.ParseText(_codeBehind!, parseOptions,
                    path: _codeBehindPath ?? "");
                _codeBehindTree = codeBehindTree;
                compilation = _baseCompilation.AddSyntaxTrees(codeBehindTree);
                var model = compilation.GetSemanticModel(codeBehindTree);
                foreach (var sourceDeclaration in codeBehindTree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())
                {
                    if (sourceDeclaration.Identifier.ValueText != Ast.ClassName) continue;
                    sourceType = model.GetDeclaredSymbol(sourceDeclaration);
                    break;
                }
            }

            // 编写中的文档可尚未输入 @namespace 或根标签；保留已有 C# 命名空间，缺根时使用
            // UiObject 作为临时基类。此容错只影响编辑视图，构建期语法诊断仍然保留。
            var namespaceName = Ast.Namespace ??
                (sourceType?.ContainingNamespace is { IsGlobalNamespace: false } sourceNamespace
                    ? sourceNamespace.ToDisplayString() : "");
            var declaration = UiTemplateScanner.FromFile(Ast.FileName, Text);
            if (declaration is null) return null;
            declaration = declaration with
            {
                Namespace = namespaceName,
                RootTag = declaration.RootTag ?? "global::BubuUI.UiObject",
            };
            compilation = UiCatalogFactory.WithTemplateTypes(compilation, new[] { declaration });
            var className = Ast.ClassName + (Ast.TemplateTarget is null ? "" : "__TemplateView");
            var metadataName = namespaceName.Length == 0 ? className : namespaceName + "." + className;
            var type = compilation.GetTypeByMetadataName(metadataName);
            _memberTree = compilation.SyntaxTrees.FirstOrDefault(t =>
                t.FilePath == declaration.Path + ".catalog.cs" && declaration.CodeBlocks.Count > 0);
            return new ExpressionProbe(compilation, type, className, namespaceName, usings);
        }
        catch (Exception)
        {
            // code-behind 半截/探针建不起来：表达式能力静默降级，诊断与目录补全不受影响。
            _probeFailed = true;
            return null;
        }
    }

    /// <summary>项目编译里找承载该组件类的语法树：优先 code-behind 兄弟文件，其次任意同名 partial 类。</summary>
    private static SyntaxTree? FindProbeTree(Compilation compilation, string className, string? codeBehindPath)
    {
        if (codeBehindPath is not null)
        {
            var codeBehind = compilation.SyntaxTrees.FirstOrDefault(t =>
                string.Equals(t.FilePath, codeBehindPath, StringComparison.OrdinalIgnoreCase));
            if (codeBehind is not null) return codeBehind;
        }
        return compilation.SyntaxTrees.FirstOrDefault(t => t.GetRoot()
            .DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Any(c => c.Identifier.ValueText == className));
    }

    private static INamedTypeSymbol? FindTypeSymbol(Compilation compilation, SyntaxTree tree, string className)
    {
        var model = compilation.GetSemanticModel(tree);
        foreach (var declaration in tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())
            if (declaration.Identifier.ValueText == className)
                return model.GetDeclaredSymbol(declaration);
        return null;
    }

    /// <summary>生成树里含 @code 块体原文的那棵（块体逐字透传，可做 IndexOf 回映）。</summary>
    private static SyntaxTree? FindGeneratedCodeTree(IReadOnlyList<SyntaxTree> trees, UiDocument ast)
    {
        var blocks = ast.Nodes.OfType<UiCodeBlock>().Where(static b => b.Code.Length > 0).ToArray();
        if (blocks.Length == 0) return null;
        foreach (var tree in trees)
        {
            var text = tree.ToString();
            if (blocks.Any(b => text.Contains(b.Code, StringComparison.Ordinal))) return tree;
        }
        return null;
    }

    /// <summary><c>@using</c> 别名目标归一化为目录键（<c>global::</c> 前缀，无则为原样全名）。</summary>
    private static string ToDisplayName(string aliasTarget) =>
        aliasTarget.StartsWith("global::", StringComparison.Ordinal) ? aliasTarget : "global::" + aliasTarget;

    /// <summary>按可见性解析标签（别名 → 目录 tag，<c>@namespace</c> 链 + @using 过滤）。</summary>
    public UiElementDef? ResolveVisible(UiCatalog catalog, string tag)
    {
        if (tag.Length == 0) return null;
        if (Aliases.TryGetValue(tag, out var target) && target is not null)
            return catalog.FindByTypeDisplayName(target);
        return catalog.Resolve(tag, Imports, EnclosingNamespaces).Element;
    }

    /// <summary>不看可见性（悬停用：名字写错了也要能解释它是什么，补全则按可见性给提示）。</summary>
    public UiElementDef? ResolveAny(UiCatalog catalog, string tag)
    {
        if (tag.Length == 0) return null;
        if (Aliases.TryGetValue(tag, out var target) && target is not null)
            return catalog.FindByTypeDisplayName(target);
        return catalog.Resolve(tag).Element;
    }

    public UiElementDef? AttributeOwner(UiCatalog catalog, CursorContext context) =>
        ResolveVisible(catalog, context.TagName) ?? ResolveAny(catalog, context.TagName);

    /// <summary>把目录里的属性/事件/参数/附加属性名摊平，供"当前元素可写什么"的补全与悬停查询。</summary>
    public CandidateAttribute? FindAttribute(UiCatalog catalog, CursorContext context)
    {
        var element = AttributeOwner(catalog, context);
        var name = context.AttributeName;
        if (element is null || name.Length == 0) return null;

        var bare = name.StartsWith("@bind-", StringComparison.Ordinal)
            ? name.Substring("@bind-".Length)
            : name;

        if (element.Properties.TryGetValue(bare, out var property))
            return new CandidateAttribute(property.Name, CandidateAttributeKind.Property,
                TypeNames.Short(property.ClrTypeDisplayName), property.TypeShape,
                property.DeclaringTypeName, property.CanWrite, Context: property);

        if (element.Parameters.TryGetValue(bare, out var parameter))
            return new CandidateAttribute(parameter.Name, CandidateAttributeKind.Parameter,
                TypeNames.Short(parameter.ClrTypeDisplayName), parameter.TypeShape,
                element.ClrTypeDisplayName, parameter.CanWrite, Context: parameter);

        if (element.Events.TryGetValue(bare, out var @event))
            return new CandidateAttribute(@event.AttributeName, CandidateAttributeKind.Event,
                @event.ArgsTypeName, null, @event.DeclaringTypeName, CanWrite: false, Context: @event);

        int dot = bare.IndexOf('.');
        if (dot > 0)
        {
            var attached = catalog.FindAttached(bare.Substring(0, dot), bare.Substring(dot + 1));
            if (attached is not null)
                return new CandidateAttribute($"{attached.OwnerTag}.{attached.Name}",
                    CandidateAttributeKind.AttachedProperty, TypeNames.Short(attached.ClrTypeDisplayName),
                    attached.TypeShape, attached.OwnerClrName, CanWrite: true, Context: attached);
        }

        return null;
    }
}

public enum CandidateAttributeKind
{
    Property,
    Event,
    Parameter,
    AttachedProperty,
}

public sealed record CandidateAttribute(
    string Name,
    CandidateAttributeKind Kind,
    string TypeDisplay,
    UiTypeShape? Shape,
    string DeclaringTypeName,
    bool CanWrite,
    object? Context);

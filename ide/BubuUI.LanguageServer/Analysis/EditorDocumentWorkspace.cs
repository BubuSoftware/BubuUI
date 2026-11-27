using System.Runtime.CompilerServices;
using BubuUI.LanguageServer.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Host.Mef;

namespace BubuUI.LanguageServer.Analysis;

/// <summary>
/// design/18 §5.1 路线 A：每个项目快照一份 <see cref="AdhocWorkspace"/>，让生成 C# 拥有
/// Roslyn <c>Document</c> 身份，从而调得到 <c>CompletionService</c>/<c>Renamer</c>。
///
/// 构造直接以 <see cref="ProjectSnapshot.Compilation"/> 为事实源：同一批语法树（源 .cs 已含
/// 编辑器缓冲区文本、GlobalUsings、生成树）、同一套 ParseOptions/CompilationOptions/引用——
/// 「workspace 与 L2 编译一致」（§7.3）由构造保证，不另起一份求值。每棵树只加一次文档：
/// 生成树不再作为源文档重复加入（partial 成员不重复）。
///
/// 缓存按快照引用（<see cref="ConditionalWeakTable{TKey,TValue}"/>）：快照不可变，缓冲区
/// 变化时 ProjectWorkspace 整体重建快照 → 新快照自然命中新 workspace；快照被丢弃后
/// workspace 随之回收。创建后不再写 workspace，读取一律走不可变 Solution 快照，无需写锁。
/// </summary>
public sealed class EditorDocumentWorkspace
{
    private static readonly ConditionalWeakTable<ProjectSnapshot, EditorDocumentWorkspace> Cache = new();

    // 4.9.2 的 DefaultAssemblies 已含 Features/CSharp.Features（缺的跳过）；
    // 包引用把这两份 dll 带到输出目录，CompletionService.GetService 才组合得到 provider。
    private static readonly Lazy<MefHostServices> HostServices =
        new(() => MefHostServices.Create(MefHostServices.DefaultAssemblies));

    private readonly AdhocWorkspace _workspace;
    private readonly Dictionary<string, DocumentId> _generatedByUiPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DocumentId> _sourceByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<DocumentId, string> _uiPathByDocument = new();
    private readonly Dictionary<DocumentId, string> _sourcePathByDocument = new();

    private EditorDocumentWorkspace(AdhocWorkspace workspace)
    {
        _workspace = workspace;
    }

    public static EditorDocumentWorkspace For(ProjectSnapshot snapshot) =>
        Cache.GetValue(snapshot, static s => Create(s));

    public Solution Solution => _workspace.CurrentSolution;

    /// <summary>该 .bui 的生成 C# 文档（补全/重命名的 Roslyn 入口）；快照里没有则 null。</summary>
    public Document? GeneratedDocumentFor(string uiPath) =>
        uiPath.Length > 0 && Path.IsPathRooted(uiPath)
        && _generatedByUiPath.TryGetValue(Path.GetFullPath(uiPath), out var id)
            ? Solution.GetDocument(id)
            : null;

    /// <summary>项目里的源 .cs 文档（阶段 3 Renamer 与 .cs 对照测试用）。</summary>
    public Document? SourceDocumentFor(string path) =>
        path.Length > 0 && Path.IsPathRooted(path)
        && _sourceByPath.TryGetValue(Path.GetFullPath(path), out var id)
            ? Solution.GetDocument(id)
            : null;

    /// <summary>生成文档 → 它来自哪个 .bui（Renamer diff 回映用）；不是生成文档则 null。</summary>
    public string? UiPathFor(DocumentId id) =>
        _uiPathByDocument.TryGetValue(id, out var path) ? path : null;

    /// <summary>源文档 → 磁盘 .cs 路径；不是源 .cs 文档则 null。</summary>
    public string? SourcePathFor(DocumentId id) =>
        _sourcePathByDocument.TryGetValue(id, out var path) ? path : null;

    private static EditorDocumentWorkspace Create(ProjectSnapshot snapshot)
    {
        var compilation = snapshot.Compilation;
        var parseOptions = compilation.SyntaxTrees.FirstOrDefault()?.Options as CSharpParseOptions
            ?? CSharpParseOptions.Default;

        var workspace = new AdhocWorkspace(HostServices.Value);
        var project = workspace.CurrentSolution
            .AddProject(snapshot.Evaluation.AssemblyName, snapshot.Evaluation.AssemblyName, LanguageNames.CSharp)
            .WithCompilationOptions(compilation.Options)
            .WithParseOptions(parseOptions)
            .WithMetadataReferences(compilation.References);

        var generatedTrees = new HashSet<SyntaxTree>(snapshot.GeneratedTrees);
        var treeDocuments = new Dictionary<SyntaxTree, DocumentId>();
        foreach (var tree in compilation.SyntaxTrees)
        {
            var path = tree.FilePath;
            var document = project.AddDocument(Path.GetFileName(path), tree.GetText(), filePath: path);
            project = document.Project;
            treeDocuments[tree] = document.Id;
        }
        // 链式 Solution 变更不会自动落回 workspace（只有当前解上的变更才自动应用），
        // 整份解一次性 TryApplyChanges，之后 CurrentSolution 才含项目/文档。
        workspace.TryApplyChanges(project.Solution);

        var editor = new EditorDocumentWorkspace(workspace);
        foreach (var (uiPath, generated) in snapshot.GeneratedDocuments)
            if (treeDocuments.TryGetValue(generated.Tree, out var id))
            {
                var full = Path.GetFullPath(uiPath);
                editor._generatedByUiPath[full] = id;
                editor._uiPathByDocument[id] = full;
            }
        foreach (var (tree, id) in treeDocuments)
            if (!generatedTrees.Contains(tree) && Path.IsPathRooted(tree.FilePath))
            {
                var full = Path.GetFullPath(tree.FilePath);
                editor._sourceByPath[full] = id;
                editor._sourcePathByDocument[id] = full;
            }
        return editor;
    }
}
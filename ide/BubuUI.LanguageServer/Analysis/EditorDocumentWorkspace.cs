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
/// 缓存按快照引用。同一项目编辑会话保留文档 ID，逐文档更新不可变 Solution，复用
/// Roslyn 的语义缓存；旧快照仍保留自己的 Solution，延迟补全说明和重命名不会读到新文本。
/// </summary>
public sealed class EditorDocumentWorkspace
{
    private static readonly ConditionalWeakTable<ProjectSnapshot, EditorDocumentWorkspace> Cache = new();
    private sealed class Session { public EditorDocumentWorkspace? Latest; }
    private static readonly ConditionalWeakTable<object, Session> Sessions = new();

    // 4.9.2 的 DefaultAssemblies 已含 Features/CSharp.Features（缺的跳过）；
    // 包引用把这两份 dll 带到输出目录，CompletionService.GetService 才组合得到 provider。
    private static readonly Lazy<MefHostServices> HostServices =
        new(() => MefHostServices.Create(MefHostServices.DefaultAssemblies));

    private readonly AdhocWorkspace _workspace;
    private readonly Solution _solution;
    private readonly Dictionary<string, DocumentId> _generatedByUiPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DocumentId> _sourceByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<DocumentId, string> _uiPathByDocument = new();
    private readonly Dictionary<DocumentId, string> _sourcePathByDocument = new();

    private EditorDocumentWorkspace(AdhocWorkspace workspace, Solution solution)
    {
        _workspace = workspace;
        _solution = solution;
    }

    public static EditorDocumentWorkspace For(ProjectSnapshot snapshot) =>
        Cache.GetValue(snapshot, static snapshot =>
        {
            var session = Sessions.GetValue(snapshot.EditorSession, static _ => new Session());
            lock (session) return session.Latest = Create(snapshot, session.Latest);
        });

    public Solution Solution => _solution;

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

    private static EditorDocumentWorkspace Create(ProjectSnapshot snapshot, EditorDocumentWorkspace? previous)
    {
        var compilation = snapshot.Compilation;
        var parseOptions = compilation.SyntaxTrees.FirstOrDefault()?.Options as CSharpParseOptions
            ?? CSharpParseOptions.Default;

        var workspace = previous?._workspace ?? new AdhocWorkspace(HostServices.Value);
        var project = previous?.Solution.Projects.Single() ?? workspace.CurrentSolution
            .AddProject(snapshot.Evaluation.AssemblyName, snapshot.Evaluation.AssemblyName, LanguageNames.CSharp);
        if (!Equals(project.CompilationOptions, compilation.Options)) project = project.WithCompilationOptions(compilation.Options);
        if (!Equals(project.ParseOptions, parseOptions)) project = project.WithParseOptions(parseOptions);
        if (!project.MetadataReferences.SequenceEqual(compilation.References)) project = project.WithMetadataReferences(compilation.References);
        var previousDocuments = project.Documents.Where(doc => doc.FilePath is not null)
            .ToDictionary(doc => doc.FilePath!, StringComparer.OrdinalIgnoreCase);
        var paths = compilation.SyntaxTrees.Select(tree => tree.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var document in previousDocuments.Values)
            if (!paths.Contains(document.FilePath!)) project = project.RemoveDocument(document.Id);

        var generatedTrees = new HashSet<SyntaxTree>(snapshot.GeneratedTrees);
        var treeDocuments = new Dictionary<SyntaxTree, DocumentId>();
        foreach (var tree in compilation.SyntaxTrees)
        {
            var path = tree.FilePath;
            if (previousDocuments.TryGetValue(path, out var existing))
            {
                var text = tree.GetText();
                if (!existing.TryGetText(out var oldText) || !oldText.ContentEquals(text))
                    project = project.Solution.WithDocumentText(existing.Id, text).GetProject(project.Id)!;
                treeDocuments[tree] = existing.Id;
            }
            else
            {
                var document = project.AddDocument(Path.GetFileName(path), tree.GetText(), filePath: path);
                project = document.Project;
                treeDocuments[tree] = document.Id;
            }
        }
        // Each result retains its immutable solution, so deferred completion resolve
        // and rename requests still see their original document after later edits.
        var editor = new EditorDocumentWorkspace(workspace, project.Solution);
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

using BubuUI.Compiler;
using BubuUI.LanguageServer.Workspace;
using Microsoft.CodeAnalysis;

namespace BubuUI.LanguageServer.Analysis;

/// <summary>
/// IDE 能力的门面：文档 + 项目系统 → 诊断/补全/悬停/大纲。刻意不依赖任何 LSP 类型，
/// 这样 VS Code（stdio LSP）、Rider（lsp4ij）、测试三处驱动的是**同一套**行为
/// （design/06 §6.2「一份共享资产，两个薄壳」）。
///
/// §6.9：有项目归属的文档一律走项目 Compilation（L2/L3/L4）；没有项目系统（内存态测试、
/// untitled、无 csproj 的工作区）才退回 legacy 的 dll 目录，且「不属于任何项目」的磁盘文档
/// 按口径不给目录也不给诊断。
/// </summary>
public sealed class LanguageService
{
    private readonly CatalogProvider _legacy;
    private readonly ProjectWorkspace? _projects;
    private readonly DocumentStore _documents;
    private readonly Dictionary<string, (int Version, string CodeBehind, ProjectSnapshot? Snapshot, DocumentAnalysis Analysis)> _cache =
        new(StringComparer.Ordinal);

    public LanguageService(CatalogProvider catalogs, DocumentStore documents, ProjectWorkspace? projects = null)
    {
        _legacy = catalogs;
        _projects = projects;
        _documents = documents;
    }

    /// <summary>无文档上下文时的目录（命令面板/能力声明用）：项目快照优先，否则 legacy。</summary>
    public UiCatalog Catalog => _projects?.DefaultCatalog ?? _legacy.Catalog;

    /// <summary>当前目录是不是由项目 Compilation 派生的（日志/提示需要说清来源）。</summary>
    public bool UsesProjectCatalog => _projects?.DefaultCatalog is not null;

    public DocumentStore Documents => _documents;

    public IReadOnlyList<string> Notes
    {
        get
        {
            var notes = new List<string>(_legacy.Notes);
            if (_projects is not null) notes.AddRange(_projects.Notes);
            return notes;
        }
    }

    /// <summary>手动刷新 / watcher 触发后由宿主调用。</summary>
    public void Reload()
    {
        _legacy.Reload();
        _projects?.Invalidate(Array.Empty<string>());
        _cache.Clear();
    }

    /// <summary>L7：watcher 变更 → 项目 dirty → 重建并让宿主重发诊断。</summary>
    public void Invalidate(IEnumerable<string> paths)
    {
        _projects?.Invalidate(paths);
        _cache.Clear();
    }

    private static bool IsTemplate(string uri) =>
        !DocumentStore.UriToPath(uri).EndsWith(".bss", StringComparison.OrdinalIgnoreCase);

    /// <summary>文档归属的全部项目快照（§6.9.3 L1：多项目命中时都算，不选一个）。</summary>
    public IReadOnlyList<ProjectSnapshot> Snapshots(string uri) =>
        _projects?.ResolveAll(DocumentStore.UriToPath(uri)) ?? Array.Empty<ProjectSnapshot>();

    public IReadOnlyList<string> RelatedDocuments(string uri)
    {
        var projects = Snapshots(uri);
        return _documents.All.Where(d => d.Uri != uri &&
            (_projects is null || projects.Any(p => p.Evaluation.Owns(DocumentStore.UriToPath(d.Uri)))))
            .Select(d => d.Uri).ToArray();
    }

    /// <summary>取（或重算）当前文档的分析结果：版本 + 文本 + code-behind + 项目快照都不变才复用。</summary>
    public DocumentAnalysis? Analyze(string uri)
    {
        if (!_documents.TryGet(uri, out var document)) return null;
        var project = Snapshots(uri).FirstOrDefault();
        var (catalog, compilation, _) = Current(uri, project);
        var (codeBehind, codeBehindPath) = ReadCodeBehind(DocumentStore.UriToPath(uri));
        if (_cache.TryGetValue(uri, out var cached)
            && cached.Version == document.Version
            && ReferenceEquals(cached.Snapshot, project)
            && string.Equals(cached.CodeBehind, codeBehind ?? "", StringComparison.Ordinal)
            && string.Equals(cached.Analysis.Text, document.Text, StringComparison.Ordinal))
            return cached.Analysis;

        var analysis = DocumentAnalysis.Create(document.Text, document.FileName, catalog,
            compilation, codeBehind, codeBehindPath, project, _projects, DocumentStore.UriToPath(uri));
        _cache[uri] = (document.Version, codeBehind ?? "", project, analysis);
        return analysis;
    }

    private DocumentAnalysis? AnalyzeIn(string uri, ProjectSnapshot project)
    {
        if (!_documents.TryGet(uri, out var document)) return null;
        var (codeBehind, codeBehindPath) = ReadCodeBehind(DocumentStore.UriToPath(uri));
        return DocumentAnalysis.Create(document.Text, document.FileName, project.Catalog,
            project.InputCompilation, codeBehind, codeBehindPath, project, _projects, DocumentStore.UriToPath(uri));
    }

    private (UiCatalog Catalog, Compilation? Compilation, bool Unowned) Current(string uri, ProjectSnapshot? project)
    {
        if (project is not null) return (project.Catalog, project.InputCompilation, false);
        var path = DocumentStore.UriToPath(uri);
        if (_projects is not null && _projects.IsUnowned(path))
            return (CatalogProvider.EmptyCatalog(), null, true);
        return (_legacy.Catalog, _legacy.Compilation, false);
    }

    /// <summary>
    /// 同目录同名 <c>{Name}.cs</c>（spec/02 §2.2 的文件契约）。打开的 .cs 用编辑器缓冲区
    /// 覆盖磁盘（L2/R4）：未保存的 code-behind 成员立刻在 <c>{expr}</c> 里可见。
    /// </summary>
    private (string? Text, string? Path) ReadCodeBehind(string path)
    {
        try
        {
            if (path.Length == 0 || !path.EndsWith(".bui", StringComparison.OrdinalIgnoreCase))
                return (null, null);
            var candidate = Path.ChangeExtension(path, ".cs");
            if (_projects is not null && _projects.TryBuffer(candidate, out var buffered))
                return (buffered, candidate);
            return File.Exists(candidate) ? (File.ReadAllText(candidate), candidate) : (null, null);
        }
        catch (Exception)
        {
            // 盘上读不了（被锁/被删）：等价于"没有 code-behind"，不拖垮其余能力。
            return (null, null);
        }
    }

    public void Forget(string uri)
    {
        _cache.Remove(uri);
        _projects?.ForgetBuffer(uri);
    }

    /// <summary>didOpen / didChange：项目快照要用编辑器缓冲区覆盖磁盘（L2，R4）。</summary>
    public void ApplyBuffer(string uri, string text) => _projects?.ApplyBuffer(uri, text);

    /// <summary>Use the standard LSP initialize root when the process was started without --workspace.</summary>
    public bool ConfigureWorkspaceRoot(string root)
    {
        if (_projects?.ConfigureWorkspaceRoot(root) != true) return false;
        _cache.Clear();
        return true;
    }

    /// <summary>
    /// 诊断双通道（§6.9.4）：Tier 1（每次按键，FrontEnd.Parse + Analyze on 缓冲区目录）+
    /// Tier 2（项目 Compilation 的生成器/CS 诊断，经 #line 回映）。多项目命中时逐份都算，
    /// 与 dotnet build 一致；不属于任何项目的文档明确不给诊断。
    /// </summary>
    public IReadOnlyList<UiDiagnostic> GetDiagnostics(string uri, bool includeTier2 = true)
    {
        var path = DocumentStore.UriToPath(uri);
        var projects = Snapshots(uri);
        if (_projects is not null && _projects.IsUnowned(path)) return Array.Empty<UiDiagnostic>();

        var result = new List<UiDiagnostic>();

        // .bss 不是模板：语法/语义诊断全部来自真生成器（ExecuteStylesheet 的
        // EUI050–052），不能拿 FrontEnd 去解析样式表（§6.9.4）。
        if (path.EndsWith(".bss", StringComparison.OrdinalIgnoreCase))
        {
            if (includeTier2)
                foreach (var project in projects)
                    foreach (var diagnostic in project.Diagnostics)
                        if (SamePath(diagnostic.Path, path)) result.Add(diagnostic.Diagnostic);
            if (projects.Count == 0 && _documents.TryGet(uri, out var styleDocument))
            {
                if (includeTier2 && _legacy.Compilation is { } compilation)
                {
                    var files = StyleFiles(uri, null).Select(f => (Path: f.Key, Text: f.Value));
                    var generated = GeneratorHost.Run(compilation, files, Microsoft.CodeAnalysis.CSharp.CSharpParseOptions.Default, "StandaloneStyles");
                    foreach (var diagnostic in generated.Diagnostics.Where(d => d.Id.StartsWith("EUI", StringComparison.Ordinal) && SamePath(d.Location.GetLineSpan().Path, path)))
                        result.Add(new(diagnostic.Id, diagnostic.GetMessage(),
                            diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error ? Compiler.DiagnosticSeverity.Error : Compiler.DiagnosticSeverity.Warning,
                            new(diagnostic.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Length)));
                }
                else
                {
                    var theme = ControlThemeSyntax.Parse(styleDocument.Text, result);
                    Bss.Parse(theme.LegacyText, path, result, properties: _legacy.Catalog.StyleProperties);
                }
            }
        }
        else if (projects.Count > 0)
        {
            foreach (var project in projects)
            {
                var analysis = AnalyzeIn(uri, project);
                if (analysis is not null) result.AddRange(Visible(analysis.Diagnostics, path));
                if (includeTier2)
                    foreach (var diagnostic in project.Diagnostics)
                        if (SamePath(diagnostic.Path, path)) result.Add(diagnostic.Diagnostic);
            }
        }
        else
        {
            var analysis = Analyze(uri);
            if (analysis is not null) result.AddRange(Visible(analysis.Diagnostics, path));
        }

        return result
            .DistinctBy(static d => (d.Code, d.Span.Start, d.Span.Length, d.Message))
            .OrderBy(static d => d.Span.Start)
            .ThenBy(static d => d.Code, StringComparer.Ordinal)
            .ToArray();
    }

    private static IEnumerable<UiDiagnostic> Visible(IEnumerable<UiDiagnostic> diagnostics, string path) =>
        diagnostics.Where(d => d.Source is null ||
                               string.Equals(d.Source.Path, path, StringComparison.OrdinalIgnoreCase));

    private static bool SamePath(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>该文档所属项目的目录（多项目命中时取第一份；诊断仍逐份都算）。</summary>
    public UiCatalog CatalogForUri(string uri) => Current(uri, Snapshots(uri).FirstOrDefault()).Catalog;

    private UiCatalog CatalogFor(string uri) => CatalogForUri(uri);

    private StyleLanguageProvider? StyleFor(string uri, StyleDocument? embedded = null)
    {
        if (!_documents.TryGet(uri, out var document)) return null;
        var path = DocumentStore.UriToPath(uri);
        var project = Snapshots(uri).FirstOrDefault();
        var (catalog, compilation, unowned) = Current(uri, project);
        if (unowned) return null;
        var files = StyleFiles(uri, project);
        return new(path, document.Text, catalog, project?.Compilation ?? compilation, files, project, embedded, _projects);
    }

    private IEnumerable<StyleLanguageProvider> EmbeddedStylesFor(string uri)
    {
        if (!_documents.TryGet(uri, out var document)) yield break;
        foreach (var region in StyleLanguageProvider.StyleDocuments(DocumentStore.UriToPath(uri), document.Text))
            if (StyleFor(uri, region) is { } provider) yield return provider;
    }

    private StyleLanguageProvider? EmbeddedStyleFor(string uri, int offset)
    {
        if (!_documents.TryGet(uri, out var document)) return null;
        var region = StyleLanguageProvider.StyleDocuments(DocumentStore.UriToPath(uri), document.Text)
            .FirstOrDefault(d => offset >= d.Region.Start && offset <= d.Region.End);
        return region is null ? null : StyleFor(uri, region);
    }

    private Dictionary<string, string> StyleFiles(string uri, ProjectSnapshot? project)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // A real project is authoritative. Standalone mode only shares explicitly opened documents.
        var paths = project?.Evaluation.AdditionalFiles ?? _documents.All.Select(d => DocumentStore.UriToPath(d.Uri)).ToArray();
        foreach (var path in paths)
        {
            if (!path.EndsWith(".bui", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".bss", StringComparison.OrdinalIgnoreCase)) continue;
            var opened = _documents.All.FirstOrDefault(d => SamePath(DocumentStore.UriToPath(d.Uri), path));
            if (opened is not null) files[path] = opened.Text;
            else if (_projects?.TryBuffer(path, out var buffered) == true) files[path] = buffered;
            else try { if (File.Exists(path)) files[path] = File.ReadAllText(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        if (_documents.TryGet(uri, out var current)) files[DocumentStore.UriToPath(uri)] = current.Text;
        return files;
    }

    public IReadOnlyList<UiTextEdit> Format(string uri, int tabSize = 4, bool insertSpaces = true,
        bool indentOnly = false, bool includeBlankLines = false) => !IsTemplate(uri)
        ? StyleFor(uri)?.Format(tabSize, insertSpaces, includeBlankLines) ?? []
        : Analyze(uri) is { } analysis
            ? EmbeddedCSharpService.Format(analysis, tabSize, insertSpaces, indentOnly, includeBlankLines) : [];

    public UiSignatureHelp? SignatureHelp(string uri, int offset) => !IsTemplate(uri)
        ? StyleFor(uri)?.SignatureHelp(offset)
        : EmbeddedStyleFor(uri, offset) is { } style ? style.SignatureHelp(offset)
        : Analyze(uri) is { } analysis ? EmbeddedCSharpService.SignatureHelp(analysis, offset) : null;

    public IReadOnlyList<UiCodeAction> CodeActions(string uri) => !IsTemplate(uri)
        ? StyleFor(uri)?.CodeActions() ?? []
        : (Analyze(uri) is { } analysis ? EmbeddedCSharpService.CodeActions(analysis, CatalogForUri(uri), GetDiagnostics(uri)) : [])
            .Concat(EmbeddedStylesFor(uri).SelectMany(s => s.CodeActions())).ToArray();

    public UiCompletionList Complete(string uri, int offset)
    {
        if (!IsTemplate(uri)) return StyleFor(uri)?.Complete(offset) ?? UiCompletionList.Empty;
        if (EmbeddedStyleFor(uri, offset) is { } embedded) return embedded.Complete(offset);
        var analysis = Analyze(uri);
        return analysis is null ? UiCompletionList.Empty : CompleteMarkup(uri, analysis, offset);
    }

    /// <summary>
    /// 补全路由（design/18 §5.5）：C# 岛（<c>@code</c> 与项目模式的 <c>{expr}</c>）走 Roslyn
    /// CompletionService（异步，真实生成文档）；标签/属性/指令仍走目录，legacy 的
    /// <c>{expr}</c> 仍走探针（同步 <see cref="Complete"/> 保留给这些位置与既有测试）。
    /// </summary>
    public async Task<UiCompletionList> CompleteAsync(
        string uri, int offset, char? triggerCharacter = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsTemplate(uri)) return StyleFor(uri)?.Complete(offset) ?? UiCompletionList.Empty;
        if (EmbeddedStyleFor(uri, offset) is { } embedded) return embedded.Complete(offset);
        var analysis = Analyze(uri);
        cancellationToken.ThrowIfCancellationRequested();
        if (analysis is null) return UiCompletionList.Empty;
        var context = CursorScanner.Scan(analysis.Text, offset);
        if (context.Kind == CursorKind.CodeBlock ||
            (context.Kind == CursorKind.Expression && analysis.GeneratedDocument is not null))
            return await EmbeddedCSharpCompletionProvider.CompleteAsync(
                analysis, DocumentStore.UriToPath(uri), offset, triggerCharacter, cancellationToken)
                .ConfigureAwait(false);
        return CompleteMarkup(uri, analysis, offset);
    }

    private UiCompletionList CompleteMarkup(string uri, DocumentAnalysis analysis, int offset)
    {
        var result = CompletionProvider.Complete(analysis, CatalogFor(uri), offset);
        var context = CursorScanner.Scan(analysis.Text, offset);
        if (context.Kind != CursorKind.AttributeValue || context.AttributeName != "Class") return result;
        if (StyleFor(uri)?.ClassCompletion(offset) is not { Items.Count: > 0 } classes) return result;
        return classes with { Items = classes.Items.Concat(result.Items).DistinctBy(i => i.Label).ToArray() };
    }

    public UiHoverResult? Hover(string uri, int offset)
    {
        if (!IsTemplate(uri)) return StyleFor(uri)?.Hover(offset);
        if (EmbeddedStyleFor(uri, offset) is { } embedded) return embedded.Hover(offset);
        var analysis = Analyze(uri);
        return analysis is null ? null : HoverProvider.Hover(analysis, CatalogFor(uri), offset);
    }

    /// <summary>跳转定义（L6）：标签与属性都走项目 Compilation 的符号查询（§6.9.3）。</summary>
    public UiDefinition? Definition(string uri, int offset)
    {
        if (!IsTemplate(uri)) return StyleFor(uri)?.Definition(offset);
        if (EmbeddedStyleFor(uri, offset) is { } embedded) return embedded.Definition(offset);
        if (StyleFor(uri)?.ClassDefinitionAt(offset) is { } styleClass) return styleClass;
        var style = StyleFor(uri);
        if (style?.CSharpThemeAt(offset) is { } theme) return style.ThemeDefinition(theme.Name);
        var analysis = Analyze(uri);
        if (analysis is null) return null;
        var project = Snapshots(uri).FirstOrDefault();
        var (catalog, compilation, _) = Current(uri, project);
        var definition = DefinitionProvider.Definition(analysis, offset,
            project?.Evaluation.Directory ?? _legacy.WorkspaceRoot, catalog, project?.Compilation ?? compilation);
        if (definition is not null) return definition;
        var context = CursorScanner.Scan(analysis.Text, offset);
        if (context.Kind == CursorKind.AttributeValue && context.AttributeName == "Class")
        {
            var start = offset; var end = offset;
            while (start > 0 && (char.IsLetterOrDigit(analysis.Text[start - 1]) || analysis.Text[start - 1] is '_' or '-')) start--;
            while (end < analysis.Text.Length && (char.IsLetterOrDigit(analysis.Text[end]) || analysis.Text[end] is '_' or '-')) end++;
            return StyleFor(uri)?.ClassDefinition(analysis.Text[start..end], context.TagName);
        }
        return null;
    }

    /// <summary>反编译虚拟文档内容（eui-decompiled: scheme，客户端内容提供器取回）。</summary>
    public string? DecompiledContent(string uri) => DecompileProvider.Content(uri);

    /// <summary>
    /// bubuui/generatedDocument（design/18 §6 阶段 0）：当前 .bui 的生成 C# + 映射表。
    /// 映射错位/补全候选不对时，第一时间能看见生成代码（对标 razor/generatedDocumentContents）。
    /// </summary>
    public GeneratedDocument? GeneratedDocumentFor(string uri) => Analyze(uri)?.GeneratedDocument;

    /// <summary>
    /// 语义着色路由（design/18 §5.8）：项目模式走 Roslyn Classifier（异步，真实生成文档 +
    /// 统一映射回映）；legacy / 无生成文档回落探针投影路径。宿主只负责 delta 编码。
    /// </summary>
    public async Task<IReadOnlyList<UiSemanticToken>> SemanticTokensAsync(
        string uri, CancellationToken cancellationToken = default)
    {
        if (!IsTemplate(uri)) return StyleFor(uri)?.SemanticTokens() ?? [];
        var analysis = Analyze(uri);
        if (analysis is null) return Array.Empty<UiSemanticToken>();
        var tokens = await EmbeddedCSharpService.SemanticTokensAsync(
            analysis, DocumentStore.UriToPath(uri), cancellationToken).ConfigureAwait(false);
        return tokens.Concat(EmbeddedStylesFor(uri).SelectMany(s => s.SemanticTokens()))
            .OrderBy(t => t.Line).ThenBy(t => t.Character).ToArray();
    }

    /// <summary>legacy 入口（同步）：探针扫描路径。项目模式的 Roslyn 重命名走异步版本。</summary>
    public UiPrepareRename? PrepareRename(string uri, int offset)
    {
        if (!IsTemplate(uri)) return StyleFor(uri)?.PrepareRename(offset);
        if (EmbeddedStyleFor(uri, offset) is { } embedded) return embedded.PrepareRename(offset);
        if (StyleFor(uri)?.ClassPrepareRename(offset) is { } styleClass) return styleClass;
        if (StyleFor(uri)?.CSharpThemeAt(offset) is { } theme) return new(theme.Span, theme.Name);
        var analysis = Analyze(uri);
        return analysis is null ? null : RenameProvider.Prepare(analysis, offset);
    }

    /// <summary>legacy 入口（同步）：探针扫描路径。项目模式的 Roslyn 重命名走异步版本。</summary>
    public UiRenameResult? Rename(string uri, int offset, string newName)
    {
        if (!IsTemplate(uri)) return StyleFor(uri)?.Rename(offset, newName);
        if (EmbeddedStyleFor(uri, offset) is { } embedded) return embedded.Rename(offset, newName);
        if (StyleFor(uri) is { } classProvider && classProvider.ClassPrepareRename(offset) is not null) return classProvider.RenameClassAt(offset, newName);
        var style = StyleFor(uri);
        if (style?.CSharpThemeAt(offset) is { } theme) return style.RenameTheme(theme.Name, newName);
        var analysis = Analyze(uri);
        var project = Snapshots(uri).FirstOrDefault();
        var root = project?.Evaluation.Directory ?? _legacy.WorkspaceRoot;
        return analysis is null ? null : RenameProvider.Rename(analysis, offset, newName, root);
    }

    /// <summary>
    /// 重命名路由（design/18 §5.6）：项目模式走 Roslyn Renamer（Solution diff + 映射回映，
    /// 覆盖 @code 声明的成员与跨文档引用）；@foreach 迭代变量与 legacy 模式仍走
    /// <see cref="RenameProvider"/> 的文档内/探针路径。prepare 与 rename 同一条路由，
    /// 保证「prepare 说能改」与「rename 真给编辑」一致。
    /// </summary>
    public async Task<UiPrepareRename?> PrepareRenameAsync(
        string uri, int offset, CancellationToken cancellationToken = default)
    {
        if (!IsTemplate(uri)) return StyleFor(uri)?.PrepareRename(offset);
        if (EmbeddedStyleFor(uri, offset) is { } embedded) return embedded.PrepareRename(offset);
        if (StyleFor(uri)?.ClassPrepareRename(offset) is { } styleClass) return styleClass;
        if (StyleFor(uri)?.CSharpThemeAt(offset) is { } theme) return new(theme.Span, theme.Name);
        var analysis = Analyze(uri);
        if (analysis is null) return null;
        if (UseWorkspaceRename(analysis, offset))
            return await WorkspaceRenameProvider.PrepareAsync(
                analysis, DocumentStore.UriToPath(uri), offset, cancellationToken).ConfigureAwait(false);
        return RenameProvider.Prepare(analysis, offset);
    }

    public async Task<UiRenameResult?> RenameAsync(
        string uri, int offset, string newName, CancellationToken cancellationToken = default)
    {
        if (!IsTemplate(uri)) return StyleFor(uri)?.Rename(offset, newName);
        if (EmbeddedStyleFor(uri, offset) is { } embedded) return embedded.Rename(offset, newName);
        if (StyleFor(uri) is { } classProvider && classProvider.ClassPrepareRename(offset) is not null) return classProvider.RenameClassAt(offset, newName);
        var style = StyleFor(uri);
        if (style?.CSharpThemeAt(offset) is { } theme) return style.RenameTheme(theme.Name, newName);
        var analysis = Analyze(uri);
        if (analysis is null) return null;
        if (UseWorkspaceRename(analysis, offset) && _projects is not null)
            return await WorkspaceRenameProvider.RenameAsync(
                analysis, _projects, DocumentStore.UriToPath(uri), offset, newName, cancellationToken)
                .ConfigureAwait(false);
        var project = Snapshots(uri).FirstOrDefault();
        var root = project?.Evaluation.Directory ?? _legacy.WorkspaceRoot;
        return RenameProvider.Rename(analysis, offset, newName, root);
    }

    private static bool UseWorkspaceRename(DocumentAnalysis analysis, int offset) =>
        analysis.GeneratedDocument is not null && analysis.Project is not null &&
        !RenameProvider.IsForeachVariable(analysis, offset);

    public IReadOnlyList<UiSymbol> GetSymbols(string uri)
    {
        if (!IsTemplate(uri)) return StyleFor(uri)?.Symbols() ?? [];
        var analysis = Analyze(uri);
        return (analysis is null ? Array.Empty<UiSymbol>() : SymbolProvider.Build(analysis, CatalogFor(uri)))
            .Concat(EmbeddedStylesFor(uri).SelectMany(s => s.Symbols())).ToArray();
    }
}

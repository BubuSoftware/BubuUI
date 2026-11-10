using EclipseUI.Compiler;
using EclipseUI.LanguageServer.Workspace;

namespace EclipseUI.LanguageServer.Analysis;

/// <summary>
/// IDE 能力的门面：文档 + 目录 → 诊断/补全/悬停/大纲。刻意不依赖任何 LSP 类型，
/// 这样 VS Code（stdio LSP）、Rider（lsp4ij）、测试三处驱动的是**同一套**行为
/// （design/06 §6.2「一份共享资产，两个薄壳」）。
/// </summary>
public sealed class LanguageService
{
    private readonly CatalogProvider _catalogs;
    private readonly DocumentStore _documents;
    private readonly Dictionary<string, (int Version, string CodeBehind, DocumentAnalysis Analysis)> _cache =
        new(StringComparer.Ordinal);

    public LanguageService(CatalogProvider catalogs, DocumentStore documents)
    {
        _catalogs = catalogs;
        _documents = documents;
    }

    public UiCatalog Catalog => _catalogs.Catalog;

    public DocumentStore Documents => _documents;

    /// <summary>取（或重算）当前文档的分析结果：同一版本 + 同一文本 + 同一 code-behind 只算一次。</summary>
    public DocumentAnalysis? Analyze(string uri)
    {
        if (!_documents.TryGet(uri, out var document)) return null;
        var (codeBehind, codeBehindPath) = ReadCodeBehind(DocumentStore.UriToPath(uri));
        if (_cache.TryGetValue(uri, out var cached)
            && cached.Version == document.Version
            && string.Equals(cached.CodeBehind, codeBehind ?? "", StringComparison.Ordinal)
            && string.Equals(cached.Analysis.Text, document.Text, StringComparison.Ordinal))
            return cached.Analysis;

        // code-behind 与探针 Compilation 一起传：表达式投影在此之上派生（design/06 §6.3）。
        // 路径也一起传——跳转定义要落到兄弟文件上（spec/02 §2.2）。
        var analysis = DocumentAnalysis.Create(document.Text, document.FileName, Catalog,
            _catalogs.Compilation, codeBehind, codeBehindPath);
        _cache[uri] = (document.Version, codeBehind ?? "", analysis);
        return analysis;
    }

    /// <summary>
    /// 同目录同名 <c>{Name}.cs</c>（spec/02 §2.2 的文件契约）——code-behind 是 partial 类的
    /// 另一半，<c>{expr}</c> 里的字段/方法/handler 都靠它解析。半截文件也没关系：探针按
    /// 当下文本编译，用户改完保存下一次请求自然就好。
    /// </summary>
    private static (string? Text, string? Path) ReadCodeBehind(string path)
    {
        try
        {
            if (path.Length == 0 || !path.EndsWith(".ui", StringComparison.OrdinalIgnoreCase))
                return (null, null);
            var candidate = Path.ChangeExtension(path, ".cs");
            return File.Exists(candidate) ? (File.ReadAllText(candidate), candidate) : (null, null);
        }
        catch (Exception)
        {
            // 盘上读不了（被锁/被删）：等价于"没有 code-behind"，不拖垮其余能力。
            return (null, null);
        }
    }

    public void Forget(string uri) => _cache.Remove(uri);

    /// <summary>当前文件的诊断（指向 code-behind 的那些留给 C# 语言服务报，避免两处重复）。</summary>
    public IReadOnlyList<UiDiagnostic> GetDiagnostics(string uri)
    {
        var analysis = Analyze(uri);
        if (analysis is null) return Array.Empty<UiDiagnostic>();

        var path = DocumentStore.UriToPath(uri);
        return analysis.Diagnostics
            .Where(d => d.Source is null || string.Equals(d.Source.Path, path, StringComparison.OrdinalIgnoreCase))
            .OrderBy(d => d.Span.Start)
            .ThenBy(d => d.Code, StringComparer.Ordinal)
            .ToArray();
    }

    public UiCompletionList Complete(string uri, int offset)
    {
        var analysis = Analyze(uri);
        return analysis is null ? UiCompletionList.Empty : CompletionProvider.Complete(analysis, Catalog, offset);
    }

    public UiHoverResult? Hover(string uri, int offset)
    {
        var analysis = Analyze(uri);
        return analysis is null ? null : HoverProvider.Hover(analysis, Catalog, offset);
    }

    /// <summary>跳转定义（L3 第一片）：表达式标识符 → foreach 头或 code-behind 声明。</summary>
    public UiDefinition? Definition(string uri, int offset)
    {
        var analysis = Analyze(uri);
        return analysis is null ? null : DefinitionProvider.Definition(analysis, offset);
    }

    public IReadOnlyList<UiSymbol> GetSymbols(string uri)
    {
        var analysis = Analyze(uri);
        return analysis is null ? Array.Empty<UiSymbol>() : SymbolProvider.Build(analysis, Catalog);
    }
}

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
    private readonly Dictionary<string, (int Version, DocumentAnalysis Analysis)> _cache =
        new(StringComparer.Ordinal);

    public LanguageService(CatalogProvider catalogs, DocumentStore documents)
    {
        _catalogs = catalogs;
        _documents = documents;
    }

    public UiCatalog Catalog => _catalogs.Catalog;

    public DocumentStore Documents => _documents;

    /// <summary>取（或重算）当前文档的分析结果：同一版本 + 同一文本只算一次。</summary>
    public DocumentAnalysis? Analyze(string uri)
    {
        if (!_documents.TryGet(uri, out var document)) return null;
        if (_cache.TryGetValue(uri, out var cached)
            && cached.Version == document.Version
            && string.Equals(cached.Analysis.Text, document.Text, StringComparison.Ordinal))
            return cached.Analysis;

        var analysis = DocumentAnalysis.Create(document.Text, document.FileName, Catalog);
        _cache[uri] = (document.Version, analysis);
        return analysis;
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

    public IReadOnlyList<UiSymbol> GetSymbols(string uri)
    {
        var analysis = Analyze(uri);
        return analysis is null ? Array.Empty<UiSymbol>() : SymbolProvider.Build(analysis, Catalog);
    }
}

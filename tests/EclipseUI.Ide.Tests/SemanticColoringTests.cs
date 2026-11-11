using EclipseUI.Compiler;
using EclipseUI.LanguageServer.Analysis;
using EclipseUI.LanguageServer.Workspace;
using Xunit;

namespace EclipseUI.Ide.Tests;

/// <summary>
/// design/18 阶段 4（§5.8）：项目模式语义着色走 Roslyn Classifier——对生成文档分类，
/// 经统一映射服务回映 .ui。验证：token 只落在 {expr}/@code 岛内（模板表面归 TextMate）、
/// 关键符号拿到正确语义类型（field/property/method/variable）、legacy 无生成文档时回落探针路径。
/// 夹具借用 Gallery 真实 .ui 路径注入缓冲区（TaskItem 是真实模型类型）。
/// </summary>
public class SemanticColoringTests
{
    private static readonly Lazy<ProjectWorkspace> SharedProjects = new(() =>
        new ProjectWorkspace(new CatalogOptions { ProbeRoot = FindRepositoryRoot() }));

    private static readonly Lazy<CatalogProvider> SharedLegacy = new(() =>
        new CatalogProvider(new CatalogOptions
        {
            RuntimePath = Path.Combine(AppContext.BaseDirectory, "EclipseUI.Runtime.dll"),
            IncludeSiblingAssemblies = false,
        }));

    private const string CounterDoc = """
        @namespace Gallery
        @using EclipseUI.Elements
        <StackPanel>
            <TextBlock>{task.Name}</TextBlock>
            <TextBlock>{Count()}</TextBlock>
        </StackPanel>
        @code {
            public TaskItem task = new();
            public List<int> items = new();
            public int Count() => items.Count;
            void Probe()
            {
                var local = task.Name;
                var n = task.Name.Length;
                System.Console.WriteLine(local);
            }
        }
        """;

    private static (LanguageService Service, string Uri, DocumentAnalysis Analysis) Create(
        string doc, string fileName = "Counter.ui")
    {
        var path = System.IO.Path.Combine(FindRepositoryRoot(), "samples", "Gallery", fileName);
        var uri = DocumentStore.PathToUri(path);
        var service = new LanguageService(SharedLegacy.Value, new DocumentStore(), SharedProjects.Value);
        service.ApplyBuffer(uri, doc);
        service.Documents.Open(uri, doc, 1);
        var analysis = service.Analyze(uri);
        Assert.NotNull(analysis);
        Assert.NotNull(analysis!.GeneratedDocument);
        return (service, uri, analysis);
    }

    /// <summary>{expr} 与 @code 块在 .ui 里的区间（着色只允许落在这些岛内）。</summary>
    private static List<(int Start, int End)> Islands(DocumentAnalysis analysis)
    {
        var islands = new List<(int, int)>();
        foreach (var expression in CursorScanner.CollectExpressions(analysis.Text))
        {
            var start = expression.Header?.CollectionStart ?? expression.Start;
            var end = expression.Header?.CollectionEnd ?? expression.End;
            if (end > start) islands.Add((start, end));
        }
        if (analysis.Ast is not null)
            foreach (var block in analysis.Ast.Nodes.OfType<UiCodeBlock>())
                islands.Add((block.CodeSpan.Start, block.CodeSpan.Start + block.CodeSpan.Length));
        return islands;
    }

    [Fact]
    public async Task ProjectMode_TokensStayInsideCSharpIslands()
    {
        var (service, uri, analysis) = Create(CounterDoc);
        var tokens = await service.SemanticTokensAsync(uri);
        Assert.NotEmpty(tokens);

        var islands = Islands(analysis);
        Assert.NotEmpty(islands);
        foreach (var token in tokens)
        {
            var offset = analysis.Positions.ToOffset(token.Line, token.Character);
            Assert.True(token.Length > 0);
            Assert.Contains(islands, i => offset >= i.Start && offset + token.Length <= i.End);
            Assert.InRange(token.TokenType, 0, SemanticTokenTypes.All.Length - 1);
        }
    }

    [Fact]
    public async Task ProjectMode_ClassifiesSymbolsSemantically()
    {
        var (service, uri, analysis) = Create(CounterDoc);
        var tokens = await service.SemanticTokensAsync(uri);
        var byOffset = tokens.ToDictionary(
            t => analysis.Positions.ToOffset(t.Line, t.Character), t => t);

        void AssertToken(string anchor, string identifier, string expectedType)
        {
            var start = CounterDoc.IndexOf(anchor, StringComparison.Ordinal);
            Assert.True(start >= 0, $"anchor '{anchor}' not found");
            var offset = start + anchor.IndexOf(identifier, StringComparison.Ordinal);
            Assert.True(byOffset.TryGetValue(offset, out var token),
                $"no token at '{identifier}' (offset {offset})");
            Assert.Equal(identifier.Length, token!.Length);
            Assert.Equal(SemanticTokenTypes.IndexOf(expectedType), token.TokenType);
        }

        // @code 声明的字段 / {expr} 里的属性与方法 / 局部变量。
        AssertToken("public TaskItem task", "task", "field");
        AssertToken("{task.Name}", "Name", "property");
        AssertToken("{Count()}", "Count", "method");
        AssertToken("var local", "local", "variable");
        AssertToken("public List<int> items", "items", "field");
    }

    [Fact]
    public async Task ProjectMode_KeywordAndNumberClassified()
    {
        var (service, uri, analysis) = Create(CounterDoc);
        var tokens = await service.SemanticTokensAsync(uri);
        Assert.Contains(tokens, t => t.TokenType == SemanticTokenTypes.IndexOf("keyword"));
        // @code 里 `new()` / 字面量；用带数字的夹具确保 number 出现。
        var (service2, uri2, _) = Create(CounterDoc.Replace("public int Count() => items.Count;",
            "public int Count() => items.Count + 42;"));
        var tokens2 = await service2.SemanticTokensAsync(uri2);
        Assert.Contains(tokens2, t => t.TokenType == SemanticTokenTypes.IndexOf("number"));
    }

    [Fact]
    public async Task LegacyMode_FallsBackToProbeProjection()
    {
        // 内存态分析（无项目、无生成文档）：异步入口必须回落到与同步完全一致的探针路径。
        var (analysis, _) = TestHost.Analyze(CounterDoc);
        Assert.Null(analysis.GeneratedDocument);
        var sync = EmbeddedCSharpService.SemanticTokens(analysis);
        var async = await EmbeddedCSharpService.SemanticTokensAsync(analysis, "Counter.ui", default);
        Assert.Equal(sync, async);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
            if (File.Exists(System.IO.Path.Combine(directory.FullName, "EclipseUI.slnx")))
                return directory.FullName;
        throw new InvalidOperationException("cannot locate EclipseUI.slnx from the test output directory");
    }
}
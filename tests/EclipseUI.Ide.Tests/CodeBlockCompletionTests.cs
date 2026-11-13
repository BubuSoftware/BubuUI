using System.Diagnostics;
using System.Text.Json.Nodes;
using EclipseUI.Compiler;
using EclipseUI.LanguageServer.Analysis;
using EclipseUI.LanguageServer.Protocol;
using EclipseUI.LanguageServer.Server;
using EclipseUI.LanguageServer.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Xunit;
using Xunit.Abstractions;

namespace EclipseUI.Ide.Tests;

/// <summary>
/// design/18 阶段 2：<c>@code</c> 内走 Roslyn CompletionService（真实生成文档 +
/// EditorDocumentWorkspace），验收按 §7.2 与 .cs 对照。项目模式夹具借用 Gallery 的
/// Counter.ui 路径注入缓冲区（ProjectWorkspace.TryRead 优先取缓冲区，生成文档与注入文本对齐）；
/// 成员声明在 @code 里（Gallery 的 Counter 本就没有 .cs），TaskItem 是 Gallery 真实模型类型。
/// </summary>
public class CodeBlockCompletionTests
{
    private readonly ITestOutputHelper _output;

    public CodeBlockCompletionTests(ITestOutputHelper output) => _output = output;

    private const string Prefix = """
        @namespace Gallery
        @using EclipseUI.Elements
        <StackPanel>
            <TextBlock />
        </StackPanel>
        @code {
            public TaskItem task = new();
            public List<int> items = new();
            public bool ready;
            public Task<int> LoadAsync() => Task.FromResult(1);

        """;

    private const string Suffix = """
        }
        """;

    // 求值只做一次（MSBuild 全仓库扫描）；缓冲区按用例覆写，xUnit 同类内串行，互不串扰。
    private static readonly Lazy<ProjectWorkspace> SharedProjects = new(() =>
        new ProjectWorkspace(new CatalogOptions { ProbeRoot = FindRepositoryRoot() }));

    private static readonly Lazy<CatalogProvider> SharedLegacy = new(() =>
        new CatalogProvider(new CatalogOptions
        {
            RuntimePath = Path.Combine(AppContext.BaseDirectory, "EclipseUI.Runtime.dll"),
            IncludeSiblingAssemblies = false,
        }));

    private sealed class Fixture
    {
        public required LanguageService Service { get; init; }
        public required ProjectWorkspace Projects { get; init; }
        public required string Path { get; init; }
        public required string Uri { get; init; }
        public required string Doc { get; init; }
        public required int Offset { get; init; }
    }

    /// <param name="body">方法体片段，含 <c>|</c> 光标标记（移除后偏移原样返回）。</param>
    private static Fixture Create(string body, bool asyncMethod = false, ProjectWorkspace? projects = null)
    {
        var method = asyncMethod ? "async Task Probe()" : "void Probe()";
        var marked = Prefix + "    " + method + "\n    {\n        " + body + "\n    }\n" + Suffix;
        var offset = marked.IndexOf('|', StringComparison.Ordinal);
        Assert.True(offset >= 0, "fixture body must contain a | cursor marker");
        var doc = marked.Remove(offset, 1);

        projects ??= SharedProjects.Value;
        var root = FindRepositoryRoot();
        var path = System.IO.Path.Combine(root, "samples", "Gallery", "Counter.ui");
        var uri = DocumentStore.PathToUri(path);
        var service = new LanguageService(SharedLegacy.Value, new DocumentStore(), projects);
        service.ApplyBuffer(uri, doc);
        service.Documents.Open(uri, doc, 1);
        Assert.NotNull(service.GeneratedDocumentFor(uri));
        return new Fixture
        {
            Service = service, Projects = projects, Path = path, Uri = uri, Doc = doc, Offset = offset,
        };
    }

    // ---- §7.2 四项与 .cs 对照 ----

    [Theory]
    [InlineData("r|", "ready", UiCompletionKind.Field)]
    [InlineData("rea|", "ready", UiCompletionKind.Field)]
    [InlineData("Loa|", "LoadAsync", UiCompletionKind.Method)]
    [InlineData("Inv|", "Invalidate", UiCompletionKind.Method)]
    [InlineData("Upda|", "UpdateState", UiCompletionKind.Method)]
    [InlineData("Beg|", "BeginStateUpdate", UiCompletionKind.Method)]
    [InlineData("Ga|", "Gap", UiCompletionKind.Property)]
    public async Task CodeBlock_TypingLettersOffersOwnAndInheritedMembersWithoutThis(
        string body, string name, UiCompletionKind kind)
    {
        var fixture = Create(body);
        var list = await fixture.Service.CompleteAsync(fixture.Uri, fixture.Offset,
            triggerCharacter: fixture.Doc[fixture.Offset - 1]);
        var item = Assert.Single(list.Items, candidate => candidate.Label == name && candidate.Kind == kind);
        Assert.NotNull(item.Replacement);
        var span = item.Replacement!.Value;
        Assert.Equal(body.TrimEnd('|'), fixture.Doc.Substring(span.Start, span.Length));
        Assert.True(string.CompareOrdinal(item.SortText, "2") < 0, item.SortText);
    }

    [Fact]
    public async Task CodeBlock_MemberCompletionMatchesCSharpDocument()
    {
        var fixture = Create("task.|");
        var list = await fixture.Service.CompleteAsync(fixture.Uri, fixture.Offset);
        Assert.NotEmpty(list.Items);
        foreach (var label in new[] { "Id", "Name", "Done", "ToString" })
            Assert.Contains(list.Items, i => i.Label == label);

        // 对照：同一 workspace 里加一份 .cs，同位置 `task.`，候选集合必须一致。
        var snapshot = Assert.Single(fixture.Projects.ResolveAll(fixture.Path));
        var editor = EditorDocumentWorkspace.For(snapshot);
        var generated = editor.GeneratedDocumentFor(fixture.Path);
        Assert.NotNull(generated);
        var csText = """
            namespace Gallery;
            public class CsParity
            {
                public TaskItem task = new();
                void Probe()
                {
                    task.
                }
            }
            """;
        var csDocument = generated!.Project.AddDocument("CsParity.cs", csText);
        var caret = csText.IndexOf("task.", StringComparison.Ordinal) + "task.".Length;
        var service = CompletionService.GetService(csDocument);
        Assert.NotNull(service);
        var csList = await service!.GetCompletionsAsync(csDocument, caret);
        Assert.NotNull(csList);

        var uiLabels = list.Items.Select(static i => i.Label).ToHashSet(StringComparer.Ordinal);
        var csLabels = csList!.ItemsList.Select(static i => i.DisplayText).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(csLabels, uiLabels);
    }

    [Fact]
    public async Task CodeBlock_TypeNameCompletionAfterVar()
    {
        var fixture = Create("var t = ta|;");
        var list = await fixture.Service.CompleteAsync(fixture.Uri, fixture.Offset);
        Assert.Contains(list.Items, i => i.Label == "TaskItem" && i.Kind == UiCompletionKind.Class);
    }

    [Fact]
    public async Task CodeBlock_IfConditionOffersBooleanContext()
    {
        var fixture = Create("if (|) { }");
        var list = await fixture.Service.CompleteAsync(fixture.Uri, fixture.Offset);
        // 布尔上下文：关键字 true/false + bool 字段；不是接收者成员列表（TaskItem.Done 不出现）。
        Assert.Contains(list.Items, i => i.Label == "true" && i.Kind == UiCompletionKind.Keyword);
        Assert.Contains(list.Items, i => i.Label == "false");
        Assert.Contains(list.Items, i => i.Label == "ready");
        Assert.DoesNotContain(list.Items, i => i.Label == "Done");
    }

    [Fact]
    public async Task CodeBlock_AwaitContextMatchesCSharpBehavior()
    {
        var fixture = Create("await |;", asyncMethod: true);
        var list = await fixture.Service.CompleteAsync(fixture.Uri, fixture.Offset);
        // Roslyn 4.9.2 实测（AbstractSymbolDescriptionBuilder）：await 上下文**不过滤**候选——
        // await 关键字 + 全量符号，可等待类型只在描述里加 "(可等待)/(Awaitable)" 前缀。
        // 验收口径是 §7.2「与 .cs 一致」，不是字面「只出可等待对象」。
        Assert.Contains(list.Items, i => i.Label == "await" && i.Kind == UiCompletionKind.Keyword);
        Assert.Contains(list.Items, i => i.Label == "LoadAsync");
        Assert.Contains(list.Items, i => i.Label == "task");

        // 对照：同一 workspace 的 .cs 文档同位置，Task 类型的描述必须逐字一致（同样带装饰）。
        var snapshot = Assert.Single(fixture.Projects.ResolveAll(fixture.Path));
        var editor = EditorDocumentWorkspace.For(snapshot);
        var generated = editor.GeneratedDocumentFor(fixture.Path);
        Assert.NotNull(generated);
        var csText = """
            using EclipseUI.Elements;
            namespace Gallery;
            public class CsParity
            {
                public TaskItem task = new();
                async Task Probe()
                {
                    await ;
                }
            }
            """;
        var csDocument = generated!.Project.AddDocument("CsParity.cs", csText);
        var caret = csText.IndexOf("await ", StringComparison.Ordinal) + "await ".Length;
        var service = CompletionService.GetService(csDocument);
        Assert.NotNull(service);
        var csList = await service!.GetCompletionsAsync(csDocument, caret);
        Assert.NotNull(csList);

        var uiTask = list.Items.FirstOrDefault(i => i.Label == "Task");
        var csTask = csList!.ItemsList.FirstOrDefault(i => i.DisplayText == "Task");
        Assert.NotNull(uiTask);
        Assert.NotNull(csTask);
        var csDescription = await service.GetDescriptionAsync(csDocument, csTask!);
        Assert.NotNull(csDescription);
        Assert.Null(uiTask!.Documentation);
        Assert.Equal(csDescription!.Text, await uiTask.GetDocumentationAsync());
    }

    // ---- 扩展方法 / snippet / 触发字符 ----

    [Fact]
    public async Task CodeBlock_ExtensionMethodsAppear()
    {
        var fixture = Create("items.|");
        var list = await fixture.Service.CompleteAsync(fixture.Uri, fixture.Offset);
        Assert.Contains(list.Items, i => i.Label == "Where" && i.Kind == UiCompletionKind.Method);
        Assert.Contains(list.Items, i => i.Label == "Select");
    }

    [Fact]
    public async Task CodeBlock_SnippetExpandsToRealText()
    {
        var fixture = Create("|");
        var list = await fixture.Service.CompleteAsync(fixture.Uri, fixture.Offset);
        var snippet = list.Items.FirstOrDefault(i => i.Label == "for" && i.Kind == UiCompletionKind.Snippet);
        Assert.NotNull(snippet);
        // snippet 的真实展开文本来自 GetChangeAsync，不是只插一个 "for"。
        Assert.Contains("for (", snippet!.Text);
    }

    [Fact]
    public async Task CodeBlock_TriggerCharacterDotIsHonored()
    {
        var fixture = Create("task.|");
        var list = await fixture.Service.CompleteAsync(fixture.Uri, fixture.Offset, triggerCharacter: '.');
        Assert.Contains(list.Items, i => i.Label == "Name");
    }

    // ---- 映射 / 缓冲区 / 边界 ----

    [Fact]
    public async Task CodeBlock_ReplacementSpanMapsBackToUi()
    {
        var fixture = Create("task.Na|");
        var list = await fixture.Service.CompleteAsync(fixture.Uri, fixture.Offset);
        var name = list.Items.FirstOrDefault(i => i.Label == "Name");
        Assert.NotNull(name);
        Assert.NotNull(name!.Replacement);
        var span = name.Replacement!.Value;
        Assert.Equal("Na", fixture.Doc.Substring(span.Start, span.Length));
    }

    [Fact]
    public async Task CodeBlock_UnsavedCodeBehindBufferIsVisible()
    {
        // 独立实例：TaskItem.cs 缓冲区只在本用例生效，不污染共享夹具。
        var projects = new ProjectWorkspace(new CatalogOptions { ProbeRoot = FindRepositoryRoot() });
        var root = FindRepositoryRoot();
        var taskItemPath = System.IO.Path.Combine(root, "samples", "Gallery", "TaskItem.cs");
        Assert.DoesNotContain("Extra", File.ReadAllText(taskItemPath));
        var buffered = """
            namespace Gallery;
            public sealed class TaskItem
            {
                public int Id { get; set; }
                public string Name { get; set; } = "";
                public bool Done { get; set; }
                public string Extra => "unsaved";
            }
            """;
        projects.ApplyBuffer(DocumentStore.PathToUri(taskItemPath), buffered);

        var fixture = Create("task.|", projects: projects);
        var list = await fixture.Service.CompleteAsync(fixture.Uri, fixture.Offset);
        Assert.Contains(list.Items, i => i.Label == "Extra");
    }

    [Fact]
    public async Task CodeBlock_LegacyModeReturnsEmpty()
    {
        // 无项目快照（内存态）：没有生成文档 → 不另造合成投影，返回空。
        const string doc = """
            @code {
                void Tick()
                {
                    int count = 1;
                    cou|nt++;
                }
            }
            """;
        var (service, uri, offset) = TestHost.Open(doc);
        var list = await service.CompleteAsync(uri, offset);
        Assert.Empty(list.Items);
    }

    // ---- §5.1 验收：workspace 与 L3 同文 / 语义模型 / 服务可用 ----

    [Fact]
    public async Task EditorWorkspace_GeneratedDocumentMatchesL3AndResolvesMembers()
    {
        var fixture = Create("task.|");
        var snapshot = Assert.Single(fixture.Projects.ResolveAll(fixture.Path));
        var editor = EditorDocumentWorkspace.For(snapshot);
        var document = editor.GeneratedDocumentFor(fixture.Path);
        Assert.NotNull(document);

        var generated = snapshot.GeneratedDocuments[System.IO.Path.GetFullPath(fixture.Path)];
        var text = await document!.GetTextAsync();
        Assert.Equal(generated.Tree.ToString(), text.ToString());

        Assert.NotNull(CompletionService.GetService(document));
        var model = await document.GetSemanticModelAsync();
        Assert.NotNull(model);
        var counter = model!.Compilation.GetTypeByMetadataName("Gallery.Counter");
        Assert.NotNull(counter);
        Assert.Contains(counter!.GetMembers("task"), static m => m is IFieldSymbol);
    }

    // ---- §7.3 延迟实测（warm/cold 分开记录） ----

    [Fact]
    public async Task CodeBlock_LspListDefersDescriptionsAndResolvePreservesEdits()
    {
        var fixture = Create("|");
        var host = new LanguageServerHost(new JsonRpcConnection(Stream.Null, Stream.Null),
            fixture.Service, SharedLegacy.Value);
        var (line, character) = new PositionMap(fixture.Doc).ToPosition(fixture.Offset);
        var parameters = new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = fixture.Uri },
            ["position"] = new JsonObject { ["line"] = line, ["character"] = character },
        };
        parameters = JsonNode.Parse(parameters.ToJsonString())!.AsObject();
        var response = await host.HandleRequestAsync(new(JsonValue.Create(1)!, "textDocument/completion", parameters));
        var items = response!["items"]!.AsArray();
        Assert.True(items.Count > 100);
        var candidate = Assert.Single(items, item => item!["label"]!.GetValue<string>() == "LoadAsync")!;
        Assert.Null(candidate["documentation"]);
        Assert.NotNull(candidate["data"]);
        var original = candidate.ToJsonString();

        // A newer list must not change the description/edit belonging to this selection.
        await host.HandleRequestAsync(new(JsonValue.Create(2)!, "textDocument/completion", parameters));
        var resolved = await host.HandleRequestAsync(new(JsonValue.Create(3)!, "completionItem/resolve", candidate));
        Assert.Contains("LoadAsync", resolved!["documentation"]!["value"]!.GetValue<string>());
        Assert.Equal(candidate["textEdit"]!.ToJsonString(), resolved["textEdit"]!.ToJsonString());
        Assert.Equal(original, candidate.ToJsonString());
        var repeated = await host.HandleRequestAsync(new(JsonValue.Create(4)!, "completionItem/resolve", candidate));
        Assert.Equal(resolved.ToJsonString(), repeated!.ToJsonString());

        // Retained lists are bounded; expired items are returned unchanged, never rebound by label.
        for (var i = 0; i < 3; i++)
            await host.HandleRequestAsync(new(JsonValue.Create(5 + i)!, "textDocument/completion", parameters));
        var expired = await host.HandleRequestAsync(new(JsonValue.Create(8)!, "completionItem/resolve", candidate));
        Assert.Equal(original, expired!.ToJsonString());
    }

    [Fact]
    public async Task CodeBlock_CompletionLatencyColdAndWarm()
    {
        // 独立实例：cold = 首次补全请求（workspace 创建 + MEF 组合 + provider 首载 + 语义绑定）；
        // 项目快照已由夹具断言预建（真实 server 里它在 didOpen 诊断时就绪，不计入补全延迟）。
        var projects = new ProjectWorkspace(new CatalogOptions { ProbeRoot = FindRepositoryRoot() });
        var fixture = Create("|", projects: projects);

        var watch = Stopwatch.StartNew();
        var cold = await fixture.Service.CompleteAsync(fixture.Uri, fixture.Offset);
        watch.Stop();
        var coldMs = watch.ElapsedMilliseconds;

        // 热路径 = 同文档同位置再请求一次：workspace/MEF/provider 全部命中缓存。
        watch.Restart();
        var warm = await fixture.Service.CompleteAsync(fixture.Uri, fixture.Offset);
        watch.Stop();
        var warmMs = watch.ElapsedMilliseconds;

        _output.WriteLine($"completion latency: cold={coldMs}ms ({cold.Items.Count} items), warm={warmMs}ms ({warm.Items.Count} items)");
        Assert.NotEmpty(cold.Items);
        Assert.NotEmpty(warm.Items);
        // 宽松上限防回归（热路径不应重建 workspace/MEF）；具体数值记录进 design/18 §12。
        Assert.True(warmMs < 5000, $"warm completion too slow: {warmMs}ms");
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

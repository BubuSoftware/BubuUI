using EclipseUI.Compiler;
using EclipseUI.LanguageServer.Analysis;
using EclipseUI.LanguageServer.Workspace;
using Xunit;

namespace EclipseUI.Ide.Tests;

/// <summary>
/// design/18 阶段 3：项目模式的 <c>{expr}</c> 补全迁移到真实生成文档上的 Roslyn
/// CompletionService（与 @code 同一台机器）。legacy（无项目）的探针补全由既有
/// CompletionTests / ProtocolTests 覆盖，行为不变。
/// </summary>
public class ExpressionCompletionTests
{
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
        public required string Uri { get; init; }
        public required string Doc { get; init; }
        public required int Offset { get; init; }
    }

    /// <summary>整份 .ui 文档注入 Gallery 的 Counter.ui 路径（缓冲区优先，生成文档与之对齐）。</summary>
    private static Fixture Create(string markedDoc)
    {
        var offset = markedDoc.IndexOf('|', StringComparison.Ordinal);
        Assert.True(offset >= 0, "fixture must contain a | cursor marker");
        var doc = markedDoc.Remove(offset, 1);
        var path = System.IO.Path.Combine(FindRepositoryRoot(), "tests", "GalleryFixtures", "Counter.ui");
        var uri = DocumentStore.PathToUri(path);
        var service = new LanguageService(SharedLegacy.Value, new DocumentStore(), SharedProjects.Value);
        service.ApplyBuffer(uri, doc);
        service.Documents.Open(uri, doc, 1);
        Assert.NotNull(service.GeneratedDocumentFor(uri));
        return new Fixture { Service = service, Uri = uri, Doc = doc, Offset = offset };
    }

    [Fact]
    public async Task Expression_MemberCompletionComesFromRoslyn()
    {
        var fixture = Create("""
            @namespace Gallery
            @using EclipseUI.Elements
            <StackPanel>
                <TextBlock>{task.|}</TextBlock>
            </StackPanel>
            @code {
                public TaskItem task = new();
            }
            """);
        var list = await fixture.Service.CompleteAsync(fixture.Uri, fixture.Offset);
        // TaskItem 实例成员 + object 方法（Roslyn 全量，不是手写 Members() 的降噪子集）。
        Assert.Contains(list.Items, i => i.Label == "Id" && i.Kind == UiCompletionKind.Property);
        Assert.Contains(list.Items, i => i.Label == "Name");
        Assert.Contains(list.Items, i => i.Label == "Done");
        Assert.Contains(list.Items, i => i.Label == "ToString");
    }

    [Fact]
    public async Task Expression_ReplacementSpanMapsBackToUi()
    {
        var fixture = Create("""
            @namespace Gallery
            @using EclipseUI.Elements
            <StackPanel>
                <TextBlock>{task.Na|}</TextBlock>
            </StackPanel>
            @code {
                public TaskItem task = new();
            }
            """);
        var list = await fixture.Service.CompleteAsync(fixture.Uri, fixture.Offset);
        var name = list.Items.FirstOrDefault(i => i.Label == "Name");
        Assert.NotNull(name);
        Assert.NotNull(name!.Replacement);
        var span = name.Replacement!.Value;
        Assert.Equal("Na", fixture.Doc.Substring(span.Start, span.Length));
    }

    [Fact]
    public async Task Expression_BareIdentifierOffersClassMembers()
    {
        var fixture = Create("""
            @namespace Gallery
            @using EclipseUI.Elements
            <StackPanel>
                <TextBlock>{Cou|}</TextBlock>
            </StackPanel>
            @code {
                public TaskItem task = new();
                public int Count() => 1;
            }
            """);
        var list = await fixture.Service.CompleteAsync(fixture.Uri, fixture.Offset);
        Assert.Contains(list.Items, i => i.Label == "Count" && i.Kind == UiCompletionKind.Method);
    }

    [Theory]
    [InlineData("c", "count", UiCompletionKind.Field)]
    [InlineData("cou", "count", UiCompletionKind.Field)]
    [InlineData("Cou", "CounterLabel", UiCompletionKind.Property)]
    [InlineData("Com", "Compute", UiCompletionKind.Method)]
    [InlineData("Ga", "Gap", UiCompletionKind.Property)]
    public async Task Expression_TypingLettersOffersOwnAndInheritedMembersWithoutThis(
        string prefix, string name, UiCompletionKind kind)
    {
        var fixture = Create("@namespace Gallery\n@using EclipseUI.Elements\n"
            + "<StackPanel><TextBlock>{" + prefix + "|}</TextBlock></StackPanel>\n"
            + "@code { public int count; public string CounterLabel => count.ToString(); public string Compute() => \"\"; }");
        var list = await fixture.Service.CompleteAsync(fixture.Uri, fixture.Offset,
            triggerCharacter: prefix[^1]);
        var item = Assert.Single(list.Items, candidate => candidate.Label == name && candidate.Kind == kind);
        Assert.NotNull(item.Replacement);
        var span = item.Replacement!.Value;
        Assert.Equal(prefix, fixture.Doc.Substring(span.Start, span.Length));
        Assert.True(string.CompareOrdinal(item.SortText, "2") < 0, item.SortText);
    }

    [Fact]
    public async Task Expression_ForeachVariableMembersComeFromRoslyn()
    {
        // foreach 迭代变量在生成代码里是 SetTemplate lambda 的强类型参数——
        // Roslyn 直接知道它的类型，不需要探针的 foreach 环境注入。
        var fixture = Create("""
            @namespace Gallery
            @using EclipseUI.Elements
            @using System.Collections.Generic
            <StackPanel>
                @foreach (var t in tasks) {
                    <ListBoxItem Key={t.Id}>
                        <TextBlock>{t.|}</TextBlock>
                    </ListBoxItem>
                }
            </StackPanel>
            @code {
                public List<TaskItem> tasks = new();
            }
            """);
        var list = await fixture.Service.CompleteAsync(fixture.Uri, fixture.Offset);
        Assert.Contains(list.Items, i => i.Label == "Name");
        Assert.Contains(list.Items, i => i.Label == "Done");
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
            if (File.Exists(System.IO.Path.Combine(directory.FullName, "EclipseUI.slnx")))
                return directory.FullName;
        throw new InvalidOperationException("repository root not found");
    }
}

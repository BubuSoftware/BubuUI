using EclipseUI.Compiler;
using EclipseUI.LanguageServer.Analysis;
using EclipseUI.LanguageServer.Workspace;
using Xunit;

namespace EclipseUI.Ide.Tests;

/// <summary>
/// design/18 阶段 3（§5.6）：项目模式重命名走 Roslyn Renamer——Solution diff，
/// 源 .cs 直接交付，生成文档编辑经统一映射服务回映 .ui，任一处映不回整体拒绝。
/// 夹具借用 Gallery 真实 .ui 路径注入缓冲区；TaskItem 是 Gallery 真实模型类型，
/// 跨文档引用（TaskList.ui / TaskItem.cs）天然存在。
/// </summary>
public class WorkspaceRenameTests
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
        public required string Path { get; init; }
        public required string Uri { get; init; }
        public required string Doc { get; init; }
        public required int Offset { get; init; }
    }

    private static Fixture Create(string markedDoc, string fileName = "Counter.ui")
    {
        var offset = markedDoc.IndexOf('|', StringComparison.Ordinal);
        Assert.True(offset >= 0, "fixture must contain a | cursor marker");
        var doc = markedDoc.Remove(offset, 1);
        var path = System.IO.Path.Combine(FindRepositoryRoot(), "samples", "Gallery", fileName);
        var uri = DocumentStore.PathToUri(path);
        var service = new LanguageService(SharedLegacy.Value, new DocumentStore(), SharedProjects.Value);
        service.ApplyBuffer(uri, doc);
        service.Documents.Open(uri, doc, 1);
        Assert.NotNull(service.GeneratedDocumentFor(uri));
        return new Fixture { Service = service, Path = path, Uri = uri, Doc = doc, Offset = offset };
    }

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

    // 字段光标（声明处）：CounterDoc 本身不带 marker，各测试按需注入唯一一个 |。
    private static readonly string FieldMarkedDoc = CounterDoc.Replace("public TaskItem task", "public TaskItem ta|sk");

    // ---- @code 声明的成员（阶段 1 拒绝，阶段 3 覆盖）----

    [Fact]
    public async Task Rename_CodeBlockDeclaredField_RenamesDeclarationAndReferences()
    {
        var fixture = Create(FieldMarkedDoc);
        var result = await fixture.Service.RenameAsync(fixture.Uri, fixture.Offset, "entry");
        Assert.NotNull(result);
        var file = Assert.Single(result!.Files);
        Assert.Null(file.Path);
        // 声明 + {task.Name} 表达式 + Probe 体内两处引用（local 与 n 的初始化），共 4 处。
        Assert.Equal(4, file.Edits.Count);
        foreach (var edit in file.Edits)
        {
            Assert.Equal("task", fixture.Doc.Substring(edit.Span.Start, edit.Span.Length));
            Assert.Equal("entry", edit.NewText);
        }
    }

    [Fact]
    public async Task Rename_CodeBlockDeclaredMethod_RenamesDeclarationAndExpression()
    {
        var doc = CounterDoc.Replace("public int Count() => items.Count;", "public int Cou|nt() => items.Count;");
        var fixture = Create(doc);
        var result = await fixture.Service.RenameAsync(fixture.Uri, fixture.Offset, "Total");
        Assert.NotNull(result);
        var file = Assert.Single(result!.Files);
        Assert.Null(file.Path);
        // 声明 + {Count()} 表达式；items.Count 是 List<int> 的属性，不同符号，不动。
        Assert.Equal(2, file.Edits.Count);
        foreach (var edit in file.Edits)
            Assert.Equal("Count", fixture.Doc.Substring(edit.Span.Start, edit.Span.Length));
    }

    [Fact]
    public async Task PrepareRename_CodeBlockDeclaredMember_ReturnsSpan()
    {
        var fixture = Create(FieldMarkedDoc);
        var prepare = await fixture.Service.PrepareRenameAsync(fixture.Uri, fixture.Offset);
        Assert.NotNull(prepare);
        Assert.Equal("task", prepare!.Placeholder);
        Assert.Equal("task", fixture.Doc.Substring(prepare.Span.Start, prepare.Span.Length));
    }

    // ---- 局部变量（与阶段 1 同行为，改走 Renamer）----

    [Fact]
    public async Task Rename_CodeBlockLocal_RenamesDeclarationAndReferences()
    {
        var doc = CounterDoc.Replace("var local = task.Name;", "var lo|cal = task.Name;");
        var fixture = Create(doc);
        var result = await fixture.Service.RenameAsync(fixture.Uri, fixture.Offset, "value");
        Assert.NotNull(result);
        var file = Assert.Single(result!.Files);
        Assert.Null(file.Path);
        Assert.Equal(2, file.Edits.Count);
        foreach (var edit in file.Edits)
            Assert.Equal("local", fixture.Doc.Substring(edit.Span.Start, edit.Span.Length));
    }

    // ---- 跨文档：外部模型成员（{expr} 光标 → Renamer 改全项目）----

    [Fact]
    public async Task Rename_ExternalModelMember_CrossDocument()
    {
        // 共享工作区里 TaskList.ui 可能被 bind/foreach 测试留过缓冲区（那些夹具没有 {t.Name}）；
        // 本测试要它作为**磁盘 sibling** 参与跨文档引用，先ForgetBuffer 回到磁盘内容。
        var root = FindRepositoryRoot();
        var taskListPath = System.IO.Path.Combine(root, "samples", "Gallery", "TaskList.ui");
        SharedProjects.Value.ForgetBuffer(DocumentStore.PathToUri(taskListPath));

        var doc = CounterDoc.Replace("<TextBlock>{task.Name}</TextBlock>", "<TextBlock>{task.Na|me}</TextBlock>");
        var fixture = Create(doc);
        var result = await fixture.Service.RenameAsync(fixture.Uri, fixture.Offset, "Title");
        Assert.NotNull(result);

        var byPath = result!.Files.ToDictionary(
            f => f.Path is null ? fixture.Path : System.IO.Path.GetFullPath(f.Path),
            StringComparer.OrdinalIgnoreCase);
        // 当前 .ui + TaskItem.cs 声明 + TaskList.ui（{t.Name} + 种子/追加的对象初始化器，design/20 P2）。
        Assert.Contains(System.IO.Path.GetFullPath(System.IO.Path.Combine(root, "samples", "Gallery", "TaskItem.cs")), byPath.Keys);
        Assert.Contains(System.IO.Path.GetFullPath(System.IO.Path.Combine(root, "samples", "Gallery", "TaskList.ui")), byPath.Keys);
        Assert.Contains(fixture.Path, byPath.Keys);

        // Roslyn 最小 diff 可能只替换标识符的一部分（Name→Title 共享后缀 e，实际编辑是
        // Nam→Titl），所以按「应用全部编辑后的整文」断言，而非逐条覆盖完整标识符。
        foreach (var file in result.Files)
        {
            var applied = ApplyEdits(file.Text, file.Edits);
            Assert.Equal(
                System.Text.RegularExpressions.Regex.Replace(file.Text, @"\bName\b", "Title"), applied);
        }

        // TaskList.ui 引用 TaskItem.Name 五处：{t.Name} + 三条种子 + Add() 的对象初始化器（design/20 P2）。
        var taskList = byPath[System.IO.Path.GetFullPath(System.IO.Path.Combine(root, "samples", "Gallery", "TaskList.ui"))];
        Assert.Equal(5, taskList.Edits.Count);
    }

    // 按区间倒序应用编辑，得到重命名后的整文（与 diff 粒度无关）。
    private static string ApplyEdits(string text, IEnumerable<UiTextEdit> edits)
    {
        foreach (var e in edits.OrderByDescending(static e => e.Span.Start))
            text = text.Substring(0, e.Span.Start) + e.NewText + text.Substring(e.Span.Start + e.Span.Length);
        return text;
    }

    // ---- @bind 双出现：readSource/writeSource 映到同一原文区间，去重 ----

    [Fact]
    public async Task Rename_BindExpression_DeduplicatesDualOccurrence()
    {
        var fixture = Create("""
            @namespace Gallery
            @using EclipseUI.Elements
            @using System.Collections.Generic
            <StackPanel>
                @foreach (var t in tasks) {
                    <ListBoxItem Key={t.Id}>
                        <CheckBox @bind-Checked={t.Do|ne}>done</CheckBox>
                    </ListBoxItem>
                }
            </StackPanel>
            @code {
                public List<TaskItem> tasks = new();
            }
            """, fileName: "TaskList.ui");
        var result = await fixture.Service.RenameAsync(fixture.Uri, fixture.Offset, "Finished");
        Assert.NotNull(result);

        var ui = result!.Files.Single(f => f.Path is null);
        // readSource/writeSource 两处生成编辑映到同一 {t.Done} 区间：只交付一条。
        var edit = Assert.Single(ui.Edits);
        Assert.Equal("Done", fixture.Doc.Substring(edit.Span.Start, edit.Span.Length));
        Assert.Equal("Finished", edit.NewText);

        var taskItem = result.Files.Single(f => f.Path is not null && f.Path.EndsWith("TaskItem.cs", StringComparison.Ordinal));
        Assert.Contains(taskItem.Edits, e => taskItem.Text.Substring(e.Span.Start, e.Span.Length) == "Done");
    }

    // ---- 拒绝路径 ----

    [Fact]
    public async Task Rename_ConflictingName_Rejected()
    {
        var fixture = Create(FieldMarkedDoc);
        // items 已存在：同名冲突，Renamer 抛 ArgumentException → 整体拒绝。
        Assert.Null(await fixture.Service.RenameAsync(fixture.Uri, fixture.Offset, "items"));
    }

    [Fact]
    public async Task Rename_MetadataMember_Rejected()
    {
        var doc = CounterDoc.Replace("var n = task.Name.Length;", "var n = task.Name.Le|ngth;");
        var fixture = Create(doc);
        Assert.Null(await fixture.Service.PrepareRenameAsync(fixture.Uri, fixture.Offset));
        Assert.Null(await fixture.Service.RenameAsync(fixture.Uri, fixture.Offset, "Size"));
    }

    [Fact]
    public async Task Rename_InvalidName_Rejected()
    {
        var fixture = Create(FieldMarkedDoc);
        Assert.Null(await fixture.Service.RenameAsync(fixture.Uri, fixture.Offset, "1bad"));
        Assert.Null(await fixture.Service.RenameAsync(fixture.Uri, fixture.Offset, "class"));
    }

    // ---- @foreach 迭代变量：项目模式下仍走 legacy 文档内路径 ----

    [Fact]
    public async Task Rename_ForeachVar_ProjectMode_UsesLegacyPath()
    {
        var fixture = Create("""
            @namespace Gallery
            @using EclipseUI.Elements
            @using System.Collections.Generic
            <StackPanel>
                @foreach (var t| in tasks) {
                    <ListBoxItem Key={t.Id}>
                        <TextBlock>{t.Name}</TextBlock>
                    </ListBoxItem>
                }
            </StackPanel>
            @code {
                public List<TaskItem> tasks = new();
            }
            """, fileName: "TaskList.ui");
        var result = await fixture.Service.RenameAsync(fixture.Uri, fixture.Offset, "entry");
        Assert.NotNull(result);
        var file = Assert.Single(result!.Files);
        Assert.Null(file.Path);
        // 头声明 + {t.Id} + {t.Name}，共 3 处；迭代变量作用域 = 本文档。
        Assert.Equal(3, file.Edits.Count);
        foreach (var edit in file.Edits)
        {
            Assert.Equal("t", fixture.Doc.Substring(edit.Span.Start, edit.Span.Length));
            Assert.Equal("entry", edit.NewText);
        }
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
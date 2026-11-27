using BubuUI.Compiler;
using BubuUI.LanguageServer.Analysis;
using BubuUI.LanguageServer.Workspace;
using Xunit;

namespace BubuUI.Ide.Tests;

/// <summary>
/// design/18 阶段 1：<c>@code</c> 块内的悬停与重命名（design/06 §6.9.10 第 4、5 条）。
/// 三层：内存态（独立符号视图回映）→ 磁盘 code-behind（成员路径）→ 项目模式（统一映射服务回映）。
/// 生成文本/#line 不变由 Compiler.Tests golden 钉住，这里只验证 IDE 行为。
/// </summary>
public class CodeBlockIntellisenseTests
{
    private const string CodeBehind = """
        #nullable enable
        using System.Collections.Generic;

        namespace Gallery;

        public partial class ProbeDoc
        {
            public List<int> items = new();
            public int Total;
        }
        """;

    // ---- 悬停 ----

    [Fact]
    public void Hover_CodeBlockLocalShowsType()
    {
        const string doc = """
            @code {
                void Tick()
                {
                    int count = 1;
                    cou|nt++;
                }
            }
            """;
        var (analysis, offset) = TestHost.Analyze(doc);
        var hover = HoverProvider.Hover(analysis, TestHost.Catalog, offset);

        Assert.NotNull(hover);
        Assert.Contains("count", hover!.Markdown);
        Assert.Contains("int", hover.Markdown);
        Assert.Contains("local", hover.Markdown);
        Assert.Equal("count", analysis.Text.Substring(hover.Span.Start, hover.Span.Length));
    }

    [Fact]
    public void Hover_CodeBlockFieldShowsType()
    {
        const string doc = """
            @code {
                public int mode;
                void Tick()
                {
                    mo|de = 1;
                }
            }
            """;
        var (analysis, offset) = TestHost.Analyze(doc);
        var hover = HoverProvider.Hover(analysis, TestHost.Catalog, offset);

        Assert.NotNull(hover);
        Assert.Contains("mode", hover!.Markdown);
        Assert.Contains("int", hover.Markdown);
        Assert.Contains("field", hover.Markdown);
    }

    [Fact]
    public void Hover_CodeBlockReferenceToCodeBehindMemberShowsType()
    {
        const string doc = """
            @code {
                void Tick()
                {
                    var x = To|tal;
                }
            }
            """;
        var (analysis, offset) = TestHost.Analyze(doc, CodeBehind);
        var hover = HoverProvider.Hover(analysis, TestHost.Catalog, offset);

        Assert.NotNull(hover);
        Assert.Contains("Total", hover!.Markdown);
        Assert.Contains("int", hover.Markdown);
    }

    [Fact]
    public void Hover_CodeBlockKeywordHasNoSymbol()
    {
        const string doc = """
            @code {
                void Tick()
                {
                    int count = 1;
                    if (cou|nt > 0) { }
                }
            }
            """;
        var (analysis, offset) = TestHost.Analyze(doc);
        // 光标落在关键字 `if` 上：没有可悬停的符号。
        var atIf = analysis.Text.IndexOf("if (", StringComparison.Ordinal);
        Assert.Null(HoverProvider.Hover(analysis, TestHost.Catalog, atIf));
        Assert.NotNull(HoverProvider.Hover(analysis, TestHost.Catalog, offset));
    }

    // ---- 重命名：@code 局部变量 ----

    [Fact]
    public void Rename_CodeBlockLocalUpdatesDeclarationAndUses()
    {
        const string doc = """
            @code {
                void Tick()
                {
                    int count = 1;
                    cou|nt++;
                }
            }
            """;
        var (analysis, offset) = TestHost.Analyze(doc);
        var result = RenameProvider.Rename(analysis, offset, "total", workspaceRoot: null);

        Assert.NotNull(result);
        var file = Assert.Single(result!.Files);
        Assert.Null(file.Path); // 局部变量作用域就在 @code：编辑全落在 .bui
        Assert.Equal(2, file.Edits.Count); // 声明 + 引用
        foreach (var edit in file.Edits)
        {
            Assert.Equal("count", analysis.Text.Substring(edit.Span.Start, edit.Span.Length));
            Assert.Equal("total", edit.NewText);
        }
    }

    [Fact]
    public void Rename_CodeBlockLocalDoesNotTouchSameNameInOtherMethod()
    {
        const string doc = """
            @code {
                void A()
                {
                    int count = 1;
                    cou|nt++;
                }
                void B()
                {
                    int count = 2;
                    count++;
                }
            }
            """;
        var (analysis, offset) = TestHost.Analyze(doc);
        var result = RenameProvider.Rename(analysis, offset, "total", workspaceRoot: null);

        Assert.NotNull(result);
        var file = Assert.Single(result!.Files);
        Assert.Equal(2, file.Edits.Count); // 只动 A 的声明与引用，B 的同名局部变量不碰
        var firstDecl = analysis.Text.IndexOf("int count", StringComparison.Ordinal) + "int ".Length;
        Assert.Equal(firstDecl, file.Edits[0].Span.Start);
    }

    [Fact]
    public void Prepare_CodeBlockLocalGivesSpanAndPlaceholder()
    {
        const string doc = """
            @code {
                void Tick()
                {
                    int count = 1;
                    cou|nt++;
                }
            }
            """;
        var (analysis, offset) = TestHost.Analyze(doc);
        var prepare = RenameProvider.Prepare(analysis, offset);

        Assert.NotNull(prepare);
        Assert.Equal("count", prepare!.Placeholder);
        Assert.Equal("count", analysis.Text.Substring(prepare.Span.Start, prepare.Span.Length));
    }

    // ---- 重命名：从 @code 发起的 code-behind 成员 ----

    [Fact]
    public void Rename_CodeBehindMemberInitiatedFromCodeBlock()
    {
        const string doc = """
            @using BubuUI.Elements
            <TextBlock Text={Total} />
            @code {
                void Tick()
                {
                    var x = To|tal;
                }
            }
            """;
        var (service, uri, offset) = TestHost.OpenWithCodeBehind(doc, CodeBehind);
        var result = service.Rename(uri, offset, "Count");

        Assert.NotNull(result);
        Assert.Equal(2, result!.Files.Count); // .bui + 兄弟 ProbeDoc.cs

        var ui = result.Files[0];
        Assert.Null(ui.Path);
        Assert.Equal(2, ui.Edits.Count); // {Total} 表达式 + @code 引用
        foreach (var edit in ui.Edits)
            Assert.Equal("Total", ui.Text.Substring(edit.Span.Start, edit.Span.Length));

        var codeBehind = result.Files[1];
        Assert.EndsWith("ProbeDoc.cs", codeBehind.Path, StringComparison.Ordinal);
        var declaration = Assert.Single(codeBehind.Edits);
        Assert.Equal("Total", codeBehind.Text.Substring(declaration.Span.Start, declaration.Span.Length));
    }

    [Fact]
    public void Rename_CodeBlockRejectsInvalidNames()
    {
        const string doc = """
            @code {
                void Tick()
                {
                    int count = 1;
                    cou|nt++;
                }
            }
            """;
        var (service, uri, offset) = TestHost.OpenWithCodeBehind(doc, CodeBehind);
        Assert.Null(service.Rename(uri, offset, "1bad"));
        Assert.Null(service.Rename(uri, offset, "class"));
        Assert.NotNull(service.Rename(uri, offset, "total"));
    }

    // ---- 项目模式：统一映射服务回映 ----

    [Fact]
    public void Rename_CodeBlockLocalInProjectModeUsesMappingService()
    {
        var root = FindRepositoryRoot();
        var projects = new ProjectWorkspace(new CatalogOptions { ProbeRoot = root });
        var legacy = new CatalogProvider(new CatalogOptions
        {
            RuntimePath = Path.Combine(AppContext.BaseDirectory, "BubuUI.Runtime.dll"),
            IncludeSiblingAssemblies = false,
        });
        var service = new LanguageService(legacy, new DocumentStore(), projects);

        // 借用测试专用 Gallery 夹具中被 AdditionalFiles 收录的 .bui 路径，注入自定义缓冲区文本
        // （ProjectWorkspace.TryRead 优先取缓冲区，生成文档与映射因此对齐注入文本）。
        var path = Path.Combine(root, "tests", "GalleryFixtures", "Counter.bui");
        var uri = DocumentStore.PathToUri(path);
        const string doc = """
            @namespace Gallery
            @using BubuUI.Elements
            <StackPanel>
                <TextBlock />
            </StackPanel>
            @code {
                void Tick()
                {
                    int count = 1;
                    count++;
                }
            }
            """;
        service.ApplyBuffer(uri, doc);
        service.Documents.Open(uri, doc, 1);

        // 确认真的走了项目模式（有生成文档），否则本用例没有验证到映射服务。
        Assert.NotNull(service.GeneratedDocumentFor(uri));

        var offset = doc.IndexOf("count++", StringComparison.Ordinal);
        var result = service.Rename(uri, offset, "total");

        Assert.NotNull(result);
        var file = Assert.Single(result!.Files);
        Assert.Null(file.Path);
        Assert.Equal(2, file.Edits.Count);
        foreach (var edit in file.Edits)
        {
            Assert.Equal("count", doc.Substring(edit.Span.Start, edit.Span.Length));
            Assert.Equal("total", edit.NewText);
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "BubuUI.slnx")))
                return directory.FullName;
        throw new InvalidOperationException("cannot locate BubuUI.slnx from the test output directory");
    }
}
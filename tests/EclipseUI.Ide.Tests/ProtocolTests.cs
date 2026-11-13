using System.Text;
using System.Text.Json.Nodes;
using EclipseUI.LanguageServer.Analysis;
using EclipseUI.LanguageServer.Protocol;
using EclipseUI.LanguageServer.Server;
using EclipseUI.LanguageServer.Workspace;
using Xunit;

namespace EclipseUI.Ide.Tests;

/// <summary>
/// 协议层测试：先验帧（Content-Length + 分片 + EOF），再验一次完整的会话
/// （initialize → didOpen → publishDiagnostics → completion → shutdown）。
/// 这两层一旦出错，编辑器侧表现是"插件没反应"，最难远程排查，所以必须由测试兜住。
/// </summary>
public class ProtocolTests
{
    private static JsonObject Request(int id, string method, JsonObject? parameters = null) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["method"] = method,
        ["params"] = parameters,
    };

    private static JsonObject Notification(string method, JsonObject parameters) => new()
    {
        ["jsonrpc"] = "2.0",
        ["method"] = method,
        ["params"] = parameters,
    };

    private static JsonObject DidOpen(string uri, string text) => Notification("textDocument/didOpen", new JsonObject
    {
        ["textDocument"] = new JsonObject
        {
            ["uri"] = uri,
            ["languageId"] = "eclipseui",
            ["version"] = 1,
            ["text"] = text,
        },
    });

    private static JsonObject Position(string uri, int line, int character) => new()
    {
        ["textDocument"] = new JsonObject { ["uri"] = uri },
        ["position"] = new JsonObject { ["line"] = line, ["character"] = character },
    };

    // ---- 帧 ----

    [Fact]
    public async Task Reader_DecodesConsecutiveMessages()
    {
        var buffer = new MemoryStream();
        var writer = new LspMessageWriter(buffer);
        await writer.WriteAsync(Request(1, "initialize"));
        await writer.WriteAsync(Request(2, "shutdown"));

        buffer.Position = 0;
        var reader = new LspMessageReader(buffer);

        var first = await reader.ReadAsync();
        var second = await reader.ReadAsync();
        var end = await reader.ReadAsync();

        Assert.Equal("initialize", first!["method"]!.GetValue<string>());
        Assert.Equal(2, second!["id"]!.GetValue<int>());
        Assert.Null(end); // 干净的 EOF = 客户端说再见，不是错误
    }

    [Fact]
    public async Task Reader_HandlesOneByteAtATime()
    {
        var payload = LspMessageWriter.Encode(Request(7, "initialize"));
        var reader = new LspMessageReader(new DripStream(payload));

        var message = await reader.ReadAsync();
        Assert.Equal(7, message!["id"]!.GetValue<int>());
    }

    [Fact]
    public async Task Reader_HandlesMultibyteBodies()
    {
        var buffer = new MemoryStream();
        await new LspMessageWriter(buffer).WriteAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "window/showMessage",
            ["params"] = new JsonObject { ["message"] = "找不到 EclipseUI.Runtime.dll —— 标签补全已禁用" },
        });

        buffer.Position = 0;
        var message = await new LspMessageReader(buffer).ReadAsync();
        Assert.Contains("找不到", message!["params"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public void Writer_EmitsContentLengthHeader()
    {
        var bytes = LspMessageWriter.Encode(Request(1, "initialize"));
        var text = Encoding.ASCII.GetString(bytes, 0, 40);
        Assert.StartsWith("Content-Length: ", text);
    }

    // ---- 会话 ----

    [Fact]
    public async Task Session_InitializeAdvertisesTheCapabilities()
    {
        var transcript = await RunSession(
            Request(1, "initialize", new JsonObject { ["capabilities"] = new JsonObject() }),
            Notification("initialized", new JsonObject()));

        var capabilities = transcript
            .Single(m => m["id"]?.GetValue<int>() == 1)["result"]!["capabilities"]!;

        Assert.True(capabilities["hoverProvider"]!.GetValue<bool>());
        Assert.True(capabilities["definitionProvider"]!.GetValue<bool>());
        Assert.True(capabilities["completionProvider"]!["resolveProvider"]!.GetValue<bool>());
        Assert.True(capabilities["documentSymbolProvider"]!.GetValue<bool>());
        Assert.Contains("<", capabilities["completionProvider"]!["triggerCharacters"]!
            .AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(1, capabilities["textDocumentSync"]!["change"]!.GetValue<int>()); // Full
    }

    [Fact]
    public async Task Session_InitializationDoesNotCompileUnopenedProjects()
    {
        var log = new List<string>();
        var projects = new ProjectWorkspace(new CatalogOptions { ProbeRoot = RepositoryRoot() }, log.Add);
        var catalogs = new CatalogProvider(new CatalogOptions
        {
            RuntimePath = Path.Combine(AppContext.BaseDirectory, "EclipseUI.Runtime.dll"),
            IncludeSiblingAssemblies = false,
        });
        await RunSessionWithProjects(catalogs, projects,
            Request(1, "initialize"), Notification("initialized", new JsonObject()));

        Assert.DoesNotContain(log, message => message.StartsWith("project '", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Session_PublishesDiagnosticsOnOpen()
    {
        var uri = "file:///D:/workspace/Session.ui";
        var transcript = await RunSession(
            Request(1, "initialize"),
            Notification("initialized", new JsonObject()),
            DidOpen(uri, "@using EclipseUI.Elements\n\n<Buton />\n"));

        var published = transcript.Single(m => m["method"]?.GetValue<string>() == "textDocument/publishDiagnostics");
        var diagnostics = published["params"]!["diagnostics"]!.AsArray();

        Assert.Equal(uri, published["params"]!["uri"]!.GetValue<string>());
        Assert.Contains(diagnostics, d => d!["code"]!.GetValue<string>() == "EUI001");
        Assert.All(diagnostics, d => Assert.Equal("eclipseui", d!["source"]!.GetValue<string>()));
    }

    /// <summary>K22：删掉 @using 后，已写好的内置标签必须立刻变成 EUI001（用户实际遇到的场景）。</summary>
    [Fact]
    public async Task Session_RemovingUsingReportsUnknownTagOnChange()
    {
        var uri = "file:///D:/workspace/Counter.ui";
        var transcript = await RunSession(
            Request(1, "initialize"),
            Notification("initialized", new JsonObject()),
            DidOpen(uri, "@using EclipseUI.Elements\n<StackPanel />\n"),
            Notification("textDocument/didChange", new JsonObject
            {
                ["textDocument"] = new JsonObject { ["uri"] = uri, ["version"] = 2 },
                ["contentChanges"] = new JsonArray { new JsonObject { ["text"] = "<StackPanel />\n" } },
            }));

        var published = transcript
            .Where(m => m["method"]?.GetValue<string>() == "textDocument/publishDiagnostics")
            .ToArray();

        Assert.Equal(2, published.Length);
        Assert.Empty(published[0]["params"]!["diagnostics"]!.AsArray());

        var after = published[1]["params"]!["diagnostics"]!.AsArray();
        Assert.Contains(after, d => d!["code"]!.GetValue<string>() == "EUI001");
        Assert.Contains(after, d => d!["message"]!.GetValue<string>().Contains("@using"));
    }

    /// <summary>
    /// 目录为空时语义诊断会被刻意跳过（避免把配置问题伪装成"每个标签都未知"），
    /// 但**必须**弹警告，否则编辑器表现为"高亮正常、零诊断"，用户无从排查。
    /// 回归：旧实现在后台 Build 完成前就读 Notes，警告被静默吞掉。
    /// </summary>
    [Fact]
    public async Task Session_EmptyCatalogWarnsInsteadOfFailingSilently()
    {
        var empty = new CatalogProvider(new CatalogOptions
        {
            RuntimePath = Path.Combine(Path.GetTempPath(), "eclipseui-missing-" + Guid.NewGuid().ToString("N")),
            IncludeSiblingAssemblies = false,
        });
        var uri = "file:///D:/workspace/NoCatalog.ui";
        var transcript = await RunSessionWith(empty,
            Request(1, "initialize"),
            Notification("initialized", new JsonObject()),
            DidOpen(uri, "<StackPanel />\n"));

        var warning = Assert.Single(transcript,
            m => m["method"]?.GetValue<string>() == "window/showMessage");
        Assert.Equal(2, warning["params"]!["type"]!.GetValue<int>()); // Warning
        Assert.Contains("diagnostics", warning["params"]!["message"]!.GetValue<string>());

        var published = Assert.Single(transcript,
            m => m["method"]?.GetValue<string>() == "textDocument/publishDiagnostics");
        Assert.Empty(published["params"]!["diagnostics"]!.AsArray());
    }

    [Fact]
    public async Task Session_CompletionReturnsEditsInsideTheTag()
    {
        var uri = "file:///D:/workspace/Session.ui";
        var transcript = await RunSession(
            Request(1, "initialize"),
            Notification("initialized", new JsonObject()),
            DidOpen(uri, "@using EclipseUI.Elements\n\n<But\n"),
            Request(2, "textDocument/completion", Position(uri, 2, 4)));

        var items = transcript.Single(m => m["id"]?.GetValue<int>() == 2)["result"]!["items"]!.AsArray();
        Assert.NotEmpty(items);

        var button = items.Single(i => i!["label"]!.GetValue<string>() == "Button")!;
        var edit = button["textEdit"]!;
        Assert.Equal("Button", edit["newText"]!.GetValue<string>());
        Assert.Equal(1, edit["range"]!["start"]!["character"]!.GetValue<int>());
        Assert.Equal(4, edit["range"]!["end"]!["character"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task Session_UnimportedTagCompletionReturnsAdditionalImportEdit(string newline)
    {
        const string uri = "file:///D:/workspace/ImportCompletion.ui";
        var text = "@namespace Gallery" + newline + "<But" + newline;
        var transcript = await RunSession(
            Request(1, "initialize"),
            Notification("initialized", new JsonObject()),
            DidOpen(uri, text),
            Request(2, "textDocument/completion", Position(uri, 1, 4)));

        var items = transcript.Single(m => m["id"]?.GetValue<int>() == 2)["result"]!["items"]!.AsArray();
        var button = Assert.Single(items, i => i!["label"]!.GetValue<string>() == "Button")!;
        var edit = button["textEdit"]!;
        Assert.Equal("Button", edit["newText"]!.GetValue<string>());
        Assert.Equal(1, edit["range"]!["start"]!["line"]!.GetValue<int>());
        Assert.Equal(1, edit["range"]!["start"]!["character"]!.GetValue<int>());
        Assert.Equal(4, edit["range"]!["end"]!["character"]!.GetValue<int>());

        var import = Assert.Single(button["additionalTextEdits"]!.AsArray())!;
        Assert.Contains("@using EclipseUI.Elements" + newline, import["newText"]!.GetValue<string>());
        var applied = ApplyCompletion(text, button);
        Assert.Contains("<Button" + newline, applied);
        Assert.Equal(1, applied.Split("@using EclipseUI.Elements", StringSplitOptions.None).Length - 1);
        if (newline == "\n") Assert.DoesNotContain('\r', applied);
        else Assert.DoesNotContain('\n', applied.Replace("\r\n", "", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Session_AlreadyImportedTagCompletionDoesNotRepeatUsing()
    {
        const string uri = "file:///D:/workspace/ExistingImport.ui";
        const string text = "@namespace Gallery\n@using EclipseUI.Elements\n<But";
        var transcript = await RunSession(
            Request(1, "initialize"),
            Notification("initialized", new JsonObject()),
            DidOpen(uri, text),
            Request(2, "textDocument/completion", Position(uri, 2, 4)));

        var items = transcript.Single(m => m["id"]?.GetValue<int>() == 2)["result"]!["items"]!.AsArray();
        var button = Assert.Single(items, i => i!["label"]!.GetValue<string>() == "Button")!;
        Assert.True(button["additionalTextEdits"] is null || button["additionalTextEdits"]!.AsArray().Count == 0);
        Assert.Equal(text.Replace("<But", "<Button", StringComparison.Ordinal), ApplyCompletion(text, button));
    }

    [Fact]
    public async Task Session_UsingCompletionReplacesTheQualifiedPrefix()
    {
        const string uri = "file:///D:/workspace/UsingCompletion.ui";
        const string text = "@namespace Gallery\n@using System.Col";
        var transcript = await RunSession(
            Request(1, "initialize"),
            Notification("initialized", new JsonObject()),
            DidOpen(uri, text),
            Request(2, "textDocument/completion", Position(uri, 1, "@using System.Col".Length)));

        var items = transcript.Single(m => m["id"]?.GetValue<int>() == 2)["result"]!["items"]!.AsArray();
        var collections = Assert.Single(items,
            i => i!["label"]!.GetValue<string>() == "System.Collections")!;
        Assert.Equal("@namespace Gallery\n@using System.Collections", ApplyCompletion(text, collections));
    }

    [Fact]
    public async Task Session_ClientsWithoutSnippetSupportReceiveTheUsingDefaultAsPlainText()
    {
        const string uri = "file:///D:/workspace/PlainDirective.ui";
        const string text = "@us";
        var transcript = await RunSession(
            Request(1, "initialize", new JsonObject
            {
                ["capabilities"] = new JsonObject
                {
                    ["textDocument"] = new JsonObject
                    {
                        ["completion"] = new JsonObject
                        {
                            ["completionItem"] = new JsonObject { ["snippetSupport"] = false },
                        },
                    },
                },
            }),
            Notification("initialized", new JsonObject()),
            DidOpen(uri, text),
            Request(2, "textDocument/completion", Position(uri, 0, text.Length)));

        var items = transcript.Single(m => m["id"]?.GetValue<int>() == 2)["result"]!["items"]!.AsArray();
        var usingItem = Assert.Single(items, i => i!["label"]!.GetValue<string>() == "@using")!;
        Assert.Equal(1, usingItem["insertTextFormat"]!.GetValue<int>());
        Assert.Equal("@using EclipseUI.Elements", ApplyCompletion(text, usingItem));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Session_OverrideCompletionCommitsAValidMemberWithOrWithoutSnippets(bool snippetSupport)
    {
        var repository = RepositoryRoot();
        var path = Path.Combine(repository, "samples", "Gallery", "TaskList.ui");
        var uri = DocumentStore.PathToUri(path);
        const string marked = """
            @namespace Gallery
            @using EclipseUI.Elements
            <StackPanel />
            @code {
                public System.Collections.Generic.List<TaskItem> tasks = new();
                public void Add() { }
                protected override |
            }
            """;
        var offset = marked.IndexOf('|');
        var text = marked.Remove(offset, 1);
        var (line, character) = new PositionMap(text).ToPosition(offset);
        var projects = new ProjectWorkspace(new CatalogOptions { ProbeRoot = repository });
        projects.ApplyBuffer(uri, text);
        var catalogs = new CatalogProvider(new CatalogOptions
        {
            RuntimePath = Path.Combine(AppContext.BaseDirectory, "EclipseUI.Runtime.dll"),
            IncludeSiblingAssemblies = false,
        });
        var transcript = await RunSessionWithProjects(catalogs, projects,
            Request(1, "initialize", new JsonObject
            {
                ["capabilities"] = new JsonObject
                {
                    ["textDocument"] = new JsonObject
                    {
                        ["completion"] = new JsonObject
                        {
                            ["completionItem"] = new JsonObject { ["snippetSupport"] = snippetSupport },
                        },
                    },
                },
            }),
            Notification("initialized", new JsonObject()),
            DidOpen(uri, text),
            Request(2, "textDocument/completion", Position(uri, line, character)));

        var items = transcript.Single(message => message["id"]?.GetValue<int>() == 2)["result"]!["items"]!.AsArray();
        var candidate = Assert.Single(items,
            item => item!["label"]!.GetValue<string>().StartsWith("OnInitialized(", StringComparison.Ordinal))!;
        Assert.StartsWith("protected override ", candidate["filterText"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(snippetSupport ? 2 : 1, candidate["insertTextFormat"]!.GetValue<int>());
        var insertion = candidate["textEdit"]!["newText"]!.GetValue<string>();
        string plain;
        if (snippetSupport)
        {
            // Interpret the escaped wire text exactly as an LSP snippet client does.
            var expanded = new StringBuilder();
            var caret = -1;
            for (var index = 0; index < insertion.Length; index++)
            {
                if (insertion[index] == '\\' && index + 1 < insertion.Length
                    && insertion[index + 1] is '\\' or '$' or '}')
                    expanded.Append(insertion[++index]);
                else if (insertion[index] == '$' && index + 1 < insertion.Length && insertion[index + 1] == '0')
                {
                    Assert.Equal(-1, caret);
                    caret = expanded.Length;
                    index++;
                }
                else expanded.Append(insertion[index]);
            }
            plain = expanded.ToString();
            Assert.InRange(caret, plain.IndexOf('{') + 1, plain.LastIndexOf('}'));
        }
        else
        {
            plain = insertion;
            Assert.DoesNotContain("$0", plain);
            Assert.DoesNotContain("${", plain);
            Assert.DoesNotContain("\\}", plain);
        }
        Assert.Contains("protected override void OnInitialized()", plain);
        Assert.Contains("base.OnInitialized();", plain);
        Assert.DoesNotContain("#line", plain);
        var selected = candidate.DeepClone();
        selected["textEdit"]!["newText"] = plain;
        var applied = ApplyCompletion(text, selected);
        Assert.DoesNotContain("protected override protected override", applied);
        projects.ApplyBuffer(uri, applied);
        var snapshot = Assert.Single(projects.ResolveAll(path));
        var errors = snapshot.Compilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .ToArray();
        Assert.True(errors.Length == 0, string.Join(Environment.NewLine, errors.Select(error => error.ToString())));
    }

    [Fact]
    public async Task Session_CSharpTypeCompletionAddsUsingAndCompilesAfterAllWireEditsAreApplied()
    {
        var repository = RepositoryRoot();
        var path = Path.Combine(repository, "samples", "Gallery", "TaskList.ui");
        var uri = DocumentStore.PathToUri(path);
        const string marked = """
            @namespace Gallery
            @using EclipseUI.Elements
            <StackPanel />
            @code {
                public System.Collections.Generic.List<TaskItem> tasks = new();
                public void Add() { }
                void Create() { StringB| builder = new(); }
            }
            """;
        var offset = marked.IndexOf('|');
        var text = marked.Remove(offset, 1);
        var (line, character) = new PositionMap(text).ToPosition(offset);
        var projects = new ProjectWorkspace(new CatalogOptions { ProbeRoot = repository });
        projects.ApplyBuffer(uri, text);
        var catalogs = new CatalogProvider(new CatalogOptions
        {
            RuntimePath = Path.Combine(AppContext.BaseDirectory, "EclipseUI.Runtime.dll"),
            IncludeSiblingAssemblies = false,
        });
        var transcript = await RunSessionWithProjects(catalogs, projects,
            Request(1, "initialize"),
            Notification("initialized", new JsonObject()),
            DidOpen(uri, text),
            Request(2, "textDocument/completion", Position(uri, line, character)));

        var items = transcript.Single(message => message["id"]?.GetValue<int>() == 2)["result"]!["items"]!.AsArray();
        var matching = items.Where(item => item!["label"]!.GetValue<string>() == "StringBuilder").ToArray();
        Assert.True(matching.Length == 1, string.Join("\n", matching.Select(item => item!.ToJsonString())));
        var candidate = matching[0]!;
        var import = Assert.Single(candidate["additionalTextEdits"]!.AsArray())!;
        Assert.Contains("@using System.Text", import["newText"]!.GetValue<string>());
        Assert.Equal("StringBuilder", candidate["textEdit"]!["newText"]!.GetValue<string>());
        var applied = ApplyCompletion(text, candidate);
        Assert.Contains("void Create() { StringBuilder builder = new(); }", applied);
        Assert.Equal(1, applied.Split("@using System.Text", StringSplitOptions.None).Length - 1);
        projects.ApplyBuffer(uri, applied);
        var snapshot = Assert.Single(projects.ResolveAll(path));
        var errors = snapshot.Compilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .ToArray();
        Assert.True(errors.Length == 0, string.Join(Environment.NewLine, errors.Select(error => error.ToString())));
    }

    private static string ApplyCompletion(string text, JsonNode item)
    {
        var positions = new PositionMap(text);
        var edits = new List<JsonNode> { item["textEdit"]! };
        if (item["additionalTextEdits"] is JsonArray additional)
            edits.AddRange(additional.Select(edit => edit!));
        var changes = edits.Select(edit =>
        {
            var range = edit["range"]!;
            var start = range["start"]!;
            var end = range["end"]!;
            return (
                Start: positions.ToOffset(start["line"]!.GetValue<int>(), start["character"]!.GetValue<int>()),
                End: positions.ToOffset(end["line"]!.GetValue<int>(), end["character"]!.GetValue<int>()),
                Text: edit["newText"]!.GetValue<string>());
        }).OrderByDescending(edit => edit.Start).ToArray();
        foreach (var change in changes)
            text = text.Remove(change.Start, change.End - change.Start).Insert(change.Start, change.Text);
        return text;
    }

    /// <summary>
    /// 表达式投影端到端（design/06 §6.3）：code-behind 是磁盘上的同名兄弟 .cs
    /// （spec/02 §2.2 文件契约），server 经 LanguageService 读它建探针，
    /// <c>{tasks.Cou}</c> 里补全 <c>Count</c>，textEdit 只覆盖点号后的 "Cou"。
    /// </summary>
    [Fact]
    public async Task Session_ExpressionCompletionProjectsCodeBehind()
    {
        var directory = Path.Combine(Path.GetTempPath(), "eclipseui-ide-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "Session.ui");
        const string text = "<TextBlock Text={tasks.Cou} />";
        await File.WriteAllTextAsync(path, text);
        await File.WriteAllTextAsync(Path.ChangeExtension(path, ".cs"), """
            using System.Collections.Generic;
            namespace Gallery;
            public partial class Session
            {
                public List<string> tasks = new();
            }
            """);

        var uri = DocumentStore.PathToUri(path);
        var transcript = await RunSession(
            Request(1, "initialize"),
            Notification("initialized", new JsonObject()),
            DidOpen(uri, text),
            Request(2, "textDocument/completion", Position(uri, 0, 26)));

        var items = transcript.Single(m => m["id"]?.GetValue<int>() == 2)["result"]!["items"]!.AsArray();
        Assert.Contains(items, static i => i!["label"]!.GetValue<string>() == "Count");

        var edit = items.Single(i => i!["label"]!.GetValue<string>() == "Count")!["textEdit"]!;
        Assert.Equal("Count", edit["newText"]!.GetValue<string>());
        Assert.Equal(23, edit["range"]!["start"]!["character"]!.GetValue<int>());
        Assert.Equal(26, edit["range"]!["end"]!["character"]!.GetValue<int>());
    }

    /// <summary>
    /// 跳转定义端到端（L3 第一片）：<c>{Total}</c> 里的标识符落到兄弟 .cs 的属性声明——
    /// 返回的 Location 换成 code-behind 文件的 uri，range 用 .cs 原文换算行位。
    /// </summary>
    [Fact]
    public async Task Session_DefinitionOpensTheCodeBehindDeclaration()
    {
        var directory = Path.Combine(Path.GetTempPath(), "eclipseui-ide-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "Session.ui");
        const string text = "<TextBlock Text={Total} />";
        await File.WriteAllTextAsync(path, text);
        await File.WriteAllTextAsync(Path.ChangeExtension(path, ".cs"), """
            namespace Gallery;
            public partial class Session
            {
                public int Total => 42;
            }
            """);

        var uri = DocumentStore.PathToUri(path);
        var transcript = await RunSession(
            Request(1, "initialize"),
            Notification("initialized", new JsonObject()),
            DidOpen(uri, text),
            Request(2, "textDocument/definition", Position(uri, 0, 18)));

        var location = transcript.Single(m => m["id"]?.GetValue<int>() == 2)["result"]!;
        Assert.EndsWith("Session.cs", location["uri"]!.GetValue<string>(), StringComparison.Ordinal);

        // range 是 .cs 的行/列：反算回偏移，必须落在属性名 "Total" 上。
        var cs = await File.ReadAllTextAsync(Path.ChangeExtension(path, ".cs"));
        var expectedOffset = cs.IndexOf("public int Total", StringComparison.Ordinal) + "public int ".Length;
        var positions = new PositionMap(cs);
        var actual = positions.ToOffset(
            location["range"]!["start"]!["line"]!.GetValue<int>(),
            location["range"]!["start"]!["character"]!.GetValue<int>());
        Assert.Equal(expectedOffset, actual);
    }

    /// <summary>
    /// 重命名端到端（L3 第二片）：prepareRename 给区间与占位符；rename 返回
    /// WorkspaceEdit.changes——当前 .ui 与兄弟 .cs 各一个 uri，range 用各自原文换算行位。
    /// </summary>
    [Fact]
    public async Task Session_PrepareAndRenameProduceWorkspaceEdit()
    {
        var directory = Path.Combine(Path.GetTempPath(), "eclipseui-ide-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "Session.ui");
        const string text = "<TextBlock Text={Total} />";
        await File.WriteAllTextAsync(path, text);
        await File.WriteAllTextAsync(Path.ChangeExtension(path, ".cs"), """
            namespace Gallery;
            public partial class Session
            {
                public int Total => 42;
            }
            """);

        var catalogs = new CatalogProvider(new CatalogOptions
        {
            RuntimePath = Path.Combine(AppContext.BaseDirectory, "EclipseUI.Runtime.dll"),
            IncludeSiblingAssemblies = false,
            ProbeRoot = directory,
        });
        var uri = DocumentStore.PathToUri(path);
        var transcript = await RunSessionWith(catalogs,
            Request(1, "initialize"),
            Notification("initialized", new JsonObject()),
            DidOpen(uri, text),
            Request(2, "textDocument/prepareRename", Position(uri, 0, 18)),
            Request(3, "textDocument/rename", new JsonObject
            {
                ["textDocument"] = new JsonObject { ["uri"] = uri },
                ["position"] = new JsonObject { ["line"] = 0, ["character"] = 18 },
                ["newName"] = "Count",
            }));

        var capabilities = transcript.Single(m => m["id"]?.GetValue<int>() == 1)["result"]!["capabilities"]!;
        Assert.True(capabilities["renameProvider"]!["prepareProvider"]!.GetValue<bool>());

        var prepare = transcript.Single(m => m["id"]?.GetValue<int>() == 2)["result"]!;
        Assert.Equal("Total", prepare["placeholder"]!.GetValue<string>());

        var changes = transcript.Single(m => m["id"]?.GetValue<int>() == 3)["result"]!["changes"]!.AsObject();
        Assert.Equal(2, changes.Count);
        Assert.True(changes.ContainsKey(uri));
        var csUri = changes.Single(kvp => kvp.Key != uri).Key;
        Assert.EndsWith("Session.cs", csUri, StringComparison.Ordinal);

        // .ui 侧：{Total} 的 Total（character 是列号：{ 在 16，Total 17..22，5 个字母）。
        var uiEdit = changes[uri]![0]!;
        Assert.Equal("Count", uiEdit["newText"]!.GetValue<string>());
        Assert.Equal(17, uiEdit["range"]!["start"]!["character"]!.GetValue<int>());
        Assert.Equal(22, uiEdit["range"]!["end"]!["character"]!.GetValue<int>());

        // .cs 侧：属性声明的名字。用原文反算（列号 ≠ 偏移）。
        var csText = await File.ReadAllTextAsync(Path.ChangeExtension(path, ".cs"));
        var expectedOffset = csText.IndexOf("public int Total", StringComparison.Ordinal) + "public int ".Length;
        var positions = new PositionMap(csText);
        var csEdit = changes[csUri]![0]!;
        var actual = positions.ToOffset(
            csEdit["range"]!["start"]!["line"]!.GetValue<int>(),
            csEdit["range"]!["start"]!["character"]!.GetValue<int>());
        Assert.Equal(expectedOffset, actual);
    }

    [Fact]
    public async Task Session_HoverAndSymbolsRespond()
    {
        var uri = "file:///D:/workspace/Session.ui";
        var transcript = await RunSession(
            Request(1, "initialize"),
            Notification("initialized", new JsonObject()),
            DidOpen(uri, "@using EclipseUI.Elements\n\n<Button>+1</Button>\n"),
            Request(2, "textDocument/hover", Position(uri, 2, 3)),
            Request(3, "textDocument/documentSymbol", new JsonObject
            {
                ["textDocument"] = new JsonObject { ["uri"] = uri },
            }));

        var hover = transcript.Single(m => m["id"]?.GetValue<int>() == 2)["result"]!;
        Assert.Contains("EclipseUI.Elements.Button", hover["contents"]!["value"]!.GetValue<string>());

        var symbols = transcript.Single(m => m["id"]?.GetValue<int>() == 3)["result"]!.AsArray();
        Assert.Equal("Button", symbols[0]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Session_UnknownMethodReturnsMethodNotFound()
    {
        // P8 之后 formatting 已是受支持方法；这里换一个真正未实现的 LSP 方法。
        var transcript = await RunSession(Request(1, "textDocument/selectionRange"));
        var error = transcript.Single(m => m["id"]?.GetValue<int>() == 1)["error"]!;
        Assert.Equal(JsonRpcException.MethodNotFound, error["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task Session_CloseClearsDiagnostics()
    {
        var uri = "file:///D:/workspace/Session.ui";
        var transcript = await RunSession(
            Request(1, "initialize"),
            Notification("initialized", new JsonObject()),
            DidOpen(uri, "@using EclipseUI.Elements\n\n<Buton />\n"),
            Notification("textDocument/didClose", new JsonObject
            {
                ["textDocument"] = new JsonObject { ["uri"] = uri },
            }));

        var published = transcript
            .Where(m => m["method"]?.GetValue<string>() == "textDocument/publishDiagnostics")
            .ToArray();

        Assert.Equal(2, published.Length);
        Assert.Empty(published[^1]["params"]!["diagnostics"]!.AsArray());
    }

    // ---- §6.9.7 P5–P8 的协议级回归夹具 ----

    /// <summary>P8：四项新能力必须在 initialize 里如实声明，否则客户端不会发请求。</summary>
    [Fact]
    public async Task Session_AdvertisesEmbeddedCSharpCapabilities()
    {
        var transcript = await RunSession(Request(1, "initialize"));
        var capabilities = transcript.Single(m => m["id"]?.GetValue<int>() == 1)["result"]!["capabilities"]!;

        Assert.NotNull(capabilities["signatureHelpProvider"]);
        Assert.Equal(new[] { "(", "," }, capabilities["signatureHelpProvider"]!["triggerCharacters"]!
            .AsArray().Select(n => n!.GetValue<string>()).ToArray());
        Assert.True(capabilities["semanticTokensProvider"]!["full"]!.GetValue<bool>());
        Assert.Contains("method", capabilities["semanticTokensProvider"]!["legend"]!["tokenTypes"]!
            .AsArray().Select(n => n!.GetValue<string>()));
        Assert.Contains("quickfix", capabilities["codeActionProvider"]!["codeActionKinds"]!
            .AsArray().Select(n => n!.GetValue<string>()));
        Assert.True(capabilities["documentFormattingProvider"]!.GetValue<bool>());
        Assert.True(capabilities["documentRangeFormattingProvider"]!.GetValue<bool>());
    }

    /// <summary>P8 signature help：{expr} 里的方法调用给出重载与当前参数位。</summary>
    [Fact]
    public async Task Session_SignatureHelpResolvesInvocationInExpression()
    {
        const string uri = "file:///D:/workspace/Signature.ui";
        const string text = "@using System\n<TextBlock Text={Math.Max(1, 2)} />\n";
        var transcript = await RunSession(
            Request(1, "initialize"),
            Notification("initialized", new JsonObject()),
            DidOpen(uri, text),
            Request(2, "textDocument/signatureHelp", Position(uri, 1, 26)));

        var result = transcript.Single(m => m["id"]?.GetValue<int>() == 2)["result"]!;
        var signatures = result["signatures"]!.AsArray();
        Assert.NotEmpty(signatures);
        Assert.Contains("Max", string.Join(" ", signatures.Select(s => s!["label"]!.GetValue<string>())));
        Assert.True(result["activeParameter"]!.GetValue<int>() >= 0);
    }

    /// <summary>P8 semantic tokens：LSP 3.17 的五元组 delta 编码，且不越出内嵌 C# 区间。</summary>
    [Fact]
    public async Task Session_SemanticTokensAreDeltaEncoded()
    {
        const string uri = "file:///D:/workspace/Tokens.ui";
        const string text = "@using System\n<TextBlock Text={Math.Max(1, 2)} />\n";
        var transcript = await RunSession(
            Request(1, "initialize"),
            Notification("initialized", new JsonObject()),
            DidOpen(uri, text),
            Request(2, "textDocument/semanticTokens/full", new JsonObject
            {
                ["textDocument"] = new JsonObject { ["uri"] = uri },
            }));

        var data = transcript.Single(m => m["id"]?.GetValue<int>() == 2)["result"]!["data"]!.AsArray();
        Assert.NotEmpty(data);
        Assert.Equal(0, data.Count % 5);
        Assert.All(data, n => Assert.True(n!.GetValue<int>() >= 0));
        var legend = transcript.Single(m => m["id"]?.GetValue<int>() == 1)["result"]!
            ["capabilities"]!["semanticTokensProvider"]!["legend"]!["tokenTypes"]!.AsArray();
        for (var i = 3; i < data.Count; i += 5)
            Assert.True(data[i]!.GetValue<int>() < legend.Count);
    }

    /// <summary>P8 code action：模板级 EUI001 给确定性的「补 @using」修复，编辑落在 .ui 起始。</summary>
    [Fact]
    public async Task Session_CodeActionAddsMissingUsing()
    {
        const string uri = "file:///D:/workspace/Action.ui";
        var transcript = await RunSession(
            Request(1, "initialize"),
            Notification("initialized", new JsonObject()),
            DidOpen(uri, "<StackPanel />\n"),
            Request(2, "textDocument/codeAction", new JsonObject
            {
                ["textDocument"] = new JsonObject { ["uri"] = uri },
                ["range"] = new JsonObject
                {
                    ["start"] = new JsonObject { ["line"] = 0, ["character"] = 0 },
                    ["end"] = new JsonObject { ["line"] = 0, ["character"] = 14 },
                },
                ["context"] = new JsonObject { ["diagnostics"] = new JsonArray() },
            }));

        var actions = transcript.Single(m => m["id"]?.GetValue<int>() == 2)["result"]!.AsArray();
        Assert.Contains(actions, a => a!["title"]!.GetValue<string>().Contains("@using EclipseUI.Elements"));
        var fix = actions.Single(a => a!["title"]!.GetValue<string>().Contains("@using"))!;
        var edit = fix["edit"]!["changes"]![uri]![0]!;
        Assert.Equal("@using EclipseUI.Elements" + Environment.NewLine, edit["newText"]!.GetValue<string>());
    }

    /// <summary>P8 格式化 / 区间格式化：同一批编辑，range 只按请求区间过滤。</summary>
    [Fact]
    public async Task Session_FormattingAndRangeFormattingAgree()
    {
        const string uri = "file:///D:/workspace/Format.ui";
        const string text = "@using EclipseUI.Elements\n<StackPanel>\n   <Button />\n</StackPanel>\n";
        var transcript = await RunSession(
            Request(1, "initialize"),
            Notification("initialized", new JsonObject()),
            DidOpen(uri, text),
            Request(2, "textDocument/formatting", new JsonObject
            {
                ["textDocument"] = new JsonObject { ["uri"] = uri },
                ["options"] = new JsonObject { ["tabSize"] = 4, ["insertSpaces"] = true },
            }),
            Request(3, "textDocument/rangeFormatting", new JsonObject
            {
                ["textDocument"] = new JsonObject { ["uri"] = uri },
                ["range"] = new JsonObject
                {
                    ["start"] = new JsonObject { ["line"] = 0, ["character"] = 0 },
                    ["end"] = new JsonObject { ["line"] = 1, ["character"] = 0 },
                },
                ["options"] = new JsonObject { ["tabSize"] = 4, ["insertSpaces"] = true },
            }));

        var full = transcript.Single(m => m["id"]?.GetValue<int>() == 2)["result"]!.AsArray();
        var ranged = transcript.Single(m => m["id"]?.GetValue<int>() == 3)["result"]!.AsArray();
        Assert.NotEmpty(full);
        Assert.True(ranged.Count <= full.Count);
        Assert.All(full, e => Assert.NotNull(e!["newText"]));
    }

    /// <summary>
    /// Rider/lsp4ij 没有 VS Code 那种客户端静态 watcher，只认动态注册
    /// （lsp4ij 的 WatchedFilesTest）。不注册的话改 .cs/.csproj 后项目快照不重建。
    /// </summary>
    [Fact]
    public async Task Session_RegistersWatchersDynamicallyForRider()
    {
        var transcript = await RunSession(
            Request(1, "initialize"),
            Notification("initialized", new JsonObject()));

        var registration = Assert.Single(transcript,
            m => m["method"]?.GetValue<string>() == "client/registerCapability");
        Assert.Equal(0, registration["id"]!.GetValue<int>()); // 不与客户端请求的 1/2/3… 冲突

        var entry = Assert.Single(registration["params"]!["registrations"]!.AsArray());
        Assert.Equal("eclipseui-watched-files", entry!["id"]!.GetValue<string>());
        Assert.Equal("workspace/didChangeWatchedFiles", entry["method"]!.GetValue<string>());

        var globs = entry["registerOptions"]!["watchers"]!.AsArray()
            .Select(w => w!["globPattern"]!.GetValue<string>()).ToArray();
        Assert.Equal(new[]
        {
            "**/*.cs",
            "**/*.ui",
            "**/*.eustyle",
            "**/*.csproj",
            "**/Directory.Build.props",
            "**/obj/project.assets.json",
        }, globs);
    }

    /// <summary>P5 watcher：文件变更通知后重发已打开文档的诊断，无需手动 Reload。</summary>
    [Fact]
    public async Task Session_WatchedFilesRepublishDiagnostics()
    {
        const string uri = "file:///D:/workspace/Watched.ui";
        var transcript = await RunSession(
            Request(1, "initialize"),
            Notification("initialized", new JsonObject()),
            DidOpen(uri, "@using EclipseUI.Elements\n<Buton />\n"),
            Notification("workspace/didChangeWatchedFiles", new JsonObject
            {
                ["changes"] = new JsonArray
                {
                    new JsonObject { ["uri"] = "file:///D:/workspace/Widget.cs", ["type"] = 2 },
                },
            }));

        var published = transcript
            .Where(m => m["method"]?.GetValue<string>() == "textDocument/publishDiagnostics")
            .ToArray();
        Assert.True(published.Length >= 2);
        Assert.Contains(published[^1]["params"]!["diagnostics"]!.AsArray(),
            d => d!["code"]!.GetValue<string>() == "EUI001");
    }

    /// <summary>把一串消息喂给真实的 server（内存流，全会话跑完），回收 server 发出的所有消息。</summary>
    private static Task<JsonNode[]> RunSession(params JsonObject[] messages) =>
        RunSessionWith(new CatalogProvider(new CatalogOptions
        {
            RuntimePath = Path.Combine(AppContext.BaseDirectory, "EclipseUI.Runtime.dll"),
            IncludeSiblingAssemblies = false,
        }), messages);

    private static Task<JsonNode[]> RunSessionWith(CatalogProvider catalogs, params JsonObject[] messages) =>
        RunSessionWithProjects(catalogs, null, messages);

    private static async Task<JsonNode[]> RunSessionWithProjects(CatalogProvider catalogs,
        ProjectWorkspace? projects, params JsonObject[] messages)
    {
        var input = new MemoryStream();
        var writer = new LspMessageWriter(input);
        foreach (var message in messages) await writer.WriteAsync(message);
        input.Position = 0;

        var output = new MemoryStream();
        var service = new LanguageService(catalogs, new DocumentStore(), projects);
        var connection = new JsonRpcConnection(input, output);
        var host = new LanguageServerHost(connection, service, catalogs);
        connection.RequestHandler = host.HandleRequestAsync;
        connection.NotificationHandler = host.HandleNotificationAsync;
        host.ExitRequested = () => { };

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await connection.RunAsync(timeout.Token);

        output.Position = 0;
        var reader = new LspMessageReader(output);
        var transcript = new List<JsonNode>();
        while (true)
        {
            var message = await reader.ReadAsync(timeout.Token);
            if (message is null) break;
            transcript.Add(message);
        }
        return transcript.ToArray();
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "EclipseUI.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Cannot locate the repository root.");
    }

    /// <summary>每次只给一个字节的流：验证头部/体的分片读取不会跳字节。</summary>
    private sealed class DripStream : Stream
    {
        private readonly byte[] _data;
        private int _position;

        public DripStream(byte[] data) => _data = data;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _data.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= _data.Length) return 0;
            buffer[offset] = _data[_position++];
            return 1;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position >= _data.Length) return ValueTask.FromResult(0);
            buffer.Span[0] = _data[_position++];
            return ValueTask.FromResult(1);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

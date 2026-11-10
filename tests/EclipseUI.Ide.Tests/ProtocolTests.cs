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
        Assert.True(capabilities["documentSymbolProvider"]!.GetValue<bool>());
        Assert.Contains("<", capabilities["completionProvider"]!["triggerCharacters"]!
            .AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(1, capabilities["textDocumentSync"]!["change"]!.GetValue<int>()); // Full
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
        var transcript = await RunSession(Request(1, "textDocument/formatting"));
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

    /// <summary>把一串消息喂给真实的 server（内存流，全会话跑完），回收 server 发出的所有消息。</summary>
    private static Task<JsonNode[]> RunSession(params JsonObject[] messages) =>
        RunSessionWith(new CatalogProvider(new CatalogOptions
        {
            RuntimePath = Path.Combine(AppContext.BaseDirectory, "EclipseUI.Runtime.dll"),
            IncludeSiblingAssemblies = false,
        }), messages);

    private static async Task<JsonNode[]> RunSessionWith(CatalogProvider catalogs, params JsonObject[] messages)
    {
        var input = new MemoryStream();
        var writer = new LspMessageWriter(input);
        foreach (var message in messages) await writer.WriteAsync(message);
        input.Position = 0;

        var output = new MemoryStream();
        var service = new LanguageService(catalogs, new DocumentStore());
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

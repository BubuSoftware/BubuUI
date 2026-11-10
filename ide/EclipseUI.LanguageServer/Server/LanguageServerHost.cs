using System.Text.Json.Nodes;
using EclipseUI.Compiler;
using EclipseUI.LanguageServer.Analysis;
using EclipseUI.LanguageServer.Protocol;
using EclipseUI.LanguageServer.Workspace;

namespace EclipseUI.LanguageServer.Server;

/// <summary>
/// LSP 协议层：JSON ↔ <see cref="LanguageService"/> 模型。这里只做"翻译 + 能力声明"，
/// 任何语义判断都不落在这层——换 IDE（VS Code → Rider/lsp4ij）时这一层完全不动。
/// </summary>
public sealed class LanguageServerHost
{
    private readonly JsonRpcConnection _connection;
    private readonly LanguageService _service;
    private readonly CatalogProvider _catalogs;
    private readonly Action<string>? _log;

    private bool _snippetSupport = true;
    private bool _shutdownRequested;

    public LanguageServerHost(
        JsonRpcConnection connection,
        LanguageService service,
        CatalogProvider catalogs,
        Action<string>? log = null)
    {
        _connection = connection;
        _service = service;
        _catalogs = catalogs;
        _log = log;
    }

    /// <summary>收到 <c>exit</c> 通知（或 stdin 关闭）时触发，宿主据此结束进程。</summary>
    public Action? ExitRequested { get; set; }

    public int ExitCode => _shutdownRequested ? 0 : 1;

    public async Task<JsonNode?> HandleRequestAsync(JsonRpcRequest request)
    {
        switch (request.Method)
        {
            case "initialize":
                return Initialize(request.Parameters);

            case "shutdown":
                _shutdownRequested = true;
                return null;

            case "textDocument/completion":
                return Completion(request.Parameters);

            case "textDocument/hover":
                return Hover(request.Parameters);

            case "textDocument/documentSymbol":
                return DocumentSymbols(request.Parameters);

            case "eclipseui/reloadCatalog":
                _catalogs.Reload();
                await PublishAllDiagnosticsAsync().ConfigureAwait(false);
                return new JsonObject { ["tags"] = _catalogs.Catalog.Elements.Count };

            default:
                throw new JsonRpcException(JsonRpcException.MethodNotFound, $"unknown method '{request.Method}'");
        }
    }

    public async Task HandleNotificationAsync(string method, JsonNode? parameters)
    {
        switch (method)
        {
            case "initialized":
                // 目录没找到是**配置问题**，必须显式告诉人，不能让补全静默空着。
                if (_catalogs.Notes.Count > 0)
                {
                    await _connection.NotifyAsync("window/showMessage", new JsonObject
                    {
                        ["type"] = 2, // Warning
                        ["message"] = "EclipseUI: " + string.Join("\n", _catalogs.Notes),
                    }).ConfigureAwait(false);
                }
                _log?.Invoke($"catalog: {_catalogs.Catalog.Elements.Count} tags from " +
                             $"{_catalogs.RuntimePath ?? "<none>"}");
                break;

            case "textDocument/didOpen":
            {
                var (uri, text, version) = DocumentEvent(parameters);
                _service.Documents.Open(uri, text, version);
                await PublishDiagnosticsAsync(uri).ConfigureAwait(false);
                break;
            }

            case "textDocument/didChange":
            {
                var (uri, text, version) = DocumentEvent(parameters);
                // v1 只支持全文同步：didChange 的 contentChanges 里最后一条是全文。
                if (!_service.Documents.Change(uri, text, version))
                {
                    _service.Documents.Open(uri, text, version);
                    _service.Forget(uri);
                }
                await PublishDiagnosticsAsync(uri).ConfigureAwait(false);
                break;
            }

            case "textDocument/didClose":
            {
                var uri = String(parameters, "textDocument", "uri");
                if (uri is not null)
                {
                    _service.Documents.Close(uri);
                    _service.Forget(uri);
                    // 空诊断 = 清掉编辑器里残留的红线。
                    await PublishDiagnosticsAsync(uri, Array.Empty<UiDiagnostic>(), 0).ConfigureAwait(false);
                }
                break;
            }

            case "exit":
                ExitRequested?.Invoke();
                break;

            default:
                _log?.Invoke($"ignored notification '{method}'");
                break;
        }
    }

    // ---- 请求实现 ----

    private JsonNode? Initialize(JsonNode? parameters)
    {
        _snippetSupport = parameters?["capabilities"]?["textDocument"]?["completion"]
            ?["completionItem"]?["snippetSupport"]?.GetValue<bool>() ?? true;

        return new JsonObject
        {
            ["capabilities"] = new JsonObject
            {
                ["textDocumentSync"] = new JsonObject
                {
                    ["openClose"] = true,
                    ["change"] = (int)TextDocumentSyncKind.Full,
                },
                ["completionProvider"] = new JsonObject
                {
                    ["triggerCharacters"] = new JsonArray("<", "/", " ", "\"", "@", "-", ".", ":"),
                    ["resolveProvider"] = false,
                },
                ["hoverProvider"] = true,
                ["documentSymbolProvider"] = true,
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = "EclipseUI Language Server",
                ["version"] = typeof(LanguageServerHost).Assembly.GetName().Version?.ToString() ?? "0.1.0",
            },
        };
    }

    private JsonNode? Completion(JsonNode? parameters)
    {
        var (uri, offset) = Position(parameters);
        if (uri is null) return null;

        var list = _service.Complete(uri, offset);
        var items = new JsonArray();
        var document = _service.Documents.TryGet(uri, out var doc) ? doc : null;
        var positions = document is null ? null : new PositionMap(document.Text);

        foreach (var item in list.Items)
        {
            var entry = new JsonObject
            {
                ["label"] = item.Label,
                ["kind"] = (int)ToCompletionKind(item.Kind),
                ["insertTextFormat"] = item.IsSnippet && _snippetSupport
                    ? (int)InsertTextFormat.Snippet
                    : (int)InsertTextFormat.PlainText,
            };
            if (item.Detail is not null) entry["detail"] = item.Detail;
            if (item.Documentation is not null)
                entry["documentation"] = new JsonObject
                {
                    ["kind"] = "markdown",
                    ["value"] = item.Documentation,
                };
            if (item.SortText is not null) entry["sortText"] = item.SortText;

            // 显式 textEdit：让替换范围与扫描器判定的 token 一致（@bind- / Owner.Name 都靠它）。
            if (positions is not null)
            {
                entry["textEdit"] = new JsonObject
                {
                    ["range"] = ToRange(positions, list.Replacement),
                    ["newText"] = item.Text,
                };
            }
            else
            {
                entry["insertText"] = item.Text;
            }

            items.Add(entry);
        }

        return new JsonObject
        {
            ["isIncomplete"] = false,
            ["items"] = items,
        };
    }

    private JsonNode? Hover(JsonNode? parameters)
    {
        var (uri, offset) = Position(parameters);
        if (uri is null) return null;

        var hover = _service.Hover(uri, offset);
        if (hover is null) return null;

        var positions = _service.Documents.TryGet(uri, out var document) ? new PositionMap(document.Text) : null;
        var result = new JsonObject
        {
            ["contents"] = new JsonObject
            {
                ["kind"] = "markdown",
                ["value"] = hover.Markdown,
            },
        };
        if (positions is not null) result["range"] = ToRange(positions, hover.Span);
        return result;
    }

    private JsonNode? DocumentSymbols(JsonNode? parameters)
    {
        var uri = String(parameters, "textDocument", "uri");
        if (uri is null) return null;
        if (!_service.Documents.TryGet(uri, out var document)) return null;

        var positions = new PositionMap(document.Text);
        var array = new JsonArray();
        foreach (var symbol in _service.GetSymbols(uri))
            array.Add(ToSymbol(positions, symbol));
        return array;
    }

    // ---- 诊断 ----

    public Task PublishAllDiagnosticsAsync()
    {
        var tasks = _service.Documents.All.Select(d => PublishDiagnosticsAsync(d.Uri)).ToArray();
        return tasks.Length == 0 ? Task.CompletedTask : Task.WhenAll(tasks);
    }

    private Task PublishDiagnosticsAsync(string uri) =>
        PublishDiagnosticsAsync(uri, _service.GetDiagnostics(uri), CurrentVersion(uri));

    private int CurrentVersion(string uri) =>
        _service.Documents.TryGet(uri, out var document) ? document.Version : 0;

    private Task PublishDiagnosticsAsync(string uri, IReadOnlyList<UiDiagnostic> diagnostics, int version)
    {
        if (!_service.Documents.TryGet(uri, out var document) && diagnostics.Count > 0)
            return Task.CompletedTask;

        var positions = document is null ? null : new PositionMap(document.Text);
        var array = new JsonArray();

        if (positions is not null)
        {
            foreach (var diagnostic in diagnostics)
            {
                var entry = new JsonObject
                {
                    ["range"] = ToRange(positions, diagnostic.Span),
                    ["severity"] = (int)ToSeverity(diagnostic.Severity),
                    ["code"] = diagnostic.Code,
                    ["source"] = "eclipseui",
                    ["message"] = diagnostic.Message,
                };
                array.Add(entry);
            }
        }

        return _connection.NotifyAsync("textDocument/publishDiagnostics", new JsonObject
        {
            ["uri"] = uri,
            ["version"] = version,
            ["diagnostics"] = array,
        });
    }

    // ---- JSON ↔ 模型 ----

    private (string? Uri, int Offset) Position(JsonNode? parameters)
    {
        var uri = String(parameters, "textDocument", "uri");
        var line = Int(parameters?["position"], "line") ?? 0;
        var character = Int(parameters?["position"], "character") ?? 0;
        if (uri is null || !_service.Documents.TryGet(uri, out var document)) return (uri, 0);
        return (uri, new PositionMap(document.Text).ToOffset(line, character));
    }

    private static (string Uri, string Text, int Version) DocumentEvent(JsonNode? parameters)
    {
        var uri = String(parameters, "textDocument", "uri") ?? "";
        var version = Int(parameters?["textDocument"], "version") ?? 0;
        var changes = parameters?["contentChanges"] as JsonArray;
        var text = changes is { Count: > 0 }
            ? changes[^1]?["text"]?.GetValue<string>() ?? ""
            : parameters?["textDocument"]?["text"]?.GetValue<string>() ?? "";
        return (uri, text, version);
    }

    private static JsonObject ToRange(PositionMap positions, TextSpan span)
    {
        var (startLine, startCharacter) = positions.ToPosition(span.Start);
        var (endLine, endCharacter) = positions.ToPosition(span.End);
        return new JsonObject
        {
            ["start"] = new JsonObject { ["line"] = startLine, ["character"] = startCharacter },
            ["end"] = new JsonObject { ["line"] = endLine, ["character"] = endCharacter },
        };
    }

    private static JsonObject ToSymbol(PositionMap positions, UiSymbol symbol)
    {
        var children = new JsonArray();
        foreach (var child in symbol.Children) children.Add(ToSymbol(positions, child));

        return new JsonObject
        {
            ["name"] = symbol.Name,
            ["detail"] = symbol.Detail,
            ["kind"] = (int)ToSymbolKind(symbol.Kind),
            ["range"] = ToRange(positions, symbol.Span),
            ["selectionRange"] = ToRange(positions, symbol.SelectionSpan),
            ["children"] = children,
        };
    }

    private static DiagnosticSeverityDto ToSeverity(DiagnosticSeverity severity) => severity switch
    {
        DiagnosticSeverity.Error => DiagnosticSeverityDto.Error,
        DiagnosticSeverity.Warning => DiagnosticSeverityDto.Warning,
        _ => DiagnosticSeverityDto.Information,
    };

    private static CompletionKindDto ToCompletionKind(UiCompletionKind kind) => kind switch
    {
        UiCompletionKind.Tag => CompletionKindDto.Class,
        UiCompletionKind.Property => CompletionKindDto.Property,
        UiCompletionKind.Event => CompletionKindDto.Event,
        UiCompletionKind.Parameter => CompletionKindDto.Variable,
        UiCompletionKind.AttachedProperty => CompletionKindDto.Property,
        UiCompletionKind.Directive => CompletionKindDto.Keyword,
        UiCompletionKind.EnumMember => CompletionKindDto.EnumMember,
        UiCompletionKind.Keyword => CompletionKindDto.Keyword,
        UiCompletionKind.StyleProperty => CompletionKindDto.Property,
        UiCompletionKind.Namespace => CompletionKindDto.Module,
        _ => CompletionKindDto.Text,
    };

    private static SymbolKindDto ToSymbolKind(UiSymbolKind kind) => kind switch
    {
        UiSymbolKind.Control => SymbolKindDto.Class,
        UiSymbolKind.Component => SymbolKindDto.Object,
        UiSymbolKind.Block => SymbolKindDto.Module,
        _ => SymbolKindDto.Module,
    };

    private static string? String(JsonNode? node, params string[] path)
    {
        var current = node;
        foreach (var step in path) current = current?[step];
        return current?.GetValue<string>();
    }

    private static int? Int(JsonNode? node, string name)
    {
        var value = node?[name];
        return value is null ? null : (int)value.GetValue<double>();
    }

    // 协议枚举的数值（LSP 3.17）；用具名枚举避免魔数散落。
    private enum TextDocumentSyncKind { None = 0, Full = 1, Incremental = 2 }

    private enum InsertTextFormat { PlainText = 1, Snippet = 2 }

    private enum DiagnosticSeverityDto { Error = 1, Warning = 2, Information = 3, Hint = 4 }

    private enum CompletionKindDto
    {
        Text = 1, Method = 2, Function = 3, Constructor = 4, Field = 5, Variable = 6, Class = 7,
        Interface = 8, Module = 9, Property = 10, Unit = 11, Value = 12, Enum = 13, Keyword = 14,
        Snippet = 15, Color = 16, File = 17, Reference = 18, Folder = 19, EnumMember = 20,
        Constant = 21, Struct = 22, Event = 23, Operator = 24, TypeParameter = 25,
    }

    private enum SymbolKindDto
    {
        File = 1, Module = 2, Namespace = 3, Package = 4, Class = 5, Method = 6, Property = 7,
        Field = 8, Constructor = 9, Enum = 10, Interface = 11, Function = 12, Variable = 13,
        Constant = 14, String = 15, Number = 16, Boolean = 17, Array = 18, Object = 19,
    }
}

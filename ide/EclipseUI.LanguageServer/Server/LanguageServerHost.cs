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
    private readonly Dictionary<string, CancellationTokenSource> _tier2Debounce = new(StringComparer.Ordinal);
    // Retain a few lists so a delayed selection still describes its original snapshot.
    // Evict whole batches to bound the Roslyn documents captured by these callbacks.
    private readonly Dictionary<string, Lazy<Task<string?>>> _completionDescriptions = new(StringComparer.Ordinal);
    private readonly Queue<string[]> _completionBatches = new();
    private long _nextCompletionId;

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
                return await CompletionAsync(request.Parameters).ConfigureAwait(false);

            case "completionItem/resolve":
                return await ResolveCompletionAsync(request.Parameters).ConfigureAwait(false);

            case "textDocument/hover":
                return Hover(request.Parameters);

            case "textDocument/definition":
                return Definition(request.Parameters);

            case "textDocument/prepareRename":
                return await PrepareRenameAsync(request.Parameters).ConfigureAwait(false);

            case "textDocument/rename":
                return await RenameAsync(request.Parameters).ConfigureAwait(false);

            case "textDocument/documentSymbol":
                return DocumentSymbols(request.Parameters);

            // L8 内嵌 C# 完整语言服务（§6.9.0 第 5 条）：同一份项目 Compilation + 同一套回映。
            case "textDocument/signatureHelp":
                return SignatureHelp(request.Parameters);

            case "textDocument/semanticTokens/full":
                return await SemanticTokensAsync(request.Parameters).ConfigureAwait(false);

            case "textDocument/codeAction":
                return CodeActions(request.Parameters);

            case "textDocument/formatting":
                return Formatting(request.Parameters);

            case "textDocument/rangeFormatting":
                return RangeFormatting(request.Parameters);

            case "eclipseui/reloadCatalog":
                _service.Reload();
                await PublishAllDiagnosticsAsync().ConfigureAwait(false);
                return new JsonObject { ["tags"] = _service.Catalog.Elements.Count };

            // 反编译虚拟文档内容（eui-decompiled:）：VS Code 的 TextDocumentContentProvider
            // 按 scheme 取文本时回传 uri，服务端从缓存反编译文本里取。
            case "eclipseui/decompiledContent":
                return DecompiledContent(request.Parameters);

            // design/18 §6 阶段 0：当前 .ui 的生成 C# + 双向映射表（对标 razor/generatedDocumentContents）。
            case "eclipseui/generatedDocument":
                return GeneratedDocument(request.Parameters);

            default:
                throw new JsonRpcException(JsonRpcException.MethodNotFound, $"unknown method '{request.Method}'");
        }
    }

    public async Task HandleNotificationAsync(string method, JsonNode? parameters)
    {
        switch (method)
        {
            case "initialized":
            {
                // L7 watcher 动态注册：VS Code 客户端自己带一组静态 watcher，
                // 但 Rider/lsp4ij 没有——它只认 client/registerCapability（实测
                // lsp4ij WatchedFilesTest）。不注册的话 Rider 上改 .cs/.csproj
                // 不会重建项目快照，补全一直用旧目录。这样注册两边都走标准 LSP。
                await RegisterWatchersAsync().ConfigureAwait(false);

                // 先强制完成目录构建，再读 Notes。后台预热与 initialized 可能并发：
                // 若先读 Notes 会得到空列表，"目录没找到"的警告被静默吞掉，编辑器表现为
                // "高亮正常、补全空、零诊断"，用户无从判断是配置问题还是代码没问题。
                // No document has selected a project yet. Building DefaultCatalog here
                // compiles an arbitrary workspace project (often a large test project),
                // then didOpen immediately rebuilds the actual owner's compilation.
                var count = _catalogs.Catalog.Elements.Count;
                var notes = _service.Notes;
                if (notes.Count > 0)
                {
                    await _connection.NotifyAsync("window/showMessage", new JsonObject
                    {
                        ["type"] = 2, // Warning
                        ["message"] = "EclipseUI: " + string.Join("\n", notes),
                    }).ConfigureAwait(false);
                }
                _log?.Invoke($"catalog: {count} tags from {_catalogs.RuntimePath ?? "<none>"}; " +
                             "project catalogs load with documents");
                // 兜住 didOpen 早于目录就绪的窗口：目录一就绪就重发已打开文档的诊断。
                await PublishAllDiagnosticsAsync().ConfigureAwait(false);
                break;
            }

            case "textDocument/didOpen":
            {
                var (uri, text, version) = DocumentEvent(parameters);
                _service.Documents.Open(uri, text, version);
                _service.ApplyBuffer(uri, text);
                // 归属是全部能力的前提：0 个项目 = 没有目录、没有诊断、跳转全空。
                // 把这条打出来，"打开的文件不在任何项目里"就不再是无声失败。
                var owners = _service.Snapshots(uri).Count;
                _log?.Invoke($"open {Path.GetFileName(DocumentStore.UriToPath(uri))} · {owners} project(s)");
                if (owners == 0)
                    _log?.Invoke($"  ! {DocumentStore.UriToPath(uri)} is not in any project's AdditionalFiles");
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
                }
                _service.Forget(uri);
                _service.ApplyBuffer(uri, text);
                // Tier 1（~1ms）每次按键立即发；Tier 2（跑生成器 + 全编译绑定）
                // debounce 400ms，且只在文档版本未变时发布（§6.9.4 / R3）。
                await PublishDiagnosticsAsync(uri, _service.GetDiagnostics(uri, includeTier2: false), version)
                    .ConfigureAwait(false);
                _ = PublishTier2DebouncedAsync(uri, version);
                break;
            }

            // L7：watcher 变更 → 标记项目 dirty → 重建 + 重发已打开文档诊断。
            case "workspace/didChangeWatchedFiles":
            {
                var changes = parameters?["changes"] as JsonArray;
                var paths = changes is null
                    ? Array.Empty<string>()
                    : changes
                        .Select(change => DocumentStore.UriToPath(
                            change?["uri"]?.GetValue<string>() ?? ""))
                        .Where(static path => path.Length > 0)
                        .ToArray();
                _service.Invalidate(paths);
                await PublishAllDiagnosticsAsync().ConfigureAwait(false);
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

    /// <summary>
    /// L7 依赖的文件模式：项目 Compilation 的输入与结构文件。必须与
    /// <c>ide/vscode/client/extension.js</c> 的 <c>WATCH_PATTERNS</c> 一致
    /// （<c>ide/rider/test/plugin.test.js</c> 会把三处对死）。
    /// </summary>
    private static readonly string[] WatcherGlobs =
    {
        "**/*.cs",
        "**/*.ui",
        "**/*.eustyle",
        "**/*.csproj",
        "**/Directory.Build.props",
        "**/obj/project.assets.json",
    };

    private Task RegisterWatchersAsync()
    {
        var watchers = new JsonArray();
        foreach (var glob in WatcherGlobs)
            watchers.Add(new JsonObject { ["globPattern"] = glob });

        return _connection.RequestAsync("client/registerCapability", new JsonObject
        {
            ["registrations"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "eclipseui-watched-files",
                    ["method"] = "workspace/didChangeWatchedFiles",
                    ["registerOptions"] = new JsonObject { ["watchers"] = watchers },
                },
            },
        });
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
                    // "(" 服务 @code 内 `if (|` 这类 C# 上下文（design/18 阶段 2）。
                    ["triggerCharacters"] = new JsonArray("<", "/", " ", "\"", "@", "-", ".", ":", "("),
                    ["resolveProvider"] = true,
                },
                ["hoverProvider"] = true,
                ["definitionProvider"] = true,
                ["renameProvider"] = new JsonObject { ["prepareProvider"] = true },
                ["documentSymbolProvider"] = true,
                ["signatureHelpProvider"] = new JsonObject
                {
                    ["triggerCharacters"] = new JsonArray("(", ","),
                },
                ["semanticTokensProvider"] = new JsonObject
                {
                    ["legend"] = new JsonObject
                    {
                        ["tokenTypes"] = StringArray(SemanticTokenTypes.All),
                        ["tokenModifiers"] = new JsonArray("static", "readonly"),
                    },
                    ["full"] = true,
                    ["range"] = false,
                },
                ["codeActionProvider"] = new JsonObject
                {
                    ["codeActionKinds"] = new JsonArray("quickfix"),
                },
                ["documentFormattingProvider"] = true,
                ["documentRangeFormattingProvider"] = true,
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = "EclipseUI Language Server",
                ["version"] = typeof(LanguageServerHost).Assembly.GetName().Version?.ToString() ?? "0.1.0",
            },
        };
    }

    private async Task<JsonNode?> CompletionAsync(JsonNode? parameters)
    {
        var (uri, offset) = Position(parameters);
        if (uri is null) return null;

        // LSP triggerKind：1=Invoked，2=TriggerCharacter，3=TriggerForIncompleteCompletions。
        // 只有 2 带 triggerCharacter；其余按主动调用（Roslyn CompletionTrigger.Invoke）。
        char? trigger = null;
        if (Int(parameters?["context"], "triggerKind") == 2)
        {
            var character = String(parameters?["context"], "triggerCharacter");
            if (character is { Length: > 0 }) trigger = character[0];
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var list = await _service.CompleteAsync(uri, offset, trigger).ConfigureAwait(false);
        var items = new JsonArray();
        var descriptionKeys = new List<string>();
        var document = _service.Documents.TryGet(uri, out var doc) ? doc : null;
        var positions = document is null ? null : new PositionMap(document.Text);

        foreach (var item in list.Items)
        {
            var insertion = item.Text;
            var snippet = item.IsSnippet;
            if (item.CaretOffset is { } caret && !snippet && _snippetSupport)
            {
                caret = Math.Clamp(caret, 0, insertion.Length);
                insertion = EscapeSnippet(insertion[..caret]) + "$0" + EscapeSnippet(insertion[caret..]);
                snippet = true;
            }
            if (snippet && !_snippetSupport) insertion = PlainSnippet(insertion);
            var entry = new JsonObject
            {
                ["label"] = item.Label,
                ["kind"] = (int)ToCompletionKind(item.Kind),
                ["insertTextFormat"] = snippet && _snippetSupport
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
            if (item.DocumentationResolver is { } resolver)
            {
                var key = (++_nextCompletionId).ToString(System.Globalization.CultureInfo.InvariantCulture);
                _completionDescriptions[key] = new Lazy<Task<string?>>(() => resolver(CancellationToken.None));
                descriptionKeys.Add(key);
                entry["data"] = new JsonObject { ["eclipseuiCompletion"] = key };
            }
            if (item.SortText is not null) entry["sortText"] = item.SortText;
            if (item.FilterText is not null) entry["filterText"] = item.FilterText;
            if (item.Preselect) entry["preselect"] = true;
            if (item.CommitCharacters is { Count: > 0 } commits)
            {
                var array = new JsonArray();
                foreach (var commit in commits) array.Add(commit);
                entry["commitCharacters"] = array;
            }
            // LSP tags 只支持 Deprecated=1；Roslyn 4.9.2 的公开补全项没有弃用标记，
            // 目录候选将来可用 Tags 表达。其余 Roslyn 标签已用于派生 kind。
            if (item.Tags is not null && item.Tags.Contains("Deprecated"))
                entry["tags"] = new JsonArray(1);

            // 显式 textEdit：per-item 区间优先（Roslyn 候选每项自带），共享区间兜底
            // （@bind- / Owner.Name 这些目录候选靠它对齐扫描器判定的 token）。
            if (positions is not null)
            {
                entry["textEdit"] = new JsonObject
                {
                    ["range"] = ToRange(positions, item.Replacement ?? list.Replacement),
                    ["newText"] = insertion,
                };
                if (item.AdditionalEdits is { Count: > 0 } additional)
                {
                    var edits = new JsonArray();
                    foreach (var edit in additional)
                        edits.Add(new JsonObject
                        {
                            ["range"] = ToRange(positions, edit.Span),
                            ["newText"] = edit.NewText,
                        });
                    entry["additionalTextEdits"] = edits;
                }
            }
            else
            {
                entry["insertText"] = insertion;
            }

            items.Add(entry);
        }

        _completionBatches.Enqueue(descriptionKeys.ToArray());
        while (_completionBatches.Count > 3)
            foreach (var key in _completionBatches.Dequeue()) _completionDescriptions.Remove(key);
        _log?.Invoke($"completion: {Path.GetFileName(DocumentStore.UriToPath(uri))} @{offset} " +
                     $"-> {items.Count} items in {watch.ElapsedMilliseconds}ms");

        return new JsonObject
        {
            ["isIncomplete"] = list.IsIncomplete,
            ["items"] = items,
        };
    }

    private async Task<JsonNode?> ResolveCompletionAsync(JsonNode? parameters)
    {
        if (parameters is not JsonObject item)
            throw new JsonRpcException(JsonRpcException.InvalidParams, "completion item must be an object");
        var result = (JsonObject)item.DeepClone();
        if (item["data"] is JsonObject data
            && data["eclipseuiCompletion"] is JsonValue value && value.TryGetValue<string>(out var key)
            && _completionDescriptions.TryGetValue(key, out var description)
            && await description.Value.ConfigureAwait(false) is { Length: > 0 } documentation)
            result["documentation"] = new JsonObject { ["kind"] = "markdown", ["value"] = documentation };
        return result;
    }

    private static string EscapeSnippet(string text) => text.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("$", "\\$", StringComparison.Ordinal).Replace("}", "\\}", StringComparison.Ordinal);

    private static string PlainSnippet(string text)
    {
        // Template snippets have ordinary defaults and tab stops; Roslyn edits use plain
        // text + CaretOffset, so clients without snippet support always receive valid C#.
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\$\{\d+:([^{}]*)\}", "$1");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\$\{?\d+\}?", "");
        return text.Replace("\\$", "$", StringComparison.Ordinal).Replace("\\}", "}", StringComparison.Ordinal)
            .Replace("\\\\", "\\", StringComparison.Ordinal);
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

    /// <summary>
    /// 跳转定义（L3）：本文档目标用 .ui 的 PositionMap；磁盘目标用 UiDefinition.Text
    /// （与偏移同源的原文——工作区扫描/仓库源码目标从此不再错位）；反编译虚拟文档
    /// （eui-decompiled:）用缓存的反编译文本，uri 原样透传给客户端内容提供器。
    /// </summary>
    private JsonNode? Definition(JsonNode? parameters)
    {
        var (uri, offset) = Position(parameters);
        if (uri is null) return null;

        var definition = _service.Definition(uri, offset);
        // F12 是「没有反馈」最容易的表现形式：跳不动时用户只能看到什么也没发生。
        // 一行日志把「哪个文件、哪个偏移、有没有结果」钉下来，是最快的自诊断路径。
        _log?.Invoke(definition is null
            ? $"definition: no result · {Path.GetFileName(DocumentStore.UriToPath(uri))} @{offset}"
            : $"definition: {Path.GetFileName(DocumentStore.UriToPath(uri))} @{offset} -> "
              + (definition.Uri is null ? "<self>" : Path.GetFileName(definition.Uri)));
        if (definition is null) return null;

        string targetUri;
        PositionMap positions;
        if (definition.Uri is null)
        {
            if (!_service.Documents.TryGet(uri, out var document)) return null;
            targetUri = uri;
            positions = new PositionMap(document.Text);
        }
        else if (definition.Uri.StartsWith(DecompileProvider.Scheme + ":", StringComparison.Ordinal))
        {
            var decompiled = _service.DecompiledContent(definition.Uri);
            if (decompiled is null) return null;
            targetUri = definition.Uri;
            positions = new PositionMap(decompiled);
        }
        else if (definition.Text is not null)
        {
            targetUri = DocumentStore.PathToUri(definition.Uri);
            positions = new PositionMap(definition.Text);
        }
        else
        {
            var analysis = _service.Analyze(uri);
            if (analysis?.CodeBehindText is null) return null;
            targetUri = DocumentStore.PathToUri(definition.Uri);
            positions = new PositionMap(analysis.CodeBehindText);
        }

        return new JsonObject
        {
            ["uri"] = targetUri,
            ["range"] = ToRange(positions, definition.Span),
        };
    }

    /// <summary>反编译虚拟文档内容（eclipseui/decompiledContent）：客户端内容提供器
    /// 按 eui-decompiled: uri 回来取文本；缓存未命中（服务端重启过）返回 null，
    /// 客户端显示空文档而不是旧缓存。</summary>
    private JsonNode? DecompiledContent(JsonNode? parameters)
    {
        var uri = String(parameters, "uri");
        if (uri is null) return null;
        var content = _service.DecompiledContent(uri);
        return new JsonObject { ["content"] = content };
    }

    /// <summary>
    /// 生成文档查看（eclipseui/generatedDocument，design/18 §6 阶段 0）：返回生成 C# 全文、
    /// hint name 与映射表。没有项目归属/错误短路没产出时返回 null，客户端显示空文档。
    /// </summary>
    private JsonNode? GeneratedDocument(JsonNode? parameters)
    {
        var uri = String(parameters, "uri");
        if (uri is null) return null;
        var generated = _service.GeneratedDocumentFor(uri);
        if (generated is null) return null;

        var mappings = new JsonArray();
        foreach (var mapping in generated.Mappings.Mappings)
            mappings.Add(new JsonObject
            {
                ["originalStart"] = mapping.Original.Start,
                ["originalLength"] = mapping.Original.Length,
                ["generatedStart"] = mapping.Generated.Start,
                ["generatedLength"] = mapping.Generated.Length,
                ["verbatim"] = mapping.Verbatim,
            });
        return new JsonObject
        {
            ["hintName"] = generated.HintName,
            ["content"] = generated.Tree.ToString(),
            ["mappings"] = mappings,
        };
    }

    /// <summary>
    /// 重命名（L3 第二片；阶段 3 起项目模式走 Roslyn Renamer）。prepareRename 告诉客户端
    /// "这里能不能改"；rename 返回 WorkspaceEdit.changes——当前 .ui 用请求里的 uri，
    /// 其他 .ui / 兄弟 .cs / 工作区 .cs 换成磁盘路径的 file uri，各自用自己原文的
    /// PositionMap 换算行位。
    /// </summary>
    private async Task<JsonNode?> PrepareRenameAsync(JsonNode? parameters)
    {
        var (uri, offset) = Position(parameters);
        if (uri is null) return null;

        var prepare = await _service.PrepareRenameAsync(uri, offset).ConfigureAwait(false);
        if (prepare is null) return null;
        if (!_service.Documents.TryGet(uri, out var document)) return null;
        var positions = new PositionMap(document.Text);

        return new JsonObject
        {
            ["range"] = ToRange(positions, prepare.Span),
            ["placeholder"] = prepare.Placeholder,
        };
    }

    private async Task<JsonNode?> RenameAsync(JsonNode? parameters)
    {
        var (uri, offset) = Position(parameters);
        if (uri is null) return null;
        var newName = String(parameters, "newName");
        if (string.IsNullOrEmpty(newName)) return null;

        var result = await _service.RenameAsync(uri, offset, newName).ConfigureAwait(false);
        if (result is null) return null;

        var changes = new JsonObject();
        foreach (var file in result.Files)
        {
            var fileUri = file.Path is null ? uri : DocumentStore.PathToUri(file.Path);
            var positions = new PositionMap(file.Text);
            var array = new JsonArray();
            foreach (var edit in file.Edits)
                array.Add(new JsonObject
                {
                    ["range"] = ToRange(positions, edit.Span),
                    ["newText"] = edit.NewText,
                });
            changes[fileUri] = array;
        }
        return new JsonObject { ["changes"] = changes };
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

    // ---- L8：内嵌 C# 完整语言服务（design/06 §6.9.3 L8） ----

    private JsonNode? SignatureHelp(JsonNode? parameters)
    {
        var (uri, offset) = Position(parameters);
        if (uri is null) return null;
        var analysis = _service.Analyze(uri);
        if (analysis is null) return null;
        var help = _service.SignatureHelp(uri, offset);
        if (help is null || help.Signatures.Count == 0) return null;

        var signatures = new JsonArray();
        foreach (var signature in help.Signatures)
        {
            var parametersArray = new JsonArray();
            foreach (var parameter in signature.Parameters)
                parametersArray.Add(new JsonObject
                {
                    ["label"] = parameter.Label,
                    ["documentation"] = parameter.Documentation,
                });
            signatures.Add(new JsonObject
            {
                ["label"] = signature.Label,
                ["documentation"] = signature.Documentation,
                ["parameters"] = parametersArray,
            });
        }
        return new JsonObject
        {
            ["signatures"] = signatures,
            ["activeSignature"] = help.ActiveSignature,
            ["activeParameter"] = help.ActiveParameter,
        };
    }

    /// <summary>semanticTokens/full：只编码 {expr} / @code 区间，delta 编码由这里做（LSP 3.17）。
    /// 项目模式的分类走 Roslyn Classifier（异步），legacy 回落探针投影。</summary>
    private async Task<JsonNode?> SemanticTokensAsync(JsonNode? parameters)
    {
        var uri = String(parameters, "textDocument", "uri");
        if (uri is null) return null;
        var data = new JsonArray();
        var lastLine = 0;
        var lastCharacter = 0;
        foreach (var token in await _service.SemanticTokensAsync(uri).ConfigureAwait(false))
        {
            var deltaLine = token.Line - lastLine;
            data.Add(deltaLine);
            data.Add(deltaLine == 0 ? token.Character - lastCharacter : token.Character);
            data.Add(token.Length);
            data.Add(token.TokenType);
            data.Add(token.TokenModifiers);
            lastLine = token.Line;
            lastCharacter = token.Character;
        }
        return new JsonObject { ["data"] = data };
    }

    private JsonNode? CodeActions(JsonNode? parameters)
    {
        var uri = String(parameters, "textDocument", "uri");
        if (uri is null) return null;
        var analysis = _service.Analyze(uri);
        if (analysis is null) return new JsonArray();

        var actions = _service.CodeActions(uri);
        var result = new JsonArray();
        foreach (var action in actions)
        {
            string targetUri;
            string text;
            if (action.Path is null)
            {
                targetUri = uri;
                text = analysis.Text;
            }
            else
            {
                targetUri = DocumentStore.PathToUri(action.Path);
                text = File.Exists(action.Path) ? File.ReadAllText(action.Path) : "";
            }
            result.Add(new JsonObject
            {
                ["title"] = action.Title,
                ["kind"] = action.Kind,
                ["edit"] = new JsonObject
                {
                    ["changes"] = new JsonObject
                    {
                        [targetUri] = ToTextEdits(new PositionMap(text), action.Edits),
                    },
                },
            });
        }
        return result;
    }

    private JsonNode? Formatting(JsonNode? parameters)
    {
        var uri = String(parameters, "textDocument", "uri");
        if (uri is null) return null;
        var analysis = _service.Analyze(uri);
        return analysis is null
            ? new JsonArray()
            : ToTextEdits(analysis.Positions, _service.Format(uri));
    }

    private JsonNode? RangeFormatting(JsonNode? parameters)
    {
        var uri = String(parameters, "textDocument", "uri");
        if (uri is null) return null;
        var analysis = _service.Analyze(uri);
        if (analysis is null) return new JsonArray();

        var range = parameters?["range"];
        var start = ToOffset(analysis.Positions, range?["start"]);
        var end = ToOffset(analysis.Positions, range?["end"]);
        return ToTextEdits(analysis.Positions,
            _service.Format(uri)
                .Where(edit => edit.Span.Start >= start && edit.Span.Start <= end));
    }

    private static int ToOffset(PositionMap positions, JsonNode? position) =>
        positions.ToOffset(Int(position, "line") ?? 0, Int(position, "character") ?? 0);

    private static JsonArray ToTextEdits(PositionMap positions, IEnumerable<UiTextEdit> edits)
    {
        var array = new JsonArray();
        foreach (var edit in edits)
            array.Add(new JsonObject
            {
                ["range"] = ToRange(positions, edit.Span),
                ["newText"] = edit.NewText,
            });
        return array;
    }

    private static JsonArray StringArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values) array.Add(value);
        return array;
    }

    // ---- 诊断 ----

    /// <summary>
    /// Tier 2 debounce：连续按键只跑最后一次全编译；版本变了（用户又敲了）就丢弃这次结果。
    /// 不做精细增量缓存（§6.9.2 第 3 条），但也不让每次按键都触发 GetDiagnostics。
    /// </summary>
    private async Task PublishTier2DebouncedAsync(string uri, int version)
    {
        CancellationTokenSource source;
        lock (_tier2Debounce)
        {
            if (_tier2Debounce.TryGetValue(uri, out var previous))
            {
                previous.Cancel();
                previous.Dispose();
            }
            source = new CancellationTokenSource();
            _tier2Debounce[uri] = source;
        }

        try
        {
            await Task.Delay(400, source.Token).ConfigureAwait(false);
            if (CurrentVersion(uri) != version) return;
            await PublishDiagnosticsAsync(uri).ConfigureAwait(false);
            // Theme/template edits change diagnostics in their consumers even before save.
            foreach (var related in _service.RelatedDocuments(uri))
            {
                source.Token.ThrowIfCancellationRequested();
                if (CurrentVersion(uri) != version) return;
                var relatedVersion = CurrentVersion(related);
                var diagnostics = _service.GetDiagnostics(related);
                if (CurrentVersion(related) == relatedVersion)
                    await PublishDiagnosticsAsync(related, diagnostics, relatedVersion).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 被后续按键取消：正常路径，不需要日志。
        }
        finally
        {
            lock (_tier2Debounce)
            {
                if (_tier2Debounce.TryGetValue(uri, out var current) && ReferenceEquals(current, source))
                    _tier2Debounce.Remove(uri);
            }
        }
    }

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
        UiCompletionKind.LiteralValue => CompletionKindDto.Value,
        UiCompletionKind.Keyword => CompletionKindDto.Keyword,
        UiCompletionKind.StyleProperty => CompletionKindDto.Property,
        UiCompletionKind.Namespace => CompletionKindDto.Module,
        UiCompletionKind.Field => CompletionKindDto.Field,
        UiCompletionKind.Method => CompletionKindDto.Method,
        UiCompletionKind.Variable => CompletionKindDto.Variable,
        UiCompletionKind.Class => CompletionKindDto.Class,
        UiCompletionKind.Interface => CompletionKindDto.Interface,
        UiCompletionKind.Struct => CompletionKindDto.Struct,
        UiCompletionKind.Enum => CompletionKindDto.Enum,
        UiCompletionKind.Constant => CompletionKindDto.Constant,
        UiCompletionKind.TypeParameter => CompletionKindDto.TypeParameter,
        UiCompletionKind.Operator => CompletionKindDto.Operator,
        UiCompletionKind.Snippet => CompletionKindDto.Snippet,
        UiCompletionKind.Text => CompletionKindDto.Text,
        _ => CompletionKindDto.Text,
    };

    private static SymbolKindDto ToSymbolKind(UiSymbolKind kind) => kind switch
    {
        UiSymbolKind.Control => SymbolKindDto.Class,
        UiSymbolKind.UiObject => SymbolKindDto.Object,
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

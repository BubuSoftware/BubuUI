using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace BubuUI.LanguageServer.Protocol;

/// <summary>一条 JSON-RPC 请求；<see cref="Id"/> 原样回声（LSP 允许数字或字符串 id）。</summary>
public sealed record JsonRpcRequest(JsonNode Id, string Method, JsonNode? Parameters,
    CancellationToken CancellationToken = default);

/// <summary>带 JSON-RPC 错误码的异常；宿主转成 <c>error</c> 响应而不是崩进程。</summary>
public sealed class JsonRpcException : Exception
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;
    public const int RequestCancelled = -32800;

    public JsonRpcException(int code, string message) : base(message) => Code = code;

    public int Code { get; }
}

/// <summary>
/// 最小 JSON-RPC 2.0 over LSP 派发器：请求/通知两路，响应由宿主用
/// <see cref="RespondAsync"/>/<see cref="NotifyAsync"/> 主动发。
/// 文档同步和请求仍按到达顺序执行。独立读循环即时处理取消通知，避免
/// 过期补全阻塞后续输入；读循环不访问文档或语言服务状态。
/// </summary>
public sealed class JsonRpcConnection
{
    private readonly LspMessageReader _reader;
    private readonly LspMessageWriter _writer;
    private readonly Action<string>? _log;

    // server→client 请求的 id 从 0 起，避开 LSP 客户端惯用的 1/2/3…；
    // 派发是单线程的，不需要原子计数。
    private int _nextRequestId;

    public JsonRpcConnection(Stream input, Stream output, Action<string>? log = null)
    {
        _reader = new LspMessageReader(input);
        _writer = new LspMessageWriter(output);
        _log = log;
    }

    /// <summary>请求处理器；返回值作为 <c>result</c>（<c>null</c> → JSON <c>null</c>）。</summary>
    public Func<JsonRpcRequest, Task<JsonNode?>>? RequestHandler { get; set; }

    /// <summary>通知处理器（无 id，不回包）。</summary>
    public Func<string, JsonNode?, Task>? NotificationHandler { get; set; }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pending = new Dictionary<string, CancellationTokenSource>(StringComparer.Ordinal);
        var gate = new object();
        var queue = Channel.CreateUnbounded<(JsonNode Message, string? Key, CancellationTokenSource? Source)>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

        // A separate task also keeps reading when a handler does synchronous Roslyn work.
        var reader = Task.Run(async () =>
        {
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    // Console stdin on some hosts cannot interrupt an already pending read.
                    // Stop waiting on exit even if the stream ignores cancellation.
                    var message = await _reader.ReadAsync(lifetime.Token).WaitAsync(lifetime.Token)
                        .ConfigureAwait(false);
                    if (message is null) break;
                    string? key = null;
                    CancellationTokenSource? source = null;
                    if (message is JsonObject obj)
                    {
                        if (obj["method"] is JsonValue method && method.TryGetValue<string>(out var name))
                        {
                            if (name == "$/cancelRequest" && obj["id"] is null)
                            {
                                var cancelledId = (obj["params"] as JsonObject)?["id"];
                                if (cancelledId is JsonValue)
                                    lock (gate)
                                        if (pending.TryGetValue(cancelledId.ToJsonString(), out var cancelled))
                                            cancelled.Cancel();
                                continue;
                            }
                            if (obj["id"] is JsonValue id)
                            {
                                // JSON encoding distinguishes numeric 1 from string "1".
                                key = id.ToJsonString();
                                source = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                                lock (gate) pending[key] = source;
                            }
                        }
                    }
                    queue.Writer.TryWrite((message, key, source));
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                _log?.Invoke($"transport closed: {ex.Message}");
            }
            finally { queue.Writer.TryComplete(); }
        }, CancellationToken.None);

        try
        {
            await foreach (var entry in queue.Reader.ReadAllAsync(lifetime.Token).ConfigureAwait(false))
            {
                try
                {
                    lifetime.Token.ThrowIfCancellationRequested();
                    await DispatchAsync(entry.Message, entry.Source?.Token ?? lifetime.Token, lifetime.Token)
                        .ConfigureAwait(false);
                }
                finally
                {
                    lock (gate)
                    {
                        if (entry.Key is not null && pending.TryGetValue(entry.Key, out var current)
                            && ReferenceEquals(current, entry.Source)) pending.Remove(entry.Key);
                        entry.Source?.Dispose();
                    }
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally
        {
            lifetime.Cancel();
            await reader.ConfigureAwait(false);
            while (queue.Reader.TryRead(out var entry)) entry.Source?.Dispose();
        }
    }

    private async Task DispatchAsync(JsonNode message, CancellationToken requestToken,
        CancellationToken cancellationToken)
    {
        if (message is not JsonObject obj)
        {
            await WriteErrorAsync(null, JsonRpcException.InvalidRequest, "message must be a JSON object",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var method = obj["method"]?.GetValue<string>();
        var id = obj["id"];
        var parameters = obj["params"];

        if (method is null)
        {
            // 客户端对我们发出的请求（目前只有 client/registerCapability）的响应。
            // 注册失败只是让 watcher 降级，不影响会话，忽略响应/错误即可。
            return;
        }

        if (id is null)
        {
            try
            {
                if (NotificationHandler is not null)
                    await NotificationHandler(method, parameters).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 通知没有回包通道：写日志，绝不因一个坏文档搞挂 server。
                _log?.Invoke($"notification '{method}' failed: {ex}");
            }
            return;
        }

        try
        {
            requestToken.ThrowIfCancellationRequested();
            var request = new JsonRpcRequest(id, method, parameters, requestToken);
            var result = RequestHandler is null ? null : await RequestHandler(request).ConfigureAwait(false);
            requestToken.ThrowIfCancellationRequested();
            await RespondAsync(id, result, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (requestToken.IsCancellationRequested)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                _log?.Invoke($"request '{method}' cancelled");
                await WriteErrorAsync(id, JsonRpcException.RequestCancelled, "request cancelled", cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (JsonRpcException ex)
        {
            await WriteErrorAsync(id, ex.Code, ex.Message, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"request '{method}' failed: {ex}");
            await WriteErrorAsync(id, JsonRpcException.InternalError, ex.Message, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public Task RespondAsync(JsonNode id, JsonNode? result, CancellationToken cancellationToken = default)
        => SendAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id.DeepClone(),
            ["result"] = result,
        }, cancellationToken);

    private Task WriteErrorAsync(JsonNode? id, int code, string message, CancellationToken cancellationToken)
        => SendAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
        }, cancellationToken);

    public Task NotifyAsync(string method, JsonNode? parameters, CancellationToken cancellationToken = default)
        => SendAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = parameters,
        }, cancellationToken);

    /// <summary>
    /// 发一个 server→client 请求并且**不等响应**（用于 <c>client/registerCapability</c>）。
    /// 手写 JSON-RPC 层没有挂起的请求表：动态注册是「尽力而为」，客户端不支持时
    /// watcher 降级到只靠编辑器缓冲区刷新，绝不能因为一个未答的请求把派发循环卡住。
    /// </summary>
    public Task RequestAsync(string method, JsonNode? parameters, CancellationToken cancellationToken = default)
        => SendAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = _nextRequestId++,
            ["method"] = method,
            ["params"] = parameters,
        }, cancellationToken);

    private Task SendAsync(JsonObject message, CancellationToken cancellationToken)
        => _writer.WriteAsync(message, cancellationToken);
}

using System.Text.Json;
using System.Text.Json.Nodes;

namespace EclipseUI.LanguageServer.Protocol;

/// <summary>一条 JSON-RPC 请求；<see cref="Id"/> 原样回声（LSP 允许数字或字符串 id）。</summary>
public sealed record JsonRpcRequest(JsonNode Id, string Method, JsonNode? Parameters);

/// <summary>带 JSON-RPC 错误码的异常；宿主转成 <c>error</c> 响应而不是崩进程。</summary>
public sealed class JsonRpcException : Exception
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;

    public JsonRpcException(int code, string message) : base(message) => Code = code;

    public int Code { get; }
}

/// <summary>
/// 最小 JSON-RPC 2.0 over LSP 派发器：请求/通知两路，响应由宿主用
/// <see cref="RespondAsync"/>/<see cref="NotifyAsync"/> 主动发。
/// 单线程顺序处理——LSP 的文档同步语义要求 <c>didChange</c> 与随后的
/// <c>completion</c> 按到达顺序执行，并发处理反而要额外加锁。
/// </summary>
public sealed class JsonRpcConnection
{
    private readonly LspMessageReader _reader;
    private readonly LspMessageWriter _writer;
    private readonly Action<string>? _log;

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
        while (!cancellationToken.IsCancellationRequested)
        {
            JsonNode? message;
            try
            {
                message = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                _log?.Invoke($"transport closed: {ex.Message}");
                break;
            }

            if (message is null) break; // 客户端关流 = 退出
            await DispatchAsync(message, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task DispatchAsync(JsonNode message, CancellationToken cancellationToken)
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
            // 响应（我们暂时不发 server→client 请求，收到就忽略，避免噪声日志）。
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
            var request = new JsonRpcRequest(id, method, parameters);
            var result = RequestHandler is null ? null : await RequestHandler(request).ConfigureAwait(false);
            await RespondAsync(id, result, cancellationToken).ConfigureAwait(false);
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

    private Task SendAsync(JsonObject message, CancellationToken cancellationToken)
        => _writer.WriteAsync(message, cancellationToken);
}

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BubuUI.LanguageServer.Protocol;

/// <summary>
/// 写端：序列化 + <c>Content-Length</c> 头。多个请求会并发写同一 stdout，
/// 所以序列化与写字节必须在同一把锁内完成（否则两条消息的字节会交错）。
/// </summary>
public sealed class LspMessageWriter
{
    private static readonly JsonSerializerOptions Json = new()
    {
        // 诊断消息里全是中文与 .bui 源码片段；不转义可读性更好，JSON 语义不变。
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly Stream _stream;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public LspMessageWriter(Stream stream) => _stream = stream;

    public async Task WriteAsync(JsonNode message, CancellationToken cancellationToken = default)
    {
        var bytes = Encode(message);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>测试用：把一条消息编码成可直接塞进输入流的字节（头 + 体）。</summary>
    public static byte[] Encode(JsonNode message)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        var all = new byte[header.Length + body.Length];
        header.CopyTo(all, 0);
        body.CopyTo(all, header.Length);
        return all;
    }
}

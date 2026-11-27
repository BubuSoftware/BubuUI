using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace BubuUI.LanguageServer.Protocol;

/// <summary>
/// LSP 线上格式的读取端：<c>Content-Length</c> 头 + UTF-8 JSON 体（LSP base protocol 3.17 §header）。
/// 刻意不引第三方 LSP/RPC 库——协议面只有「读头 + 读体 + 派发」三件事，
/// 自己实现能把 stdio 的生命周期（EOF、半包、坏头）握在手里，也避免 NuGet 版本面扩大。
/// </summary>
public sealed class LspMessageReader
{
    private const int MaxHeaderLineLength = 8 * 1024;
    private const int MaxContentLength = 64 * 1024 * 1024;

    private readonly Stream _stream;
    private readonly byte[] _buffer = new byte[16 * 1024];
    private int _position;
    private int _length;

    public LspMessageReader(Stream stream) => _stream = stream;

    private async ValueTask<int> ReadByteAsync(CancellationToken cancellationToken)
    {
        if (_position >= _length)
        {
            _length = await _stream.ReadAsync(_buffer, cancellationToken).ConfigureAwait(false);
            _position = 0;
            if (_length <= 0) return -1;
        }
        return _buffer[_position++];
    }

    /// <summary>
    /// 读一条消息；流干净结束时返回 <c>null</c>（客户端关闭 stdio = 请求退出，
    /// 宿主据此结束进程，不当作错误）。
    /// </summary>
    public async Task<JsonNode?> ReadAsync(CancellationToken cancellationToken = default)
    {
        var contentLength = -1;
        var header = new StringBuilder();

        while (true)
        {
            int b = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
            if (b < 0)
            {
                // 文件尾：头部一个字节都没读到 → 正常退出；读到一半 → 协议破损。
                if (header.Length == 0 && contentLength < 0) return null;
                throw new IOException("unexpected end of stream while reading LSP headers");
            }

            if (b != '\n')
            {
                if (header.Length >= MaxHeaderLineLength)
                    throw new IOException("LSP header line too long");
                header.Append((char)b);
                continue;
            }

            var line = header.ToString().TrimEnd('\r');
            header.Clear();

            if (line.Length == 0)
            {
                if (contentLength < 0) continue; // 头之间允许空行
                break;                           // 空行 = 头结束
            }

            int colon = line.IndexOf(':');
            if (colon <= 0) continue; // 未知/畸形头按 RFC 7230 忽略
            if (!line.AsSpan(0, colon).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!int.TryParse(line.AsSpan(colon + 1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out contentLength) || contentLength < 0 || contentLength > MaxContentLength)
                throw new IOException($"invalid Content-Length in LSP header: '{line}'");
        }

        var body = contentLength == 0 ? Array.Empty<byte>() : new byte[contentLength];
        int read = 0;
        while (read < contentLength)
        {
            // 头是按字节读的，body 大概率已经在内部缓冲里——必须先掏缓冲再读流，
            // 否则会把已缓冲的 body 跳过（表现为 body 从 'C'(Content-Length) 开头）。
            if (_position < _length)
            {
                int take = Math.Min(_length - _position, contentLength - read);
                Buffer.BlockCopy(_buffer, _position, body, read, take);
                _position += take;
                read += take;
                continue;
            }

            int chunk = await _stream.ReadAsync(body.AsMemory(read, contentLength - read), cancellationToken)
                .ConfigureAwait(false);
            if (chunk <= 0) throw new IOException("unexpected end of stream while reading LSP body");
            read += chunk;
        }

        return JsonNode.Parse(body);
    }
}

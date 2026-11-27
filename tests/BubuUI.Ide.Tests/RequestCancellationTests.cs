using System.Text.Json.Nodes;
using System.Threading.Channels;
using BubuUI.LanguageServer.Protocol;
using Xunit;

namespace BubuUI.Ide.Tests;

public class RequestCancellationTests
{
    [Fact]
    public async Task ActiveCancellationSkipsQueuedWorkAndPreservesDocumentOrderAndIdTypes()
    {
        using var input = new LiveInput();
        using var output = new MemoryStream();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = new JsonRpcConnection(input, output);
        var calls = new List<string>();
        var version = 1;
        connection.RequestHandler = async request =>
        {
            calls.Add(request.Method);
            if (request.Method == "slow")
            {
                started.SetResult();
                await Task.Delay(Timeout.Infinite, request.CancellationToken);
            }
            return JsonValue.Create(version);
        };
        connection.NotificationHandler = (method, _) =>
        {
            Assert.Equal("textDocument/didChange", method);
            version = 2;
            return Task.CompletedTask;
        };
        var run = connection.RunAsync(timeout.Token);
        input.Send("""{"id":0,"method":"slow"}""");
        await started.Task.WaitAsync(timeout.Token);
        input.Send("""{"id":1,"method":"obsolete"}""");
        input.Send("""{"method":"$/cancelRequest","params":{"id":1}}""");
        input.Send("""{"method":"textDocument/didChange"}""");
        input.Send("""{"id":"1","method":"latest"}""");
        // Unknown / malformed cancellations must not poison future IDs or stop reading.
        input.Send("""{"method":"$/cancelRequest","params":{"id":99}}""");
        input.Send("""{"method":"$/cancelRequest","params":[]}""");
        input.Send("""{"id":99,"method":"future"}""");
        input.Send("""{"method":"$/cancelRequest","params":{"id":0}}""");
        input.Complete();
        await run.WaitAsync(timeout.Token);

        Assert.Equal(new[] { "slow", "latest", "future" }, calls);
        output.Position = 0;
        var reader = new LspMessageReader(output);
        var active = await reader.ReadAsync(timeout.Token);
        var queued = await reader.ReadAsync(timeout.Token);
        var latest = await reader.ReadAsync(timeout.Token);
        var future = await reader.ReadAsync(timeout.Token);
        Assert.Equal(0, active!["id"]!.GetValue<int>());
        Assert.Equal(JsonRpcException.RequestCancelled, active["error"]!["code"]!.GetValue<int>());
        Assert.Equal(1, queued!["id"]!.GetValue<int>());
        Assert.Equal(JsonRpcException.RequestCancelled, queued["error"]!["code"]!.GetValue<int>());
        Assert.Equal("1", latest!["id"]!.GetValue<string>());
        Assert.Equal(2, latest["result"]!.GetValue<int>());
        Assert.Equal(2, future!["result"]!.GetValue<int>());
        Assert.Null(await reader.ReadAsync(timeout.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShutdownCancellationStopsAnActiveRequestAndPendingRead(bool ignoreReadCancellation)
    {
        using var input = new LiveInput { IgnoreCancellation = ignoreReadCancellation };
        using var output = new MemoryStream();
        using var shutdown = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = new JsonRpcConnection(input, output)
        {
            RequestHandler = async request =>
            {
                started.SetResult();
                await Task.Delay(Timeout.Infinite, request.CancellationToken);
                return null;
            },
        };
        var run = connection.RunAsync(shutdown.Token);
        input.Send("""{"id":1,"method":"slow"}""");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        shutdown.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        input.Complete();
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public async Task ExitDoesNotDispatchBufferedNotificationsAfterShutdown()
    {
        using var input = new LiveInput();
        using var output = new MemoryStream();
        using var shutdown = new CancellationTokenSource();
        var calls = new List<string>();
        var connection = new JsonRpcConnection(input, output)
        {
            NotificationHandler = (method, _) =>
            {
                calls.Add(method);
                if (method == "exit") shutdown.Cancel();
                return Task.CompletedTask;
            },
        };
        input.Send("""{"method":"exit"}""");
        input.Send("""{"method":"textDocument/didChange"}""");
        input.Complete();
        await connection.RunAsync(shutdown.Token).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(new[] { "exit" }, calls);
    }

    // A live transport, rather than a preloaded transcript: cancellation is sent only
    // after the first request starts, while the server is still waiting for more input.
    private sealed class LiveInput : Stream
    {
        private readonly Channel<byte[]> _frames = Channel.CreateUnbounded<byte[]>();
        private ReadOnlyMemory<byte> _remaining;
        public bool IgnoreCancellation { get; init; }
        public void Send(string json) => _frames.Writer.TryWrite(LspMessageWriter.Encode(JsonNode.Parse(json)!));
        public void Complete() => _frames.Writer.TryComplete();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (_remaining.IsEmpty)
            {
                if (!await _frames.Reader.WaitToReadAsync(IgnoreCancellation ? default : cancellationToken)) return 0;
                if (_frames.Reader.TryRead(out var frame)) _remaining = frame;
            }
            var length = Math.Min(buffer.Length, _remaining.Length);
            _remaining[..length].CopyTo(buffer);
            _remaining = _remaining[length..];
            return length;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

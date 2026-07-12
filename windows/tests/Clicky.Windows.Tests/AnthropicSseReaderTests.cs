using System.Text;
using Clicky.Windows.Networking;

namespace Clicky.Windows.Tests;

[TestClass]
public sealed class AnthropicSseReaderTests
{
    [TestMethod]
    public async Task ReadTextDeltasAsync_FragmentedInput_ReturnsTextDeltas()
    {
        const string sse = """
            event: content_block_delta
            data: {"type":"content_block_delta","delta":{"type":"text_delta","text":"hello "}}

            event: content_block_delta
            data: {"type":"content_block_delta","delta":{"type":"text_delta","text":"world"}}

            """;
        await using var stream = new FragmentedReadStream(Encoding.UTF8.GetBytes(sse), maximumReadSize: 2);

        var deltas = await ReadAllAsync(stream);

        CollectionAssert.AreEqual(new[] { "hello ", "world" }, deltas);
    }

    [TestMethod]
    public async Task ReadTextDeltasAsync_DoneMarker_StopsBeforeLaterEvents()
    {
        const string sse = """
            data: {"type":"content_block_delta","delta":{"type":"text_delta","text":"before"}}

            data: [DONE]

            data: {"type":"content_block_delta","delta":{"type":"text_delta","text":"after"}}

            """;
        await using var stream = new FragmentedReadStream(Encoding.UTF8.GetBytes(sse), maximumReadSize: 3);

        var deltas = await ReadAllAsync(stream);

        CollectionAssert.AreEqual(new[] { "before" }, deltas);
    }

    [TestMethod]
    public async Task ReadTextDeltasAsync_MessageStop_StopsBeforeLaterEvents()
    {
        const string sse = """
            data: {"type":"content_block_delta","delta":{"type":"text_delta","text":"complete"}}

            event: message_stop
            data: {"type":"message_stop"}

            data: {"type":"content_block_delta","delta":{"type":"text_delta","text":"ignored"}}

            """;
        await using var stream = new FragmentedReadStream(Encoding.UTF8.GetBytes(sse), maximumReadSize: 1);

        var deltas = await ReadAllAsync(stream);

        CollectionAssert.AreEqual(new[] { "complete" }, deltas);
    }

    [TestMethod]
    public async Task ReadTextDeltasAsync_EofDispatchesFinalEventWithoutBlankLine()
    {
        const string sse = "data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"at eof\"}}";
        await using var stream = new FragmentedReadStream(Encoding.UTF8.GetBytes(sse), maximumReadSize: 4);

        var deltas = await ReadAllAsync(stream);

        CollectionAssert.AreEqual(new[] { "at eof" }, deltas);
    }

    [TestMethod]
    public async Task ReadTextDeltasAsync_MalformedAndUnrelatedEvents_AreIgnored()
    {
        const string sse = """
            : keepalive

            data: not-json

            data: {"type":"content_block_delta","delta":{"type":"input_json_delta","partial_json":"{}"}}

            data: {"type":"content_block_delta","delta":{"type":"text_delta","text":"valid"}}

            """;
        await using var stream = new FragmentedReadStream(Encoding.UTF8.GetBytes(sse), maximumReadSize: 5);

        var deltas = await ReadAllAsync(stream);

        CollectionAssert.AreEqual(new[] { "valid" }, deltas);
    }

    [TestMethod]
    public async Task ReadTextDeltasAsync_Cancellation_InterruptsPendingRead()
    {
        await using var stream = new CancellationOnlyStream();
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var cancellationWasObserved = false;
        try
        {
            await foreach (var _ in AnthropicSseReader.ReadTextDeltasAsync(stream, cancellationSource.Token))
            {
            }
        }
        catch (OperationCanceledException)
        {
            cancellationWasObserved = true;
        }

        Assert.IsTrue(cancellationWasObserved);
    }

    [TestMethod]
    public async Task ReadTextDeltasAsync_StrictModeRequiresMessageStop()
    {
        const string sse = """
            data: {"type":"content_block_delta","delta":{"type":"text_delta","text":"partial"}}

            data: [DONE]

            """;
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sse));

        var exception = await Assert.ThrowsExactlyAsync<AnthropicProviderException>(
            async () =>
            {
                await foreach (var _ in AnthropicSseReader.ReadTextDeltasAsync(
                    stream,
                    requireMessageStop: true))
                {
                }
            });

        Assert.AreEqual(AnthropicProviderErrorKind.PrematureEnd, exception.ErrorKind);
    }

    [TestMethod]
    public async Task ReadTextDeltasAsync_ErrorEventThrowsWithoutRawMessage()
    {
        const string secret = "do-not-echo-this";
        const string sse = """
            event: error
            data: {"type":"error","error":{"type":"overloaded_error","message":"do-not-echo-this"}}

            """;
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sse));

        var exception = await Assert.ThrowsExactlyAsync<AnthropicProviderException>(
            () => ReadAllAsync(stream));

        Assert.AreEqual(AnthropicProviderErrorKind.StreamError, exception.ErrorKind);
        Assert.AreEqual("overloaded_error", exception.ErrorType);
        Assert.IsFalse(exception.Message.Contains(secret, StringComparison.Ordinal));
    }

    private static async Task<string[]> ReadAllAsync(Stream stream)
    {
        var deltas = new List<string>();
        await foreach (var delta in AnthropicSseReader.ReadTextDeltasAsync(stream))
        {
            deltas.Add(delta);
        }

        return deltas.ToArray();
    }

    private sealed class FragmentedReadStream(byte[] bytes, int maximumReadSize) : MemoryStream(bytes)
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            return base.Read(buffer, offset, Math.Min(count, maximumReadSize));
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            return base.ReadAsync(buffer[..Math.Min(buffer.Length, maximumReadSize)], cancellationToken);
        }
    }

    private sealed class CancellationOnlyStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }
    }
}

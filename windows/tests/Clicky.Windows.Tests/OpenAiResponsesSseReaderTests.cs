using System.Text;
using Clicky.Windows.Networking;

namespace Clicky.Windows.Tests;

[TestClass]
public sealed class OpenAiResponsesSseReaderTests
{
    [TestMethod]
    public async Task ReadTextDeltasAsync_HandlesFragmentedFramesAndMultilineData()
    {
        const string sse =
            ": keepalive\r\n\r\n" +
            "event: response.output_text.delta\r\n" +
            "data: {\"type\":\"response.output_text.delta\",\r\n" +
            "data: \"delta\":\"hello\"}\r\n\r\n" +
            "data: {\"type\":\"response.output_text.delta\",\"delta\":\" world\"}\r\n\r\n" +
            "event: response.completed\r\n" +
            "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\"}}\r\n\r\n";
        await using var stream = new FragmentedReadStream(Encoding.UTF8.GetBytes(sse), maximumChunkSize: 3);

        var deltas = await CollectAsync(OpenAiResponsesSseReader.ReadTextDeltasAsync(stream));

        CollectionAssert.AreEqual(new[] { "hello", " world" }, deltas);
    }

    [TestMethod]
    public async Task ReadTextDeltasAsync_PrematureDoneIsProtocolFailure()
    {
        await using var stream = Utf8Stream(
            "data: {\"type\":\"response.output_text.delta\",\"delta\":\"partial\"}\n\n" +
            "data: [DONE]\n\n");

        var exception = await Assert.ThrowsExactlyAsync<OpenAiProviderException>(async () =>
        {
            await CollectAsync(OpenAiResponsesSseReader.ReadTextDeltasAsync(stream));
        });

        Assert.AreEqual(OpenAiProviderFailureKind.StreamProtocol, exception.FailureKind);
        StringAssert.Contains(exception.Message, "response.completed");
    }

    [TestMethod]
    public async Task ReadTextDeltasAsync_MalformedJsonIsProtocolFailureWithoutRawData()
    {
        const string secretMarker = "sk-secret-in-malformed-event";
        await using var stream = Utf8Stream($"data: {{not-json:{secretMarker}}}\n\n");

        var exception = await Assert.ThrowsExactlyAsync<OpenAiProviderException>(async () =>
        {
            await CollectAsync(OpenAiResponsesSseReader.ReadTextDeltasAsync(stream));
        });

        Assert.AreEqual(OpenAiProviderFailureKind.StreamProtocol, exception.FailureKind);
        Assert.IsFalse(exception.ToString().Contains(secretMarker, StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow(
        "{\"type\":\"response.failed\",\"response\":{\"error\":{\"code\":\"model_error! unsafe words\"}}}",
        OpenAiProviderFailureKind.Failed,
        "model_errorunsafewords")]
    [DataRow(
        "{\"type\":\"response.incomplete\",\"response\":{\"incomplete_details\":{\"reason\":\"max_output_tokens\"}}}",
        OpenAiProviderFailureKind.Incomplete,
        "max_output_tokens")]
    [DataRow(
        "{\"type\":\"response.refusal.delta\",\"delta\":\"private refusal text\"}",
        OpenAiProviderFailureKind.Refusal,
        "refusal")]
    [DataRow(
        "{\"type\":\"error\",\"code\":\"server_error\",\"message\":\"private provider body\"}",
        OpenAiProviderFailureKind.Failed,
        "server_error")]
    public async Task ReadTextDeltasAsync_TerminalFailureEventsAreTypedAndSanitized(
        string eventJson,
        OpenAiProviderFailureKind expectedKind,
        string expectedCode)
    {
        await using var stream = Utf8Stream($"data: {eventJson}\n\n");

        var exception = await Assert.ThrowsExactlyAsync<OpenAiProviderException>(async () =>
        {
            await CollectAsync(OpenAiResponsesSseReader.ReadTextDeltasAsync(stream));
        });

        Assert.AreEqual(expectedKind, exception.FailureKind);
        Assert.AreEqual(expectedCode, exception.ProviderCode);
        Assert.IsFalse(exception.ToString().Contains("private", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ReadTextDeltasAsync_CompletedEventWithNonCompletedStatusFails()
    {
        await using var stream = Utf8Stream(
            "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"failed\"}}\n\n");

        var exception = await Assert.ThrowsExactlyAsync<OpenAiProviderException>(async () =>
        {
            await CollectAsync(OpenAiResponsesSseReader.ReadTextDeltasAsync(stream));
        });

        Assert.AreEqual(OpenAiProviderFailureKind.Failed, exception.FailureKind);
        Assert.AreEqual("failed", exception.ProviderCode);
    }

    [TestMethod]
    public async Task ReadTextDeltasAsync_ObservesCancellation()
    {
        await using var stream = new NeverCompletingStream();
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () =>
        {
            await CollectAsync(OpenAiResponsesSseReader.ReadTextDeltasAsync(stream, cancellation.Token));
        });
    }

    private static MemoryStream Utf8Stream(string value) =>
        new(Encoding.UTF8.GetBytes(value));

    private static async Task<List<string>> CollectAsync(IAsyncEnumerable<string> source)
    {
        var values = new List<string>();
        await foreach (var value in source)
        {
            values.Add(value);
        }

        return values;
    }

    private sealed class FragmentedReadStream(byte[] bytes, int maximumChunkSize) : Stream
    {
        private readonly MemoryStream inner = new(bytes);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) =>
            inner.Read(buffer, offset, Math.Min(count, maximumChunkSize));

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer[..Math.Min(buffer.Length, maximumChunkSize)], cancellationToken);

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override Task FlushAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class NeverCompletingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

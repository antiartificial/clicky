using System.Text;
using Clicky.Windows.Networking;

namespace Clicky.Windows.Tests;

[TestClass]
public sealed class GeminiSseReaderTests
{
    [TestMethod]
    public async Task ReadTextDeltasAsync_HandlesFragmentedFramesMultilineDataAndThoughtParts()
    {
        const string sse =
            ": keepalive\r\n\r\n" +
            "data: {\"candidates\":[{\"index\":0,\r\n" +
            "data: \"content\":{\"parts\":[{\"thought\":true,\"text\":\"private reasoning\"},{\"text\":\"hello\"}]}}]}\r\n\r\n" +
            "data: {\"candidates\":[{\"index\":0,\"content\":{\"parts\":[{\"text\":\" world\"}]},\"finishReason\":\"STOP\"}]}\r\n\r\n";
        await using var stream = new FragmentedReadStream(Encoding.UTF8.GetBytes(sse), maximumChunkSize: 3);

        var deltas = await CollectAsync(GeminiSseReader.ReadTextDeltasAsync(stream));

        CollectionAssert.AreEqual(new[] { "hello", " world" }, deltas);
    }

    [TestMethod]
    public async Task ReadTextDeltasAsync_PromptSafetyBlockIsTypedAndSanitized()
    {
        const string secret = "private safety details";
        await using var stream = Utf8Stream(
            "data: {\"promptFeedback\":{\"blockReason\":\"PROHIBITED_CONTENT<script>\",\"details\":\"" +
            secret +
            "\"},\"candidates\":[]}\n\n");

        var exception = await Assert.ThrowsExactlyAsync<GeminiProviderException>(
            () => CollectAsync(GeminiSseReader.ReadTextDeltasAsync(stream)));

        Assert.AreEqual(GeminiProviderFailureKind.Safety, exception.FailureKind);
        Assert.AreEqual("PROHIBITED_CONTENTscript", exception.ProviderCode);
        Assert.IsFalse(exception.ToString().Contains(secret, StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("SAFETY", GeminiProviderFailureKind.Safety)]
    [DataRow("RECITATION", GeminiProviderFailureKind.Safety)]
    [DataRow("MAX_TOKENS", GeminiProviderFailureKind.Incomplete)]
    [DataRow("OTHER", GeminiProviderFailureKind.Failed)]
    public async Task ReadTextDeltasAsync_NonStopFinishReasonsAreTyped(
        string finishReason,
        GeminiProviderFailureKind expectedFailureKind)
    {
        await using var stream = Utf8Stream(
            $"data: {{\"candidates\":[{{\"index\":0,\"finishReason\":\"{finishReason}\"}}]}}\n\n");

        var exception = await Assert.ThrowsExactlyAsync<GeminiProviderException>(
            () => CollectAsync(GeminiSseReader.ReadTextDeltasAsync(stream)));

        Assert.AreEqual(expectedFailureKind, exception.FailureKind);
        Assert.AreEqual(finishReason, exception.ProviderCode);
    }

    [TestMethod]
    public async Task ReadTextDeltasAsync_MalformedJsonIsProtocolFailureWithoutRawData()
    {
        const string secretMarker = "gemini-secret-in-malformed-event";
        await using var stream = Utf8Stream($"data: {{not-json:{secretMarker}}}\n\n");

        var exception = await Assert.ThrowsExactlyAsync<GeminiProviderException>(
            () => CollectAsync(GeminiSseReader.ReadTextDeltasAsync(stream)));

        Assert.AreEqual(GeminiProviderFailureKind.StreamProtocol, exception.FailureKind);
        Assert.IsFalse(exception.ToString().Contains(secretMarker, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ReadTextDeltasAsync_EofBeforeStopIsProtocolFailure()
    {
        await using var stream = Utf8Stream(
            "data: {\"candidates\":[{\"index\":0,\"content\":{\"parts\":[{\"text\":\"partial\"}]}}]}\n\n");

        var exception = await Assert.ThrowsExactlyAsync<GeminiProviderException>(
            () => CollectAsync(GeminiSseReader.ReadTextDeltasAsync(stream)));

        Assert.AreEqual(GeminiProviderFailureKind.StreamProtocol, exception.FailureKind);
        StringAssert.Contains(exception.Message, "STOP");
    }

    [TestMethod]
    public async Task ReadTextDeltasAsync_ObservesCancellation()
    {
        await using var stream = new NeverCompletingStream();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(
            () => CollectAsync(GeminiSseReader.ReadTextDeltasAsync(stream, cancellation.Token)));
    }

    private static MemoryStream Utf8Stream(string value) =>
        new(Encoding.UTF8.GetBytes(value));

    private static async Task<string[]> CollectAsync(IAsyncEnumerable<string> source)
    {
        var values = new List<string>();
        await foreach (var value in source)
        {
            values.Add(value);
        }

        return values.ToArray();
    }

    private sealed class FragmentedReadStream(byte[] bytes, int maximumChunkSize) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, maximumChunkSize)], cancellationToken);
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

using Clicky.Windows.Configuration;
using Clicky.Windows.Networking;
using Clicky.Windows.Providers;

namespace Clicky.Windows.Tests;

[TestClass]
public sealed class ProviderRoutingChatClientTests
{
    [TestMethod]
    [DataRow(AiProviderKind.Worker, "worker")]
    [DataRow(AiProviderKind.Anthropic, "anthropic")]
    [DataRow(AiProviderKind.OpenAI, "openai")]
    [DataRow(AiProviderKind.Gemini, "gemini")]
    public async Task StreamChatAsync_RoutesToSelectedProvider(
        AiProviderKind provider,
        string expectedChunk)
    {
        var settings = new CompanionSettings { SelectedProvider = provider };
        var worker = new RecordingClient("worker");
        var anthropic = new RecordingClient("anthropic");
        var openAI = new RecordingClient("openai");
        var gemini = new RecordingClient("gemini");
        var router = new ProviderRoutingChatClient(settings, worker, anthropic, openAI, gemini);

        var chunks = await ReadAllAsync(router.StreamChatAsync(CreateRequest()));

        CollectionAssert.AreEqual(new[] { expectedChunk }, chunks);
        Assert.AreEqual(provider == AiProviderKind.Worker ? 1 : 0, worker.CallCount);
        Assert.AreEqual(provider == AiProviderKind.Anthropic ? 1 : 0, anthropic.CallCount);
        Assert.AreEqual(provider == AiProviderKind.OpenAI ? 1 : 0, openAI.CallCount);
        Assert.AreEqual(provider == AiProviderKind.Gemini ? 1 : 0, gemini.CallCount);
    }

    [TestMethod]
    public async Task StreamChatAsync_SnapshotsProviderBeforeEnumeration()
    {
        var settings = new CompanionSettings { SelectedProvider = AiProviderKind.Worker };
        var worker = new RecordingClient("worker");
        var anthropic = new RecordingClient("anthropic");
        var router = new ProviderRoutingChatClient(
            settings,
            worker,
            anthropic,
            new RecordingClient("openai"),
            new RecordingClient("gemini"));

        var stream = router.StreamChatAsync(CreateRequest());
        settings.SelectedProvider = AiProviderKind.Anthropic;
        var chunks = await ReadAllAsync(stream);

        CollectionAssert.AreEqual(new[] { "worker" }, chunks);
        Assert.AreEqual(1, worker.CallCount);
        Assert.AreEqual(0, anthropic.CallCount);
    }

    private static WorkerChatRequest CreateRequest() =>
        new("worker-model", "system", "question");

    private static async Task<string[]> ReadAllAsync(IAsyncEnumerable<string> stream)
    {
        var chunks = new List<string>();
        await foreach (var chunk in stream)
        {
            chunks.Add(chunk);
        }

        return chunks.ToArray();
    }

    private sealed class RecordingClient(string chunk) : IWorkerClient
    {
        public int CallCount { get; private set; }

        public IAsyncEnumerable<string> StreamChatAsync(
            WorkerChatRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Stream(chunk);
        }

        private static async IAsyncEnumerable<string> Stream(string text)
        {
            yield return text;
            await Task.CompletedTask;
        }
    }
}

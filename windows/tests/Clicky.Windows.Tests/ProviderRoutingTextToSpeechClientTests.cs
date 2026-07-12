using Clicky.Windows.Configuration;
using Clicky.Windows.Speech;

namespace Clicky.Windows.Tests;

[TestClass]
public sealed class ProviderRoutingTextToSpeechClientTests
{
    private static readonly SpeechAudio FixtureAudio = new(
        new byte[] { 0x52, 0x49, 0x46, 0x46, 0x04, 0x00, 0x00, 0x00, 0x57, 0x41, 0x56, 0x45 },
        "audio/wav");

    [TestMethod]
    public async Task SynthesizeAsync_RoutesEachRequestToCurrentProvider()
    {
        var settings = new CompanionSettings
        {
            TextToSpeechProvider = TextToSpeechProviderKind.OpenAI,
        };
        var openAiClient = new RecordingTextToSpeechClient();
        var elevenLabsClient = new RecordingTextToSpeechClient();
        var routingClient = new ProviderRoutingTextToSpeechClient(
            settings,
            openAiClient,
            elevenLabsClient);

        await routingClient.SynthesizeAsync("OpenAI request.");
        settings.TextToSpeechProvider = TextToSpeechProviderKind.ElevenLabs;
        await routingClient.SynthesizeAsync("ElevenLabs request.");

        CollectionAssert.AreEqual(
            new[] { "OpenAI request." },
            openAiClient.RequestedTexts);
        CollectionAssert.AreEqual(
            new[] { "ElevenLabs request." },
            elevenLabsClient.RequestedTexts);
    }

    [TestMethod]
    public async Task SynthesizeAsync_ForwardsCancellationToken()
    {
        var settings = new CompanionSettings
        {
            TextToSpeechProvider = TextToSpeechProviderKind.OpenAI,
        };
        var openAiClient = new RecordingTextToSpeechClient();
        var routingClient = new ProviderRoutingTextToSpeechClient(
            settings,
            openAiClient,
            new RecordingTextToSpeechClient());
        using var cancellation = new CancellationTokenSource();

        await routingClient.SynthesizeAsync("Hello.", cancellation.Token);

        Assert.AreEqual(cancellation.Token, openAiClient.LastCancellationToken);
    }

    private sealed class RecordingTextToSpeechClient : ITextToSpeechClient
    {
        public List<string> RequestedTexts { get; } = [];

        public CancellationToken LastCancellationToken { get; private set; }

        public Task<SpeechAudio> SynthesizeAsync(
            string text,
            CancellationToken cancellationToken = default)
        {
            RequestedTexts.Add(text);
            LastCancellationToken = cancellationToken;
            return Task.FromResult(FixtureAudio);
        }
    }
}

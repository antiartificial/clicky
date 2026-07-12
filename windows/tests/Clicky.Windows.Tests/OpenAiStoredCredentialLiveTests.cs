using Clicky.Windows.Configuration;
using Clicky.Windows.Networking;
using Clicky.Windows.Providers;
using Clicky.Windows.Speech;

namespace Clicky.Windows.Tests;

[TestClass]
public sealed class OpenAiStoredCredentialLiveTests
{
    [TestMethod]
    [TestCategory("Live")]
    public async Task StoredCredential_CanCompleteMinimalResponsesRequest()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("CLICKY_LIVE_OPENAI"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var settings = new CompanionSettingsStore().Load();
        using var client = OpenAiResponsesClient.CreateProduction(
            new WindowsCredentialApiKeyStore(),
            settings);
        var request = new WorkerChatRequest(
            "ignored-by-direct-client",
            "You are a connection check. Reply with exactly OK.",
            "Reply with exactly OK.",
            maxTokens: 32);

        try
        {
            var responseText = new System.Text.StringBuilder();
            await foreach (var delta in client.StreamChatAsync(request))
            {
                responseText.Append(delta);
            }

            Assert.IsFalse(
                string.IsNullOrWhiteSpace(responseText.ToString()),
                "OpenAI returned no text for the minimal connection check.");
        }
        catch (OpenAiProviderException exception)
        {
            var httpStatus = exception.StatusCode is null
                ? "none"
                : ((int)exception.StatusCode.Value).ToString(System.Globalization.CultureInfo.InvariantCulture);
            Assert.Fail(
                $"OpenAI doctor failed: category={exception.FailureKind}; " +
                $"http={httpStatus}; code={exception.ProviderCode ?? "none"}; " +
                $"request={exception.RequestId ?? "none"}.");
        }
    }

    [TestMethod]
    [TestCategory("Live")]
    public async Task StoredCredential_CanGenerateOpenAiSpeech()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("CLICKY_LIVE_OPENAI_SPEECH"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var settings = new CompanionSettingsStore().Load();
        using var client = OpenAiSpeechClient.CreateProduction(
            new WindowsCredentialApiKeyStore(),
            settings);

        try
        {
            var speechAudio = await client.SynthesizeAsync(Branding.BrandText.TestVoicePhrase);
            Assert.IsGreaterThan(12, speechAudio.AudioBytes.Length);
            Assert.AreEqual("audio/wav", speechAudio.MediaType);
            Assert.IsTrue(speechAudio.AudioBytes.Span[..4].SequenceEqual("RIFF"u8));
            Assert.IsTrue(speechAudio.AudioBytes.Span.Slice(8, 4).SequenceEqual("WAVE"u8));

            using var playbackService = new WaveAudioPlaybackService();
            await playbackService.PlayAsync(speechAudio);
        }
        catch (SpeechSynthesisException exception)
        {
            var httpStatus = exception.StatusCode is null
                ? "none"
                : ((int)exception.StatusCode.Value).ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
            Assert.Fail(
                $"OpenAI speech doctor failed: category={exception.FailureKind}; " +
                $"http={httpStatus}; request={exception.RequestId ?? "none"}.");
        }
        catch (AudioPlaybackException exception)
        {
            Assert.Fail($"Windows speech playback failed: category={exception.FailureKind}.");
        }
    }
}

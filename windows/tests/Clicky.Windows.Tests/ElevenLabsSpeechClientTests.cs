using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Clicky.Windows.Configuration;
using Clicky.Windows.Providers;
using Clicky.Windows.Speech;

namespace Clicky.Windows.Tests;

[TestClass]
public sealed class ElevenLabsSpeechClientTests
{
    private const string ApiKey = "elevenlabs-speech-secret";
    private static readonly byte[] AudioBytes =
        [0x52, 0x49, 0x46, 0x46, 0x04, 0x00, 0x00, 0x00, 0x57, 0x41, 0x56, 0x45];

    [TestMethod]
    public async Task SynthesizeAsync_PostsDirectRequestWithEscapedVoiceAndApiKey()
    {
        var handler = new RecordingHandler(AudioBytes, "audio/x-wav");
        using var httpClient = new HttpClient(handler);
        var keyStore = new FakeApiKeyStore(ApiKey);
        var settings = new CompanionSettings
        {
            ElevenLabsVoiceId = "custom voice/one",
        };
        using var client = new ElevenLabsSpeechClient(httpClient, keyStore, settings);

        var speechAudio = await client.SynthesizeAsync("Use my custom voice.");

        CollectionAssert.AreEqual(AudioBytes, speechAudio.AudioBytes.ToArray());
        Assert.AreEqual("audio/x-wav", speechAudio.MediaType);
        Assert.AreEqual(1, keyStore.GetCalls);
        Assert.AreEqual(AiProviderKind.ElevenLabs, keyStore.LastProvider);
        Assert.AreEqual(HttpMethod.Post, handler.RequestMethod);
        Assert.AreEqual(
            "https://api.elevenlabs.io/v1/text-to-speech/custom%20voice%2Fone?output_format=wav_24000",
            handler.RequestUri?.AbsoluteUri);
        Assert.AreEqual("application/json", handler.ContentType);
        Assert.AreEqual("audio/wav", handler.Accept);
        Assert.AreEqual(ApiKey, handler.ApiKey);

        using var document = JsonDocument.Parse(handler.RequestBody!);
        var root = document.RootElement;
        Assert.AreEqual("Use my custom voice.", root.GetProperty("text").GetString());
        Assert.AreEqual(1, root.EnumerateObject().Count());
        Assert.IsFalse(handler.RequestBody!.Contains(ApiKey, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task SynthesizeAsync_MissingVoiceFailsBeforeReadingKeyOrSending()
    {
        var handler = new RecordingHandler(AudioBytes, "audio/wav");
        var keyStore = new FakeApiKeyStore(ApiKey);
        using var httpClient = new HttpClient(handler);
        using var client = new ElevenLabsSpeechClient(
            httpClient,
            keyStore,
            new CompanionSettings { ElevenLabsVoiceId = "  " });

        var exception = await Assert.ThrowsExactlyAsync<SpeechSynthesisException>(
            () => client.SynthesizeAsync("Hello."));

        Assert.AreEqual(SpeechSynthesisProvider.ElevenLabs, exception.Provider);
        Assert.AreEqual(SpeechSynthesisFailureKind.InvalidConfiguration, exception.FailureKind);
        Assert.AreEqual(0, keyStore.GetCalls);
        Assert.AreEqual(0, handler.SendCalls);
    }

    [TestMethod]
    public async Task SynthesizeAsync_MissingKeyFailsBeforeSending()
    {
        var handler = new RecordingHandler(AudioBytes, "audio/wav");
        using var httpClient = new HttpClient(handler);
        using var client = new ElevenLabsSpeechClient(
            httpClient,
            new FakeApiKeyStore(apiKey: null),
            new CompanionSettings { ElevenLabsVoiceId = "voice-id" });

        var exception = await Assert.ThrowsExactlyAsync<SpeechSynthesisException>(
            () => client.SynthesizeAsync("Hello."));

        Assert.AreEqual(SpeechSynthesisFailureKind.MissingApiKey, exception.FailureKind);
        Assert.AreEqual(0, handler.SendCalls);
    }

    [TestMethod]
    public async Task SynthesizeAsync_HttpFailureIsTypedWithoutBodyOrKeyLeak()
    {
        var responseBody = Encoding.UTF8.GetBytes(
            $"provider body contains {ApiKey} and private diagnostics");
        var handler = new RecordingHandler(
            responseBody,
            "application/json",
            HttpStatusCode.Forbidden);
        handler.ResponseHeaders["request-id"] = ["eleven_req !! private"];
        using var httpClient = new HttpClient(handler);
        using var client = new ElevenLabsSpeechClient(
            httpClient,
            new FakeApiKeyStore(ApiKey),
            new CompanionSettings { ElevenLabsVoiceId = "voice-id" });

        var exception = await Assert.ThrowsExactlyAsync<SpeechSynthesisException>(
            () => client.SynthesizeAsync("Hello."));

        Assert.AreEqual(SpeechSynthesisProvider.ElevenLabs, exception.Provider);
        Assert.AreEqual(SpeechSynthesisFailureKind.Permission, exception.FailureKind);
        Assert.AreEqual(HttpStatusCode.Forbidden, exception.StatusCode);
        Assert.AreEqual("eleven_reqprivate", exception.RequestId);
        Assert.IsFalse(exception.ToString().Contains(ApiKey, StringComparison.Ordinal));
        Assert.IsFalse(exception.ToString().Contains("private diagnostics", StringComparison.Ordinal));
    }

    private sealed class FakeApiKeyStore(string? apiKey) : IProviderApiKeyStore
    {
        public int GetCalls { get; private set; }

        public AiProviderKind? LastProvider { get; private set; }

        public Task<string?> GetApiKeyAsync(
            AiProviderKind provider,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GetCalls++;
            LastProvider = provider;
            return Task.FromResult(apiKey);
        }

        public Task SaveApiKeyAsync(
            AiProviderKind provider,
            string apiKey,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteApiKeyAsync(
            AiProviderKind provider,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> HasApiKeyAsync(
            AiProviderKind provider,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingHandler(
        byte[] responseBody,
        string responseMediaType,
        HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int SendCalls { get; private set; }
        public HttpMethod? RequestMethod { get; private set; }
        public Uri? RequestUri { get; private set; }
        public string? ContentType { get; private set; }
        public string? Accept { get; private set; }
        public string? ApiKey { get; private set; }
        public string? RequestBody { get; private set; }
        public Dictionary<string, string[]> ResponseHeaders { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            SendCalls++;
            RequestMethod = request.Method;
            RequestUri = request.RequestUri;
            ContentType = request.Content?.Headers.ContentType?.MediaType;
            Accept = request.Headers.Accept.Single().MediaType;
            ApiKey = request.Headers.GetValues("xi-api-key").Single();
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            var response = new HttpResponseMessage(statusCode)
            {
                Content = new ByteArrayContent(responseBody),
            };
            response.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue(responseMediaType);
            foreach (var header in ResponseHeaders)
            {
                response.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            return response;
        }
    }
}

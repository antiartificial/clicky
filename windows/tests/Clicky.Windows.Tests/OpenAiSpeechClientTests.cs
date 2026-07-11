using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Clicky.Windows.Configuration;
using Clicky.Windows.Providers;
using Clicky.Windows.Speech;

namespace Clicky.Windows.Tests;

[TestClass]
public sealed class OpenAiSpeechClientTests
{
    private const string ApiKey = "sk-openai-speech-secret";
    private static readonly byte[] WaveBytes =
        [0x52, 0x49, 0x46, 0x46, 0x04, 0x00, 0x00, 0x00, 0x57, 0x41, 0x56, 0x45];

    [TestMethod]
    public async Task SynthesizeAsync_PostsWavRequestWithPerRequestAuthorization()
    {
        var handler = new RecordingHandler(WaveBytes, "audio/wav");
        using var httpClient = new HttpClient(handler);
        var keyStore = new FakeApiKeyStore(ApiKey);
        var settings = new CompanionSettings
        {
            OpenAITtsModelId = "tts-fixture-model",
            OpenAITtsVoice = "fixture-voice",
        };
        using var client = new OpenAiSpeechClient(httpClient, keyStore, settings);

        var speechAudio = await client.SynthesizeAsync("Read this aloud.");

        CollectionAssert.AreEqual(WaveBytes, speechAudio.AudioBytes.ToArray());
        Assert.AreEqual("audio/wav", speechAudio.MediaType);
        Assert.AreEqual(1, keyStore.GetCalls);
        Assert.AreEqual(AiProviderKind.OpenAI, keyStore.LastProvider);
        Assert.AreEqual(HttpMethod.Post, handler.RequestMethod);
        Assert.AreEqual(new Uri("https://api.openai.com/v1/audio/speech"), handler.RequestUri);
        Assert.AreEqual("application/json", handler.ContentType);
        Assert.AreEqual("audio/wav", handler.Accept);
        Assert.AreEqual("Bearer", handler.AuthorizationScheme);
        Assert.AreEqual(ApiKey, handler.AuthorizationParameter);

        using var document = JsonDocument.Parse(handler.RequestBody!);
        var root = document.RootElement;
        Assert.AreEqual("tts-fixture-model", root.GetProperty("model").GetString());
        Assert.AreEqual("Read this aloud.", root.GetProperty("input").GetString());
        Assert.AreEqual("fixture-voice", root.GetProperty("voice").GetString());
        Assert.AreEqual("wav", root.GetProperty("response_format").GetString());
        Assert.AreEqual(4, root.EnumerateObject().Count());
        Assert.IsFalse(handler.RequestBody!.Contains(ApiKey, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task SynthesizeAsync_ReadsCredentialForEveryRequest()
    {
        var handler = new RecordingHandler(WaveBytes, "audio/wav");
        var keyStore = new FakeApiKeyStore(ApiKey);
        using var httpClient = new HttpClient(handler);
        using var client = new OpenAiSpeechClient(
            httpClient,
            keyStore,
            new CompanionSettings());

        await client.SynthesizeAsync("First response.");
        await client.SynthesizeAsync("Second response.");

        Assert.AreEqual(2, keyStore.GetCalls);
        Assert.AreEqual(2, handler.SendCalls);
    }

    [TestMethod]
    public async Task SynthesizeAsync_MissingKeyFailsBeforeSending()
    {
        var handler = new RecordingHandler(WaveBytes, "audio/wav");
        using var httpClient = new HttpClient(handler);
        using var client = new OpenAiSpeechClient(
            httpClient,
            new FakeApiKeyStore(apiKey: null),
            new CompanionSettings());

        var exception = await Assert.ThrowsExactlyAsync<SpeechSynthesisException>(
            () => client.SynthesizeAsync("Hello."));

        Assert.AreEqual(SpeechSynthesisProvider.OpenAI, exception.Provider);
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
            HttpStatusCode.TooManyRequests);
        handler.ResponseHeaders["x-request-id"] = ["req_speech !! private words"];
        using var httpClient = new HttpClient(handler);
        using var client = new OpenAiSpeechClient(
            httpClient,
            new FakeApiKeyStore(ApiKey),
            new CompanionSettings());

        var exception = await Assert.ThrowsExactlyAsync<SpeechSynthesisException>(
            () => client.SynthesizeAsync("Hello."));

        Assert.AreEqual(SpeechSynthesisProvider.OpenAI, exception.Provider);
        Assert.AreEqual(SpeechSynthesisFailureKind.RateLimited, exception.FailureKind);
        Assert.AreEqual(HttpStatusCode.TooManyRequests, exception.StatusCode);
        Assert.AreEqual("req_speechprivatewords", exception.RequestId);
        Assert.IsFalse(exception.ToString().Contains(ApiKey, StringComparison.Ordinal));
        Assert.IsFalse(exception.ToString().Contains("private diagnostics", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task SynthesizeAsync_EmptySuccessfulResponseIsTyped()
    {
        var handler = new RecordingHandler([], "audio/wav");
        using var httpClient = new HttpClient(handler);
        using var client = new OpenAiSpeechClient(
            httpClient,
            new FakeApiKeyStore(ApiKey),
            new CompanionSettings());

        var exception = await Assert.ThrowsExactlyAsync<SpeechSynthesisException>(
            () => client.SynthesizeAsync("Hello."));

        Assert.AreEqual(SpeechSynthesisFailureKind.InvalidResponse, exception.FailureKind);
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
        public string? AuthorizationScheme { get; private set; }
        public string? AuthorizationParameter { get; private set; }
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
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
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

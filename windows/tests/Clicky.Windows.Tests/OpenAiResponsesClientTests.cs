using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Clicky.Windows.Configuration;
using Clicky.Windows.Networking;
using Clicky.Windows.Providers;

namespace Clicky.Windows.Tests;

[TestClass]
public sealed class OpenAiResponsesClientTests
{
    private const string ApiKey = "sk-test-super-secret";

    [TestMethod]
    public async Task StreamChatAsync_PostsResponsesShapeWithPerRequestAuthorization()
    {
        const string responseSse =
            "event: response.output_text.delta\n" +
            "data: {\"type\":\"response.output_text.delta\",\"delta\":\"great\"}\n\n" +
            "event: response.completed\n" +
            "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\"}}\n\n";
        var handler = new RecordingHandler(responseSse);
        using var httpClient = new HttpClient(handler);
        var settings = new CompanionSettings { OpenAIModelId = "gpt-direct-test" };
        var keyStore = new FakeApiKeyStore(ApiKey);
        using var client = new OpenAiResponsesClient(httpClient, keyStore, settings);
        var request = new WorkerChatRequest(
            "this-request-model-must-be-ignored",
            "system instructions",
            "where is the channel rack?",
            images:
            [
                new WorkerChatImage(
                    [0xFF, 0xD8, 0xFF, 0xD9],
                    "active window",
                    "image/jpeg"),
            ],
            conversationHistory:
            [
                new WorkerConversationTurn("earlier question", "earlier answer"),
            ],
            maxTokens: 321);

        var deltas = await CollectAsync(client.StreamChatAsync(request));

        CollectionAssert.AreEqual(new[] { "great" }, deltas);
        Assert.AreEqual(1, keyStore.GetCalls);
        Assert.AreEqual(AiProviderKind.OpenAI, keyStore.LastProvider);
        Assert.AreEqual(HttpMethod.Post, handler.RequestMethod);
        Assert.AreEqual(new Uri("https://api.openai.com/v1/responses"), handler.RequestUri);
        Assert.AreEqual("application/json", handler.ContentType);
        Assert.AreEqual("text/event-stream", handler.Accept);
        Assert.AreEqual("Bearer", handler.AuthorizationScheme);
        Assert.AreEqual(ApiKey, handler.AuthorizationParameter);
        Assert.IsNotNull(handler.RequestBody);
        Assert.IsFalse(handler.RequestBody.Contains(ApiKey, StringComparison.Ordinal));

        using var document = JsonDocument.Parse(handler.RequestBody);
        var root = document.RootElement;
        Assert.AreEqual("gpt-direct-test", root.GetProperty("model").GetString());
        Assert.AreEqual("system instructions", root.GetProperty("instructions").GetString());
        Assert.AreEqual(321, root.GetProperty("max_output_tokens").GetInt32());
        Assert.IsTrue(root.GetProperty("stream").GetBoolean());
        Assert.IsFalse(root.GetProperty("store").GetBoolean());
        Assert.AreEqual(6, root.EnumerateObject().Count());

        var input = root.GetProperty("input");
        Assert.AreEqual(3, input.GetArrayLength());
        Assert.AreEqual("user", input[0].GetProperty("role").GetString());
        Assert.AreEqual("input_text", input[0].GetProperty("content")[0].GetProperty("type").GetString());
        Assert.AreEqual("earlier question", input[0].GetProperty("content")[0].GetProperty("text").GetString());
        Assert.AreEqual("assistant", input[1].GetProperty("role").GetString());
        Assert.AreEqual("input_text", input[1].GetProperty("content")[0].GetProperty("type").GetString());
        Assert.AreEqual("earlier answer", input[1].GetProperty("content")[0].GetProperty("text").GetString());

        var currentContent = input[2].GetProperty("content");
        Assert.AreEqual(3, currentContent.GetArrayLength());
        Assert.AreEqual("input_image", currentContent[0].GetProperty("type").GetString());
        Assert.AreEqual(
            "data:image/jpeg;base64,/9j/2Q==",
            currentContent[0].GetProperty("image_url").GetString());
        Assert.AreEqual("input_text", currentContent[1].GetProperty("type").GetString());
        Assert.AreEqual("active window", currentContent[1].GetProperty("text").GetString());
        Assert.AreEqual("where is the channel rack?", currentContent[2].GetProperty("text").GetString());
    }

    [TestMethod]
    public async Task StreamChatAsync_HttpFailureIsCategorizedWithoutBodyOrKeyLeak()
    {
        const string responseBody = "provider body contains sk-test-super-secret and private diagnostics";
        var handler = new RecordingHandler(responseBody, HttpStatusCode.Unauthorized);
        handler.ResponseHeaders["x-request-id"] = ["req_safe !! unsafe"];
        using var httpClient = new HttpClient(handler);
        using var client = new OpenAiResponsesClient(
            httpClient,
            new FakeApiKeyStore(ApiKey),
            new CompanionSettings());

        var exception = await Assert.ThrowsExactlyAsync<OpenAiProviderException>(async () =>
        {
            await CollectAsync(client.StreamChatAsync(new WorkerChatRequest("ignored", "system", "prompt")));
        });

        Assert.AreEqual(OpenAiProviderFailureKind.Authentication, exception.FailureKind);
        Assert.AreEqual(HttpStatusCode.Unauthorized, exception.StatusCode);
        Assert.AreEqual("req_safeunsafe", exception.RequestId);
        Assert.IsFalse(exception.ToString().Contains(ApiKey, StringComparison.Ordinal));
        Assert.IsFalse(exception.ToString().Contains("private diagnostics", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task StreamChatAsync_HttpFailureKeepsOnlySanitizedProviderCode()
    {
        const string responseBody =
            "{\"error\":{\"message\":\"private prompt and sk-test-super-secret\"," +
            "\"type\":\"invalid_request_error\",\"code\":\"model_not_found\"}}";
        var handler = new RecordingHandler(responseBody, HttpStatusCode.BadRequest);
        using var httpClient = new HttpClient(handler);
        using var client = new OpenAiResponsesClient(
            httpClient,
            new FakeApiKeyStore(ApiKey),
            new CompanionSettings());

        var exception = await Assert.ThrowsExactlyAsync<OpenAiProviderException>(async () =>
        {
            await CollectAsync(client.StreamChatAsync(new WorkerChatRequest("ignored", "system", "prompt")));
        });

        Assert.AreEqual(OpenAiProviderFailureKind.RequestRejected, exception.FailureKind);
        Assert.AreEqual("model_not_found", exception.ProviderCode);
        Assert.IsFalse(exception.ToString().Contains(ApiKey, StringComparison.Ordinal));
        Assert.IsFalse(exception.ToString().Contains("private prompt", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Forbidden, OpenAiProviderFailureKind.Permission)]
    [DataRow(HttpStatusCode.TooManyRequests, OpenAiProviderFailureKind.RateLimited)]
    [DataRow(HttpStatusCode.BadRequest, OpenAiProviderFailureKind.RequestRejected)]
    [DataRow(HttpStatusCode.BadGateway, OpenAiProviderFailureKind.Server)]
    public async Task StreamChatAsync_HttpStatusMapsToStableCategory(
        HttpStatusCode statusCode,
        OpenAiProviderFailureKind expectedKind)
    {
        var handler = new RecordingHandler("ignored provider body", statusCode);
        using var httpClient = new HttpClient(handler);
        using var client = new OpenAiResponsesClient(
            httpClient,
            new FakeApiKeyStore(ApiKey),
            new CompanionSettings());

        var exception = await Assert.ThrowsExactlyAsync<OpenAiProviderException>(async () =>
        {
            await CollectAsync(client.StreamChatAsync(new WorkerChatRequest("ignored", "system", "prompt")));
        });

        Assert.AreEqual(expectedKind, exception.FailureKind);
        Assert.AreEqual(statusCode, exception.StatusCode);
    }

    [TestMethod]
    public async Task StreamChatAsync_StreamFailureCarriesSanitizedRequestId()
    {
        var handler = new RecordingHandler(
            "data: {\"type\":\"response.incomplete\",\"response\":{\"incomplete_details\":{\"reason\":\"max_output_tokens\"}}}\n\n");
        handler.ResponseHeaders["x-request-id"] = ["req_stream !! private words"];
        using var httpClient = new HttpClient(handler);
        using var client = new OpenAiResponsesClient(
            httpClient,
            new FakeApiKeyStore(ApiKey),
            new CompanionSettings());

        var exception = await Assert.ThrowsExactlyAsync<OpenAiProviderException>(async () =>
        {
            await CollectAsync(client.StreamChatAsync(new WorkerChatRequest("ignored", "system", "prompt")));
        });

        Assert.AreEqual(OpenAiProviderFailureKind.Incomplete, exception.FailureKind);
        Assert.AreEqual("req_streamprivatewords", exception.RequestId);
        Assert.AreEqual("max_output_tokens", exception.ProviderCode);
    }

    [TestMethod]
    public async Task StreamChatAsync_MissingKeyFailsBeforeSending()
    {
        var handler = new RecordingHandler(string.Empty);
        using var httpClient = new HttpClient(handler);
        using var client = new OpenAiResponsesClient(
            httpClient,
            new FakeApiKeyStore(apiKey: null),
            new CompanionSettings());

        var exception = await Assert.ThrowsExactlyAsync<OpenAiProviderException>(async () =>
        {
            await CollectAsync(client.StreamChatAsync(new WorkerChatRequest("ignored", "system", "prompt")));
        });

        Assert.AreEqual(OpenAiProviderFailureKind.MissingApiKey, exception.FailureKind);
        Assert.AreEqual(0, handler.SendCalls);
    }

    [TestMethod]
    public async Task StreamChatAsync_ReadsCredentialForEveryRequest()
    {
        const string responseSse =
            "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\"}}\n\n";
        var handler = new RecordingHandler(responseSse);
        var keyStore = new FakeApiKeyStore(ApiKey);
        using var httpClient = new HttpClient(handler);
        using var client = new OpenAiResponsesClient(
            httpClient,
            keyStore,
            new CompanionSettings());
        var request = new WorkerChatRequest("ignored", "system", "prompt");

        await CollectAsync(client.StreamChatAsync(request));
        await CollectAsync(client.StreamChatAsync(request));

        Assert.AreEqual(2, keyStore.GetCalls);
        Assert.AreEqual(2, handler.SendCalls);
    }

    [TestMethod]
    public async Task StreamChatAsync_TrimsClipboardWhitespaceFromStoredKey()
    {
        const string responseSse =
            "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\"}}\n\n";
        var handler = new RecordingHandler(responseSse);
        using var httpClient = new HttpClient(handler);
        using var client = new OpenAiResponsesClient(
            httpClient,
            new FakeApiKeyStore($" \t{ApiKey[..8]}\r\n{ApiKey[8..]} \n"),
            new CompanionSettings());

        await CollectAsync(
            client.StreamChatAsync(new WorkerChatRequest("ignored", "system", "prompt")));

        Assert.AreEqual(ApiKey, handler.AuthorizationParameter);
    }

    [TestMethod]
    public async Task StreamChatAsync_RejectsUnsupportedKeyCharactersBeforeSending()
    {
        var handler = new RecordingHandler(string.Empty);
        using var httpClient = new HttpClient(handler);
        using var client = new OpenAiResponsesClient(
            httpClient,
            new FakeApiKeyStore("sk-valid-prefix-\u00e9"),
            new CompanionSettings());

        var exception = await Assert.ThrowsExactlyAsync<OpenAiProviderException>(async () =>
        {
            await CollectAsync(
                client.StreamChatAsync(new WorkerChatRequest("ignored", "system", "prompt")));
        });

        Assert.AreEqual(OpenAiProviderFailureKind.Authentication, exception.FailureKind);
        Assert.AreEqual("invalid_key_format", exception.ProviderCode);
        Assert.AreEqual(0, handler.SendCalls);
    }

    [TestMethod]
    public async Task StreamChatAsync_TransportFailureDoesNotRetainHandlerMessageOrKey()
    {
        var handler = new ThrowingHandler($"transport included {ApiKey}");
        using var httpClient = new HttpClient(handler);
        using var client = new OpenAiResponsesClient(
            httpClient,
            new FakeApiKeyStore(ApiKey),
            new CompanionSettings());

        var exception = await Assert.ThrowsExactlyAsync<OpenAiProviderException>(async () =>
        {
            await CollectAsync(client.StreamChatAsync(new WorkerChatRequest("ignored", "system", "prompt")));
        });

        Assert.AreEqual(OpenAiProviderFailureKind.Transport, exception.FailureKind);
        Assert.AreEqual("http_transport", exception.ProviderCode);
        Assert.IsFalse(exception.ToString().Contains(ApiKey, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task StreamChatAsync_MidStreamIoFailureIsTypedAndSanitized()
    {
        var handler = new MidStreamFailureHandler($"stream included {ApiKey}");
        using var httpClient = new HttpClient(handler);
        using var client = new OpenAiResponsesClient(
            httpClient,
            new FakeApiKeyStore(ApiKey),
            new CompanionSettings());

        var exception = await Assert.ThrowsExactlyAsync<OpenAiProviderException>(async () =>
        {
            await CollectAsync(client.StreamChatAsync(new WorkerChatRequest("ignored", "system", "prompt")));
        });

        Assert.AreEqual(OpenAiProviderFailureKind.Transport, exception.FailureKind);
        Assert.AreEqual("req_midstream", exception.RequestId);
        Assert.IsFalse(exception.ToString().Contains(ApiKey, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task StreamChatAsync_PropagatesCancellation()
    {
        var handler = new WaitingHandler();
        using var httpClient = new HttpClient(handler);
        using var client = new OpenAiResponsesClient(
            httpClient,
            new FakeApiKeyStore(ApiKey),
            new CompanionSettings());
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () =>
        {
            await CollectAsync(
                client.StreamChatAsync(
                    new WorkerChatRequest("ignored", "system", "prompt"),
                    cancellation.Token));
        });
    }

    private static async Task<List<string>> CollectAsync(IAsyncEnumerable<string> source)
    {
        var values = new List<string>();
        await foreach (var value in source)
        {
            values.Add(value);
        }

        return values;
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
        string responseBody,
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

        public Dictionary<string, string[]> ResponseHeaders { get; } = new(StringComparer.OrdinalIgnoreCase);

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
                Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(responseBody))),
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
                statusCode == HttpStatusCode.OK ? "text/event-stream" : "application/json");
            foreach (var header in ResponseHeaders)
            {
                response.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            return response;
        }
    }

    private sealed class ThrowingHandler(string message) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException(message);
    }

    private sealed class WaitingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }
    }

    private sealed class MidStreamFailureHandler(string message) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new ThrowingReadStream(message)),
            };
            response.Headers.TryAddWithoutValidation("x-request-id", "req_midstream");
            return Task.FromResult(response);
        }
    }

    private sealed class ThrowingReadStream(string message) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => throw new IOException(message);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException(message));

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

using System.Net;
using System.Text;
using System.Text.Json;
using Clicky.Windows.Configuration;
using Clicky.Windows.Networking;
using Clicky.Windows.Providers;

namespace Clicky.Windows.Tests;

[TestClass]
public sealed class GeminiDirectApiClientTests
{
    private const string ApiKey = "gemini-test-secret";

    [TestMethod]
    public async Task StreamChatAsync_PostsExactGeminiPayloadAndProviderHeaders()
    {
        const string responseSse = """
            data: {"candidates":[{"index":0,"content":{"role":"model","parts":[{"text":"great"}]}}]}

            data: {"candidates":[{"index":0,"finishReason":"STOP"}]}

            """;
        var handler = new RecordingHandler(responseSse);
        using var httpClient = new HttpClient(handler);
        var settings = new CompanionSettings { GeminiModelId = "gemini/test model" };
        using var client = new GeminiDirectApiClient(
            httpClient,
            new FakeApiKeyStore(ApiKey),
            settings);
        var request = new WorkerChatRequest(
            "request-model-must-be-ignored",
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
        Assert.AreEqual(HttpMethod.Post, handler.Method);
        Assert.AreEqual(
            "https://generativelanguage.googleapis.com/v1beta/models/gemini%2Ftest%20model:streamGenerateContent?alt=sse",
            handler.RequestUri!.OriginalString);
        Assert.AreEqual("application/json", handler.ContentType);
        CollectionAssert.AreEqual(new[] { "text/event-stream" }, handler.Headers["Accept"]);
        CollectionAssert.AreEqual(new[] { ApiKey }, handler.Headers["x-goog-api-key"]);

        using var document = JsonDocument.Parse(handler.Body!);
        var root = document.RootElement;
        Assert.IsFalse(root.TryGetProperty("model", out _));
        Assert.AreEqual(
            "system instructions",
            root.GetProperty("system_instruction").GetProperty("parts")[0].GetProperty("text").GetString());
        Assert.AreEqual(
            321,
            root.GetProperty("generationConfig").GetProperty("maxOutputTokens").GetInt32());

        var contents = root.GetProperty("contents");
        Assert.AreEqual(3, contents.GetArrayLength());
        Assert.AreEqual("user", contents[0].GetProperty("role").GetString());
        Assert.AreEqual("earlier question", contents[0].GetProperty("parts")[0].GetProperty("text").GetString());
        Assert.AreEqual("model", contents[1].GetProperty("role").GetString());
        Assert.AreEqual("earlier answer", contents[1].GetProperty("parts")[0].GetProperty("text").GetString());

        var currentParts = contents[2].GetProperty("parts");
        Assert.AreEqual("user", contents[2].GetProperty("role").GetString());
        Assert.AreEqual(3, currentParts.GetArrayLength());
        Assert.AreEqual(
            "image/jpeg",
            currentParts[0].GetProperty("inline_data").GetProperty("mime_type").GetString());
        Assert.AreEqual(
            "/9j/2Q==",
            currentParts[0].GetProperty("inline_data").GetProperty("data").GetString());
        Assert.AreEqual("active window", currentParts[1].GetProperty("text").GetString());
        Assert.AreEqual("where is the channel rack?", currentParts[2].GetProperty("text").GetString());
        Assert.IsFalse(handler.Body!.Contains(ApiKey, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task StreamChatAsync_FragmentedSse_ReturnsAllTextDeltas()
    {
        const string responseSse = """
            : keepalive

            data: {"candidates":[{"index":0,"content":{"parts":[{"text":"hello "}]}}]}

            data: {"candidates":[{"index":0,"content":{"parts":[{"text":"world"}]},"finishReason":"STOP"}]}

            """;
        var handler = new RecordingHandler(responseSse, maximumReadSize: 2);
        using var httpClient = new HttpClient(handler);
        using var client = CreateClient(httpClient);

        var deltas = await CollectAsync(client.StreamChatAsync(CreateRequest()));

        CollectionAssert.AreEqual(new[] { "hello ", "world" }, deltas);
    }

    [TestMethod]
    public async Task StreamChatAsync_MissingKey_FailsBeforeSendingRequest()
    {
        var handler = new RecordingHandler(string.Empty);
        using var httpClient = new HttpClient(handler);
        using var client = new GeminiDirectApiClient(
            httpClient,
            new FakeApiKeyStore(null),
            new CompanionSettings { GeminiModelId = "gemini-test" });

        var exception = await Assert.ThrowsExactlyAsync<GeminiProviderException>(
            () => CollectAsync(client.StreamChatAsync(CreateRequest())));

        Assert.AreEqual(GeminiProviderFailureKind.MissingApiKey, exception.FailureKind);
        Assert.AreEqual(0, handler.SendCount);
    }

    [TestMethod]
    public async Task StreamChatAsync_HttpError_IsTypedBoundedAndSanitized()
    {
        var rawSecret = ApiKey + new string('x', 20_000);
        var errorBody = JsonSerializer.Serialize(new
        {
            error = new
            {
                code = 429,
                status = "RESOURCE_EXHAUSTED<script>",
                message = rawSecret,
            },
        });
        var handler = new RecordingHandler(
            errorBody,
            HttpStatusCode.TooManyRequests,
            requestId: "goog_req<script>:123");
        using var httpClient = new HttpClient(handler);
        using var client = CreateClient(httpClient);

        var exception = await Assert.ThrowsExactlyAsync<GeminiProviderException>(
            () => CollectAsync(client.StreamChatAsync(CreateRequest())));

        Assert.AreEqual(GeminiProviderFailureKind.RateLimited, exception.FailureKind);
        Assert.AreEqual(HttpStatusCode.TooManyRequests, exception.StatusCode);
        Assert.AreEqual("RESOURCE_EXHAUSTEDscript", exception.ProviderCode);
        Assert.AreEqual("goog_reqscript:123", exception.RequestId);
        Assert.IsTrue(exception.Message.Length < 300);
        Assert.IsFalse(exception.ToString().Contains(ApiKey, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task StreamChatAsync_UnexpectedSuccessContentType_IsProtocolFailure()
    {
        var handler = new RecordingHandler("{}", successMediaType: "application/json");
        using var httpClient = new HttpClient(handler);
        using var client = CreateClient(httpClient);

        var exception = await Assert.ThrowsExactlyAsync<GeminiProviderException>(
            () => CollectAsync(client.StreamChatAsync(CreateRequest())));

        Assert.AreEqual(GeminiProviderFailureKind.StreamProtocol, exception.FailureKind);
    }

    [TestMethod]
    public async Task StreamChatAsync_TransportFailureIsTypedWithoutRawExceptionMessage()
    {
        const string secret = "secret-from-transport";
        var handler = new ThrowingHandler(secret);
        using var httpClient = new HttpClient(handler);
        using var client = CreateClient(httpClient);

        var exception = await Assert.ThrowsExactlyAsync<GeminiProviderException>(
            () => CollectAsync(client.StreamChatAsync(CreateRequest())));

        Assert.AreEqual(GeminiProviderFailureKind.Transport, exception.FailureKind);
        Assert.IsFalse(exception.ToString().Contains(secret, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task StreamChatAsync_Cancellation_InterruptsPendingResponse()
    {
        var handler = new CancellationHandler();
        using var httpClient = new HttpClient(handler);
        using var client = CreateClient(httpClient);
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(
            () => CollectAsync(client.StreamChatAsync(CreateRequest(), cancellationSource.Token)));
    }

    private static GeminiDirectApiClient CreateClient(HttpClient httpClient) =>
        new(
            httpClient,
            new FakeApiKeyStore(ApiKey),
            new CompanionSettings { GeminiModelId = "gemini-test" });

    private static WorkerChatRequest CreateRequest() =>
        new("ignored", "system", "prompt");

    private static async Task<string[]> CollectAsync(IAsyncEnumerable<string> source)
    {
        var values = new List<string>();
        await foreach (var value in source)
        {
            values.Add(value);
        }

        return values.ToArray();
    }

    private sealed class FakeApiKeyStore(string? apiKey) : IProviderApiKeyStore
    {
        public Task<string?> GetApiKeyAsync(
            AiProviderKind provider,
            CancellationToken cancellationToken = default)
        {
            Assert.AreEqual(AiProviderKind.Gemini, provider);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(apiKey);
        }

        public Task SaveApiKeyAsync(
            AiProviderKind provider,
            string apiKey,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DeleteApiKeyAsync(
            AiProviderKind provider,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> HasApiKeyAsync(
            AiProviderKind provider,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class RecordingHandler(
        string responseBody,
        HttpStatusCode statusCode = HttpStatusCode.OK,
        string? requestId = null,
        int? maximumReadSize = null,
        string successMediaType = "text/event-stream") : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }

        public Uri? RequestUri { get; private set; }

        public string? ContentType { get; private set; }

        public string? Body { get; private set; }

        public int SendCount { get; private set; }

        public Dictionary<string, string[]> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            SendCount++;
            Method = request.Method;
            RequestUri = request.RequestUri;
            ContentType = request.Content?.Headers.ContentType?.MediaType;
            Body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            foreach (var header in request.Headers)
            {
                Headers[header.Key] = header.Value.ToArray();
            }

            Stream responseStream = maximumReadSize is int readSize
                ? new FragmentedReadStream(Encoding.UTF8.GetBytes(responseBody), readSize)
                : new MemoryStream(Encoding.UTF8.GetBytes(responseBody));
            var response = new HttpResponseMessage(statusCode)
            {
                Content = new StreamContent(responseStream),
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
                statusCode == HttpStatusCode.OK ? successMediaType : "application/json");
            if (requestId is not null)
            {
                response.Headers.TryAddWithoutValidation("x-goog-request-id", requestId);
            }

            return response;
        }
    }

    private sealed class CancellationHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }
    }

    private sealed class ThrowingHandler(string message) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException(message);
    }

    private sealed class FragmentedReadStream(byte[] bytes, int maximumReadSize) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, maximumReadSize)], cancellationToken);
    }
}

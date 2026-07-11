using System.Net;
using System.Text;
using System.Text.Json;
using Clicky.Windows.Configuration;
using Clicky.Windows.Networking;
using Clicky.Windows.Providers;

namespace Clicky.Windows.Tests;

[TestClass]
public sealed class AnthropicDirectApiClientTests
{
    private const string ApiKey = "sk-ant-test-secret";

    [TestMethod]
    public async Task StreamChatAsync_PostsExactMessagesPayloadAndProviderHeaders()
    {
        const string responseSse = """
            event: content_block_delta
            data: {"type":"content_block_delta","delta":{"type":"text_delta","text":"great"}}

            event: message_stop
            data: {"type":"message_stop"}

            """;
        var handler = new RecordingHandler(responseSse);
        using var httpClient = new HttpClient(handler);
        var settings = new CompanionSettings { AnthropicModelId = "claude-settings-model" };
        using var client = new AnthropicDirectApiClient(
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

        var deltas = await ReadAllAsync(client.StreamChatAsync(request));

        CollectionAssert.AreEqual(new[] { "great" }, deltas);
        Assert.AreEqual(HttpMethod.Post, handler.Method);
        Assert.AreEqual(AnthropicDirectApiClient.MessagesEndpoint, handler.RequestUri);
        Assert.AreEqual("application/json", handler.ContentType);
        CollectionAssert.AreEqual(new[] { "text/event-stream" }, handler.Headers["Accept"]);
        CollectionAssert.AreEqual(new[] { ApiKey }, handler.Headers["x-api-key"]);
        CollectionAssert.AreEqual(new[] { "2023-06-01" }, handler.Headers["anthropic-version"]);

        using var document = JsonDocument.Parse(handler.Body!);
        var root = document.RootElement;
        Assert.AreEqual("claude-settings-model", root.GetProperty("model").GetString());
        Assert.AreEqual(321, root.GetProperty("max_tokens").GetInt32());
        Assert.IsTrue(root.GetProperty("stream").GetBoolean());
        Assert.AreEqual("system instructions", root.GetProperty("system").GetString());
        Assert.IsFalse(handler.Body!.Contains(ApiKey, StringComparison.Ordinal));

        var messages = root.GetProperty("messages");
        Assert.AreEqual(3, messages.GetArrayLength());
        Assert.AreEqual("user", messages[0].GetProperty("role").GetString());
        Assert.AreEqual("earlier question", messages[0].GetProperty("content").GetString());
        Assert.AreEqual("assistant", messages[1].GetProperty("role").GetString());
        Assert.AreEqual("earlier answer", messages[1].GetProperty("content").GetString());

        var currentContent = messages[2].GetProperty("content");
        Assert.AreEqual(3, currentContent.GetArrayLength());
        Assert.AreEqual("image", currentContent[0].GetProperty("type").GetString());
        Assert.AreEqual("base64", currentContent[0].GetProperty("source").GetProperty("type").GetString());
        Assert.AreEqual("image/jpeg", currentContent[0].GetProperty("source").GetProperty("media_type").GetString());
        Assert.AreEqual("/9j/2Q==", currentContent[0].GetProperty("source").GetProperty("data").GetString());
        Assert.AreEqual("active window", currentContent[1].GetProperty("text").GetString());
        Assert.AreEqual("where is the channel rack?", currentContent[2].GetProperty("text").GetString());
    }

    [TestMethod]
    public async Task StreamChatAsync_FragmentedSse_ReturnsAllTextDeltas()
    {
        const string responseSse = """
            event: content_block_delta
            data: {"type":"content_block_delta","delta":{"type":"text_delta","text":"hello "}}

            event: ping
            data: {"type":"ping"}

            event: content_block_delta
            data: {"type":"content_block_delta","delta":{"type":"text_delta","text":"world"}}

            event: message_stop
            data: {"type":"message_stop"}

            """;
        var handler = new RecordingHandler(responseSse, maximumReadSize: 2);
        using var httpClient = new HttpClient(handler);
        using var client = CreateClient(httpClient);

        var deltas = await ReadAllAsync(client.StreamChatAsync(CreateRequest()));

        CollectionAssert.AreEqual(new[] { "hello ", "world" }, deltas);
    }

    [TestMethod]
    public async Task StreamChatAsync_MissingKey_FailsBeforeSendingRequest()
    {
        var handler = new RecordingHandler(string.Empty);
        using var httpClient = new HttpClient(handler);
        using var client = new AnthropicDirectApiClient(
            httpClient,
            new FakeApiKeyStore(null),
            new CompanionSettings());

        var exception = await Assert.ThrowsExactlyAsync<AnthropicProviderException>(
            () => ReadAllAsync(client.StreamChatAsync(CreateRequest())));

        Assert.AreEqual(AnthropicProviderErrorKind.Configuration, exception.ErrorKind);
        Assert.AreEqual(0, handler.SendCount);
        Assert.IsFalse(exception.Message.Contains("key:", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task StreamChatAsync_HttpError_ReportsBoundedCategoryWithoutRawBodyOrKey()
    {
        var rawSecret = ApiKey + new string('x', 20_000);
        var errorBody = JsonSerializer.Serialize(new
        {
            type = "error",
            error = new
            {
                type = "authentication_error<script>",
                message = rawSecret,
            },
        });
        var handler = new RecordingHandler(
            errorBody,
            HttpStatusCode.Unauthorized,
            requestId: "req_test<script>:value");
        using var httpClient = new HttpClient(handler);
        using var client = CreateClient(httpClient);

        var exception = await Assert.ThrowsExactlyAsync<AnthropicProviderException>(
            () => ReadAllAsync(client.StreamChatAsync(CreateRequest())));

        Assert.AreEqual(AnthropicProviderErrorKind.HttpStatus, exception.ErrorKind);
        Assert.AreEqual(HttpStatusCode.Unauthorized, exception.StatusCode);
        Assert.AreEqual("authentication_errorscript", exception.ErrorType);
        Assert.AreEqual("req_testscript:value", exception.RequestId);
        StringAssert.Contains(exception.Message, "HTTP 401");
        Assert.IsTrue(exception.Message.Length < 300);
        Assert.IsFalse(exception.Message.Contains(ApiKey, StringComparison.Ordinal));
        Assert.IsFalse(exception.Message.Contains("message", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task StreamChatAsync_MidStreamError_ThrowsSanitizedProviderException()
    {
        const string responseSse = """
            event: content_block_delta
            data: {"type":"content_block_delta","delta":{"type":"text_delta","text":"partial"}}

            event: error
            data: {"type":"error","error":{"type":"overloaded_error","message":"sk-ant-test-secret"}}

            """;
        var handler = new RecordingHandler(responseSse, requestId: "req_stream_123");
        using var httpClient = new HttpClient(handler);
        using var client = CreateClient(httpClient);

        var exception = await Assert.ThrowsExactlyAsync<AnthropicProviderException>(
            () => ReadAllAsync(client.StreamChatAsync(CreateRequest())));

        Assert.AreEqual(AnthropicProviderErrorKind.StreamError, exception.ErrorKind);
        Assert.AreEqual("overloaded_error", exception.ErrorType);
        Assert.AreEqual("req_stream_123", exception.RequestId);
        Assert.IsFalse(exception.Message.Contains(ApiKey, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task StreamChatAsync_EofWithoutMessageStop_ThrowsPrematureEnd()
    {
        const string responseSse = """
            event: content_block_delta
            data: {"type":"content_block_delta","delta":{"type":"text_delta","text":"partial"}}

            """;
        var handler = new RecordingHandler(responseSse);
        using var httpClient = new HttpClient(handler);
        using var client = CreateClient(httpClient);

        var exception = await Assert.ThrowsExactlyAsync<AnthropicProviderException>(
            () => ReadAllAsync(client.StreamChatAsync(CreateRequest())));

        Assert.AreEqual(AnthropicProviderErrorKind.PrematureEnd, exception.ErrorKind);
    }

    [TestMethod]
    public async Task StreamChatAsync_MaxTokensStop_RejectsTruncatedResponse()
    {
        const string responseSse = """
            event: content_block_delta
            data: {"type":"content_block_delta","delta":{"type":"text_delta","text":"partial [POINT:none]"}}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"max_tokens","stop_sequence":null},"usage":{"output_tokens":321}}

            event: message_stop
            data: {"type":"message_stop"}

            """;
        var handler = new RecordingHandler(responseSse);
        using var httpClient = new HttpClient(handler);
        using var client = CreateClient(httpClient);

        var exception = await Assert.ThrowsExactlyAsync<AnthropicProviderException>(
            () => ReadAllAsync(client.StreamChatAsync(CreateRequest())));

        Assert.AreEqual(AnthropicProviderErrorKind.Incomplete, exception.ErrorKind);
    }

    [TestMethod]
    public async Task StreamChatAsync_Cancellation_InterruptsPendingResponse()
    {
        var handler = new CancellationHandler();
        using var httpClient = new HttpClient(handler);
        using var client = CreateClient(httpClient);
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(
            () => ReadAllAsync(client.StreamChatAsync(CreateRequest(), cancellationSource.Token)));
    }

    private static AnthropicDirectApiClient CreateClient(HttpClient httpClient)
    {
        return new AnthropicDirectApiClient(
            httpClient,
            new FakeApiKeyStore(ApiKey),
            new CompanionSettings { AnthropicModelId = "claude-test" });
    }

    private static WorkerChatRequest CreateRequest()
    {
        return new WorkerChatRequest("ignored", "system", "prompt");
    }

    private static async Task<string[]> ReadAllAsync(IAsyncEnumerable<string> source)
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
            Assert.AreEqual(AiProviderKind.Anthropic, provider);
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
        int? maximumReadSize = null) : HttpMessageHandler
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
                statusCode == HttpStatusCode.OK ? "text/event-stream" : "application/json");
            if (requestId is not null)
            {
                response.Headers.TryAddWithoutValidation("request-id", requestId);
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

    private sealed class FragmentedReadStream(byte[] bytes, int maximumReadSize) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            return base.ReadAsync(buffer[..Math.Min(buffer.Length, maximumReadSize)], cancellationToken);
        }
    }
}

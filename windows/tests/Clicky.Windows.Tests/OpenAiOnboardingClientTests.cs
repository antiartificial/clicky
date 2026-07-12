using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Clicky.Windows.Networking;
using Clicky.Windows.Providers;

namespace Clicky.Windows.Tests;

[TestClass]
public sealed class OpenAiOnboardingClientTests
{
    private const string ApiKey = "sk-onboarding-super-secret";
    private const string ValidationResponse = "{\"id\":\"resp_test\",\"status\":\"completed\"}";

    [TestMethod]
    public async Task DiscoverAndValidateAsync_UsesExpectedRequestsAndReturnsUtcResult()
    {
        var handler = new QueueHandler(
            JsonResponse(HttpStatusCode.OK, ModelsJson("gpt-4o-mini", "gpt-5.4-mini")),
            JsonResponse(HttpStatusCode.OK, ValidationResponse));
        using var httpClient = new HttpClient(handler);
        var keyStore = new FakeApiKeyStore(ApiKey);
        using var client = new OpenAiOnboardingClient(httpClient, keyStore);
        var before = DateTimeOffset.UtcNow;

        var result = await client.DiscoverAndValidateAsync();

        var after = DateTimeOffset.UtcNow;
        Assert.AreEqual(1, keyStore.GetCalls);
        Assert.AreEqual(AiProviderKind.OpenAI, keyStore.LastProvider);
        Assert.AreEqual(2, handler.Requests.Count);
        AssertRequest(handler.Requests[0], HttpMethod.Get, "https://api.openai.com/v1/models");
        AssertRequest(handler.Requests[1], HttpMethod.Post, "https://api.openai.com/v1/responses");
        CollectionAssert.AreEqual(
            new[] { "gpt-4o-mini", "gpt-5.4-mini" },
            result.Models.ToArray());
        Assert.AreEqual("gpt-5.4-mini", result.RecommendedModelId);
        Assert.AreEqual("gpt-5.4-mini", result.ValidatedModelId);
        Assert.AreEqual(TimeSpan.Zero, result.ValidatedAtUtc.Offset);
        Assert.IsTrue(result.ValidatedAtUtc >= before && result.ValidatedAtUtc <= after);

        using var body = JsonDocument.Parse(handler.Requests[1].Body!);
        var root = body.RootElement;
        Assert.AreEqual("gpt-5.4-mini", root.GetProperty("model").GetString());
        Assert.IsFalse(root.GetProperty("stream").GetBoolean());
        Assert.IsFalse(root.GetProperty("store").GetBoolean());
        Assert.AreEqual(64, root.GetProperty("max_output_tokens").GetInt32());
        Assert.AreEqual(5, root.EnumerateObject().Count());
        var content = root.GetProperty("input")[0].GetProperty("content");
        Assert.AreEqual("input_image", content[0].GetProperty("type").GetString());
        StringAssert.StartsWith(
            content[0].GetProperty("image_url").GetString(),
            "data:image/png;base64,iVBORw0KGgo");
        Assert.AreEqual("low", content[0].GetProperty("detail").GetString());
        Assert.AreEqual("input_text", content[1].GetProperty("type").GetString());
        Assert.IsFalse(handler.Requests[1].Body!.Contains(ApiKey, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task DiscoverAndValidateAsync_FiltersDeduplicatesAndSortsModels()
    {
        var handler = SuccessHandler(
            "gpt-5.7-mini",
            "gpt-4o-mini",
            "gpt-5.7-mini",
            "gpt-4.1",
            "gpt-5-chat-latest",
            "gpt-5-codex",
            "gpt-4o-audio-preview",
            "gpt-4o-realtime-preview",
            "gpt-4o-transcribe",
            "gpt-4o-tts",
            "gpt-image-1",
            "gpt-4o-search-preview",
            "gpt-4o-mini-2024-07-18",
            "gpt-3.5-turbo",
            "gpt-50-mini",
            "gpt-5 unsafe");
        using var httpClient = new HttpClient(handler);
        using var client = new OpenAiOnboardingClient(httpClient, new FakeApiKeyStore(ApiKey));

        var result = await client.DiscoverAndValidateAsync();

        CollectionAssert.AreEqual(
            new[] { "gpt-4.1", "gpt-4o-mini", "gpt-5.7-mini" },
            result.Models.ToArray());
        Assert.AreEqual("gpt-4o-mini", result.RecommendedModelId);
    }

    [TestMethod]
    public async Task DiscoverAndValidateAsync_UsesPriorityRecommendationOrder()
    {
        var handler = SuccessHandler(
            "gpt-4o-mini",
            "gpt-4.1-mini",
            "gpt-5-mini",
            "gpt-5.6-terra",
            "gpt-5.4-mini");
        using var httpClient = new HttpClient(handler);
        using var client = new OpenAiOnboardingClient(httpClient, new FakeApiKeyStore(ApiKey));

        var result = await client.DiscoverAndValidateAsync();

        Assert.AreEqual("gpt-5.4-mini", result.RecommendedModelId);
    }

    [TestMethod]
    public async Task DiscoverAndValidateAsync_UsesSensibleMiniFallback()
    {
        var handler = SuccessHandler("gpt-4o", "gpt-4.1", "gpt-5.7", "gpt-5.8-mini", "gpt-5.9-mini");
        using var httpClient = new HttpClient(handler);
        using var client = new OpenAiOnboardingClient(httpClient, new FakeApiKeyStore(ApiKey));

        var result = await client.DiscoverAndValidateAsync();

        Assert.AreEqual("gpt-5.9-mini", result.RecommendedModelId);
    }

    [TestMethod]
    public async Task DiscoverAndValidateAsync_ValidatesAvailablePreferredModel()
    {
        var handler = SuccessHandler("gpt-5.4-mini", "gpt-4.1");
        using var httpClient = new HttpClient(handler);
        using var client = new OpenAiOnboardingClient(httpClient, new FakeApiKeyStore(ApiKey));

        var result = await client.DiscoverAndValidateAsync("  gpt-4.1  ");

        Assert.AreEqual("gpt-5.4-mini", result.RecommendedModelId);
        Assert.AreEqual("gpt-4.1", result.ValidatedModelId);
        using var body = JsonDocument.Parse(handler.Requests[1].Body!);
        Assert.AreEqual("gpt-4.1", body.RootElement.GetProperty("model").GetString());
    }

    [TestMethod]
    public async Task DiscoverAndValidateAsync_RejectsUnavailablePreferredModelWithoutValidationCall()
    {
        var handler = SuccessHandler("gpt-5.4-mini");
        using var httpClient = new HttpClient(handler);
        using var client = new OpenAiOnboardingClient(httpClient, new FakeApiKeyStore(ApiKey));

        var exception = await Assert.ThrowsExactlyAsync<OpenAiProviderException>(
            () => client.DiscoverAndValidateAsync("gpt-5-codex"));

        Assert.AreEqual(OpenAiProviderFailureKind.RequestRejected, exception.FailureKind);
        Assert.AreEqual("model_not_available", exception.ProviderCode);
        Assert.AreEqual(1, handler.Requests.Count);
    }

    [TestMethod]
    public async Task DiscoverAndValidateAsync_MissingKeyFailsBeforeRequest()
    {
        var handler = SuccessHandler("gpt-5.4-mini");
        using var httpClient = new HttpClient(handler);
        using var client = new OpenAiOnboardingClient(httpClient, new FakeApiKeyStore(null));

        var exception = await Assert.ThrowsExactlyAsync<OpenAiProviderException>(
            () => client.DiscoverAndValidateAsync());

        Assert.AreEqual(OpenAiProviderFailureKind.MissingApiKey, exception.FailureKind);
        Assert.AreEqual(0, handler.Requests.Count);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Unauthorized, OpenAiProviderFailureKind.Authentication)]
    [DataRow(HttpStatusCode.Forbidden, OpenAiProviderFailureKind.Permission)]
    [DataRow(HttpStatusCode.TooManyRequests, OpenAiProviderFailureKind.RateLimited)]
    [DataRow(HttpStatusCode.BadGateway, OpenAiProviderFailureKind.Server)]
    public async Task DiscoverAndValidateAsync_CategorizesHttpFailures(
        HttpStatusCode statusCode,
        OpenAiProviderFailureKind expectedKind)
    {
        var handler = new QueueHandler(JsonResponse(statusCode, "provider text that must stay private"));
        using var httpClient = new HttpClient(handler);
        using var client = new OpenAiOnboardingClient(httpClient, new FakeApiKeyStore(ApiKey));

        var exception = await Assert.ThrowsExactlyAsync<OpenAiProviderException>(
            () => client.DiscoverAndValidateAsync());

        Assert.AreEqual(expectedKind, exception.FailureKind);
        Assert.AreEqual(statusCode, exception.StatusCode);
        Assert.IsFalse(exception.ToString().Contains("stay private", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow(HttpStatusCode.TooManyRequests, "insufficient_quota", OpenAiProviderFailureKind.RateLimited)]
    [DataRow(HttpStatusCode.TooManyRequests, "rate_limit_exceeded", OpenAiProviderFailureKind.RateLimited)]
    [DataRow(HttpStatusCode.NotFound, "model_not_found", OpenAiProviderFailureKind.RequestRejected)]
    public async Task DiscoverAndValidateAsync_PreservesOnlySafeErrorCategory(
        HttpStatusCode statusCode,
        string providerCode,
        OpenAiProviderFailureKind expectedKind)
    {
        var errorBody =
            $"{{\"error\":{{\"message\":\"private {ApiKey}\",\"code\":\"{providerCode} !!\"}}}}";
        var error = JsonResponse(statusCode, errorBody);
        error.Headers.TryAddWithoutValidation("x-request-id", "req_safe !! private");
        var handler = new QueueHandler(
            JsonResponse(HttpStatusCode.OK, ModelsJson("gpt-5.4-mini")),
            error);
        using var httpClient = new HttpClient(handler);
        using var client = new OpenAiOnboardingClient(httpClient, new FakeApiKeyStore(ApiKey));

        var exception = await Assert.ThrowsExactlyAsync<OpenAiProviderException>(
            () => client.DiscoverAndValidateAsync());

        Assert.AreEqual(expectedKind, exception.FailureKind);
        Assert.AreEqual(providerCode, exception.ProviderCode);
        Assert.AreEqual("req_safeprivate", exception.RequestId);
        Assert.IsFalse(exception.ToString().Contains(ApiKey, StringComparison.Ordinal));
        Assert.IsFalse(exception.ToString().Contains("private ", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task DiscoverAndValidateAsync_RejectsMalformedModelsJson()
    {
        var handler = new QueueHandler(JsonResponse(HttpStatusCode.OK, "{not-json"));
        using var httpClient = new HttpClient(handler);
        using var client = new OpenAiOnboardingClient(httpClient, new FakeApiKeyStore(ApiKey));

        var exception = await Assert.ThrowsExactlyAsync<OpenAiProviderException>(
            () => client.DiscoverAndValidateAsync());

        Assert.AreEqual(OpenAiProviderFailureKind.StreamProtocol, exception.FailureKind);
        Assert.AreEqual("models_response_malformed", exception.ProviderCode);
    }

    [TestMethod]
    public async Task DiscoverAndValidateAsync_RejectsOversizedModelsJson()
    {
        var handler = new QueueHandler(JsonResponse(HttpStatusCode.OK, new string(' ', 1024 * 1024 + 1)));
        using var httpClient = new HttpClient(handler);
        using var client = new OpenAiOnboardingClient(httpClient, new FakeApiKeyStore(ApiKey));

        var exception = await Assert.ThrowsExactlyAsync<OpenAiProviderException>(
            () => client.DiscoverAndValidateAsync());

        Assert.AreEqual(OpenAiProviderFailureKind.StreamProtocol, exception.FailureKind);
        Assert.AreEqual("models_response_too_large", exception.ProviderCode);
    }

    [TestMethod]
    public async Task DiscoverAndValidateAsync_RejectsMalformedValidationJson()
    {
        var handler = new QueueHandler(
            JsonResponse(HttpStatusCode.OK, ModelsJson("gpt-5.4-mini")),
            JsonResponse(HttpStatusCode.OK, "{}"));
        using var httpClient = new HttpClient(handler);
        using var client = new OpenAiOnboardingClient(httpClient, new FakeApiKeyStore(ApiKey));

        var exception = await Assert.ThrowsExactlyAsync<OpenAiProviderException>(
            () => client.DiscoverAndValidateAsync());

        Assert.AreEqual(OpenAiProviderFailureKind.StreamProtocol, exception.FailureKind);
        Assert.AreEqual("validation_response_malformed", exception.ProviderCode);
    }

    [TestMethod]
    public async Task DiscoverAndValidateAsync_RejectsOversizedValidationJson()
    {
        var handler = new QueueHandler(
            JsonResponse(HttpStatusCode.OK, ModelsJson("gpt-5.4-mini")),
            JsonResponse(HttpStatusCode.OK, new string(' ', 64 * 1024 + 1)));
        using var httpClient = new HttpClient(handler);
        using var client = new OpenAiOnboardingClient(httpClient, new FakeApiKeyStore(ApiKey));

        var exception = await Assert.ThrowsExactlyAsync<OpenAiProviderException>(
            () => client.DiscoverAndValidateAsync());

        Assert.AreEqual(OpenAiProviderFailureKind.StreamProtocol, exception.FailureKind);
        Assert.AreEqual("validation_response_too_large", exception.ProviderCode);
    }

    private static QueueHandler SuccessHandler(params string[] modelIds) =>
        new(
            JsonResponse(HttpStatusCode.OK, ModelsJson(modelIds)),
            JsonResponse(HttpStatusCode.OK, ValidationResponse));

    private static string ModelsJson(params string[] modelIds) =>
        JsonSerializer.Serialize(new
        {
            data = modelIds.Select(id => new { id }).ToArray(),
        });

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string body)
    {
        var response = new HttpResponseMessage(statusCode)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)),
        };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
            "application/json");
        return response;
    }

    private static void AssertRequest(RequestSnapshot request, HttpMethod method, string uri)
    {
        Assert.AreEqual(method, request.Method);
        Assert.AreEqual(new Uri(uri), request.Uri);
        Assert.AreEqual("application/json", request.Accept);
        Assert.AreEqual("Bearer", request.AuthorizationScheme);
        Assert.AreEqual(ApiKey, request.AuthorizationParameter);
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

    private sealed class QueueHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> responses = new(responses);

        public List<RequestSnapshot> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new RequestSnapshot(
                request.Method,
                request.RequestUri,
                request.Headers.Accept.SingleOrDefault()?.MediaType,
                request.Headers.Authorization?.Scheme,
                request.Headers.Authorization?.Parameter,
                request.Content?.Headers.ContentType?.MediaType,
                request.Content is null
                    ? null
                    : await request.Content.ReadAsStringAsync(cancellationToken)));

            return responses.Count > 0
                ? responses.Dequeue()
                : throw new InvalidOperationException("No local response was queued.");
        }
    }

    private sealed record RequestSnapshot(
        HttpMethod Method,
        Uri? Uri,
        string? Accept,
        string? AuthorizationScheme,
        string? AuthorizationParameter,
        string? ContentType,
        string? Body);
}

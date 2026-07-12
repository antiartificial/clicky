using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Clicky.Windows.Configuration;
using Clicky.Windows.Providers;

namespace Clicky.Windows.Networking;

public sealed class AnthropicDirectApiClient : IWorkerClient, IDisposable
{
    public static readonly Uri MessagesEndpoint = new("https://api.anthropic.com/v1/messages");

    private const string ApiVersion = "2023-06-01";
    private const int MaximumErrorDocumentBytes = 16 * 1024;

    private readonly HttpClient httpClient;
    private readonly IProviderApiKeyStore apiKeyStore;
    private readonly CompanionSettings settings;
    private readonly bool ownsHttpClient;

    public AnthropicDirectApiClient(
        HttpClient httpClient,
        IProviderApiKeyStore apiKeyStore,
        CompanionSettings settings)
        : this(httpClient, apiKeyStore, settings, ownsHttpClient: false)
    {
    }

    private AnthropicDirectApiClient(
        HttpClient httpClient,
        IProviderApiKeyStore apiKeyStore,
        CompanionSettings settings,
        bool ownsHttpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(apiKeyStore);
        ArgumentNullException.ThrowIfNull(settings);

        this.httpClient = httpClient;
        this.apiKeyStore = apiKeyStore;
        this.settings = settings;
        this.ownsHttpClient = ownsHttpClient;
    }

    public static AnthropicDirectApiClient CreateProduction(
        IProviderApiKeyStore apiKeyStore,
        CompanionSettings settings)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
        };

        return new AnthropicDirectApiClient(
            new HttpClient(handler, disposeHandler: true),
            apiKeyStore,
            settings,
            ownsHttpClient: true);
    }

    public async IAsyncEnumerable<string> StreamChatAsync(
        WorkerChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var apiKey = await apiKeyStore
            .GetApiKeyAsync(AiProviderKind.Anthropic, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw AnthropicProviderException.MissingApiKey();
        }

        var modelId = settings.AnthropicModelId;
        if (string.IsNullOrWhiteSpace(modelId))
        {
            throw AnthropicProviderException.MissingModel();
        }

        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(BuildRequestPayload(request, modelId));
        using var requestMessage = new HttpRequestMessage(HttpMethod.Post, MessagesEndpoint);
        requestMessage.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        requestMessage.Headers.TryAddWithoutValidation("x-api-key", apiKey);
        requestMessage.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);
        apiKey = null;
        requestMessage.Content = new ByteArrayContent(payloadBytes);
        requestMessage.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
        {
            CharSet = "utf-8",
        };

        using var response = await httpClient.SendAsync(
            requestMessage,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        var requestId = GetRequestId(response);
        if (!response.IsSuccessStatusCode)
        {
            var errorType = await ReadErrorTypeAsync(response.Content, cancellationToken)
                .ConfigureAwait(false);
            throw AnthropicProviderException.HttpFailure(
                response.StatusCode,
                errorType,
                requestId);
        }

        await using var responseStream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);

        await foreach (var textDelta in AnthropicSseReader
            .ReadTextDeltasAsync(
                responseStream,
                requireMessageStop: true,
                requestId,
                cancellationToken)
            .ConfigureAwait(false))
        {
            yield return textDelta;
        }
    }

    public void Dispose()
    {
        if (ownsHttpClient)
        {
            httpClient.Dispose();
        }
    }

    private static object BuildRequestPayload(WorkerChatRequest request, string modelId)
    {
        var messages = new List<object>();
        foreach (var conversationTurn in request.ConversationHistory)
        {
            messages.Add(new { role = "user", content = conversationTurn.UserText });
            messages.Add(new { role = "assistant", content = conversationTurn.AssistantText });
        }

        var currentContent = new List<object>();
        foreach (var image in request.Images)
        {
            currentContent.Add(new
            {
                type = "image",
                source = new
                {
                    type = "base64",
                    media_type = image.MediaType,
                    data = Convert.ToBase64String(image.ImageBytes.Span),
                },
            });
            currentContent.Add(new { type = "text", text = image.Label });
        }

        currentContent.Add(new { type = "text", text = request.UserPrompt });
        messages.Add(new { role = "user", content = currentContent });

        return new
        {
            model = modelId,
            max_tokens = request.MaxTokens,
            stream = true,
            system = request.SystemPrompt,
            messages,
        };
    }

    private static async Task<string?> ReadErrorTypeAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (buffer.Length < MaximumErrorDocumentBytes)
        {
            var remaining = MaximumErrorDocumentBytes - (int)buffer.Length;
            var bytesRead = await stream
                .ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length, remaining)), cancellationToken)
                .ConfigureAwait(false);
            if (bytesRead == 0)
            {
                break;
            }

            buffer.Write(chunk, 0, bytesRead);
        }

        return TryReadErrorType(buffer.ToArray());
    }

    private static string? TryReadErrorType(byte[] errorDocument)
    {
        try
        {
            using var document = JsonDocument.Parse(errorDocument);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (root.TryGetProperty("error", out var error) &&
                error.ValueKind == JsonValueKind.Object &&
                error.TryGetProperty("type", out var nestedType) &&
                nestedType.ValueKind == JsonValueKind.String)
            {
                return nestedType.GetString();
            }

            if (root.TryGetProperty("type", out var topLevelType) &&
                topLevelType.ValueKind == JsonValueKind.String &&
                !topLevelType.ValueEquals("error"))
            {
                return topLevelType.GetString();
            }
        }
        catch (JsonException)
        {
        }

        try
        {
            var reader = new Utf8JsonReader(errorDocument, isFinalBlock: false, state: default);
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName ||
                    !reader.ValueTextEquals("type") ||
                    !reader.Read() ||
                    reader.TokenType != JsonTokenType.String)
                {
                    continue;
                }

                var candidate = reader.GetString();
                if (!string.Equals(candidate, "error", StringComparison.Ordinal))
                {
                    return candidate;
                }
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static string? GetRequestId(HttpResponseMessage response)
    {
        return response.Headers.TryGetValues("request-id", out var values)
            ? values.FirstOrDefault()
            : null;
    }
}

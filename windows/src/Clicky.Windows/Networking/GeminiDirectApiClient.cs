using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Clicky.Windows.Configuration;
using Clicky.Windows.Providers;

namespace Clicky.Windows.Networking;

public sealed class GeminiDirectApiClient : IWorkerClient, IDisposable
{
    public static readonly Uri ApiBaseAddress =
        new("https://generativelanguage.googleapis.com/v1beta/models/");

    private const int MaximumErrorDocumentBytes = 16 * 1024;

    private readonly HttpClient httpClient;
    private readonly IProviderApiKeyStore apiKeyStore;
    private readonly CompanionSettings settings;
    private readonly bool ownsHttpClient;

    public GeminiDirectApiClient(
        HttpClient httpClient,
        IProviderApiKeyStore apiKeyStore,
        CompanionSettings settings)
        : this(httpClient, apiKeyStore, settings, ownsHttpClient: false)
    {
    }

    private GeminiDirectApiClient(
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

    public static GeminiDirectApiClient CreateProduction(
        IProviderApiKeyStore apiKeyStore,
        CompanionSettings settings)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
        };

        return new GeminiDirectApiClient(
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
        cancellationToken.ThrowIfCancellationRequested();

        var modelId = settings.GeminiModelId;
        if (string.IsNullOrWhiteSpace(modelId))
        {
            throw new GeminiProviderException(
                GeminiProviderFailureKind.InvalidConfiguration,
                "The Gemini model is not configured.");
        }

        var apiKey = await apiKeyStore
            .GetApiKeyAsync(AiProviderKind.Gemini, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new GeminiProviderException(
                GeminiProviderFailureKind.MissingApiKey,
                "A Gemini API key is not configured.");
        }

        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(BuildRequestPayload(request));
        var endpoint = BuildEndpoint(modelId);
        using var requestMessage = new HttpRequestMessage(HttpMethod.Post, endpoint);
        requestMessage.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        requestMessage.Headers.TryAddWithoutValidation("x-goog-api-key", apiKey);
        apiKey = null;
        requestMessage.Content = new ByteArrayContent(payloadBytes);
        requestMessage.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
        {
            CharSet = "utf-8",
        };

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(
                requestMessage,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            throw TransportFailure(requestId: null);
        }

        using (response)
        {
            var requestId = ReadRequestId(response);
            if (!response.IsSuccessStatusCode)
            {
                string? providerStatus;
                try
                {
                    providerStatus = await ReadErrorStatusAsync(response.Content, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception) when (exception is HttpRequestException or IOException)
                {
                    throw TransportFailure(requestId);
                }

                throw BuildHttpFailure(response.StatusCode, requestId, providerStatus);
            }

            if (!string.Equals(
                response.Content.Headers.ContentType?.MediaType,
                "text/event-stream",
                StringComparison.OrdinalIgnoreCase))
            {
                throw new GeminiProviderException(
                    GeminiProviderFailureKind.StreamProtocol,
                    "Gemini returned an unexpected response content type.",
                    requestId: requestId);
            }

            Stream responseStream;
            try
            {
                responseStream = await response.Content
                    .ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException)
            {
                throw TransportFailure(requestId);
            }

            await using (responseStream)
            {
                var deltas = GeminiSseReader.ReadTextDeltasAsync(
                    responseStream,
                    requestId,
                    cancellationToken);
                await using var enumerator = deltas.GetAsyncEnumerator(cancellationToken);

                while (true)
                {
                    bool hasNext;
                    try
                    {
                        hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
                    }
                    catch (GeminiProviderException exception)
                    {
                        throw exception.WithRequestId(requestId);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception) when (exception is HttpRequestException or IOException)
                    {
                        throw TransportFailure(requestId);
                    }

                    if (!hasNext)
                    {
                        yield break;
                    }

                    yield return enumerator.Current;
                }
            }
        }
    }

    public void Dispose()
    {
        if (ownsHttpClient)
        {
            httpClient.Dispose();
        }
    }

    private static Uri BuildEndpoint(string modelId)
    {
        var escapedModelId = Uri.EscapeDataString(modelId.Trim());
        return new Uri(ApiBaseAddress, $"{escapedModelId}:streamGenerateContent?alt=sse");
    }

    private static object BuildRequestPayload(WorkerChatRequest request)
    {
        var contents = new List<object>();
        foreach (var conversationTurn in request.ConversationHistory)
        {
            contents.Add(new
            {
                role = "user",
                parts = new[] { new { text = conversationTurn.UserText } },
            });
            contents.Add(new
            {
                role = "model",
                parts = new[] { new { text = conversationTurn.AssistantText } },
            });
        }

        var currentParts = new List<object>();
        foreach (var image in request.Images)
        {
            currentParts.Add(new
            {
                inline_data = new
                {
                    mime_type = "image/jpeg",
                    data = Convert.ToBase64String(image.ImageBytes.Span),
                },
            });
            currentParts.Add(new { text = image.Label });
        }

        currentParts.Add(new { text = request.UserPrompt });
        contents.Add(new { role = "user", parts = currentParts });

        return new
        {
            system_instruction = new
            {
                parts = new[] { new { text = request.SystemPrompt } },
            },
            contents,
            generationConfig = new
            {
                maxOutputTokens = request.MaxTokens,
            },
        };
    }

    private static async Task<string?> ReadErrorStatusAsync(
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

        var errorDocument = buffer.ToArray();
        try
        {
            using var document = JsonDocument.Parse(errorDocument);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("error", out var error) &&
                error.ValueKind == JsonValueKind.Object &&
                error.TryGetProperty("status", out var status) &&
                status.ValueKind == JsonValueKind.String)
            {
                return status.GetString();
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
                    !reader.ValueTextEquals("status") ||
                    !reader.Read() ||
                    reader.TokenType != JsonTokenType.String)
                {
                    continue;
                }

                return reader.GetString();
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static string? ReadRequestId(HttpResponseMessage response)
    {
        foreach (var headerName in new[]
        {
            "x-request-id",
            "request-id",
            "x-goog-request-id",
            "x-guploader-uploadid",
        })
        {
            if (response.Headers.TryGetValues(headerName, out var values))
            {
                return GeminiProviderException.SanitizeIdentifier(values.FirstOrDefault(), 128);
            }
        }

        return null;
    }

    private static GeminiProviderException BuildHttpFailure(
        HttpStatusCode statusCode,
        string? requestId,
        string? providerStatus)
    {
        var failureKind = statusCode switch
        {
            HttpStatusCode.Unauthorized => GeminiProviderFailureKind.Authentication,
            HttpStatusCode.Forbidden => GeminiProviderFailureKind.Permission,
            HttpStatusCode.TooManyRequests => GeminiProviderFailureKind.RateLimited,
            HttpStatusCode.BadRequest or
            HttpStatusCode.NotFound or
            HttpStatusCode.Conflict or
            HttpStatusCode.UnprocessableEntity => GeminiProviderFailureKind.RequestRejected,
            >= HttpStatusCode.InternalServerError => GeminiProviderFailureKind.Server,
            _ => GeminiProviderFailureKind.Failed,
        };
        var safeStatus = GeminiProviderException.SanitizeIdentifier(providerStatus, 64);
        var statusSuffix = safeStatus is null ? string.Empty : $" ({safeStatus})";
        var requestSuffix = requestId is null ? string.Empty : $" Request ID: {requestId}.";

        return new GeminiProviderException(
            failureKind,
            $"Gemini request failed with HTTP {(int)statusCode}{statusSuffix}.{requestSuffix}",
            statusCode,
            requestId,
            safeStatus);
    }

    private static GeminiProviderException TransportFailure(string? requestId) =>
        new(
            GeminiProviderFailureKind.Transport,
            "The Gemini request could not be completed.",
            requestId: requestId);
}

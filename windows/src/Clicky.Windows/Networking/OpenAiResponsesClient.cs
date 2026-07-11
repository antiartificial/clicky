using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Clicky.Windows.Configuration;
using Clicky.Windows.Providers;

namespace Clicky.Windows.Networking;

public sealed class OpenAiResponsesClient : IWorkerClient, IDisposable
{
    private static readonly Uri ResponsesEndpoint = new("https://api.openai.com/v1/responses");

    private readonly HttpClient httpClient;
    private readonly IProviderApiKeyStore apiKeyStore;
    private readonly CompanionSettings settings;
    private readonly bool ownsHttpClient;

    public OpenAiResponsesClient(
        HttpClient httpClient,
        IProviderApiKeyStore apiKeyStore,
        CompanionSettings settings)
        : this(httpClient, apiKeyStore, settings, ownsHttpClient: false)
    {
    }

    private OpenAiResponsesClient(
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

    public static OpenAiResponsesClient CreateProduction(
        IProviderApiKeyStore apiKeyStore,
        CompanionSettings settings)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
        };

        return new OpenAiResponsesClient(
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

        var model = settings.OpenAIModelId;
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new OpenAiProviderException(
                OpenAiProviderFailureKind.InvalidConfiguration,
                "The OpenAI model is not configured.");
        }

        var apiKey = await apiKeyStore
            .GetApiKeyAsync(AiProviderKind.OpenAI, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new OpenAiProviderException(
                OpenAiProviderFailureKind.MissingApiKey,
                "An OpenAI API key is not configured.");
        }

        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(BuildRequestPayload(request, model));
        using var requestMessage = new HttpRequestMessage(HttpMethod.Post, ResponsesEndpoint);
        requestMessage.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
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
        catch (HttpRequestException)
        {
            throw new OpenAiProviderException(
                OpenAiProviderFailureKind.Transport,
                "The OpenAI request could not be completed.");
        }

        using (response)
        {
            var requestId = ReadRequestId(response);
            if (!response.IsSuccessStatusCode)
            {
                throw BuildHttpFailure(response.StatusCode, requestId);
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
                IAsyncEnumerable<string> deltas = OpenAiResponsesSseReader.ReadTextDeltasAsync(
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
                    catch (OpenAiProviderException exception)
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

    private static object BuildRequestPayload(WorkerChatRequest request, string model)
    {
        var input = new List<object>();
        foreach (var conversationTurn in request.ConversationHistory)
        {
            input.Add(new
            {
                role = "user",
                content = new[]
                {
                    new { type = "input_text", text = conversationTurn.UserText },
                },
            });
            input.Add(new
            {
                role = "assistant",
                content = new[]
                {
                    new { type = "input_text", text = conversationTurn.AssistantText },
                },
            });
        }

        var currentContent = new List<object>();
        foreach (var image in request.Images)
        {
            currentContent.Add(new
            {
                type = "input_image",
                image_url = $"data:{image.MediaType};base64,{Convert.ToBase64String(image.ImageBytes.Span)}",
            });
            currentContent.Add(new { type = "input_text", text = image.Label });
        }

        currentContent.Add(new { type = "input_text", text = request.UserPrompt });
        input.Add(new { role = "user", content = currentContent });

        return new
        {
            model,
            instructions = request.SystemPrompt,
            input,
            stream = true,
            store = false,
            max_output_tokens = request.MaxTokens,
        };
    }

    private static string? ReadRequestId(HttpResponseMessage response)
    {
        foreach (var headerName in new[] { "x-request-id", "request-id", "openai-request-id" })
        {
            if (response.Headers.TryGetValues(headerName, out var values))
            {
                return OpenAiProviderException.SanitizeIdentifier(values.FirstOrDefault(), 128);
            }
        }

        return null;
    }

    private static OpenAiProviderException BuildHttpFailure(
        HttpStatusCode statusCode,
        string? requestId)
    {
        var failureKind = statusCode switch
        {
            HttpStatusCode.Unauthorized => OpenAiProviderFailureKind.Authentication,
            HttpStatusCode.Forbidden => OpenAiProviderFailureKind.Permission,
            HttpStatusCode.TooManyRequests => OpenAiProviderFailureKind.RateLimited,
            >= HttpStatusCode.InternalServerError => OpenAiProviderFailureKind.Server,
            _ => OpenAiProviderFailureKind.RequestRejected,
        };

        return new OpenAiProviderException(
            failureKind,
            $"OpenAI rejected the request with HTTP {(int)statusCode}.",
            statusCode,
            requestId);
    }

    private static OpenAiProviderException TransportFailure(string? requestId) =>
        new(
            OpenAiProviderFailureKind.Transport,
            "The OpenAI response stream could not be read.",
            requestId: requestId);
}

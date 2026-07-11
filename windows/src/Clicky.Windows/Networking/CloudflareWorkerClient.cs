using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Clicky.Windows.Networking;

public sealed class CloudflareWorkerClient : IWorkerClient
{
    private readonly HttpClient httpClient;
    private readonly Uri chatEndpoint;

    public CloudflareWorkerClient(HttpClient httpClient, Uri workerBaseUrl)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(workerBaseUrl);

        ValidateWorkerBaseUrl(workerBaseUrl);

        this.httpClient = httpClient;
        chatEndpoint = BuildChatEndpoint(workerBaseUrl);
    }

    public async IAsyncEnumerable<string> StreamChatAsync(
        WorkerChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(BuildRequestPayload(request));
        using var requestMessage = new HttpRequestMessage(HttpMethod.Post, chatEndpoint);
        requestMessage.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        requestMessage.Content = new ByteArrayContent(payloadBytes);
        requestMessage.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
        {
            CharSet = "utf-8",
        };

        using var response = await httpClient.SendAsync(
            requestMessage,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Worker chat request failed with HTTP {(int)response.StatusCode}.",
                inner: null,
                response.StatusCode);
        }

        await using var responseStream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);

        await foreach (var textDelta in AnthropicSseReader
            .ReadTextDeltasAsync(responseStream, cancellationToken)
            .ConfigureAwait(false))
        {
            yield return textDelta;
        }
    }

    private static object BuildRequestPayload(WorkerChatRequest request)
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
            model = request.Model,
            max_tokens = request.MaxTokens,
            stream = true,
            system = request.SystemPrompt,
            messages,
        };
    }

    private static Uri BuildChatEndpoint(Uri workerBaseUrl)
    {
        var builder = new UriBuilder(workerBaseUrl)
        {
            Query = string.Empty,
            Fragment = string.Empty,
        };

        builder.Path = "/chat";
        return builder.Uri;
    }

    private static void ValidateWorkerBaseUrl(Uri workerBaseUrl)
    {
        if (!workerBaseUrl.IsAbsoluteUri ||
            (workerBaseUrl.Scheme != Uri.UriSchemeHttp &&
             workerBaseUrl.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException(
                "The worker URL must be an absolute HTTP or HTTPS URL.",
                nameof(workerBaseUrl));
        }

        if (workerBaseUrl.Scheme == Uri.UriSchemeHttp && !workerBaseUrl.IsLoopback)
        {
            throw new ArgumentException(
                "Remote Worker URLs must use HTTPS. HTTP is allowed only for local development.",
                nameof(workerBaseUrl));
        }

        if (!string.IsNullOrEmpty(workerBaseUrl.UserInfo))
        {
            throw new ArgumentException(
                "The worker URL cannot contain embedded credentials.",
                nameof(workerBaseUrl));
        }
    }
}

using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Clicky.Windows.Providers;

namespace Clicky.Windows.Networking;

public interface IOpenAiOnboardingClient
{
    Task<OpenAiOnboardingResult> DiscoverAndValidateAsync(
        string? preferredModelId = null,
        CancellationToken cancellationToken = default);
}

public sealed record OpenAiOnboardingResult(
    IReadOnlyList<string> Models,
    string RecommendedModelId,
    string ValidatedModelId,
    DateTimeOffset ValidatedAtUtc);

public sealed class OpenAiOnboardingClient : IOpenAiOnboardingClient, IDisposable
{
    private static readonly Uri ModelsEndpoint = new("https://api.openai.com/v1/models");
    private static readonly Uri ResponsesEndpoint = new("https://api.openai.com/v1/responses");
    private static readonly string[] PreferredModels =
    [
        "gpt-5.4-mini",
        "gpt-5.6-terra",
        "gpt-5-mini",
        "gpt-4.1-mini",
        "gpt-4o-mini",
    ];
    private static readonly string[] ExcludedModelTerms =
    [
        "audio",
        "realtime",
        "transcribe",
        "tts",
        "image",
        "search",
        "chat",
        "codex",
    ];

    private const int MaximumModelsDocumentBytes = 1024 * 1024;
    private const int MaximumValidationDocumentBytes = 64 * 1024;
    private const int MaximumErrorDocumentBytes = 16 * 1024;
    private const int MaximumDiscoveredModels = 4096;
    private const int MaximumModelIdLength = 128;
    private const string ValidationImageDataUrl =
        "data:image/png;base64," +
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";

    private readonly HttpClient httpClient;
    private readonly IProviderApiKeyStore apiKeyStore;
    private readonly bool ownsHttpClient;

    public OpenAiOnboardingClient(
        HttpClient httpClient,
        IProviderApiKeyStore apiKeyStore)
        : this(httpClient, apiKeyStore, ownsHttpClient: false)
    {
    }

    private OpenAiOnboardingClient(
        HttpClient httpClient,
        IProviderApiKeyStore apiKeyStore,
        bool ownsHttpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(apiKeyStore);

        this.httpClient = httpClient;
        this.apiKeyStore = apiKeyStore;
        this.ownsHttpClient = ownsHttpClient;
    }

    public static OpenAiOnboardingClient CreateProduction(IProviderApiKeyStore apiKeyStore)
    {
        ArgumentNullException.ThrowIfNull(apiKeyStore);

        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
        };

        return new OpenAiOnboardingClient(
            new HttpClient(handler, disposeHandler: true),
            apiKeyStore,
            ownsHttpClient: true);
    }

    public async Task<OpenAiOnboardingResult> DiscoverAndValidateAsync(
        string? preferredModelId = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var apiKey = await ReadApiKeyAsync(cancellationToken).ConfigureAwait(false);
        var models = await DiscoverModelsAsync(apiKey, cancellationToken).ConfigureAwait(false);
        var recommendedModel = RecommendModel(models);
        var validationModel = SelectValidationModel(preferredModelId, models, recommendedModel);

        await ValidateModelAsync(apiKey, validationModel, cancellationToken).ConfigureAwait(false);

        return new OpenAiOnboardingResult(
            models,
            recommendedModel,
            validationModel,
            DateTimeOffset.UtcNow);
    }

    public void Dispose()
    {
        if (ownsHttpClient)
        {
            httpClient.Dispose();
        }
    }

    private async Task<string> ReadApiKeyAsync(CancellationToken cancellationToken)
    {
        var apiKey = await apiKeyStore
            .GetApiKeyAsync(AiProviderKind.OpenAI, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new OpenAiProviderException(
                OpenAiProviderFailureKind.MissingApiKey,
                "An OpenAI API key is not configured.");
        }

        apiKey = string.Concat(apiKey.Where(character => !char.IsWhiteSpace(character)));
        if (apiKey.Any(character => character is < (char)33 or > (char)126))
        {
            throw new OpenAiProviderException(
                OpenAiProviderFailureKind.Authentication,
                "The stored OpenAI API key contains unsupported characters.",
                providerCode: "invalid_key_format");
        }

        return apiKey;
    }

    private async Task<IReadOnlyList<string>> DiscoverModelsAsync(
        string apiKey,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, ModelsEndpoint, apiKey);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        var requestId = ReadRequestId(response);

        if (!response.IsSuccessStatusCode)
        {
            throw await BuildHttpFailureAsync(response, requestId, cancellationToken)
                .ConfigureAwait(false);
        }

        using var document = await ReadJsonDocumentAsync(
            response.Content,
            MaximumModelsDocumentBytes,
            "models_response",
            requestId,
            cancellationToken).ConfigureAwait(false);

        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            throw ProtocolFailure("models_response_malformed", requestId);
        }

        var models = new HashSet<string>(StringComparer.Ordinal);
        var examined = 0;
        foreach (var item in data.EnumerateArray())
        {
            examined++;
            if (examined > MaximumDiscoveredModels)
            {
                throw ProtocolFailure("models_response_too_many_items", requestId);
            }

            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("id", out var idProperty) ||
                idProperty.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var id = idProperty.GetString();
            if (id is not null && IsSafeModelId(id) && IsCompatibleModel(id))
            {
                models.Add(id);
            }
        }

        var sortedModels = models.Order(StringComparer.Ordinal).ToArray();
        if (sortedModels.Length == 0)
        {
            throw new OpenAiProviderException(
                OpenAiProviderFailureKind.RequestRejected,
                "OpenAI did not return a compatible tutoring model.",
                requestId: requestId,
                providerCode: "no_compatible_models");
        }

        return Array.AsReadOnly(sortedModels);
    }

    private async Task ValidateModelAsync(
        string apiKey,
        string model,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            model,
            input = new[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "input_image", image_url = ValidationImageDataUrl, detail = "low" },
                        new { type = "input_text", text = "Reply with OK." },
                    },
                },
            },
            stream = false,
            store = false,
            max_output_tokens = 64,
        });

        using var request = CreateRequest(HttpMethod.Post, ResponsesEndpoint, apiKey);
        request.Content = new ByteArrayContent(payload);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
        {
            CharSet = "utf-8",
        };

        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        var requestId = ReadRequestId(response);
        if (!response.IsSuccessStatusCode)
        {
            throw await BuildHttpFailureAsync(response, requestId, cancellationToken)
                .ConfigureAwait(false);
        }

        using var document = await ReadJsonDocumentAsync(
            response.Content,
            MaximumValidationDocumentBytes,
            "validation_response",
            requestId,
            cancellationToken).ConfigureAwait(false);

        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("id", out var id) ||
            id.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(id.GetString()) ||
            !root.TryGetProperty("status", out var status) ||
            status.ValueKind != JsonValueKind.String)
        {
            throw ProtocolFailure("validation_response_malformed", requestId);
        }

        if (!string.Equals(status.GetString(), "completed", StringComparison.Ordinal))
        {
            throw new OpenAiProviderException(
                OpenAiProviderFailureKind.Failed,
                "OpenAI did not complete the model validation request.",
                requestId: requestId,
                providerCode: "validation_not_completed");
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            var transportCode = exception.HttpRequestError == HttpRequestError.Unknown
                ? "http_transport"
                : $"transport_{exception.HttpRequestError}";
            throw new OpenAiProviderException(
                OpenAiProviderFailureKind.Transport,
                "The OpenAI onboarding request could not be completed.",
                providerCode: transportCode);
        }
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, Uri endpoint, string apiKey)
    {
        var request = new HttpRequestMessage(method, endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return request;
    }

    private static string SelectValidationModel(
        string? preferredModelId,
        IReadOnlyList<string> models,
        string recommendedModel)
    {
        if (string.IsNullOrWhiteSpace(preferredModelId))
        {
            return recommendedModel;
        }

        var trimmed = preferredModelId.Trim();
        if (!IsSafeModelId(trimmed) || !models.Contains(trimmed, StringComparer.Ordinal))
        {
            throw new OpenAiProviderException(
                OpenAiProviderFailureKind.RequestRejected,
                "The preferred OpenAI model is not available for tutoring.",
                providerCode: "model_not_available");
        }

        return trimmed;
    }

    private static string RecommendModel(IReadOnlyList<string> models)
    {
        foreach (var preferredModel in PreferredModels)
        {
            if (models.Contains(preferredModel, StringComparer.Ordinal))
            {
                return preferredModel;
            }
        }

        return models
            .OrderBy(GetModelStyleRank)
            .ThenBy(GetFamilyRank)
            .ThenByDescending(GetGpt5Version)
            .ThenBy(model => model, StringComparer.Ordinal)
            .First();
    }

    private static bool IsCompatibleModel(string modelId)
    {
        if (!IsFamily(modelId, "gpt-5") &&
            !IsFamily(modelId, "gpt-4.1") &&
            !IsFamily(modelId, "gpt-4o"))
        {
            return false;
        }

        return !HasDatedSnapshotSuffix(modelId) &&
            !ExcludedModelTerms.Any(term => modelId.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSafeModelId(string modelId)
    {
        if (modelId.Length is 0 or > MaximumModelIdLength)
        {
            return false;
        }

        var sanitized = OpenAiProviderException.SanitizeIdentifier(modelId, MaximumModelIdLength);
        return string.Equals(modelId, sanitized, StringComparison.Ordinal);
    }

    private static bool IsFamily(string modelId, string family)
    {
        if (!modelId.StartsWith(family, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return modelId.Length == family.Length ||
            modelId[family.Length] is '-' or '.';
    }

    private static bool HasDatedSnapshotSuffix(string modelId)
    {
        const int suffixLength = 11;
        if (modelId.Length < suffixLength)
        {
            return false;
        }

        var suffix = modelId.AsSpan(modelId.Length - suffixLength);
        return suffix[0] == '-' &&
            suffix[5] == '-' &&
            suffix[8] == '-' &&
            IsAsciiDigits(suffix[1..5]) &&
            IsAsciiDigits(suffix[6..8]) &&
            IsAsciiDigits(suffix[9..11]);
    }

    private static bool IsAsciiDigits(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (character is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    private static int GetModelStyleRank(string modelId)
    {
        if (modelId.Contains("-mini", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (modelId.EndsWith("-latest", StringComparison.OrdinalIgnoreCase) ||
            modelId is "gpt-5" or "gpt-4.1" or "gpt-4o")
        {
            return 1;
        }

        return 2;
    }

    private static int GetFamilyRank(string modelId) =>
        IsFamily(modelId, "gpt-5") ? 0 :
        IsFamily(modelId, "gpt-4.1") ? 1 : 2;

    private static Version GetGpt5Version(string modelId)
    {
        if (!modelId.StartsWith("gpt-5.", StringComparison.OrdinalIgnoreCase))
        {
            return new Version(5, 0);
        }

        var end = modelId.IndexOf('-', "gpt-5.".Length);
        var versionText = end < 0
            ? modelId["gpt-".Length..]
            : modelId["gpt-".Length..end];
        return Version.TryParse(versionText, out var version)
            ? version
            : new Version(5, 0);
    }

    private static async Task<JsonDocument> ReadJsonDocumentAsync(
        HttpContent content,
        int maximumBytes,
        string providerCodePrefix,
        string? requestId,
        CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await ReadBoundedAsync(content, maximumBytes, cancellationToken)
                .ConfigureAwait(false);
            return JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ResponseLimitExceededException)
        {
            throw ProtocolFailure($"{providerCodePrefix}_too_large", requestId);
        }
        catch (JsonException)
        {
            throw ProtocolFailure($"{providerCodePrefix}_malformed", requestId);
        }
        catch (Exception exception) when (exception is IOException or HttpRequestException)
        {
            throw new OpenAiProviderException(
                OpenAiProviderFailureKind.Transport,
                "The OpenAI onboarding response could not be read.",
                requestId: requestId,
                providerCode: "response_transport");
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream(Math.Min(maximumBytes, 16 * 1024));
        var readBuffer = new byte[4096];

        while (true)
        {
            var bytesRead = await stream.ReadAsync(readBuffer, cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                return buffer.ToArray();
            }

            if (buffer.Length + bytesRead > maximumBytes)
            {
                throw new ResponseLimitExceededException();
            }

            buffer.Write(readBuffer, 0, bytesRead);
        }
    }

    private static async Task<OpenAiProviderException> BuildHttpFailureAsync(
        HttpResponseMessage response,
        string? requestId,
        CancellationToken cancellationToken)
    {
        var providerCode = await TryReadErrorCodeAsync(response.Content, cancellationToken)
            .ConfigureAwait(false);
        var failureKind = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => OpenAiProviderFailureKind.Authentication,
            HttpStatusCode.Forbidden => OpenAiProviderFailureKind.Permission,
            HttpStatusCode.TooManyRequests => OpenAiProviderFailureKind.RateLimited,
            >= HttpStatusCode.InternalServerError => OpenAiProviderFailureKind.Server,
            _ => OpenAiProviderFailureKind.RequestRejected,
        };

        return new OpenAiProviderException(
            failureKind,
            $"OpenAI rejected the onboarding request with HTTP {(int)response.StatusCode}.",
            response.StatusCode,
            requestId,
            providerCode);
    }

    private static async Task<string?> TryReadErrorCodeAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await ReadBoundedAsync(content, MaximumErrorDocumentBytes, cancellationToken)
                .ConfigureAwait(false);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("error", out var error) ||
                error.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return ReadSafeString(error, "code") ?? ReadSafeString(error, "type");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or HttpRequestException or JsonException or ResponseLimitExceededException)
        {
            return null;
        }
    }

    private static string? ReadSafeString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? OpenAiProviderException.SanitizeIdentifier(property.GetString(), 64)
            : null;

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

    private static OpenAiProviderException ProtocolFailure(string providerCode, string? requestId) =>
        new(
            OpenAiProviderFailureKind.StreamProtocol,
            "OpenAI returned invalid onboarding data.",
            requestId: requestId,
            providerCode: providerCode);

    private sealed class ResponseLimitExceededException : Exception;
}

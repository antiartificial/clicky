using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Clicky.Windows.Configuration;
using Clicky.Windows.Providers;

namespace Clicky.Windows.Speech;

public sealed class OpenAiSpeechClient : ITextToSpeechClient, IDisposable
{
    private const int MaximumInputLength = 4096;
    private static readonly Uri SpeechEndpoint = new("https://api.openai.com/v1/audio/speech");

    private readonly HttpClient httpClient;
    private readonly IProviderApiKeyStore apiKeyStore;
    private readonly CompanionSettings settings;
    private readonly bool ownsHttpClient;

    public OpenAiSpeechClient(
        HttpClient httpClient,
        IProviderApiKeyStore apiKeyStore,
        CompanionSettings settings)
        : this(httpClient, apiKeyStore, settings, ownsHttpClient: false)
    {
    }

    private OpenAiSpeechClient(
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

    public static OpenAiSpeechClient CreateProduction(
        IProviderApiKeyStore apiKeyStore,
        CompanionSettings settings)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
        };

        return new OpenAiSpeechClient(
            new HttpClient(handler, disposeHandler: true),
            apiKeyStore,
            settings,
            ownsHttpClient: true);
    }

    public async Task<SpeechAudio> SynthesizeAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateText(text);

        var model = settings.OpenAITtsModelId;
        if (string.IsNullOrWhiteSpace(model))
        {
            throw InvalidConfiguration("The OpenAI speech model is not configured.");
        }

        var voice = settings.OpenAITtsVoice;
        if (string.IsNullOrWhiteSpace(voice))
        {
            throw InvalidConfiguration("The OpenAI speech voice is not configured.");
        }

        var apiKey = await apiKeyStore
            .GetApiKeyAsync(AiProviderKind.OpenAI, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new SpeechSynthesisException(
                SpeechSynthesisProvider.OpenAI,
                SpeechSynthesisFailureKind.MissingApiKey,
                "An OpenAI API key is not configured.");
        }

        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            model,
            input = text,
            voice,
            response_format = "wav",
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, SpeechEndpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/wav"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        apiKey = null;
        request.Content = new ByteArrayContent(payloadBytes);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
        {
            CharSet = "utf-8",
        };

        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        var requestId = SpeechHttpSupport.ReadRequestId(response);
        if (!response.IsSuccessStatusCode)
        {
            throw SpeechHttpSupport.BuildHttpFailure(
                SpeechSynthesisProvider.OpenAI,
                response.StatusCode,
                requestId);
        }

        byte[] audioBytes;
        try
        {
            audioBytes = await SpeechHttpSupport.ReadAudioBytesAsync(
                response.Content,
                SpeechSynthesisProvider.OpenAI,
                requestId,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SpeechSynthesisException)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            throw SpeechHttpSupport.TransportFailure(SpeechSynthesisProvider.OpenAI, requestId);
        }

        return new SpeechAudio(audioBytes, "audio/wav");
    }

    public void Dispose()
    {
        if (ownsHttpClient)
        {
            httpClient.Dispose();
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
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            throw SpeechHttpSupport.TransportFailure(SpeechSynthesisProvider.OpenAI);
        }
    }

    private static void ValidateText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new SpeechSynthesisException(
                SpeechSynthesisProvider.OpenAI,
                SpeechSynthesisFailureKind.InvalidRequest,
                "Speech text cannot be empty.");
        }

        if (text.Length > MaximumInputLength)
        {
            throw new SpeechSynthesisException(
                SpeechSynthesisProvider.OpenAI,
                SpeechSynthesisFailureKind.InvalidRequest,
                $"OpenAI speech text cannot exceed {MaximumInputLength} characters.");
        }
    }

    private static SpeechSynthesisException InvalidConfiguration(string message) =>
        new(
            SpeechSynthesisProvider.OpenAI,
            SpeechSynthesisFailureKind.InvalidConfiguration,
            message);
}

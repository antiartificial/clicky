using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Clicky.Windows.Configuration;
using Clicky.Windows.Providers;

namespace Clicky.Windows.Speech;

public sealed class ElevenLabsSpeechClient : ITextToSpeechClient, IDisposable
{
    private const string SpeechEndpointPrefix =
        "https://api.elevenlabs.io/v1/text-to-speech/";
    private const string RequestedOutputFormat = "wav_24000";

    private readonly HttpClient httpClient;
    private readonly IProviderApiKeyStore apiKeyStore;
    private readonly CompanionSettings settings;
    private readonly bool ownsHttpClient;

    public ElevenLabsSpeechClient(
        HttpClient httpClient,
        IProviderApiKeyStore apiKeyStore,
        CompanionSettings settings)
        : this(httpClient, apiKeyStore, settings, ownsHttpClient: false)
    {
    }

    private ElevenLabsSpeechClient(
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

    public static ElevenLabsSpeechClient CreateProduction(
        IProviderApiKeyStore apiKeyStore,
        CompanionSettings settings)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
        };

        return new ElevenLabsSpeechClient(
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
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new SpeechSynthesisException(
                SpeechSynthesisProvider.ElevenLabs,
                SpeechSynthesisFailureKind.InvalidRequest,
                "Speech text cannot be empty.");
        }

        var voiceId = settings.ElevenLabsVoiceId;
        if (string.IsNullOrWhiteSpace(voiceId))
        {
            throw new SpeechSynthesisException(
                SpeechSynthesisProvider.ElevenLabs,
                SpeechSynthesisFailureKind.InvalidConfiguration,
                "The ElevenLabs voice ID is not configured.");
        }

        var apiKey = await apiKeyStore
            .GetApiKeyAsync(AiProviderKind.ElevenLabs, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new SpeechSynthesisException(
                SpeechSynthesisProvider.ElevenLabs,
                SpeechSynthesisFailureKind.MissingApiKey,
                "An ElevenLabs API key is not configured.");
        }

        var endpoint = new Uri(
            $"{SpeechEndpointPrefix}{Uri.EscapeDataString(voiceId.Trim())}" +
            $"?output_format={RequestedOutputFormat}");
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(new { text });

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/wav"));
        request.Headers.TryAddWithoutValidation("xi-api-key", apiKey);
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
                SpeechSynthesisProvider.ElevenLabs,
                response.StatusCode,
                requestId);
        }

        byte[] audioBytes;
        try
        {
            audioBytes = await SpeechHttpSupport.ReadAudioBytesAsync(
                response.Content,
                SpeechSynthesisProvider.ElevenLabs,
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
            throw SpeechHttpSupport.TransportFailure(
                SpeechSynthesisProvider.ElevenLabs,
                requestId);
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        return new SpeechAudio(
            audioBytes,
            string.IsNullOrWhiteSpace(mediaType) ? "audio/wav" : mediaType);
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
            throw SpeechHttpSupport.TransportFailure(SpeechSynthesisProvider.ElevenLabs);
        }
    }
}

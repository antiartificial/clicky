using System.Net;
using System.Net.Http;
using System.IO;

namespace Clicky.Windows.Speech;

internal static class SpeechHttpSupport
{
    private const int MaximumAudioByteCount = 32 * 1024 * 1024;

    public static SpeechSynthesisException BuildHttpFailure(
        SpeechSynthesisProvider provider,
        HttpStatusCode statusCode,
        string? requestId)
    {
        var failureKind = statusCode switch
        {
            HttpStatusCode.Unauthorized => SpeechSynthesisFailureKind.Authentication,
            HttpStatusCode.Forbidden => SpeechSynthesisFailureKind.Permission,
            HttpStatusCode.TooManyRequests => SpeechSynthesisFailureKind.RateLimited,
            >= HttpStatusCode.InternalServerError => SpeechSynthesisFailureKind.Server,
            _ => SpeechSynthesisFailureKind.RequestRejected,
        };

        return new SpeechSynthesisException(
            provider,
            failureKind,
            $"{GetProviderDisplayName(provider)} rejected speech synthesis with HTTP {(int)statusCode}.",
            statusCode,
            requestId);
    }

    public static SpeechSynthesisException TransportFailure(
        SpeechSynthesisProvider provider,
        string? requestId = null) =>
        new(
            provider,
            SpeechSynthesisFailureKind.Transport,
            $"The {GetProviderDisplayName(provider)} speech request could not be completed.",
            requestId: requestId);

    public static async Task<byte[]> ReadAudioBytesAsync(
        HttpContent content,
        SpeechSynthesisProvider provider,
        string? requestId,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaximumAudioByteCount)
        {
            throw InvalidResponse(provider, requestId);
        }

        await using var responseStream = await content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var audioStream = new MemoryStream();
        var buffer = new byte[81920];

        while (true)
        {
            var bytesRead = await responseStream
                .ReadAsync(buffer, cancellationToken)
                .ConfigureAwait(false);
            if (bytesRead == 0)
            {
                break;
            }

            if (audioStream.Length + bytesRead > MaximumAudioByteCount)
            {
                throw InvalidResponse(provider, requestId);
            }

            await audioStream
                .WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken)
                .ConfigureAwait(false);
        }

        if (audioStream.Length == 0)
        {
            throw InvalidResponse(provider, requestId);
        }

        return audioStream.ToArray();
    }

    public static string? ReadRequestId(HttpResponseMessage response)
    {
        foreach (var headerName in new[]
                 {
                     "x-request-id",
                     "request-id",
                     "openai-request-id",
                     "x-trace-id",
                 })
        {
            if (response.Headers.TryGetValues(headerName, out var values))
            {
                return SpeechSynthesisException.SanitizeIdentifier(
                    values.FirstOrDefault(),
                    128);
            }
        }

        return null;
    }

    private static SpeechSynthesisException InvalidResponse(
        SpeechSynthesisProvider provider,
        string? requestId) =>
        new(
            provider,
            SpeechSynthesisFailureKind.InvalidResponse,
            $"{GetProviderDisplayName(provider)} returned invalid speech audio.",
            requestId: requestId);

    private static string GetProviderDisplayName(SpeechSynthesisProvider provider) =>
        provider switch
        {
            SpeechSynthesisProvider.OpenAI => "OpenAI",
            SpeechSynthesisProvider.ElevenLabs => "ElevenLabs",
            _ => "speech provider",
        };
}

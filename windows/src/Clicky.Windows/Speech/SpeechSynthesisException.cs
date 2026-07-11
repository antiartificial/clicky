using System.Net;

namespace Clicky.Windows.Speech;

public enum SpeechSynthesisProvider
{
    OpenAI,
    ElevenLabs,
}

public enum SpeechSynthesisFailureKind
{
    MissingApiKey,
    InvalidConfiguration,
    InvalidRequest,
    Authentication,
    Permission,
    RateLimited,
    RequestRejected,
    Server,
    Transport,
    InvalidResponse,
}

public sealed class SpeechSynthesisException : Exception
{
    public SpeechSynthesisException(
        SpeechSynthesisProvider provider,
        SpeechSynthesisFailureKind failureKind,
        string message,
        HttpStatusCode? statusCode = null,
        string? requestId = null)
        : base(message)
    {
        Provider = provider;
        FailureKind = failureKind;
        StatusCode = statusCode;
        RequestId = SanitizeIdentifier(requestId, 128);
    }

    public SpeechSynthesisProvider Provider { get; }

    public SpeechSynthesisFailureKind FailureKind { get; }

    public HttpStatusCode? StatusCode { get; }

    public string? RequestId { get; }

    internal static string? SanitizeIdentifier(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        var length = Math.Min(trimmed.Length, maximumLength);
        Span<char> sanitized = stackalloc char[length];
        var written = 0;

        for (var index = 0; index < length; index++)
        {
            var character = trimmed[index];
            if (char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.' or ':')
            {
                sanitized[written++] = character;
            }
        }

        return written == 0 ? null : new string(sanitized[..written]);
    }
}

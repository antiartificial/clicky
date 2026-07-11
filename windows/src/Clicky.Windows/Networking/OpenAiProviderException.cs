using System.Net;

namespace Clicky.Windows.Networking;

public enum OpenAiProviderFailureKind
{
    MissingApiKey,
    InvalidConfiguration,
    Authentication,
    Permission,
    RateLimited,
    RequestRejected,
    Server,
    Transport,
    StreamProtocol,
    Failed,
    Incomplete,
    Refusal,
}

public sealed class OpenAiProviderException : Exception
{
    public OpenAiProviderException(
        OpenAiProviderFailureKind failureKind,
        string message,
        HttpStatusCode? statusCode = null,
        string? requestId = null,
        string? providerCode = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        FailureKind = failureKind;
        StatusCode = statusCode;
        RequestId = SanitizeIdentifier(requestId, 128);
        ProviderCode = SanitizeIdentifier(providerCode, 64);
    }

    public OpenAiProviderFailureKind FailureKind { get; }

    public HttpStatusCode? StatusCode { get; }

    public string? RequestId { get; }

    public string? ProviderCode { get; }

    internal OpenAiProviderException WithRequestId(string? requestId) =>
        RequestId is not null || string.IsNullOrWhiteSpace(requestId)
            ? this
            : new OpenAiProviderException(
                FailureKind,
                Message,
                StatusCode,
                requestId,
                ProviderCode,
                this);

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

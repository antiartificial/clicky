using System.Net;

namespace Clicky.Windows.Networking;

public enum AnthropicProviderErrorKind
{
    Configuration,
    HttpStatus,
    StreamError,
    PrematureEnd,
    Incomplete,
}

public sealed class AnthropicProviderException : Exception
{
    private AnthropicProviderException(
        AnthropicProviderErrorKind errorKind,
        string message,
        HttpStatusCode? statusCode = null,
        string? errorType = null,
        string? requestId = null)
        : base(message)
    {
        ErrorKind = errorKind;
        StatusCode = statusCode;
        ErrorType = errorType;
        RequestId = requestId;
    }

    public AnthropicProviderErrorKind ErrorKind { get; }

    public HttpStatusCode? StatusCode { get; }

    public string? ErrorType { get; }

    public string? RequestId { get; }

    internal static AnthropicProviderException MissingApiKey()
    {
        return new AnthropicProviderException(
            AnthropicProviderErrorKind.Configuration,
            "Save an Anthropic API key in Clicky settings before asking a question.");
    }

    internal static AnthropicProviderException MissingModel()
    {
        return new AnthropicProviderException(
            AnthropicProviderErrorKind.Configuration,
            "Set an Anthropic model in Clicky settings before asking a question.");
    }

    internal static AnthropicProviderException HttpFailure(
        HttpStatusCode statusCode,
        string? errorType,
        string? requestId)
    {
        var safeType = SanitizeIdentifier(errorType, maximumLength: 64);
        var safeRequestId = SanitizeIdentifier(requestId, maximumLength: 128);
        var category = safeType is null ? string.Empty : $" ({safeType})";
        var request = safeRequestId is null ? string.Empty : $" Request ID: {safeRequestId}.";

        return new AnthropicProviderException(
            AnthropicProviderErrorKind.HttpStatus,
            $"Anthropic request failed with HTTP {(int)statusCode}{category}.{request}",
            statusCode,
            safeType,
            safeRequestId);
    }

    internal static AnthropicProviderException StreamFailure(
        string? errorType,
        string? requestId = null)
    {
        var safeType = SanitizeIdentifier(errorType, maximumLength: 64) ?? "stream_error";
        var safeRequestId = SanitizeIdentifier(requestId, maximumLength: 128);
        var request = safeRequestId is null ? string.Empty : $" Request ID: {safeRequestId}.";

        return new AnthropicProviderException(
            AnthropicProviderErrorKind.StreamError,
            $"Anthropic stream failed ({safeType}).{request}",
            errorType: safeType,
            requestId: safeRequestId);
    }

    internal static AnthropicProviderException PrematureEnd()
    {
        return new AnthropicProviderException(
            AnthropicProviderErrorKind.PrematureEnd,
            "Anthropic stream ended before its message_stop event.");
    }

    internal static AnthropicProviderException Incomplete()
    {
        return new AnthropicProviderException(
            AnthropicProviderErrorKind.Incomplete,
            "Anthropic reached the output limit before completing the response.");
    }

    private static string? SanitizeIdentifier(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var sanitized = new string(value
            .Take(maximumLength)
            .Where(character =>
                char.IsAsciiLetterOrDigit(character) ||
                character is '_' or '-' or '.' or ':')
            .ToArray());

        return sanitized.Length == 0 ? null : sanitized;
    }
}

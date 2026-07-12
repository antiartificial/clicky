using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Clicky.Windows.Networking;

public static class OpenAiResponsesSseReader
{
    private const int MaximumEventCharacters = 1_048_576;

    public static IAsyncEnumerable<string> ReadTextDeltasAsync(
        Stream stream,
        CancellationToken cancellationToken = default) =>
        ReadTextDeltasAsync(stream, requestId: null, cancellationToken);

    internal static async IAsyncEnumerable<string> ReadTextDeltasAsync(
        Stream stream,
        string? requestId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 1024,
            leaveOpen: true);

        var dataLines = new List<string>();
        string? eventName = null;
        var eventCharacters = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

            if (line is null)
            {
                if (dataLines.Count > 0 || eventName is not null)
                {
                    var finalEvent = ParseEvent(eventName, dataLines, requestId);
                    if (finalEvent.TextDelta is not null)
                    {
                        yield return finalEvent.TextDelta;
                    }

                    if (finalEvent.IsCompleted)
                    {
                        yield break;
                    }
                }

                throw ProtocolFailure(
                    "The OpenAI response stream ended before response.completed.",
                    requestId);
            }

            if (line.Length > MaximumEventCharacters)
            {
                throw ProtocolFailure("The OpenAI response stream contained an oversized event.", requestId);
            }

            if (line.Length == 0)
            {
                var parsedEvent = ParseEvent(eventName, dataLines, requestId);
                if (parsedEvent.TextDelta is not null)
                {
                    yield return parsedEvent.TextDelta;
                }

                if (parsedEvent.IsCompleted)
                {
                    yield break;
                }

                dataLines.Clear();
                eventName = null;
                eventCharacters = 0;
                continue;
            }

            if (line[0] == ':')
            {
                continue;
            }

            var colonIndex = line.IndexOf(':');
            var fieldName = colonIndex >= 0 ? line[..colonIndex] : line;
            var fieldValue = colonIndex >= 0 ? line[(colonIndex + 1)..] : string.Empty;
            if (fieldValue.StartsWith(' '))
            {
                fieldValue = fieldValue[1..];
            }

            if (fieldName == "data")
            {
                eventCharacters += fieldValue.Length;
                if (eventCharacters > MaximumEventCharacters)
                {
                    throw ProtocolFailure("The OpenAI response stream contained an oversized event.", requestId);
                }

                dataLines.Add(fieldValue);
            }
            else if (fieldName == "event")
            {
                eventName = fieldValue;
            }
        }
    }

    private static ParsedEvent ParseEvent(
        string? eventName,
        IReadOnlyList<string> dataLines,
        string? requestId)
    {
        if (dataLines.Count == 0)
        {
            return ParsedEvent.Ignore;
        }

        var eventData = string.Join('\n', dataLines);
        if (eventData == "[DONE]")
        {
            throw ProtocolFailure(
                "The OpenAI response stream ended before response.completed.",
                requestId);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(eventData);
        }
        catch (JsonException)
        {
            throw new OpenAiProviderException(
                OpenAiProviderFailureKind.StreamProtocol,
                "OpenAI returned malformed streaming data.",
                requestId: requestId);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw ProtocolFailure("OpenAI returned an invalid streaming event.", requestId);
            }

            var eventType = GetString(root, "type") ?? eventName;
            if (string.IsNullOrWhiteSpace(eventType))
            {
                throw ProtocolFailure("OpenAI returned a streaming event without a type.", requestId);
            }

            switch (eventType)
            {
                case "response.output_text.delta":
                    var delta = GetString(root, "delta");
                    if (delta is null)
                    {
                        throw ProtocolFailure("OpenAI returned a text event without a delta.", requestId);
                    }

                    return new ParsedEvent(delta, IsCompleted: false);

                case "response.completed":
                    var status = GetNestedString(root, "response", "status");
                    if (status is not null && !string.Equals(status, "completed", StringComparison.Ordinal))
                    {
                        throw ProviderFailure(
                            OpenAiProviderFailureKind.Failed,
                            "OpenAI reported that the response did not complete.",
                            requestId,
                            status);
                    }

                    return ParsedEvent.Completed;

                case "response.failed":
                    throw ProviderFailure(
                        OpenAiProviderFailureKind.Failed,
                        "OpenAI failed while generating the response.",
                        requestId,
                        GetNestedString(root, "response", "error", "code"));

                case "response.incomplete":
                    throw ProviderFailure(
                        OpenAiProviderFailureKind.Incomplete,
                        "OpenAI returned an incomplete response.",
                        requestId,
                        GetNestedString(root, "response", "incomplete_details", "reason"));

                case "response.refusal.delta":
                case "response.refusal.done":
                    throw ProviderFailure(
                        OpenAiProviderFailureKind.Refusal,
                        "OpenAI refused to answer the request.",
                        requestId,
                        providerCode: "refusal");

                case "error":
                    throw ProviderFailure(
                        OpenAiProviderFailureKind.Failed,
                        "OpenAI reported a streaming error.",
                        requestId,
                        GetString(root, "code") ?? GetNestedString(root, "error", "code"));

                default:
                    return ParsedEvent.Ignore;
            }
        }
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string? GetNestedString(JsonElement element, params string[] propertyPath)
    {
        foreach (var propertyName in propertyPath)
        {
            if (element.ValueKind != JsonValueKind.Object ||
                !element.TryGetProperty(propertyName, out element))
            {
                return null;
            }
        }

        return element.ValueKind == JsonValueKind.String ? element.GetString() : null;
    }

    private static OpenAiProviderException ProtocolFailure(string message, string? requestId) =>
        new(
            OpenAiProviderFailureKind.StreamProtocol,
            message,
            requestId: requestId);

    private static OpenAiProviderException ProviderFailure(
        OpenAiProviderFailureKind failureKind,
        string message,
        string? requestId,
        string? providerCode) =>
        new(
            failureKind,
            message,
            requestId: requestId,
            providerCode: providerCode);

    private readonly record struct ParsedEvent(string? TextDelta, bool IsCompleted)
    {
        public static ParsedEvent Ignore { get; } = new(null, IsCompleted: false);

        public static ParsedEvent Completed { get; } = new(null, IsCompleted: true);
    }
}

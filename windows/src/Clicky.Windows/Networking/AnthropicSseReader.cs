using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.IO;

namespace Clicky.Windows.Networking;

public static class AnthropicSseReader
{
    private const int MaximumEventCharacters = 1_048_576;

    public static async IAsyncEnumerable<string> ReadTextDeltasAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var delta in ReadTextDeltasAsync(
            stream,
            requireMessageStop: false,
            cancellationToken).ConfigureAwait(false))
        {
            yield return delta;
        }
    }

    public static async IAsyncEnumerable<string> ReadTextDeltasAsync(
        Stream stream,
        bool requireMessageStop,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var delta in ReadTextDeltasAsync(
            stream,
            requireMessageStop,
            requestId: null,
            cancellationToken).ConfigureAwait(false))
        {
            yield return delta;
        }
    }

    public static async IAsyncEnumerable<string> ReadTextDeltasAsync(
        Stream stream,
        bool requireMessageStop,
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

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

            if (line is null)
            {
                var finalEvent = ParseEvent(eventName, dataLines);
                var shouldStop = false;
                foreach (var textDelta in ProcessEvent(
                    finalEvent,
                    requireMessageStop,
                    requestId,
                    out shouldStop))
                {
                    yield return textDelta;
                }

                if (shouldStop || !requireMessageStop)
                {
                    yield break;
                }

                throw AnthropicProviderException.PrematureEnd();
            }

            if (line.Length == 0)
            {
                var parsedEvent = ParseEvent(eventName, dataLines);
                var shouldStop = false;
                foreach (var textDelta in ProcessEvent(
                    parsedEvent,
                    requireMessageStop,
                    requestId,
                    out shouldStop))
                {
                    yield return textDelta;
                }

                if (shouldStop)
                {
                    yield break;
                }

                dataLines.Clear();
                eventName = null;
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
                var eventCharacters = dataLines.Sum(value => value.Length) + fieldValue.Length;
                if (eventCharacters > MaximumEventCharacters)
                {
                    throw AnthropicProviderException.StreamFailure("event_too_large", requestId);
                }

                dataLines.Add(fieldValue);
            }
            else if (fieldName == "event")
            {
                eventName = fieldValue;
            }
        }
    }

    private static ParsedSseEvent ParseEvent(string? eventName, IReadOnlyList<string> dataLines)
    {
        if (string.Equals(eventName, "message_stop", StringComparison.Ordinal))
        {
            return ParsedSseEvent.MessageStop;
        }

        if (dataLines.Count == 0)
        {
            return ParsedSseEvent.Ignore;
        }

        var eventData = string.Join('\n', dataLines);
        if (eventData == "[DONE]")
        {
            return ParsedSseEvent.LegacyDone;
        }

        try
        {
            using var document = JsonDocument.Parse(eventData);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return ParsedSseEvent.Ignore;
            }

            if (!root.TryGetProperty("type", out var eventType) ||
                eventType.ValueKind != JsonValueKind.String)
            {
                return ParsedSseEvent.Ignore;
            }

            if (eventType.ValueEquals("message_stop"))
            {
                return ParsedSseEvent.MessageStop;
            }

            if (eventType.ValueEquals("error") ||
                string.Equals(eventName, "error", StringComparison.Ordinal))
            {
                string? errorType = null;
                if (root.TryGetProperty("error", out var error) &&
                    error.ValueKind == JsonValueKind.Object &&
                    error.TryGetProperty("type", out var type) &&
                    type.ValueKind == JsonValueKind.String)
                {
                    errorType = type.GetString();
                }

                return ParsedSseEvent.Error(errorType);
            }

            if (eventType.ValueEquals("message_delta") &&
                root.TryGetProperty("delta", out var messageDelta) &&
                messageDelta.ValueKind == JsonValueKind.Object &&
                messageDelta.TryGetProperty("stop_reason", out var stopReason) &&
                stopReason.ValueKind == JsonValueKind.String &&
                stopReason.ValueEquals("max_tokens"))
            {
                return ParsedSseEvent.Incomplete;
            }

            if (!eventType.ValueEquals("content_block_delta") ||
                !root.TryGetProperty("delta", out var delta) ||
                delta.ValueKind != JsonValueKind.Object ||
                !delta.TryGetProperty("type", out var deltaType) ||
                !deltaType.ValueEquals("text_delta") ||
                !delta.TryGetProperty("text", out var text) ||
                text.ValueKind != JsonValueKind.String)
            {
                return ParsedSseEvent.Ignore;
            }

            return ParsedSseEvent.Delta(text.GetString());
        }
        catch (JsonException)
        {
            return ParsedSseEvent.Ignore;
        }
    }

    private static IEnumerable<string> ProcessEvent(
        ParsedSseEvent parsedEvent,
        bool requireMessageStop,
        string? requestId,
        out bool shouldStop)
    {
        shouldStop = parsedEvent.Kind == ParsedSseEventKind.MessageStop ||
            (!requireMessageStop && parsedEvent.Kind == ParsedSseEventKind.LegacyDone);

        if (parsedEvent.Kind == ParsedSseEventKind.Error)
        {
            throw AnthropicProviderException.StreamFailure(parsedEvent.ErrorType, requestId);
        }


        if (parsedEvent.Kind == ParsedSseEventKind.Incomplete)
        {
            throw AnthropicProviderException.Incomplete();
        }

        return parsedEvent.TextDelta is null ? [] : [parsedEvent.TextDelta];
    }

    private enum ParsedSseEventKind
    {
        Ignore,
        TextDelta,
        MessageStop,
        LegacyDone,
        Error,
        Incomplete,
    }

    private readonly record struct ParsedSseEvent(
        ParsedSseEventKind Kind,
        string? TextDelta = null,
        string? ErrorType = null)
    {
        public static ParsedSseEvent Ignore { get; } = new(ParsedSseEventKind.Ignore);

        public static ParsedSseEvent MessageStop { get; } = new(ParsedSseEventKind.MessageStop);

        public static ParsedSseEvent LegacyDone { get; } = new(ParsedSseEventKind.LegacyDone);

        public static ParsedSseEvent Incomplete { get; } = new(ParsedSseEventKind.Incomplete);

        public static ParsedSseEvent Delta(string? text) =>
            new(ParsedSseEventKind.TextDelta, TextDelta: text);

        public static ParsedSseEvent Error(string? errorType) =>
            new(ParsedSseEventKind.Error, ErrorType: errorType);
    }
}

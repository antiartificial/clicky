using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Clicky.Windows.Networking;

public static class GeminiSseReader
{
    private const int MaximumEventCharacters = 1_048_576;

    private static readonly HashSet<string> SafetyFinishReasons = new(StringComparer.Ordinal)
    {
        "SAFETY",
        "RECITATION",
        "LANGUAGE",
        "BLOCKLIST",
        "PROHIBITED_CONTENT",
        "SPII",
        "IMAGE_SAFETY",
    };

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
        var eventCharacters = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

            if (line is null)
            {
                if (dataLines.Count > 0)
                {
                    var finalEvent = ParseEvent(dataLines, requestId);
                    foreach (var textDelta in finalEvent.TextDeltas)
                    {
                        yield return textDelta;
                    }

                    if (finalEvent.IsCompleted)
                    {
                        yield break;
                    }
                }

                throw ProtocolFailure(
                    "The Gemini response stream ended before a STOP finish reason.",
                    requestId);
            }

            if (line.Length > MaximumEventCharacters)
            {
                throw ProtocolFailure("The Gemini response stream contained an oversized event.", requestId);
            }

            if (line.Length == 0)
            {
                if (dataLines.Count > 0)
                {
                    var parsedEvent = ParseEvent(dataLines, requestId);
                    foreach (var textDelta in parsedEvent.TextDeltas)
                    {
                        yield return textDelta;
                    }

                    if (parsedEvent.IsCompleted)
                    {
                        yield break;
                    }
                }

                dataLines.Clear();
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

            if (fieldName != "data")
            {
                continue;
            }

            eventCharacters += fieldValue.Length;
            if (eventCharacters > MaximumEventCharacters)
            {
                throw ProtocolFailure("The Gemini response stream contained an oversized event.", requestId);
            }

            dataLines.Add(fieldValue);
        }
    }

    private static ParsedEvent ParseEvent(IReadOnlyList<string> dataLines, string? requestId)
    {
        var eventData = string.Join('\n', dataLines);
        if (eventData == "[DONE]")
        {
            throw ProtocolFailure(
                "The Gemini response stream ended before a STOP finish reason.",
                requestId);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(eventData);
        }
        catch (JsonException)
        {
            throw ProtocolFailure("Gemini returned malformed streaming data.", requestId);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw ProtocolFailure("Gemini returned an invalid streaming event.", requestId);
            }

            if (root.TryGetProperty("error", out var error))
            {
                var providerCode = GetString(error, "status") ?? GetString(error, "code");
                throw ProviderFailure(
                    GeminiProviderFailureKind.Failed,
                    "Gemini reported a streaming error.",
                    requestId,
                    providerCode);
            }

            if (root.TryGetProperty("promptFeedback", out var promptFeedback) &&
                promptFeedback.ValueKind == JsonValueKind.Object)
            {
                var blockReason = GetString(promptFeedback, "blockReason");
                if (!string.IsNullOrWhiteSpace(blockReason) &&
                    !string.Equals(blockReason, "BLOCK_REASON_UNSPECIFIED", StringComparison.Ordinal))
                {
                    throw ProviderFailure(
                        GeminiProviderFailureKind.Safety,
                        "Gemini blocked the prompt for safety or policy reasons.",
                        requestId,
                        blockReason);
                }
            }

            if (!root.TryGetProperty("candidates", out var candidates))
            {
                if (root.TryGetProperty("usageMetadata", out _))
                {
                    return ParsedEvent.Ignore;
                }

                throw ProtocolFailure("Gemini returned a streaming event without candidates.", requestId);
            }

            if (candidates.ValueKind != JsonValueKind.Array || candidates.GetArrayLength() == 0)
            {
                throw ProtocolFailure("Gemini returned an invalid candidates collection.", requestId);
            }

            var candidate = FindPrimaryCandidate(candidates);
            var finishReason = GetString(candidate, "finishReason");
            var isCompleted = string.Equals(finishReason, "STOP", StringComparison.Ordinal);

            if (string.Equals(finishReason, "MAX_TOKENS", StringComparison.Ordinal))
            {
                throw ProviderFailure(
                    GeminiProviderFailureKind.Incomplete,
                    "Gemini reached the output token limit before completing the response.",
                    requestId,
                    finishReason);
            }

            if (finishReason is not null && SafetyFinishReasons.Contains(finishReason))
            {
                throw ProviderFailure(
                    GeminiProviderFailureKind.Safety,
                    "Gemini stopped the response for safety or policy reasons.",
                    requestId,
                    finishReason);
            }

            if (finishReason is not null &&
                !isCompleted &&
                !string.Equals(finishReason, "FINISH_REASON_UNSPECIFIED", StringComparison.Ordinal))
            {
                throw ProviderFailure(
                    GeminiProviderFailureKind.Failed,
                    "Gemini stopped before completing the response.",
                    requestId,
                    finishReason);
            }

            var textDeltas = ReadTextDeltas(candidate, requestId);
            return new ParsedEvent(textDeltas, isCompleted);
        }
    }

    private static JsonElement FindPrimaryCandidate(JsonElement candidates)
    {
        var firstCandidate = candidates[0];
        foreach (var candidate in candidates.EnumerateArray())
        {
            if (candidate.ValueKind == JsonValueKind.Object &&
                candidate.TryGetProperty("index", out var index) &&
                index.ValueKind == JsonValueKind.Number &&
                index.TryGetInt32(out var candidateIndex) &&
                candidateIndex == 0)
            {
                return candidate;
            }
        }

        return firstCandidate;
    }

    private static IReadOnlyList<string> ReadTextDeltas(JsonElement candidate, string? requestId)
    {
        if (candidate.ValueKind != JsonValueKind.Object)
        {
            throw ProtocolFailure("Gemini returned an invalid candidate.", requestId);
        }

        if (!candidate.TryGetProperty("content", out var content))
        {
            return [];
        }

        if (content.ValueKind != JsonValueKind.Object ||
            !content.TryGetProperty("parts", out var parts) ||
            parts.ValueKind != JsonValueKind.Array)
        {
            throw ProtocolFailure("Gemini returned invalid candidate content.", requestId);
        }

        var textDeltas = new List<string>();
        foreach (var part in parts.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object)
            {
                throw ProtocolFailure("Gemini returned an invalid content part.", requestId);
            }

            if (part.TryGetProperty("thought", out var thought) &&
                thought.ValueKind == JsonValueKind.True)
            {
                continue;
            }

            if (!part.TryGetProperty("text", out var text))
            {
                continue;
            }

            if (text.ValueKind != JsonValueKind.String)
            {
                throw ProtocolFailure("Gemini returned a non-text content delta.", requestId);
            }

            var textDelta = text.GetString();
            if (!string.IsNullOrEmpty(textDelta))
            {
                textDeltas.Add(textDelta);
            }
        }

        return textDeltas;
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            _ => null,
        };
    }

    private static GeminiProviderException ProtocolFailure(string message, string? requestId) =>
        new(
            GeminiProviderFailureKind.StreamProtocol,
            message,
            requestId: requestId);

    private static GeminiProviderException ProviderFailure(
        GeminiProviderFailureKind failureKind,
        string message,
        string? requestId,
        string? providerCode) =>
        new(
            failureKind,
            message,
            requestId: requestId,
            providerCode: providerCode);

    private readonly record struct ParsedEvent(IReadOnlyList<string> TextDeltas, bool IsCompleted)
    {
        public static ParsedEvent Ignore { get; } = new([], IsCompleted: false);
    }
}

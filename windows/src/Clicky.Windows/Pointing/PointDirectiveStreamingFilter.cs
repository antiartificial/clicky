using System.Text;

namespace Clicky.Windows.Pointing;

public sealed class PointDirectiveStreamingFilter
{
    private const string DirectivePrefix = "[POINT:";

    private readonly StringBuilder pendingText = new();
    private bool directiveStarted;

    public string Append(string textChunk)
    {
        ArgumentNullException.ThrowIfNull(textChunk);
        if (textChunk.Length == 0 || directiveStarted)
        {
            return string.Empty;
        }

        pendingText.Append(textChunk);
        var pendingValue = pendingText.ToString();
        var directiveIndex = pendingValue.IndexOf(
            DirectivePrefix,
            StringComparison.OrdinalIgnoreCase);
        if (directiveIndex >= 0)
        {
            directiveStarted = true;
            pendingText.Clear();
            return pendingValue[..directiveIndex];
        }

        var retainedCharacterCount = FindPossibleDirectivePrefixLength(pendingValue);
        var emittedCharacterCount = pendingValue.Length - retainedCharacterCount;
        if (emittedCharacterCount == 0)
        {
            return string.Empty;
        }

        var emittedText = pendingValue[..emittedCharacterCount];
        pendingText.Remove(0, emittedCharacterCount);
        return emittedText;
    }

    public string Complete()
    {
        if (directiveStarted || pendingText.Length == 0)
        {
            return string.Empty;
        }

        var finalText = pendingText.ToString();
        pendingText.Clear();
        return finalText;
    }

    private static int FindPossibleDirectivePrefixLength(string pendingValue)
    {
        var maximumLength = Math.Min(pendingValue.Length, DirectivePrefix.Length - 1);
        for (var candidateLength = maximumLength; candidateLength > 0; candidateLength--)
        {
            if (pendingValue.EndsWith(
                    DirectivePrefix[..candidateLength],
                    StringComparison.OrdinalIgnoreCase))
            {
                return candidateLength;
            }
        }

        return 0;
    }
}

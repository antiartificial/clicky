namespace Clicky.Windows.Speech;

public sealed class SpeechAudio
{
    public SpeechAudio(ReadOnlyMemory<byte> audioBytes, string mediaType)
    {
        if (audioBytes.IsEmpty)
        {
            throw new ArgumentException("Speech audio cannot be empty.", nameof(audioBytes));
        }

        if (string.IsNullOrWhiteSpace(mediaType))
        {
            throw new ArgumentException("A speech audio media type is required.", nameof(mediaType));
        }

        AudioBytes = audioBytes.ToArray();
        MediaType = mediaType.Trim();
    }

    public ReadOnlyMemory<byte> AudioBytes { get; }

    public string MediaType { get; }
}

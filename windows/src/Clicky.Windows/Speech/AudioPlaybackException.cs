namespace Clicky.Windows.Speech;

public enum AudioPlaybackFailureKind
{
    UnsupportedMediaType,
    InvalidAudio,
    PlaybackFailed,
}

public sealed class AudioPlaybackException : Exception
{
    public AudioPlaybackException(
        AudioPlaybackFailureKind failureKind,
        string message)
        : base(message)
    {
        FailureKind = failureKind;
    }

    public AudioPlaybackFailureKind FailureKind { get; }
}

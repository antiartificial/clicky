namespace Clicky.Windows.Voice;

public enum DictationUnavailableReason
{
    SpeechRecognizer,
    Microphone,
}

public sealed class DictationUnavailableException : Exception
{
    public DictationUnavailableException(
        DictationUnavailableReason reason,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Reason = reason;
    }

    public DictationUnavailableReason Reason { get; }
}

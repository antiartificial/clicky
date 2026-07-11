namespace Clicky.Windows.Speech;

public interface ITextToSpeechClient
{
    Task<SpeechAudio> SynthesizeAsync(
        string text,
        CancellationToken cancellationToken = default);
}

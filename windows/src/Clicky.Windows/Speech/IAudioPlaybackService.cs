namespace Clicky.Windows.Speech;

public interface IAudioPlaybackService
{
    Task PlayAsync(
        SpeechAudio speechAudio,
        CancellationToken cancellationToken = default);

    void Stop();
}

namespace Clicky.Windows.Voice;

public interface IDictationTranscriber : IDisposable
{
    bool IsTranscribing { get; }

    Task StartAsync(CancellationToken cancellationToken = default);

    Task<string> StopAsync(CancellationToken cancellationToken = default);

    Task CancelAsync(CancellationToken cancellationToken = default);
}

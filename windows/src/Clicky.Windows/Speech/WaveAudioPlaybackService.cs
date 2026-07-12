using System.IO;
using System.Media;

namespace Clicky.Windows.Speech;

public sealed class WaveAudioPlaybackService : IAudioPlaybackService, IDisposable
{
    private readonly SemaphoreSlim playbackGate = new(1, 1);
    private readonly object stateGate = new();
    private readonly IWavePlaybackSessionFactory playbackSessionFactory;

    private IWavePlaybackSession? activePlaybackSession;
    private volatile bool disposed;

    public WaveAudioPlaybackService()
        : this(new SoundPlayerWavePlaybackSessionFactory())
    {
    }

    internal WaveAudioPlaybackService(IWavePlaybackSessionFactory playbackSessionFactory)
    {
        ArgumentNullException.ThrowIfNull(playbackSessionFactory);
        this.playbackSessionFactory = playbackSessionFactory;
    }

    public async Task PlayAsync(
        SpeechAudio speechAudio,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(speechAudio);
        ValidateWaveAudio(speechAudio);
        ThrowIfDisposed();

        await playbackGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();

            using var playbackSession = playbackSessionFactory.Create(speechAudio.AudioBytes);
            lock (stateGate)
            {
                ThrowIfDisposed();
                activePlaybackSession = playbackSession;
            }

            using var cancellationRegistration = cancellationToken.Register(
                static state => ((IWavePlaybackSession)state!).Stop(),
                playbackSession);

            try
            {
                await playbackSession.PlayAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or IOException or TimeoutException)
            {
                throw new AudioPlaybackException(
                    AudioPlaybackFailureKind.PlaybackFailed,
                    "WAV audio playback failed.");
            }
            finally
            {
                lock (stateGate)
                {
                    if (ReferenceEquals(activePlaybackSession, playbackSession))
                    {
                        activePlaybackSession = null;
                    }
                }
            }
        }
        finally
        {
            playbackGate.Release();
        }
    }

    public void Stop()
    {
        lock (stateGate)
        {
            activePlaybackSession?.Stop();
        }
    }

    public void Dispose()
    {
        lock (stateGate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            activePlaybackSession?.Stop();
        }
    }

    private static void ValidateWaveAudio(SpeechAudio speechAudio)
    {
        if (!speechAudio.MediaType.Equals("audio/wav", StringComparison.OrdinalIgnoreCase) &&
            !speechAudio.MediaType.Equals("audio/wave", StringComparison.OrdinalIgnoreCase) &&
            !speechAudio.MediaType.Equals("audio/x-wav", StringComparison.OrdinalIgnoreCase))
        {
            throw new AudioPlaybackException(
                AudioPlaybackFailureKind.UnsupportedMediaType,
                $"Audio playback does not support media type '{speechAudio.MediaType}'.");
        }

        var audioBytes = speechAudio.AudioBytes.Span;
        if (audioBytes.Length < 12 ||
            !audioBytes[..4].SequenceEqual("RIFF"u8) ||
            !audioBytes.Slice(8, 4).SequenceEqual("WAVE"u8))
        {
            throw new AudioPlaybackException(
                AudioPlaybackFailureKind.InvalidAudio,
                "The speech audio is not a valid WAV container.");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }
}

internal interface IWavePlaybackSessionFactory
{
    IWavePlaybackSession Create(ReadOnlyMemory<byte> waveAudioBytes);
}

internal interface IWavePlaybackSession : IDisposable
{
    Task PlayAsync();

    void Stop();
}

internal sealed class SoundPlayerWavePlaybackSessionFactory : IWavePlaybackSessionFactory
{
    public IWavePlaybackSession Create(ReadOnlyMemory<byte> waveAudioBytes) =>
        new SoundPlayerWavePlaybackSession(waveAudioBytes);
}

internal sealed class SoundPlayerWavePlaybackSession : IWavePlaybackSession
{
    private readonly MemoryStream audioStream;
    private readonly SoundPlayer soundPlayer;

    public SoundPlayerWavePlaybackSession(ReadOnlyMemory<byte> waveAudioBytes)
    {
        audioStream = new MemoryStream(waveAudioBytes.ToArray(), writable: false);
        soundPlayer = new SoundPlayer(audioStream);
    }

    public Task PlayAsync() => Task.Run(soundPlayer.PlaySync);

    public void Stop() => soundPlayer.Stop();

    public void Dispose()
    {
        soundPlayer.Stop();
        soundPlayer.Dispose();
        audioStream.Dispose();
    }
}

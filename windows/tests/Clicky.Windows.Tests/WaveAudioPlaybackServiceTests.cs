using Clicky.Windows.Speech;

namespace Clicky.Windows.Tests;

[TestClass]
public sealed class WaveAudioPlaybackServiceTests
{
    private static readonly SpeechAudio WaveAudio = new(
        new byte[] { 0x52, 0x49, 0x46, 0x46, 0x04, 0x00, 0x00, 0x00, 0x57, 0x41, 0x56, 0x45 },
        "audio/wav");

    [TestMethod]
    public async Task PlayAsync_SerializesConcurrentPlaybackAndCleansUpSessions()
    {
        var firstSession = new FakeWavePlaybackSession();
        var secondSession = new FakeWavePlaybackSession();
        var sessionFactory = new FakeWavePlaybackSessionFactory(firstSession, secondSession);
        using var playbackService = new WaveAudioPlaybackService(sessionFactory);

        var firstPlayback = playbackService.PlayAsync(WaveAudio);
        await firstSession.Started;
        var secondPlayback = playbackService.PlayAsync(WaveAudio);

        await Task.Delay(30);
        Assert.AreEqual(1, sessionFactory.CreateCalls);
        Assert.IsFalse(secondSession.Started.IsCompleted);

        firstSession.Complete();
        await firstPlayback;
        await secondSession.Started;
        secondSession.Complete();
        await secondPlayback;

        Assert.AreEqual(2, sessionFactory.CreateCalls);
        Assert.AreEqual(1, firstSession.DisposeCalls);
        Assert.AreEqual(1, secondSession.DisposeCalls);
    }

    [TestMethod]
    public async Task PlayAsync_CancellationStopsPlaybackAndPropagatesCancellation()
    {
        var session = new FakeWavePlaybackSession();
        using var playbackService = new WaveAudioPlaybackService(
            new FakeWavePlaybackSessionFactory(session));
        using var cancellation = new CancellationTokenSource();

        var playback = playbackService.PlayAsync(WaveAudio, cancellation.Token);
        await session.Started;
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => playback);
        Assert.AreEqual(1, session.StopCalls);
        Assert.AreEqual(1, session.DisposeCalls);
    }

    [TestMethod]
    public async Task Stop_StopsCurrentPlaybackWithoutStartingAnotherSession()
    {
        var session = new FakeWavePlaybackSession();
        using var playbackService = new WaveAudioPlaybackService(
            new FakeWavePlaybackSessionFactory(session));

        var playback = playbackService.PlayAsync(WaveAudio);
        await session.Started;
        playbackService.Stop();
        await playback;

        Assert.AreEqual(1, session.StopCalls);
        Assert.AreEqual(1, session.DisposeCalls);
    }

    [TestMethod]
    public async Task PlayAsync_RejectsNonWaveMediaBeforeCreatingSession()
    {
        var sessionFactory = new FakeWavePlaybackSessionFactory(new FakeWavePlaybackSession());
        using var playbackService = new WaveAudioPlaybackService(sessionFactory);
        var mp3Audio = new SpeechAudio(new byte[] { 0x01, 0x02, 0x03 }, "audio/mpeg");

        var exception = await Assert.ThrowsExactlyAsync<AudioPlaybackException>(
            () => playbackService.PlayAsync(mp3Audio));

        Assert.AreEqual(AudioPlaybackFailureKind.UnsupportedMediaType, exception.FailureKind);
        Assert.AreEqual(0, sessionFactory.CreateCalls);
    }

    [TestMethod]
    public async Task Dispose_StopsCurrentPlaybackAndRejectsFuturePlayback()
    {
        var session = new FakeWavePlaybackSession();
        var playbackService = new WaveAudioPlaybackService(
            new FakeWavePlaybackSessionFactory(session));

        var playback = playbackService.PlayAsync(WaveAudio);
        await session.Started;
        playbackService.Dispose();
        await playback;

        Assert.AreEqual(1, session.StopCalls);
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
            () => playbackService.PlayAsync(WaveAudio));
    }

    private sealed class FakeWavePlaybackSessionFactory(
        params FakeWavePlaybackSession[] sessions) : IWavePlaybackSessionFactory
    {
        private readonly Queue<FakeWavePlaybackSession> remainingSessions = new(sessions);

        public int CreateCalls { get; private set; }

        public IWavePlaybackSession Create(ReadOnlyMemory<byte> waveAudioBytes)
        {
            CreateCalls++;
            return remainingSessions.Dequeue();
        }
    }

    private sealed class FakeWavePlaybackSession : IWavePlaybackSession
    {
        private readonly TaskCompletionSource started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource completed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => started.Task;

        public int StopCalls { get; private set; }

        public int DisposeCalls { get; private set; }

        public async Task PlayAsync()
        {
            started.TrySetResult();
            await completed.Task;
        }

        public void Stop()
        {
            StopCalls++;
            completed.TrySetResult();
        }

        public void Complete() => completed.TrySetResult();

        public void Dispose() => DisposeCalls++;
    }
}

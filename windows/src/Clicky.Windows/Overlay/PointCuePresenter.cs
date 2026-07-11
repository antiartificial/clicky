using System.Windows.Threading;
using Clicky.Windows.Pointing;

namespace Clicky.Windows.Overlay;

public sealed class PointCuePresenter : IPointCuePresenter
{
    private readonly Dispatcher dispatcher;
    private readonly IPointCueMonitorProvider monitorProvider;
    private readonly PointCuePresenterOptions options;
    private PointCueWindow? window;
    private CancellationTokenSource? autoHideCancellation;
    private long displayGeneration;
    private int disposeState;

    public PointCuePresenter(PointCuePresenterOptions? options = null)
        : this(
            System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher,
            options,
            new PointCueMonitorProvider())
    {
    }

    public PointCuePresenter(Dispatcher dispatcher, PointCuePresenterOptions? options = null)
        : this(dispatcher, options, new PointCueMonitorProvider())
    {
    }

    internal PointCuePresenter(
        Dispatcher dispatcher,
        PointCuePresenterOptions? options,
        IPointCueMonitorProvider monitorProvider)
    {
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        this.monitorProvider = monitorProvider ?? throw new ArgumentNullException(nameof(monitorProvider));
        this.options = options ?? new PointCuePresenterOptions();

        if (this.options.AutoHideAfter is { } autoHideAfter && autoHideAfter <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Auto-hide duration must be greater than zero, or null to disable auto-hide.");
        }
    }

    public Task ShowAsync(
        DesktopPoint point,
        string? label = null,
        CancellationToken cancellationToken = default)
    {
        return DisplayAsync(point, label, cancellationToken);
    }

    public Task UpdateAsync(
        DesktopPoint point,
        string? label = null,
        CancellationToken cancellationToken = default)
    {
        return DisplayAsync(point, label, cancellationToken);
    }

    public Task HideAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return InvokeAsync(HideCore, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposeState, 1) != 0)
        {
            return;
        }

        if (dispatcher.CheckAccess())
        {
            DisposeCore();
            return;
        }

        if (!dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
        {
            await dispatcher.InvokeAsync(DisposeCore, DispatcherPriority.Send).Task.ConfigureAwait(false);
        }
    }

    private Task DisplayAsync(
        DesktopPoint point,
        string? label,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var normalizedLabel = NormalizeLabel(label);
        return InvokeAsync(() => DisplayCore(point, normalizedLabel), cancellationToken);
    }

    private void DisplayCore(DesktopPoint point, string? label)
    {
        ThrowIfDisposed();
        CancelAutoHideCore();

        var monitor = monitorProvider.GetMonitorContaining(point);
        var layout = PointCueLayoutCalculator.Calculate(point, monitor, label is not null);
        window ??= new PointCueWindow();
        window.ShowCue(layout, label, options.ReducedMotion);

        var generation = ++displayGeneration;
        if (options.AutoHideAfter is { } autoHideAfter)
        {
            var cancellation = new CancellationTokenSource();
            autoHideCancellation = cancellation;
            _ = AutoHideAsync(generation, autoHideAfter, cancellation);
        }
    }

    private async Task AutoHideAsync(
        long generation,
        TimeSpan delay,
        CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(delay, cancellation.Token).ConfigureAwait(false);
            await dispatcher.InvokeAsync(
                () => HideIfCurrentCore(generation, cancellation),
                DispatcherPriority.Send).Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (InvalidOperationException) when (
            dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
        }
    }

    private void HideIfCurrentCore(long generation, CancellationTokenSource cancellation)
    {
        if (!ReferenceEquals(autoHideCancellation, cancellation) || displayGeneration != generation)
        {
            return;
        }

        autoHideCancellation = null;
        cancellation.Dispose();
        window?.HideCue();
    }

    private void HideCore()
    {
        ThrowIfDisposed();
        displayGeneration++;
        CancelAutoHideCore();
        window?.HideCue();
    }

    private void DisposeCore()
    {
        displayGeneration++;
        CancelAutoHideCore();
        window?.Close();
        window = null;
    }

    private void CancelAutoHideCore()
    {
        var cancellation = autoHideCancellation;
        autoHideCancellation = null;
        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
        cancellation.Dispose();
    }

    private Task InvokeAsync(Action action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(
            action,
            DispatcherPriority.Send,
            cancellationToken).Task;
    }

    private static string? NormalizeLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return null;
        }

        var words = label.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 3)
        {
            throw new ArgumentException("Point cue labels may contain at most three words.", nameof(label));
        }

        return string.Join(' ', words);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposeState) != 0, this);
    }
}

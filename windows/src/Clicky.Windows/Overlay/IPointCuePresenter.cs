using Clicky.Windows.Pointing;

namespace Clicky.Windows.Overlay;

public interface IPointCuePresenter : IAsyncDisposable
{
    Task ShowAsync(
        DesktopPoint point,
        string? label = null,
        CancellationToken cancellationToken = default);

    Task UpdateAsync(
        DesktopPoint point,
        string? label = null,
        CancellationToken cancellationToken = default);

    Task HideAsync(CancellationToken cancellationToken = default);
}

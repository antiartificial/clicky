namespace Clicky.Windows.Overlay;

public sealed record PointCuePresenterOptions
{
    public TimeSpan? AutoHideAfter { get; init; } = TimeSpan.FromSeconds(3);

    public bool ReducedMotion { get; init; }
}

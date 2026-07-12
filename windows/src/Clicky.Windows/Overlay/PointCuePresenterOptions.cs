namespace Clicky.Windows.Overlay;

public sealed record PointCuePresenterOptions
{
    public TimeSpan? AutoHideAfter { get; init; } = TimeSpan.FromSeconds(15);

    public bool ReducedMotion { get; init; }

    public Func<bool> MotionEnabled { get; init; } = static () => true;

    internal bool ShouldUseMotion()
    {
        return !ReducedMotion && MotionEnabled();
    }
}

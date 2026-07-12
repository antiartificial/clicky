namespace Clicky.Windows.Overlay;

public sealed record PointCuePresenterOptions
{
    public TimeSpan? AutoHideAfter { get; init; } = TimeSpan.FromSeconds(15);

    public bool ReducedMotion { get; init; }

    public Func<bool> MotionEnabled { get; init; } = static () => true;

    public Func<Clicky.Windows.Pointing.DesktopPoint?> MotionOrigin { get; init; } = static () => null;

    internal bool ShouldUseMotion()
    {
        return !ReducedMotion && MotionEnabled();
    }
}

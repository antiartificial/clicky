namespace Clicky.Windows.Overlay;

internal readonly record struct PointCueMotionPoint(double X, double Y);

internal static class PointCueMotionPathCalculator
{
    internal const double RevealOffsetXDip = -16;
    internal const double RevealOffsetYDip = 10;

    private const double BendRatio = 0.12;
    private const double MaximumBendPixels = 20;
    private const double MinimumDurationMilliseconds = 180;
    private const double MaximumDurationMilliseconds = 320;

    internal static PointCueMotionPoint Calculate(
        PointCueMotionPoint start,
        PointCueMotionPoint end,
        double progress)
    {
        ValidatePoint(start, nameof(start));
        ValidatePoint(end, nameof(end));
        if (!double.IsFinite(progress))
        {
            throw new ArgumentOutOfRangeException(nameof(progress), "Progress must be finite.");
        }

        if (progress <= 0)
        {
            return start;
        }

        if (progress >= 1)
        {
            return end;
        }

        var deltaX = end.X - start.X;
        var deltaY = end.Y - start.Y;
        var distance = Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY));
        if (distance == 0)
        {
            return end;
        }

        var bend = Math.Min(distance * BendRatio, MaximumBendPixels);
        var control = new PointCueMotionPoint(
            start.X + (deltaX / 2) - ((deltaY / distance) * bend),
            start.Y + (deltaY / 2) + ((deltaX / distance) * bend));
        var easedProgress = EaseForLanding(progress);
        var remaining = 1 - easedProgress;

        return new PointCueMotionPoint(
            (remaining * remaining * start.X) +
                (2 * remaining * easedProgress * control.X) +
                (easedProgress * easedProgress * end.X),
            (remaining * remaining * start.Y) +
                (2 * remaining * easedProgress * control.Y) +
                (easedProgress * easedProgress * end.Y));
    }

    internal static double EaseForLanding(double progress)
    {
        if (!double.IsFinite(progress))
        {
            throw new ArgumentOutOfRangeException(nameof(progress), "Progress must be finite.");
        }

        var clamped = Math.Clamp(progress, 0, 1);
        return Math.Sin(clamped * Math.PI / 2);
    }

    internal static PointCueMotionPoint CreateRevealStart(
        PointCueMotionPoint destination,
        double dpiScaleX,
        double dpiScaleY)
    {
        ValidatePoint(destination, nameof(destination));
        ValidateScale(dpiScaleX, nameof(dpiScaleX));
        ValidateScale(dpiScaleY, nameof(dpiScaleY));

        return new PointCueMotionPoint(
            destination.X + (RevealOffsetXDip * dpiScaleX),
            destination.Y + (RevealOffsetYDip * dpiScaleY));
    }

    internal static TimeSpan CalculateDuration(
        PointCueMotionPoint start,
        PointCueMotionPoint end)
    {
        ValidatePoint(start, nameof(start));
        ValidatePoint(end, nameof(end));

        var deltaX = end.X - start.X;
        var deltaY = end.Y - start.Y;
        var distance = Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY));
        var milliseconds = Math.Clamp(
            150 + (distance * 0.12),
            MinimumDurationMilliseconds,
            MaximumDurationMilliseconds);
        return TimeSpan.FromMilliseconds(milliseconds);
    }

    private static void ValidatePoint(PointCueMotionPoint point, string parameterName)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
        {
            throw new ArgumentOutOfRangeException(parameterName, "Motion coordinates must be finite.");
        }
    }

    private static void ValidateScale(double scale, string parameterName)
    {
        if (!double.IsFinite(scale) || scale <= 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "DPI scale must be finite and greater than zero.");
        }
    }
}

namespace Clicky.Windows.Overlay;

internal readonly record struct PointCueMotionPoint(double X, double Y);

internal static class PointCueMotionPathCalculator
{
    internal const double RevealOffsetXDip = -84;
    internal const double RevealOffsetYDip = 54;
    internal const double ArrivalOrbitRadiusXDip = 24;
    internal const double ArrivalOrbitRadiusYDip = 16;

    private const double BendRatio = 0.2;
    private const double MaximumBendPixels = 34;
    private const double MinimumDurationMilliseconds = 220;
    private const double MaximumDurationMilliseconds = 460;

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
        var normalX = -deltaY / distance;
        var normalY = deltaX / distance;
        var firstControl = new PointCueMotionPoint(
            start.X + (deltaX * 0.3) + (normalX * bend),
            start.Y + (deltaY * 0.3) + (normalY * bend));
        var secondControl = new PointCueMotionPoint(
            start.X + (deltaX * 0.7) - (normalX * bend),
            start.Y + (deltaY * 0.7) - (normalY * bend));
        var easedProgress = EaseForLanding(progress);
        var remaining = 1 - easedProgress;

        return new PointCueMotionPoint(
            (remaining * remaining * remaining * start.X) +
                (3 * remaining * remaining * easedProgress * firstControl.X) +
                (3 * remaining * easedProgress * easedProgress * secondControl.X) +
                (easedProgress * easedProgress * easedProgress * end.X),
            (remaining * remaining * remaining * start.Y) +
                (3 * remaining * remaining * easedProgress * firstControl.Y) +
                (3 * remaining * easedProgress * easedProgress * secondControl.Y) +
                (easedProgress * easedProgress * easedProgress * end.Y));
    }

    internal static PointCueMotionPoint CalculateArrivalOrbit(
        PointCueMotionPoint destination,
        double progress,
        double dpiScaleX,
        double dpiScaleY)
    {
        ValidatePoint(destination, nameof(destination));
        ValidateScale(dpiScaleX, nameof(dpiScaleX));
        ValidateScale(dpiScaleY, nameof(dpiScaleY));
        if (!double.IsFinite(progress))
        {
            throw new ArgumentOutOfRangeException(nameof(progress), "Progress must be finite.");
        }

        var clamped = Math.Clamp(progress, 0, 1);
        if (clamped >= 1)
        {
            return destination;
        }

        var radiusEnvelope = 1 - SmoothStep(clamped);
        var angle = Math.PI + (Math.Tau * clamped);
        return new PointCueMotionPoint(
            destination.X +
                (ArrivalOrbitRadiusXDip * dpiScaleX * radiusEnvelope * Math.Cos(angle)),
            destination.Y +
                (ArrivalOrbitRadiusYDip * dpiScaleY * radiusEnvelope * Math.Sin(angle)));
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

    internal static PointCueMotionPoint ResolveStart(
        PointCueMotionPoint destination,
        PointCueMotionPoint? currentPosition,
        PointCueMotionPoint? configuredOrigin,
        bool wasVisible,
        double dpiScaleX,
        double dpiScaleY)
    {
        ValidatePoint(destination, nameof(destination));
        if (wasVisible && currentPosition is { } current)
        {
            ValidatePoint(current, nameof(currentPosition));
            return current;
        }

        if (configuredOrigin is { } origin)
        {
            ValidatePoint(origin, nameof(configuredOrigin));
            return origin;
        }

        return CreateRevealStart(destination, dpiScaleX, dpiScaleY);
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
            120 + (distance * 0.08),
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

    private static double SmoothStep(double progress)
    {
        return progress * progress * (3 - (2 * progress));
    }
}

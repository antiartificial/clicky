using Clicky.Windows.Pointing;

namespace Clicky.Windows.Overlay;

public readonly record struct PointCueMonitorMetrics
{
    public PointCueMonitorMetrics(
        int workAreaLeftPixels,
        int workAreaTopPixels,
        int workAreaWidthPixels,
        int workAreaHeightPixels,
        double effectiveDpiX,
        double effectiveDpiY)
    {
        if (workAreaWidthPixels <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(workAreaWidthPixels),
                "Monitor work-area width must be greater than zero.");
        }

        if (workAreaHeightPixels <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(workAreaHeightPixels),
                "Monitor work-area height must be greater than zero.");
        }

        if (!double.IsFinite(effectiveDpiX) || effectiveDpiX <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(effectiveDpiX),
                "Effective horizontal DPI must be finite and greater than zero.");
        }

        if (!double.IsFinite(effectiveDpiY) || effectiveDpiY <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(effectiveDpiY),
                "Effective vertical DPI must be finite and greater than zero.");
        }

        WorkAreaLeftPixels = workAreaLeftPixels;
        WorkAreaTopPixels = workAreaTopPixels;
        WorkAreaWidthPixels = workAreaWidthPixels;
        WorkAreaHeightPixels = workAreaHeightPixels;
        EffectiveDpiX = effectiveDpiX;
        EffectiveDpiY = effectiveDpiY;
    }

    public int WorkAreaLeftPixels { get; }

    public int WorkAreaTopPixels { get; }

    public int WorkAreaWidthPixels { get; }

    public int WorkAreaHeightPixels { get; }

    public double EffectiveDpiX { get; }

    public double EffectiveDpiY { get; }

    public int WorkAreaRightPixels => checked(WorkAreaLeftPixels + WorkAreaWidthPixels);

    public int WorkAreaBottomPixels => checked(WorkAreaTopPixels + WorkAreaHeightPixels);
}

public enum PointCueLabelPlacement
{
    None,
    Left,
    Right
}

public readonly record struct PointCueLayout(
    double DpiScaleX,
    double DpiScaleY,
    double PointInWorkAreaXDip,
    double PointInWorkAreaYDip,
    double WindowWidthDip,
    double WindowHeightDip,
    double CueCenterXDip,
    double CueCenterYDip,
    int WindowLeftPixels,
    int WindowTopPixels,
    int WindowWidthPixels,
    int WindowHeightPixels,
    PointCueLabelPlacement LabelPlacement);

public static class PointCueLayoutCalculator
{
    public const double DefaultDpi = 96;
    public const double CueRegionDip = 40;
    public const double CueCenterInsetDip = CueRegionDip / 2;
    public const double LabelGapDip = 6;
    public const double LabelMaximumWidthDip = 104;
    public const double WindowHeightDip = 40;

    public static PointCueLayout Calculate(
        DesktopPoint point,
        PointCueMonitorMetrics monitor,
        bool hasLabel)
    {
        if (!double.IsFinite(point.X))
        {
            throw new ArgumentOutOfRangeException(nameof(point), "Point X must be finite.");
        }

        if (!double.IsFinite(point.Y))
        {
            throw new ArgumentOutOfRangeException(nameof(point), "Point Y must be finite.");
        }

        var scaleX = monitor.EffectiveDpiX / DefaultDpi;
        var scaleY = monitor.EffectiveDpiY / DefaultDpi;
        var workAreaWidthDip = monitor.WorkAreaWidthPixels / scaleX;
        var workAreaHeightDip = monitor.WorkAreaHeightPixels / scaleY;
        var pointInWorkAreaXDip = (point.X - monitor.WorkAreaLeftPixels) / scaleX;
        var pointInWorkAreaYDip = (point.Y - monitor.WorkAreaTopPixels) / scaleY;

        var preferredWidthDip = hasLabel
            ? CueRegionDip + LabelGapDip + LabelMaximumWidthDip
            : CueRegionDip;
        var windowWidthDip = Math.Min(preferredWidthDip, workAreaWidthDip);
        var windowHeightDip = Math.Min(WindowHeightDip, workAreaHeightDip);

        var placement = ChooseLabelPlacement(
            hasLabel,
            pointInWorkAreaXDip,
            workAreaWidthDip,
            preferredWidthDip);
        var cueCenterXDip = placement == PointCueLabelPlacement.Left
            ? Math.Max(windowWidthDip - CueCenterInsetDip, windowWidthDip / 2)
            : Math.Min(CueCenterInsetDip, windowWidthDip / 2);
        var cueCenterYDip = windowHeightDip / 2;

        var maximumLeftDip = Math.Max(workAreaWidthDip - windowWidthDip, 0);
        var maximumTopDip = Math.Max(workAreaHeightDip - windowHeightDip, 0);
        var windowLeftInWorkAreaDip = Math.Clamp(
            pointInWorkAreaXDip - cueCenterXDip,
            0,
            maximumLeftDip);
        var windowTopInWorkAreaDip = Math.Clamp(
            pointInWorkAreaYDip - cueCenterYDip,
            0,
            maximumTopDip);

        var windowWidthPixels = Math.Min(
            monitor.WorkAreaWidthPixels,
            Math.Max(1, (int)Math.Ceiling(windowWidthDip * scaleX)));
        var windowHeightPixels = Math.Min(
            monitor.WorkAreaHeightPixels,
            Math.Max(1, (int)Math.Ceiling(windowHeightDip * scaleY)));
        var windowLeftPixels = ClampRoundedPixelPosition(
            monitor.WorkAreaLeftPixels + (windowLeftInWorkAreaDip * scaleX),
            monitor.WorkAreaLeftPixels,
            monitor.WorkAreaRightPixels - windowWidthPixels);
        var windowTopPixels = ClampRoundedPixelPosition(
            monitor.WorkAreaTopPixels + (windowTopInWorkAreaDip * scaleY),
            monitor.WorkAreaTopPixels,
            monitor.WorkAreaBottomPixels - windowHeightPixels);

        return new PointCueLayout(
            scaleX,
            scaleY,
            pointInWorkAreaXDip,
            pointInWorkAreaYDip,
            windowWidthDip,
            windowHeightDip,
            cueCenterXDip,
            cueCenterYDip,
            windowLeftPixels,
            windowTopPixels,
            windowWidthPixels,
            windowHeightPixels,
            placement);
    }

    private static PointCueLabelPlacement ChooseLabelPlacement(
        bool hasLabel,
        double pointInWorkAreaXDip,
        double workAreaWidthDip,
        double windowWidthDip)
    {
        if (!hasLabel)
        {
            return PointCueLabelPlacement.None;
        }

        var extentFromCueCenterDip = windowWidthDip - CueCenterInsetDip;
        var rightSpaceDip = workAreaWidthDip - pointInWorkAreaXDip;
        var leftSpaceDip = pointInWorkAreaXDip;

        if (rightSpaceDip >= extentFromCueCenterDip)
        {
            return PointCueLabelPlacement.Right;
        }

        if (leftSpaceDip >= extentFromCueCenterDip)
        {
            return PointCueLabelPlacement.Left;
        }

        return rightSpaceDip >= leftSpaceDip
            ? PointCueLabelPlacement.Right
            : PointCueLabelPlacement.Left;
    }

    private static int ClampRoundedPixelPosition(double value, int minimum, int maximum)
    {
        var rounded = checked((int)Math.Round(value, MidpointRounding.AwayFromZero));
        return Math.Clamp(rounded, minimum, maximum);
    }
}

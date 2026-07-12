namespace Clicky.Windows.Shell;

public readonly record struct PhysicalWorkArea(
    double Left,
    double Top,
    double Right,
    double Bottom)
{
    public double Width => Right - Left;

    public double Height => Bottom - Top;
}

public readonly record struct WindowPlacement(double Left, double Top);

public static class WindowPlacementCalculator
{
    public static WindowPlacement Calculate(
        PhysicalWorkArea physicalWorkArea,
        double dpiScale,
        double windowWidth,
        double windowHeight,
        double margin)
    {
        if (physicalWorkArea.Width <= 0 || physicalWorkArea.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(physicalWorkArea),
                "The physical work area must have positive dimensions.");
        }

        if (!double.IsFinite(dpiScale) || dpiScale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dpiScale));
        }

        if (!double.IsFinite(windowWidth) || windowWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(windowWidth));
        }

        if (!double.IsFinite(windowHeight) || windowHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(windowHeight));
        }

        if (!double.IsFinite(margin) || margin < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(margin));
        }

        var workAreaLeft = physicalWorkArea.Left / dpiScale;
        var workAreaTop = physicalWorkArea.Top / dpiScale;
        var workAreaRight = physicalWorkArea.Right / dpiScale;
        var workAreaBottom = physicalWorkArea.Bottom / dpiScale;

        return new WindowPlacement(
            CalculateAxis(workAreaLeft, workAreaRight, windowWidth, margin),
            CalculateAxis(workAreaTop, workAreaBottom, windowHeight, margin));
    }

    private static double CalculateAxis(
        double workAreaStart,
        double workAreaEnd,
        double windowSize,
        double margin)
    {
        var desiredPosition = workAreaEnd - windowSize - margin;
        var latestVisiblePosition = Math.Max(workAreaStart, workAreaEnd - windowSize);
        return Math.Clamp(desiredPosition, workAreaStart, latestVisiblePosition);
    }
}

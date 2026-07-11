namespace Clicky.Windows.Capture;

public readonly record struct CaptureEncodingPlan(
    int SourceWidth,
    int SourceHeight,
    int EncodedWidth,
    int EncodedHeight)
{
    public bool RequiresResize =>
        SourceWidth != EncodedWidth || SourceHeight != EncodedHeight;
}

public static class CaptureOptimizationPolicy
{
    public const int MaximumFullResolutionWidth = 1920;
    public const int MaximumFullResolutionHeight = 1080;
    public const long MaximumSourcePixelCount = 40_000_000;

    public static long ValidateSourceDimensions(long physicalWidth, long physicalHeight)
    {
        if (physicalWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(physicalWidth),
                "Capture width must be greater than zero.");
        }

        if (physicalHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(physicalHeight),
                "Capture height must be greater than zero.");
        }

        if (physicalWidth > int.MaxValue || physicalHeight > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(physicalWidth),
                "Capture dimensions exceed the supported bitmap range.");
        }

        var sourcePixelCount = checked(physicalWidth * physicalHeight);

        if (sourcePixelCount > MaximumSourcePixelCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(physicalWidth),
                $"Capture exceeds the {MaximumSourcePixelCount:N0}-pixel safety limit.");
        }

        return sourcePixelCount;
    }

    public static CaptureEncodingPlan CreatePlan(
        int physicalWidth,
        int physicalHeight,
        bool optimizeLargeCapture)
    {
        ValidateSourceDimensions(physicalWidth, physicalHeight);

        var exceedsFullResolutionThreshold =
            physicalWidth > MaximumFullResolutionWidth ||
            physicalHeight > MaximumFullResolutionHeight;
        if (!optimizeLargeCapture || !exceedsFullResolutionThreshold)
        {
            return new CaptureEncodingPlan(
                physicalWidth,
                physicalHeight,
                physicalWidth,
                physicalHeight);
        }

        return new CaptureEncodingPlan(
            physicalWidth,
            physicalHeight,
            HalfRoundedUp(physicalWidth),
            HalfRoundedUp(physicalHeight));
    }

    private static int HalfRoundedUp(int value) =>
        Math.Max(1, (value / 2) + (value % 2));
}

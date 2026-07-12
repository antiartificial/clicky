using System.Drawing;
using Clicky.Windows.Pointing;

namespace Clicky.Windows.Capture;

public static class CaptureImageProcessor
{
    public static CaptureResult CreateResult(
        Bitmap capturedBitmap,
        PhysicalPixelBounds physicalPixelBounds,
        string windowTitle,
        bool optimizeLargeCapture,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capturedBitmap);
        ArgumentNullException.ThrowIfNull(windowTitle);
        cancellationToken.ThrowIfCancellationRequested();

        var plan = CaptureOptimizationPolicy.CreatePlan(
            capturedBitmap.Width,
            capturedBitmap.Height,
            optimizeLargeCapture);
        var jpegBytes = CaptureJpegEncoder.Encode(capturedBitmap, plan, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        return new CaptureResult(
            jpegBytes,
            plan.EncodedWidth,
            plan.EncodedHeight,
            physicalPixelBounds,
            windowTitle);
    }
}

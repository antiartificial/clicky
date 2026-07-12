namespace Clicky.Windows.Pointing;

public static class CoordinateMapper
{
    public static DesktopPoint MapToDesktop(
        ImagePixelPoint screenshotPoint,
        CaptureImagePixelSize captureImageSize,
        PhysicalPixelBounds physicalCaptureBounds)
    {
        var clampedX = Math.Clamp(screenshotPoint.X, 0, captureImageSize.Width);
        var clampedY = Math.Clamp(screenshotPoint.Y, 0, captureImageSize.Height);

        var physicalLocalX = clampedX * physicalCaptureBounds.Width / captureImageSize.Width;
        var physicalLocalY = clampedY * physicalCaptureBounds.Height / captureImageSize.Height;

        var maximumX = physicalCaptureBounds.Left + Math.Max(physicalCaptureBounds.Width - 1, 0);
        var maximumY = physicalCaptureBounds.Top + Math.Max(physicalCaptureBounds.Height - 1, 0);

        return new DesktopPoint(
            Math.Clamp(
                physicalCaptureBounds.Left + physicalLocalX,
                physicalCaptureBounds.Left,
                maximumX),
            Math.Clamp(
                physicalCaptureBounds.Top + physicalLocalY,
                physicalCaptureBounds.Top,
                maximumY));
    }

    public static DesktopPoint? ResolveAndMapToDesktop(
        PointTarget target,
        IReadOnlyList<ScreenCaptureCoordinateSpace> captureCoordinateSpaces)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(captureCoordinateSpaces);

        ScreenCaptureCoordinateSpace? targetCoordinateSpace;
        if (target.ScreenNumber is int explicitScreenNumber)
        {
            targetCoordinateSpace = captureCoordinateSpaces.FirstOrDefault(
                coordinateSpace => coordinateSpace.ScreenNumber == explicitScreenNumber);
        }
        else
        {
            targetCoordinateSpace = captureCoordinateSpaces.FirstOrDefault(
                coordinateSpace => coordinateSpace.IsCursorScreen);
        }

        if (targetCoordinateSpace is null)
        {
            return null;
        }

        return MapToDesktop(
            target.PixelPoint,
            targetCoordinateSpace.ImageSize,
            targetCoordinateSpace.PhysicalPixelBounds);
    }
}

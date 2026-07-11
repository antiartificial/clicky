namespace Clicky.Windows.Pointing;

public readonly record struct ImagePixelPoint(double X, double Y);

public readonly record struct CaptureImagePixelSize
{
    public CaptureImagePixelSize(double width, double height)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Screenshot width must be greater than zero.");
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), "Screenshot height must be greater than zero.");
        }

        Width = width;
        Height = height;
    }

    public double Width { get; }

    public double Height { get; }
}

public readonly record struct PhysicalPixelBounds
{
    public PhysicalPixelBounds(double left, double top, double width, double height)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Display width must be greater than zero.");
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), "Display height must be greater than zero.");
        }

        Left = left;
        Top = top;
        Width = width;
        Height = height;
    }

    public double Left { get; }

    public double Top { get; }

    public double Width { get; }

    public double Height { get; }
}

public readonly record struct DesktopPoint(double X, double Y);

public sealed record PointTarget(
    ImagePixelPoint PixelPoint,
    string? ElementLabel,
    int? ScreenNumber);

public sealed record PointResponse(
    string SpokenText,
    PointTarget? Target,
    bool HasPointDirective);

public sealed class ScreenCaptureCoordinateSpace
{
    public ScreenCaptureCoordinateSpace(
        int screenNumber,
        bool isCursorScreen,
        CaptureImagePixelSize imageSize,
        PhysicalPixelBounds physicalPixelBounds)
    {
        if (screenNumber < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(screenNumber), "Screen number must be one-based.");
        }

        ScreenNumber = screenNumber;
        IsCursorScreen = isCursorScreen;
        ImageSize = imageSize;
        PhysicalPixelBounds = physicalPixelBounds;
    }

    public int ScreenNumber { get; }

    public bool IsCursorScreen { get; }

    public CaptureImagePixelSize ImageSize { get; }

    public PhysicalPixelBounds PhysicalPixelBounds { get; }
}

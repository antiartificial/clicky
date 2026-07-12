using Clicky.Windows.Pointing;

namespace Clicky.Windows.Capture;

public sealed class CaptureResult
{
    private readonly byte[] jpegBytes;

    public CaptureResult(
        byte[] jpegBytes,
        int pixelWidth,
        int pixelHeight,
        PhysicalPixelBounds physicalPixelBounds,
        string windowTitle)
    {
        ArgumentNullException.ThrowIfNull(jpegBytes);
        ArgumentNullException.ThrowIfNull(windowTitle);

        if (jpegBytes.Length < 4 ||
            jpegBytes[0] != 0xFF ||
            jpegBytes[1] != 0xD8 ||
            jpegBytes[^2] != 0xFF ||
            jpegBytes[^1] != 0xD9)
        {
            throw new ArgumentException("Capture data must be a complete JPEG image.", nameof(jpegBytes));
        }

        if (pixelWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pixelWidth), "Capture width must be greater than zero.");
        }

        if (pixelHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pixelHeight), "Capture height must be greater than zero.");
        }

        this.jpegBytes = jpegBytes.ToArray();
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
        PhysicalPixelBounds = physicalPixelBounds;
        WindowTitle = windowTitle;
    }

    public ReadOnlyMemory<byte> JpegBytes => jpegBytes;

    public int PixelWidth { get; }

    public int PixelHeight { get; }

    public CaptureImagePixelSize ImageSize => new(PixelWidth, PixelHeight);

    public PhysicalPixelBounds PhysicalPixelBounds { get; }

    public string WindowTitle { get; }
}

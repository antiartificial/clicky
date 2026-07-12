using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace Clicky.Windows.Capture;

public static class CaptureJpegEncoder
{
    public const long JpegQuality = 88L;

    public static byte[] Encode(
        Bitmap source,
        CaptureEncodingPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();

        if (source.Width != plan.SourceWidth || source.Height != plan.SourceHeight)
        {
            throw new ArgumentException(
                "The encoding plan source dimensions must match the captured bitmap.",
                nameof(plan));
        }

        if (plan.EncodedWidth <= 0 || plan.EncodedHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(plan),
                "Encoded dimensions must be greater than zero.");
        }

        if (!plan.RequiresResize)
        {
            return EncodeBitmap(source, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var resized = new Bitmap(
            plan.EncodedWidth,
            plan.EncodedHeight,
            PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(resized))
        using (var imageAttributes = new ImageAttributes())
        {
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.SmoothingMode = SmoothingMode.HighQuality;
            imageAttributes.SetWrapMode(WrapMode.TileFlipXY);
            graphics.DrawImage(
                source,
                new Rectangle(0, 0, plan.EncodedWidth, plan.EncodedHeight),
                0,
                0,
                source.Width,
                source.Height,
                GraphicsUnit.Pixel,
                imageAttributes);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return EncodeBitmap(resized, cancellationToken);
    }

    private static byte[] EncodeBitmap(Bitmap bitmap, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var jpegCodec = ImageCodecInfo.GetImageEncoders()
            .Single(codec => codec.FormatID == ImageFormat.Jpeg.Guid);
        using var quality = new EncoderParameter(Encoder.Quality, JpegQuality);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = quality;
        using var jpegStream = new MemoryStream();
        bitmap.Save(jpegStream, jpegCodec, parameters);
        cancellationToken.ThrowIfCancellationRequested();
        return jpegStream.ToArray();
    }
}

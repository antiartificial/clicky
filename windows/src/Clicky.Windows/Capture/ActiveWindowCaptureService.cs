using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using Clicky.Windows.Configuration;
using Clicky.Windows.Pointing;

namespace Clicky.Windows.Capture;

public sealed class ActiveWindowCaptureService : IActiveWindowCaptureService
{
    private const int DwmExtendedFrameBounds = 9;
    private const CopyPixelOperation ScreenCopyOperation = CopyPixelOperation.SourceCopy;
    private readonly Func<bool> optimizeLargeCaptures;
    private readonly ICapturePlatform capturePlatform;

    public ActiveWindowCaptureService()
        : this(static () => true)
    {
    }

    public ActiveWindowCaptureService(CompanionSettings settings)
        : this(CreateOptimizationAccessor(settings))
    {
    }

    public ActiveWindowCaptureService(Func<bool> optimizeLargeCaptures)
        : this(optimizeLargeCaptures, NativeCapturePlatform.Instance)
    {
    }

    internal ActiveWindowCaptureService(
        Func<bool> optimizeLargeCaptures,
        ICapturePlatform capturePlatform)
    {
        this.optimizeLargeCaptures = optimizeLargeCaptures ??
            throw new ArgumentNullException(nameof(optimizeLargeCaptures));
        this.capturePlatform = capturePlatform ??
            throw new ArgumentNullException(nameof(capturePlatform));
    }

    public Task<CaptureResult> CaptureAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Active-window capture is only available on Windows.");
        }

        var snapshot = CreateSnapshot();
        return Task.Run(() => CaptureSnapshot(snapshot, cancellationToken), cancellationToken);
    }

    private CaptureWindowSnapshot CreateSnapshot()
    {
        var foregroundWindow = capturePlatform.GetForegroundWindow();
        if (foregroundWindow == IntPtr.Zero)
        {
            throw new InvalidOperationException("Windows did not report a foreground window to capture.");
        }

        if (capturePlatform.IsIconic(foregroundWindow))
        {
            throw new InvalidOperationException("The foreground window is minimized and cannot be captured.");
        }

        var bounds = capturePlatform.GetPhysicalWindowBounds(foregroundWindow);
        var width = (long)bounds.Right - bounds.Left;
        var height = (long)bounds.Bottom - bounds.Top;
        CaptureOptimizationPolicy.ValidateSourceDimensions(width, height);

        return new CaptureWindowSnapshot(
            foregroundWindow,
            bounds.Left,
            bounds.Top,
            checked((int)width),
            checked((int)height),
            capturePlatform.GetWindowTitle(foregroundWindow),
            optimizeLargeCaptures());
    }

    private CaptureResult CaptureSnapshot(
        CaptureWindowSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var bitmap = capturePlatform.CopyFromScreen(snapshot, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var physicalPixelBounds = new PhysicalPixelBounds(
            snapshot.Left,
            snapshot.Top,
            snapshot.Width,
            snapshot.Height);

        return CaptureImageProcessor.CreateResult(
            bitmap,
            physicalPixelBounds,
            snapshot.WindowTitle,
            snapshot.OptimizeLargeCapture,
            cancellationToken);
    }

    private static Func<bool> CreateOptimizationAccessor(CompanionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return () => settings.OptimizeLargeCaptures;
    }

    internal readonly record struct CaptureWindowSnapshot(
        IntPtr WindowHandle,
        int Left,
        int Top,
        int Width,
        int Height,
        string WindowTitle,
        bool OptimizeLargeCapture);

    internal readonly record struct CaptureWindowBounds(
        int Left,
        int Top,
        int Right,
        int Bottom);

    internal interface ICapturePlatform
    {
        IntPtr GetForegroundWindow();

        bool IsIconic(IntPtr windowHandle);

        CaptureWindowBounds GetPhysicalWindowBounds(IntPtr windowHandle);

        string GetWindowTitle(IntPtr windowHandle);

        Bitmap CopyFromScreen(
            CaptureWindowSnapshot snapshot,
            CancellationToken cancellationToken);
    }

    private sealed class NativeCapturePlatform : ICapturePlatform
    {
        public static NativeCapturePlatform Instance { get; } = new();

        public IntPtr GetForegroundWindow() => NativeMethods.GetForegroundWindow();

        public bool IsIconic(IntPtr windowHandle) => NativeMethods.IsIconic(windowHandle);

        public CaptureWindowBounds GetPhysicalWindowBounds(IntPtr windowHandle)
        {
            var boundsResult = NativeMethods.DwmGetWindowAttribute(
                windowHandle,
                DwmExtendedFrameBounds,
                out var bounds,
                Marshal.SizeOf<NativeRect>());

            if (boundsResult != 0 && !NativeMethods.GetWindowRect(windowHandle, out bounds))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not read foreground-window bounds.");
            }

            return new CaptureWindowBounds(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
        }

        public string GetWindowTitle(IntPtr windowHandle)
        {
            var titleLength = NativeMethods.GetWindowTextLength(windowHandle);
            if (titleLength <= 0)
            {
                return string.Empty;
            }

            var title = new StringBuilder(titleLength + 1);
            return NativeMethods.GetWindowText(windowHandle, title, title.Capacity) > 0
                ? title.ToString()
                : string.Empty;
        }

        public Bitmap CopyFromScreen(
            CaptureWindowSnapshot snapshot,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bitmap = new Bitmap(snapshot.Width, snapshot.Height, PixelFormat.Format24bppRgb);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var graphics = Graphics.FromImage(bitmap);
                graphics.CopyFromScreen(
                    snapshot.Left,
                    snapshot.Top,
                    0,
                    0,
                    new Size(snapshot.Width, snapshot.Height),
                    ScreenCopyOperation);
                cancellationToken.ThrowIfCancellationRequested();
                return bitmap;
            }
            catch
            {
                bitmap.Dispose();
                throw;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowRect(IntPtr windowHandle, out NativeRect rectangle);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsIconic(IntPtr windowHandle);

        [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW", CharSet = CharSet.Unicode)]
        public static extern int GetWindowTextLength(IntPtr windowHandle);

        [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(
            IntPtr windowHandle,
            StringBuilder title,
            int maximumCharacterCount);

        [DllImport("dwmapi.dll")]
        public static extern int DwmGetWindowAttribute(
            IntPtr windowHandle,
            int attribute,
            out NativeRect attributeValue,
            int attributeSize);
    }
}

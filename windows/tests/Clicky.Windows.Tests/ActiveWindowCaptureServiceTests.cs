using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using Clicky.Windows.Capture;
using Clicky.Windows.Configuration;
using Clicky.Windows.Pointing;

namespace Clicky.Windows.Tests;

[TestClass]
public sealed class ActiveWindowCaptureServiceTests
{
    [TestMethod]
    public void CaptureResult_ValidData_ExposesPhysicalPixelContractAndCopiesImageBytes()
    {
        byte[] jpegBytes = [0xFF, 0xD8, 0x01, 0x02, 0xFF, 0xD9];
        var physicalBounds = new PhysicalPixelBounds(-120, 45, 800, 600);

        var result = new CaptureResult(jpegBytes, 800, 600, physicalBounds, "Adobe Photoshop");
        jpegBytes[2] = 0x7F;

        CollectionAssert.AreEqual(
            new byte[] { 0xFF, 0xD8, 0x01, 0x02, 0xFF, 0xD9 },
            result.JpegBytes.ToArray());
        Assert.AreEqual(800, result.PixelWidth);
        Assert.AreEqual(600, result.PixelHeight);
        Assert.AreEqual(new CaptureImagePixelSize(800, 600), result.ImageSize);
        Assert.AreEqual(physicalBounds, result.PhysicalPixelBounds);
        Assert.AreEqual("Adobe Photoshop", result.WindowTitle);
    }

    [TestMethod]
    public void CaptureResult_NonJpegData_IsRejectedWithoutDesktopCapture()
    {
        var bounds = new PhysicalPixelBounds(0, 0, 100, 100);

        Assert.ThrowsExactly<ArgumentException>(() =>
            new CaptureResult([0x01, 0x02, 0x03, 0x04], 100, 100, bounds, string.Empty));
    }

    [TestMethod]
    public void CaptureResult_NonPositiveImageDimensions_AreRejectedWithoutDesktopCapture()
    {
        byte[] jpegBytes = [0xFF, 0xD8, 0xFF, 0xD9];
        var bounds = new PhysicalPixelBounds(0, 0, 100, 100);

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new CaptureResult(jpegBytes, 0, 100, bounds, string.Empty));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new CaptureResult(jpegBytes, 100, -1, bounds, string.Empty));
    }

    [TestMethod]
    public void ActiveWindowCaptureService_ImplementsInjectableServiceContract()
    {
        IActiveWindowCaptureService service = new ActiveWindowCaptureService();
        IActiveWindowCaptureService settingsAwareService =
            new ActiveWindowCaptureService(new CompanionSettings());

        Assert.IsInstanceOfType<ActiveWindowCaptureService>(service);
        Assert.IsInstanceOfType<ActiveWindowCaptureService>(settingsAwareService);
    }

    [TestMethod]
    public void CaptureOptimizationPolicy_AtOrBelowThreshold_RetainsFullResolution()
    {
        var exactThreshold = CaptureOptimizationPolicy.CreatePlan(1920, 1080, true);
        var belowThreshold = CaptureOptimizationPolicy.CreatePlan(1600, 900, true);

        Assert.AreEqual(new CaptureEncodingPlan(1920, 1080, 1920, 1080), exactThreshold);
        Assert.IsFalse(exactThreshold.RequiresResize);
        Assert.AreEqual(new CaptureEncodingPlan(1600, 900, 1600, 900), belowThreshold);
        Assert.IsFalse(belowThreshold.RequiresResize);
    }

    [TestMethod]
    public void CaptureOptimizationPolicy_EitherAxisAboveThreshold_HalvesBothAxes()
    {
        var wide = CaptureOptimizationPolicy.CreatePlan(2560, 900, true);
        var tall = CaptureOptimizationPolicy.CreatePlan(1600, 1200, true);
        var odd = CaptureOptimizationPolicy.CreatePlan(1921, 1081, true);
        var onePixelWide = CaptureOptimizationPolicy.CreatePlan(1, 2161, true);

        Assert.AreEqual(new CaptureEncodingPlan(2560, 900, 1280, 450), wide);
        Assert.AreEqual(new CaptureEncodingPlan(1600, 1200, 800, 600), tall);
        Assert.AreEqual(new CaptureEncodingPlan(1921, 1081, 961, 541), odd);
        Assert.AreEqual(new CaptureEncodingPlan(1, 2161, 1, 1081), onePixelWide);
        Assert.IsTrue(wide.RequiresResize);
        Assert.IsTrue(tall.RequiresResize);
        Assert.IsTrue(odd.RequiresResize);
        Assert.IsTrue(onePixelWide.RequiresResize);
    }

    [TestMethod]
    public void CaptureOptimizationPolicy_Disabled_RetainsLargeCaptureResolution()
    {
        var plan = CaptureOptimizationPolicy.CreatePlan(3840, 2160, false);

        Assert.AreEqual(new CaptureEncodingPlan(3840, 2160, 3840, 2160), plan);
        Assert.IsFalse(plan.RequiresResize);
    }

    [TestMethod]
    public void CaptureOptimizationPolicy_SourcePixelBudget_Accepts8KAndExactBoundary()
    {
        var eightKPixelCount = CaptureOptimizationPolicy.ValidateSourceDimensions(7680, 4320);
        var boundaryPixelCount = CaptureOptimizationPolicy.ValidateSourceDimensions(8000, 5000);

        Assert.AreEqual(33_177_600L, eightKPixelCount);
        Assert.AreEqual(CaptureOptimizationPolicy.MaximumSourcePixelCount, boundaryPixelCount);
    }

    [TestMethod]
    public void CaptureOptimizationPolicy_SourcePixelBudget_RejectsOverBudgetAndOverflowSizedDimensions()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            CaptureOptimizationPolicy.ValidateSourceDimensions(8001, 5000));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            CaptureOptimizationPolicy.ValidateSourceDimensions(long.MaxValue, 2));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            CaptureOptimizationPolicy.ValidateSourceDimensions(int.MaxValue, int.MaxValue));
    }

    [TestMethod]
    public void CaptureJpegEncoder_UsesDeliberateHighQualitySetting()
    {
        Assert.AreEqual(88L, CaptureJpegEncoder.JpegQuality);
    }

    [TestMethod]
    public void CaptureImageProcessor_LargeBitmap_EncodesHalfSizeAndPreservesPhysicalBounds()
    {
        using var bitmap = CreateTestBitmap(2000, 1200);
        var physicalBounds = new PhysicalPixelBounds(-2000, 80, 2000, 1200);

        var result = CaptureImageProcessor.CreateResult(
            bitmap,
            physicalBounds,
            "Test editor",
            optimizeLargeCapture: true);

        using var jpeg = LoadEncodedImage(result.JpegBytes);
        Assert.AreEqual(1000, jpeg.Width);
        Assert.AreEqual(600, jpeg.Height);
        Assert.AreEqual(1000, result.PixelWidth);
        Assert.AreEqual(600, result.PixelHeight);
        Assert.AreEqual(physicalBounds, result.PhysicalPixelBounds);
        Assert.AreEqual(
            new DesktopPoint(-1000, 680),
            CoordinateMapper.MapToDesktop(
                new ImagePixelPoint(500, 300),
                result.ImageSize,
                result.PhysicalPixelBounds));
    }

    [TestMethod]
    public void CaptureImageProcessor_ThresholdBitmap_EncodesFullSize()
    {
        using var bitmap = CreateTestBitmap(1920, 1080);

        var result = CaptureImageProcessor.CreateResult(
            bitmap,
            new PhysicalPixelBounds(0, 0, 1920, 1080),
            string.Empty,
            optimizeLargeCapture: true);

        using var jpeg = LoadEncodedImage(result.JpegBytes);
        Assert.AreEqual(1920, jpeg.Width);
        Assert.AreEqual(1080, jpeg.Height);
        Assert.AreEqual(1920, result.PixelWidth);
        Assert.AreEqual(1080, result.PixelHeight);
    }

    [TestMethod]
    public void ActiveWindowCaptureService_UsesSupportedSourceCopyOperation()
    {
        var operationField = typeof(ActiveWindowCaptureService).GetField(
            "ScreenCopyOperation",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.IsNotNull(operationField);
        var configuredOperation = (CopyPixelOperation)operationField.GetRawConstantValue()!;

        Assert.IsTrue(Enum.IsDefined(configuredOperation));
        Assert.AreEqual(CopyPixelOperation.SourceCopy, configuredOperation);
        Assert.AreNotEqual(
            CopyPixelOperation.SourceCopy | CopyPixelOperation.CaptureBlt,
            configuredOperation);
    }

    [TestMethod]
    public async Task CaptureAsync_SnapshotsWindowAndOptimizationBeforeBackgroundWork()
    {
        var callingThreadId = Environment.CurrentManagedThreadId;
        var optimizeLargeCaptures = true;
        using var platform = new FakeCapturePlatform
        {
            ForegroundWindow = new IntPtr(42),
            Bounds = new ActiveWindowCaptureService.CaptureWindowBounds(10, 20, 14, 24),
            WindowTitle = "Adobe Photoshop"
        };
        var service = new ActiveWindowCaptureService(
            () => optimizeLargeCaptures,
            platform);

        var captureTask = service.CaptureAsync();

        Assert.AreEqual(callingThreadId, platform.SnapshotThreadId);
        Assert.IsTrue(platform.CopyStarted.Wait(TimeSpan.FromSeconds(5)));
        platform.ForegroundWindow = new IntPtr(99);
        platform.Bounds = new ActiveWindowCaptureService.CaptureWindowBounds(100, 200, 108, 208);
        platform.WindowTitle = "Visual Studio";
        optimizeLargeCaptures = false;
        platform.ContinueCopy.Set();

        var result = await captureTask;

        Assert.AreNotEqual(callingThreadId, platform.CopyThreadId);
        Assert.AreEqual(
            new ActiveWindowCaptureService.CaptureWindowSnapshot(
                new IntPtr(42),
                10,
                20,
                4,
                4,
                "Adobe Photoshop",
                true),
            platform.CapturedSnapshot);
        Assert.AreEqual("Adobe Photoshop", result.WindowTitle);
        Assert.AreEqual(new PhysicalPixelBounds(10, 20, 4, 4), result.PhysicalPixelBounds);
    }

    [TestMethod]
    public async Task CaptureAsync_CancellationWhileWorkerIsQueued_PreventsBitmapAllocation()
    {
        using var cancellation = new CancellationTokenSource();
        using var platform = new FakeCapturePlatform();
        var service = new ActiveWindowCaptureService(static () => true, platform);

        var captureTask = service.CaptureAsync(cancellation.Token);
        Assert.IsTrue(platform.CopyStarted.Wait(TimeSpan.FromSeconds(5)));

        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await captureTask);
        Assert.IsFalse(platform.BitmapAllocated);
    }

    [TestMethod]
    public void CaptureAsync_OverflowingNativeBounds_AreRejectedBeforeWorkerOrAllocation()
    {
        using var platform = new FakeCapturePlatform
        {
            Bounds = new ActiveWindowCaptureService.CaptureWindowBounds(
                int.MinValue,
                0,
                int.MaxValue,
                10)
        };
        var service = new ActiveWindowCaptureService(static () => true, platform);

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => service.CaptureAsync());
        Assert.IsFalse(platform.CopyStarted.IsSet);
        Assert.IsFalse(platform.BitmapAllocated);
    }

    private static Bitmap CreateTestBitmap(int width, int height)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.CornflowerBlue);
        graphics.FillRectangle(Brushes.Gold, width / 4, height / 4, width / 2, height / 2);
        return bitmap;
    }

    private static Image LoadEncodedImage(ReadOnlyMemory<byte> jpegBytes)
    {
        using var stream = new MemoryStream(jpegBytes.ToArray());
        using var decoded = Image.FromStream(stream);
        return new Bitmap(decoded);
    }

    private sealed class FakeCapturePlatform :
        ActiveWindowCaptureService.ICapturePlatform,
        IDisposable
    {
        public IntPtr ForegroundWindow { get; set; } = new(7);

        public ActiveWindowCaptureService.CaptureWindowBounds Bounds { get; set; } =
            new(0, 0, 4, 4);

        public string WindowTitle { get; set; } = "Rive";

        public int SnapshotThreadId { get; private set; }

        public int CopyThreadId { get; private set; }

        public bool BitmapAllocated { get; private set; }

        public ActiveWindowCaptureService.CaptureWindowSnapshot CapturedSnapshot { get; private set; }

        public ManualResetEventSlim CopyStarted { get; } = new(false);

        public ManualResetEventSlim ContinueCopy { get; } = new(false);

        public IntPtr GetForegroundWindow()
        {
            SnapshotThreadId = Environment.CurrentManagedThreadId;
            return ForegroundWindow;
        }

        public bool IsIconic(IntPtr windowHandle) => false;

        public ActiveWindowCaptureService.CaptureWindowBounds GetPhysicalWindowBounds(
            IntPtr windowHandle) => Bounds;

        public string GetWindowTitle(IntPtr windowHandle) => WindowTitle;

        public Bitmap CopyFromScreen(
            ActiveWindowCaptureService.CaptureWindowSnapshot snapshot,
            CancellationToken cancellationToken)
        {
            CopyThreadId = Environment.CurrentManagedThreadId;
            CapturedSnapshot = snapshot;
            CopyStarted.Set();
            ContinueCopy.Wait(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            BitmapAllocated = true;

            var bitmap = new Bitmap(snapshot.Width, snapshot.Height, PixelFormat.Format24bppRgb);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.CornflowerBlue);
            return bitmap;
        }

        public void Dispose()
        {
            CopyStarted.Dispose();
            ContinueCopy.Dispose();
        }
    }
}

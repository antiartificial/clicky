using Clicky.Windows.Pointing;

namespace Clicky.Windows.Tests;

[TestClass]
public sealed class CoordinateMapperTests
{
    [TestMethod]
    public void MapToDesktop_ScalesTopLeftScreenshotCoordinatesIntoDisplayBounds()
    {
        var result = CoordinateMapper.MapToDesktop(
            new ImagePixelPoint(640, 360),
            new CaptureImagePixelSize(1280, 720),
            new PhysicalPixelBounds(100, 50, 1920, 1080));

        Assert.AreEqual(new DesktopPoint(1060, 590), result);
    }

    [TestMethod]
    public void MapToDesktop_SupportsDisplaysWithNegativeDesktopOrigins()
    {
        var result = CoordinateMapper.MapToDesktop(
            new ImagePixelPoint(320, 180),
            new CaptureImagePixelSize(1280, 720),
            new PhysicalPixelBounds(-1920, -200, 1920, 1080));

        Assert.AreEqual(new DesktopPoint(-1440, 70), result);
    }

    [TestMethod]
    public void MapToDesktop_ClampsCoordinatesToScreenshotEdges()
    {
        var result = CoordinateMapper.MapToDesktop(
            new ImagePixelPoint(1500, -20),
            new CaptureImagePixelSize(1280, 720),
            new PhysicalPixelBounds(0, 0, 1920, 1080));

        Assert.AreEqual(new DesktopPoint(1919, 0), result);
    }

    [TestMethod]
    public void MapToDesktop_ClampsToExclusiveRightAndBottomEdges()
    {
        var result = CoordinateMapper.MapToDesktop(
            new ImagePixelPoint(1280, 720),
            new CaptureImagePixelSize(1280, 720),
            new PhysicalPixelBounds(-1920, 100, 1920, 1080));

        Assert.AreEqual(new DesktopPoint(-1, 1179), result);
    }

    [TestMethod]
    public void MapToDesktop_OnePixelPhysicalBoundsAlwaysReturnTheirOnlyPoint()
    {
        var result = CoordinateMapper.MapToDesktop(
            new ImagePixelPoint(500, 500),
            new CaptureImagePixelSize(1000, 1000),
            new PhysicalPixelBounds(42, -7, 1, 1));

        Assert.AreEqual(new DesktopPoint(42, -7), result);
    }

    [TestMethod]
    public void CaptureImagePixelSize_RejectsZeroDimensions()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new CaptureImagePixelSize(0, 720));
    }

    [TestMethod]
    public void MapToDesktop_UsesPhysicalCaptureBoundsAt125PercentDpi()
    {
        var result = CoordinateMapper.MapToDesktop(
            new ImagePixelPoint(300, 337.5),
            new CaptureImagePixelSize(1200, 675),
            new PhysicalPixelBounds(-2400, 0, 2400, 1350));

        Assert.AreEqual(new DesktopPoint(-1800, 675), result);
    }

    [TestMethod]
    public void MapToDesktop_UsesPhysicalCaptureBoundsAt150PercentDpi()
    {
        var result = CoordinateMapper.MapToDesktop(
            new ImagePixelPoint(960, 180),
            new CaptureImagePixelSize(1280, 720),
            new PhysicalPixelBounds(0, -2160, 3840, 2160));

        Assert.AreEqual(new DesktopPoint(2880, -1620), result);
    }

    [TestMethod]
    public void ResolveAndMapToDesktop_ExplicitUnknownScreenSuppressesPointing()
    {
        var target = new PointTarget(new ImagePixelPoint(100, 100), "save", ScreenNumber: 3);
        var captures = new[]
        {
            new ScreenCaptureCoordinateSpace(
                screenNumber: 1,
                isCursorScreen: true,
                new CaptureImagePixelSize(1280, 720),
                new PhysicalPixelBounds(0, 0, 1920, 1080)),
            new ScreenCaptureCoordinateSpace(
                screenNumber: 2,
                isCursorScreen: false,
                new CaptureImagePixelSize(1280, 720),
                new PhysicalPixelBounds(1920, 0, 1920, 1080))
        };

        var result = CoordinateMapper.ResolveAndMapToDesktop(target, captures);

        Assert.IsNull(result);
    }

    [TestMethod]
    public void ResolveAndMapToDesktop_OmittedScreenUsesCursorScreen()
    {
        var target = new PointTarget(new ImagePixelPoint(640, 360), "save", ScreenNumber: null);
        var captures = new[]
        {
            new ScreenCaptureCoordinateSpace(
                screenNumber: 1,
                isCursorScreen: false,
                new CaptureImagePixelSize(1280, 720),
                new PhysicalPixelBounds(0, 0, 1920, 1080)),
            new ScreenCaptureCoordinateSpace(
                screenNumber: 2,
                isCursorScreen: true,
                new CaptureImagePixelSize(1280, 720),
                new PhysicalPixelBounds(-2400, 0, 2400, 1350))
        };

        var result = CoordinateMapper.ResolveAndMapToDesktop(target, captures);

        Assert.AreEqual(new DesktopPoint(-1200, 675), result);
    }
}

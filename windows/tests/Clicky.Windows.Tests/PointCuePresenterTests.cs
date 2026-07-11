using Clicky.Windows.Overlay;
using Clicky.Windows.Pointing;

namespace Clicky.Windows.Tests;

[TestClass]
public sealed class PointCuePresenterTests
{
    [TestMethod]
    public void Calculate_UsesPhysicalPixelsDirectlyAt100PercentDpi()
    {
        var monitor = new PointCueMonitorMetrics(
            workAreaLeftPixels: 0,
            workAreaTopPixels: 0,
            workAreaWidthPixels: 1920,
            workAreaHeightPixels: 1040,
            effectiveDpiX: 96,
            effectiveDpiY: 96);

        var result = PointCueLayoutCalculator.Calculate(
            new DesktopPoint(960, 520),
            monitor,
            hasLabel: true);

        Assert.AreEqual(1, result.DpiScaleX);
        Assert.AreEqual(1, result.DpiScaleY);
        Assert.AreEqual(960, result.PointInWorkAreaXDip);
        Assert.AreEqual(520, result.PointInWorkAreaYDip);
        Assert.AreEqual(940, result.WindowLeftPixels);
        Assert.AreEqual(500, result.WindowTopPixels);
        Assert.AreEqual(150, result.WindowWidthPixels);
        Assert.AreEqual(40, result.WindowHeightPixels);
        Assert.AreEqual(PointCueLabelPlacement.Right, result.LabelPlacement);
    }

    [TestMethod]
    public void Calculate_ConvertsPhysicalPixelsToDipsAt125PercentDpiWithNegativeOrigin()
    {
        var monitor = new PointCueMonitorMetrics(
            workAreaLeftPixels: -2400,
            workAreaTopPixels: 0,
            workAreaWidthPixels: 2400,
            workAreaHeightPixels: 1350,
            effectiveDpiX: 120,
            effectiveDpiY: 120);

        var result = PointCueLayoutCalculator.Calculate(
            new DesktopPoint(-1200, 675),
            monitor,
            hasLabel: true);

        Assert.AreEqual(1.25, result.DpiScaleX);
        Assert.AreEqual(1.25, result.DpiScaleY);
        Assert.AreEqual(960, result.PointInWorkAreaXDip);
        Assert.AreEqual(540, result.PointInWorkAreaYDip);
        Assert.AreEqual(-1225, result.WindowLeftPixels);
        Assert.AreEqual(650, result.WindowTopPixels);
        Assert.AreEqual(188, result.WindowWidthPixels);
        Assert.AreEqual(50, result.WindowHeightPixels);
    }

    [TestMethod]
    public void Calculate_ConvertsPhysicalPixelsToDipsAt150PercentDpiWithNegativeTop()
    {
        var monitor = new PointCueMonitorMetrics(
            workAreaLeftPixels: 0,
            workAreaTopPixels: -2160,
            workAreaWidthPixels: 3840,
            workAreaHeightPixels: 2160,
            effectiveDpiX: 144,
            effectiveDpiY: 144);

        var result = PointCueLayoutCalculator.Calculate(
            new DesktopPoint(1920, -1080),
            monitor,
            hasLabel: true);

        Assert.AreEqual(1.5, result.DpiScaleX);
        Assert.AreEqual(1.5, result.DpiScaleY);
        Assert.AreEqual(1280, result.PointInWorkAreaXDip);
        Assert.AreEqual(720, result.PointInWorkAreaYDip);
        Assert.AreEqual(1890, result.WindowLeftPixels);
        Assert.AreEqual(-1110, result.WindowTopPixels);
        Assert.AreEqual(225, result.WindowWidthPixels);
        Assert.AreEqual(60, result.WindowHeightPixels);
    }

    [TestMethod]
    public void Calculate_ClampsLabeledCueToNegativeOriginWorkAreaRightAndBottomEdges()
    {
        var monitor = new PointCueMonitorMetrics(
            workAreaLeftPixels: -1920,
            workAreaTopPixels: -100,
            workAreaWidthPixels: 1920,
            workAreaHeightPixels: 1040,
            effectiveDpiX: 96,
            effectiveDpiY: 96);

        var result = PointCueLayoutCalculator.Calculate(
            new DesktopPoint(-1, 939),
            monitor,
            hasLabel: true);

        Assert.AreEqual(PointCueLabelPlacement.Left, result.LabelPlacement);
        Assert.AreEqual(-150, result.WindowLeftPixels);
        Assert.AreEqual(900, result.WindowTopPixels);
        Assert.AreEqual(monitor.WorkAreaRightPixels, result.WindowLeftPixels + result.WindowWidthPixels);
        Assert.AreEqual(monitor.WorkAreaBottomPixels, result.WindowTopPixels + result.WindowHeightPixels);
    }

    [TestMethod]
    public void Calculate_ClampsCueToWorkAreaLeftAndTopEdges()
    {
        var monitor = new PointCueMonitorMetrics(
            workAreaLeftPixels: -1920,
            workAreaTopPixels: -100,
            workAreaWidthPixels: 1920,
            workAreaHeightPixels: 1040,
            effectiveDpiX: 96,
            effectiveDpiY: 96);

        var result = PointCueLayoutCalculator.Calculate(
            new DesktopPoint(-1920, -100),
            monitor,
            hasLabel: false);

        Assert.AreEqual(PointCueLabelPlacement.None, result.LabelPlacement);
        Assert.AreEqual(-1920, result.WindowLeftPixels);
        Assert.AreEqual(-100, result.WindowTopPixels);
        Assert.AreEqual(40, result.WindowWidthPixels);
        Assert.AreEqual(40, result.WindowHeightPixels);
    }
}

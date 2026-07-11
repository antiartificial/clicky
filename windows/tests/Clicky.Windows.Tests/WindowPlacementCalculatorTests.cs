using Clicky.Windows.Shell;

namespace Clicky.Windows.Tests;

[TestClass]
public sealed class WindowPlacementCalculatorTests
{
    [TestMethod]
    [DataRow(1.00, 2202.0, 1196.0)]
    [DataRow(1.25, 1690.0, 916.0)]
    [DataRow(1.50, 1348.6666666667, 729.3333333333)]
    [DataRow(2.00, 922.0, 496.0)]
    public void Calculate_ConvertsPhysicalWorkAreaAtCommonDpiScales(
        double dpiScale,
        double expectedLeft,
        double expectedTop)
    {
        var placement = WindowPlacementCalculator.Calculate(
            new PhysicalWorkArea(0, 0, 2560, 1400),
            dpiScale,
            windowWidth: 344,
            windowHeight: 190,
            margin: 14);

        Assert.AreEqual(expectedLeft, placement.Left, 0.0001);
        Assert.AreEqual(expectedTop, placement.Top, 0.0001);
    }

    [TestMethod]
    public void Calculate_PreservesNegativeWorkAreaOrigins()
    {
        var placement = WindowPlacementCalculator.Calculate(
            new PhysicalWorkArea(-1920, -1080, 0, 0),
            dpiScale: 1.5,
            windowWidth: 344,
            windowHeight: 190,
            margin: 14);

        Assert.AreEqual(-358, placement.Left, 0.0001);
        Assert.AreEqual(-204, placement.Top, 0.0001);
    }

    [TestMethod]
    public void Calculate_ClampsOversizedWindowToWorkAreaOrigin()
    {
        var placement = WindowPlacementCalculator.Calculate(
            new PhysicalWorkArea(-300, -150, 0, 0),
            dpiScale: 1.5,
            windowWidth: 344,
            windowHeight: 190,
            margin: 14);

        Assert.AreEqual(-200, placement.Left, 0.0001);
        Assert.AreEqual(-100, placement.Top, 0.0001);
    }

    [TestMethod]
    public void Calculate_ClampsExcessiveMarginInsideWorkArea()
    {
        var placement = WindowPlacementCalculator.Calculate(
            new PhysicalWorkArea(100, 200, 1100, 1000),
            dpiScale: 1,
            windowWidth: 344,
            windowHeight: 190,
            margin: 2000);

        Assert.AreEqual(100, placement.Left, 0.0001);
        Assert.AreEqual(200, placement.Top, 0.0001);
    }
}

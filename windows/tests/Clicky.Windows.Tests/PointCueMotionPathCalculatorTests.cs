using Clicky.Windows.Overlay;

namespace Clicky.Windows.Tests;

[TestClass]
public sealed class PointCueMotionPathCalculatorTests
{
    [TestMethod]
    public void Calculate_ReturnsExactEndpoints()
    {
        var start = new PointCueMotionPoint(-120.5, 48.25);
        var end = new PointCueMotionPoint(640.75, 512.5);

        Assert.AreEqual(start, PointCueMotionPathCalculator.Calculate(start, end, 0));
        Assert.AreEqual(end, PointCueMotionPathCalculator.Calculate(start, end, 1));
    }

    [TestMethod]
    public void Calculate_UsesRestrainedSCurveBetweenEndpoints()
    {
        var start = new PointCueMotionPoint(0, 0);
        var end = new PointCueMotionPoint(160, 0);

        var firstTurn = PointCueMotionPathCalculator.Calculate(start, end, 0.18);
        var secondTurn = PointCueMotionPathCalculator.Calculate(start, end, 0.65);

        Assert.IsGreaterThan(0, firstTurn.X);
        Assert.IsLessThan(160, firstTurn.X);
        Assert.IsGreaterThan(0, firstTurn.Y);
        Assert.IsLessThanOrEqualTo(18, firstTurn.Y);
        Assert.IsGreaterThan(firstTurn.X, secondTurn.X);
        Assert.IsLessThan(0, secondTurn.Y);
        Assert.IsGreaterThanOrEqualTo(-18, secondTurn.Y);
    }

    [TestMethod]
    public void CalculateArrivalOrbit_CompletesShrinkingEllipseAtDestination()
    {
        var destination = new PointCueMotionPoint(500, 300);

        var start = PointCueMotionPathCalculator.CalculateArrivalOrbit(
            destination,
            progress: 0,
            dpiScaleX: 1.5,
            dpiScaleY: 1.25);
        var firstQuarter = PointCueMotionPathCalculator.CalculateArrivalOrbit(
            destination,
            progress: 0.25,
            dpiScaleX: 1.5,
            dpiScaleY: 1.25);
        var finish = PointCueMotionPathCalculator.CalculateArrivalOrbit(
            destination,
            progress: 1,
            dpiScaleX: 1.5,
            dpiScaleY: 1.25);

        Assert.AreEqual(476, start.X, 0.001);
        Assert.AreEqual(300, start.Y, 0.001);
        Assert.AreEqual(500, firstQuarter.X, 0.001);
        Assert.IsLessThan(300, firstQuarter.Y);
        Assert.AreEqual(destination, finish);
    }

    [TestMethod]
    public void EaseForLanding_SlowsNearEndpoint()
    {
        var middleStep = PointCueMotionPathCalculator.EaseForLanding(0.6) -
            PointCueMotionPathCalculator.EaseForLanding(0.5);
        var landingStep = PointCueMotionPathCalculator.EaseForLanding(1) -
            PointCueMotionPathCalculator.EaseForLanding(0.9);

        Assert.IsGreaterThan(landingStep, middleStep);
        Assert.AreEqual(0, PointCueMotionPathCalculator.EaseForLanding(0));
        Assert.AreEqual(1, PointCueMotionPathCalculator.EaseForLanding(1));
    }

    [TestMethod]
    public void CreateRevealStart_ScalesDipOffsetIntoPhysicalPixels()
    {
        var destination = new PointCueMotionPoint(500, 300);

        var result = PointCueMotionPathCalculator.CreateRevealStart(
            destination,
            dpiScaleX: 1.5,
            dpiScaleY: 1.25);

        Assert.AreEqual(374, result.X);
        Assert.AreEqual(367.5, result.Y);
    }

    [TestMethod]
    public void PresenterOptions_KeepCueAvailableForNarrationAndReplay()
    {
        var options = new PointCuePresenterOptions();

        Assert.AreEqual(TimeSpan.FromSeconds(15), options.AutoHideAfter);
    }

    [TestMethod]
    public void ShouldUseMotion_EvaluatesDynamicOptionAndHonorsReducedMotion()
    {
        var enabled = true;
        var options = new PointCuePresenterOptions
        {
            MotionEnabled = () => enabled
        };

        Assert.IsTrue(options.ShouldUseMotion());

        enabled = false;
        Assert.IsFalse(options.ShouldUseMotion());

        var reducedMotionOptions = options with
        {
            MotionEnabled = static () => true,
            ReducedMotion = true
        };
        Assert.IsFalse(reducedMotionOptions.ShouldUseMotion());
    }
}

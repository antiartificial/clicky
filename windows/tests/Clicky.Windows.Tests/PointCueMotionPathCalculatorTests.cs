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
    public void Calculate_UsesRestrainedCurveBetweenEndpoints()
    {
        var start = new PointCueMotionPoint(0, 0);
        var end = new PointCueMotionPoint(100, 0);

        var midpoint = PointCueMotionPathCalculator.Calculate(start, end, 0.5);

        Assert.IsGreaterThan(50, midpoint.X);
        Assert.IsLessThan(100, midpoint.X);
        Assert.IsGreaterThan(0, midpoint.Y);
        Assert.IsLessThanOrEqualTo(10, midpoint.Y);
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

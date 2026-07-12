using Clicky.Windows.Pointing;

namespace Clicky.Windows.Tests;

[TestClass]
public sealed class PointResponseParserTests
{
    [TestMethod]
    public void Parse_CoordinateTag_ReturnsSpokenTextAndTarget()
    {
        var result = PointResponseParser.Parse(
            "Open the color inspector. [POINT:1100,42:color inspector]");

        Assert.AreEqual("Open the color inspector.", result.SpokenText);
        Assert.IsTrue(result.HasPointDirective);
        Assert.IsNotNull(result.Target);
        Assert.AreEqual(new ImagePixelPoint(1100, 42), result.Target.PixelPoint);
        Assert.AreEqual("color inspector", result.Target.ElementLabel);
        Assert.IsNull(result.Target.ScreenNumber);
    }

    [TestMethod]
    public void Parse_ScreenTag_ReturnsOneBasedScreenNumber()
    {
        var result = PointResponseParser.Parse(
            "It is on the other display. [POINT:400,300:terminal:screen2]");

        Assert.AreEqual(2, result.Target?.ScreenNumber);
    }

    [TestMethod]
    public void Parse_NoneTag_RemovesDirectiveWithoutCreatingTarget()
    {
        var result = PointResponseParser.Parse("Here is the answer. [POINT:none]   ");

        Assert.AreEqual("Here is the answer.", result.SpokenText);
        Assert.IsTrue(result.HasPointDirective);
        Assert.IsNull(result.Target);
    }

    [TestMethod]
    public void Parse_MalformedOrNonTerminalTag_LeavesResponseUntouched()
    {
        const string response = "[POINT:10,20:button] More text follows.";

        var result = PointResponseParser.Parse(response);

        Assert.AreEqual(response, result.SpokenText);
        Assert.IsFalse(result.HasPointDirective);
        Assert.IsNull(result.Target);
    }

    [TestMethod]
    public void Parse_ScreenZero_StripsDirectiveAndSuppressesPointing()
    {
        const string response = "Look here. [POINT:10,20:button:screen0]";

        var result = PointResponseParser.Parse(response);

        Assert.AreEqual("Look here.", result.SpokenText);
        Assert.IsTrue(result.HasPointDirective);
        Assert.IsNull(result.Target);
    }
}

using Clicky.Windows.Pointing;

namespace Clicky.Windows.Tests;

[TestClass]
public sealed class PointDirectiveStreamingFilterTests
{
    [TestMethod]
    public void Append_StreamsVisibleTextAndSuppressesSplitDirective()
    {
        var filter = new PointDirectiveStreamingFilter();

        var visibleText = string.Concat(
            filter.Append("Open the "),
            filter.Append("File menu. [PO"),
            filter.Append("INT:120,80:File]"),
            filter.Complete());

        Assert.AreEqual("Open the File menu. ", visibleText);
    }

    [TestMethod]
    public void Append_DoesNotDelayOrdinaryBrackets()
    {
        var filter = new PointDirectiveStreamingFilter();

        var visibleText = string.Concat(
            filter.Append("Use [Ctrl] then "),
            filter.Append("continue."),
            filter.Complete());

        Assert.AreEqual("Use [Ctrl] then continue.", visibleText);
    }

    [TestMethod]
    public void Complete_ReleasesAnIncompleteNonDirectiveSuffix()
    {
        var filter = new PointDirectiveStreamingFilter();

        var visibleText = filter.Append("Look at [PO") + filter.Complete();

        Assert.AreEqual("Look at [PO", visibleText);
    }
}

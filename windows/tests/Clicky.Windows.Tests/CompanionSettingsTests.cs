using Clicky.Windows.Configuration;

namespace Clicky.Windows.Tests;

[TestClass]
public sealed class CompanionSettingsTests
{
    [TestMethod]
    public void Defaults_AreCaptureReadyAndPrivacyPreserving()
    {
        var settings = new CompanionSettings();

        Assert.AreEqual(CompanionSettings.DefaultWorkerBaseUrl, settings.WorkerBaseUrl);
        Assert.IsFalse(settings.CaptureAllDisplays);
        Assert.IsTrue(settings.CaptureOnlyDuringActiveRequest);
        Assert.IsTrue(settings.ExcludeCompanionWindows);
        Assert.IsFalse(settings.IncludePointerInCaptures);
        Assert.IsTrue(settings.OptimizeLargeCaptures);
        Assert.IsFalse(settings.RetainCapturesLocally);
    }
}

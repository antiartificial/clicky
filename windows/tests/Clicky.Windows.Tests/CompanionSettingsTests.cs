using Clicky.Windows.Configuration;
using Clicky.Windows.Input;

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
        Assert.IsTrue(settings.MotionEffectsEnabled);
        Assert.AreEqual(PushToTalkKey.F13, settings.PushToTalkKey);
    }

    [TestMethod]
    public void PushToTalkKey_RejectsValuesOutsideSupportedFunctionKeys()
    {
        var settings = new CompanionSettings();

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => settings.PushToTalkKey = (PushToTalkKey)0x7B);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => settings.PushToTalkKey = (PushToTalkKey)0x88);
        Assert.AreEqual(PushToTalkKey.F13, settings.PushToTalkKey);
    }
}

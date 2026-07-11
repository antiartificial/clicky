using Clicky.Windows.Configuration;
using Clicky.Windows.Input;
using Clicky.Windows.Providers;

namespace Clicky.Windows.Tests;

[TestClass]
public sealed class CompanionSettingsStoreTests
{
    [TestMethod]
    public async Task SaveAndLoad_RoundTripsNonSecretPreferences()
    {
        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "ClickySettingsTests",
            Guid.NewGuid().ToString("N"));
        var settingsPath = Path.Combine(temporaryDirectory, "settings.json");

        try
        {
            var store = new CompanionSettingsStore(settingsPath);
            var settings = new CompanionSettings
            {
                SelectedProvider = AiProviderKind.Gemini,
                GeminiModelId = "gemini-test",
                MotionEffectsEnabled = false,
                OptimizeLargeCaptures = false,
                PushToTalkKey = PushToTalkKey.F18,
                SpeechOutputEnabled = true,
                TextToSpeechProvider = TextToSpeechProviderKind.ElevenLabs,
                ElevenLabsVoiceId = "voice-test",
            };

            await store.SaveAsync(settings);
            var loadedSettings = store.Load();

            Assert.AreEqual(AiProviderKind.Gemini, loadedSettings.SelectedProvider);
            Assert.AreEqual("gemini-test", loadedSettings.GeminiModelId);
            Assert.IsFalse(loadedSettings.MotionEffectsEnabled);
            Assert.IsFalse(loadedSettings.OptimizeLargeCaptures);
            Assert.AreEqual(PushToTalkKey.F18, loadedSettings.PushToTalkKey);
            Assert.IsTrue(loadedSettings.SpeechOutputEnabled);
            Assert.AreEqual(TextToSpeechProviderKind.ElevenLabs, loadedSettings.TextToSpeechProvider);
            Assert.AreEqual("voice-test", loadedSettings.ElevenLabsVoiceId);
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public void Load_MalformedJsonReturnsOpenAIDefaults()
    {
        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "ClickySettingsTests",
            Guid.NewGuid().ToString("N"));
        var settingsPath = Path.Combine(temporaryDirectory, "settings.json");
        Directory.CreateDirectory(temporaryDirectory);
        File.WriteAllText(settingsPath, "{not-json");

        try
        {
            var settings = new CompanionSettingsStore(settingsPath).Load();

            Assert.AreEqual(AiProviderKind.OpenAI, settings.SelectedProvider);
            Assert.AreEqual(CompanionSettings.DefaultOpenAIModelId, settings.OpenAIModelId);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }
}

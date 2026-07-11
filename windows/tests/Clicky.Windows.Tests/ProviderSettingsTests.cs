using System.Text.Json;
using Clicky.Windows.Configuration;
using Clicky.Windows.Providers;

namespace Clicky.Windows.Tests;

[TestClass]
public sealed class ProviderSettingsTests
{
    [TestMethod]
    public void Defaults_PreserveWorkerBehaviorAndCurrentAnthropicModel()
    {
        var settings = new CompanionSettings();

        Assert.AreEqual(AiProviderKind.Worker, settings.SelectedProvider);
        Assert.AreEqual("claude-sonnet-4-6", settings.AnthropicModelId);
        Assert.AreEqual("gpt-5.4-mini", settings.OpenAIModelId);
    }

    [TestMethod]
    public void ProviderAndModelIds_AreEditableObservableSettings()
    {
        var settings = new CompanionSettings();
        var changedProperties = new List<string?>();
        settings.PropertyChanged += (_, eventArgs) => changedProperties.Add(eventArgs.PropertyName);

        settings.SelectedProvider = AiProviderKind.OpenAI;
        settings.AnthropicModelId = "claude-custom";
        settings.OpenAIModelId = "gpt-custom";

        Assert.AreEqual(AiProviderKind.OpenAI, settings.SelectedProvider);
        Assert.AreEqual("claude-custom", settings.AnthropicModelId);
        Assert.AreEqual("gpt-custom", settings.OpenAIModelId);
        CollectionAssert.AreEqual(
            new[]
            {
                nameof(CompanionSettings.SelectedProvider),
                nameof(CompanionSettings.AnthropicModelId),
                nameof(CompanionSettings.OpenAIModelId),
            },
            changedProperties);
    }

    [TestMethod]
    public void SettingsSerialization_HasNoApiKeySurface()
    {
        var serializedSettings = JsonSerializer.Serialize(new CompanionSettings());

        Assert.IsFalse(serializedSettings.Contains("ApiKey", StringComparison.Ordinal));
        Assert.IsFalse(serializedSettings.Contains("Secret", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SelectedProvider_RejectsUnknownValues()
    {
        var settings = new CompanionSettings();

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => settings.SelectedProvider = (AiProviderKind)999);
    }
}

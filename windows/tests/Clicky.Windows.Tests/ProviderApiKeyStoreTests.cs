using Clicky.Windows.Providers;
using System.ComponentModel;

namespace Clicky.Windows.Tests;

[TestClass]
public sealed class ProviderApiKeyStoreTests
{
    [TestMethod]
    public void CredentialTargets_AreStableAndProviderSpecific()
    {
        Assert.AreEqual(
            "Clicky/ProviderApiKey/v1/anthropic",
            WindowsCredentialApiKeyStore.GetCredentialTargetName(AiProviderKind.Anthropic));
        Assert.AreEqual(
            "Clicky/ProviderApiKey/v1/openai",
            WindowsCredentialApiKeyStore.GetCredentialTargetName(AiProviderKind.OpenAI));
        Assert.AreEqual(
            "Clicky/ProviderApiKey/v1/gemini",
            WindowsCredentialApiKeyStore.GetCredentialTargetName(AiProviderKind.Gemini));
        Assert.AreEqual(
            "Clicky/ProviderApiKey/v1/elevenlabs",
            WindowsCredentialApiKeyStore.GetCredentialTargetName(AiProviderKind.ElevenLabs));
        Assert.AreEqual(
            "Knobnote/ProviderApiKey/v1/anthropic",
            WindowsCredentialApiKeyStore.GetLegacyCredentialTargetName(AiProviderKind.Anthropic));
        Assert.AreEqual(
            "Knobnote/ProviderApiKey/v1/openai",
            WindowsCredentialApiKeyStore.GetLegacyCredentialTargetName(AiProviderKind.OpenAI));
    }

    [TestMethod]
    public void CredentialTarget_RejectsWorkerAndUnknownProviders()
    {
        Assert.ThrowsExactly<ArgumentException>(
            () => WindowsCredentialApiKeyStore.GetCredentialTargetName(AiProviderKind.Worker));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => WindowsCredentialApiKeyStore.GetCredentialTargetName((AiProviderKind)999));
    }

    [TestMethod]
    public void ValidateApiKey_AcceptsNonBlankKeysForDirectProviders()
    {
        WindowsCredentialApiKeyStore.ValidateApiKey(AiProviderKind.Anthropic, "anthropic-key");
        WindowsCredentialApiKeyStore.ValidateApiKey(AiProviderKind.OpenAI, "openai-key");
        WindowsCredentialApiKeyStore.ValidateApiKey(AiProviderKind.Gemini, "gemini-key");
        WindowsCredentialApiKeyStore.ValidateApiKey(AiProviderKind.ElevenLabs, "elevenlabs-key");
    }

    [TestMethod]
    public void ValidateApiKey_RejectsWorkerBlankAndOversizedKeysWithoutEchoingSecrets()
    {
        const string secretFragment = "do-not-echo-this-secret";
        var oversizedKey = secretFragment + new string(
            'x',
            WindowsCredentialApiKeyStore.MaximumApiKeyByteLength);

        var workerException = Assert.ThrowsExactly<ArgumentException>(
            () => WindowsCredentialApiKeyStore.ValidateApiKey(
                AiProviderKind.Worker,
                secretFragment));
        var blankException = Assert.ThrowsExactly<ArgumentException>(
            () => WindowsCredentialApiKeyStore.ValidateApiKey(
                AiProviderKind.Anthropic,
                "   "));
        var oversizedException = Assert.ThrowsExactly<ArgumentException>(
            () => WindowsCredentialApiKeyStore.ValidateApiKey(
                AiProviderKind.OpenAI,
                oversizedKey));

        Assert.IsFalse(workerException.Message.Contains(secretFragment, StringComparison.Ordinal));
        Assert.IsFalse(blankException.Message.Contains("   ", StringComparison.Ordinal));
        Assert.IsFalse(oversizedException.Message.Contains(secretFragment, StringComparison.Ordinal));
    }

    [TestMethod]
    public void ValidateApiKey_UsesUtf8ByteLimit()
    {
        var multibyteOversizedKey = new string(
            '\u00e9',
            (WindowsCredentialApiKeyStore.MaximumApiKeyByteLength / 2) + 1);

        Assert.ThrowsExactly<ArgumentException>(
            () => WindowsCredentialApiKeyStore.ValidateApiKey(
                AiProviderKind.Anthropic,
                multibyteOversizedKey));
    }

    [TestMethod]
    public async Task GetApiKeyAsync_MigratesLegacyCredentialAfterSuccessfulWrite()
    {
        var operations = new FakeCredentialOperations();
        operations.Seed(LegacyTarget(AiProviderKind.Anthropic), "legacy-secret");
        var store = new WindowsCredentialApiKeyStore(operations);

        var apiKey = await store.GetApiKeyAsync(AiProviderKind.Anthropic);

        Assert.AreEqual("legacy-secret", apiKey);
        Assert.AreEqual("legacy-secret", operations.ValueAt(CurrentTarget(AiProviderKind.Anthropic)));
        Assert.IsNull(operations.ValueAt(LegacyTarget(AiProviderKind.Anthropic)));
        CollectionAssert.AreEqual(
            new[]
            {
                $"write:{CurrentTarget(AiProviderKind.Anthropic)}",
                $"delete:{LegacyTarget(AiProviderKind.Anthropic)}",
            },
            operations.Mutations);
    }

    [TestMethod]
    public async Task GetApiKeyAsync_MigrationWriteFailureKeepsLegacyAndStillReturnsKey()
    {
        var operations = new FakeCredentialOperations
        {
            WriteFailure = new Win32Exception(5, "simulated failure without a secret"),
        };
        operations.Seed(LegacyTarget(AiProviderKind.OpenAI), "legacy-secret");
        var store = new WindowsCredentialApiKeyStore(operations);

        var apiKey = await store.GetApiKeyAsync(AiProviderKind.OpenAI);

        Assert.AreEqual("legacy-secret", apiKey);
        Assert.IsNull(operations.ValueAt(CurrentTarget(AiProviderKind.OpenAI)));
        Assert.AreEqual("legacy-secret", operations.ValueAt(LegacyTarget(AiProviderKind.OpenAI)));
        CollectionAssert.AreEqual(
            new[] { $"write:{CurrentTarget(AiProviderKind.OpenAI)}" },
            operations.Mutations);
    }

    [TestMethod]
    public async Task HasApiKeyAsync_RecognizesAndMigratesLegacyCredential()
    {
        var operations = new FakeCredentialOperations();
        operations.Seed(LegacyTarget(AiProviderKind.OpenAI), "legacy-secret");
        var store = new WindowsCredentialApiKeyStore(operations);

        var hasApiKey = await store.HasApiKeyAsync(AiProviderKind.OpenAI);

        Assert.IsTrue(hasApiKey);
        Assert.AreEqual("legacy-secret", operations.ValueAt(CurrentTarget(AiProviderKind.OpenAI)));
        Assert.IsNull(operations.ValueAt(LegacyTarget(AiProviderKind.OpenAI)));
    }

    [TestMethod]
    public async Task DeleteApiKeyAsync_RemovesCurrentAndLegacyCredentialsIdempotently()
    {
        var operations = new FakeCredentialOperations();
        operations.Seed(CurrentTarget(AiProviderKind.Anthropic), "current-secret");
        operations.Seed(LegacyTarget(AiProviderKind.Anthropic), "legacy-secret");
        var store = new WindowsCredentialApiKeyStore(operations);

        await store.DeleteApiKeyAsync(AiProviderKind.Anthropic);
        await store.DeleteApiKeyAsync(AiProviderKind.Anthropic);

        Assert.IsNull(operations.ValueAt(CurrentTarget(AiProviderKind.Anthropic)));
        Assert.IsNull(operations.ValueAt(LegacyTarget(AiProviderKind.Anthropic)));
        CollectionAssert.AreEqual(
            new[]
            {
                $"delete:{CurrentTarget(AiProviderKind.Anthropic)}",
                $"delete:{LegacyTarget(AiProviderKind.Anthropic)}",
                $"delete:{CurrentTarget(AiProviderKind.Anthropic)}",
                $"delete:{LegacyTarget(AiProviderKind.Anthropic)}",
            },
            operations.Mutations);
    }

    [TestMethod]
    public async Task DeleteApiKeyAsync_CurrentDeleteFailureStillAttemptsLegacyDelete()
    {
        var operations = new FakeCredentialOperations
        {
            DeleteFailureTarget = CurrentTarget(AiProviderKind.OpenAI),
        };
        operations.Seed(CurrentTarget(AiProviderKind.OpenAI), "current-secret");
        operations.Seed(LegacyTarget(AiProviderKind.OpenAI), "legacy-secret");
        var store = new WindowsCredentialApiKeyStore(operations);

        var exception = await Assert.ThrowsExactlyAsync<Win32Exception>(
            () => store.DeleteApiKeyAsync(AiProviderKind.OpenAI));

        Assert.IsFalse(exception.Message.Contains("current-secret", StringComparison.Ordinal));
        Assert.AreEqual("current-secret", operations.ValueAt(CurrentTarget(AiProviderKind.OpenAI)));
        Assert.IsNull(operations.ValueAt(LegacyTarget(AiProviderKind.OpenAI)));
        CollectionAssert.AreEqual(
            new[]
            {
                $"delete:{CurrentTarget(AiProviderKind.OpenAI)}",
                $"delete:{LegacyTarget(AiProviderKind.OpenAI)}",
            },
            operations.Mutations);
    }

    private static string CurrentTarget(AiProviderKind provider) =>
        WindowsCredentialApiKeyStore.GetCredentialTargetName(provider);

    private static string LegacyTarget(AiProviderKind provider) =>
        WindowsCredentialApiKeyStore.GetLegacyCredentialTargetName(provider);

    private sealed class FakeCredentialOperations : IWindowsCredentialOperations
    {
        private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);

        public Win32Exception? WriteFailure { get; init; }

        public string? DeleteFailureTarget { get; init; }

        public string[] Mutations => mutations.ToArray();

        private readonly List<string> mutations = [];

        public void Seed(string targetName, string apiKey) => values[targetName] = apiKey;

        public string? ValueAt(string targetName) =>
            values.TryGetValue(targetName, out var value) ? value : null;

        public string? Read(AiProviderKind provider, string targetName) => ValueAt(targetName);

        public bool Exists(AiProviderKind provider, string targetName) => values.ContainsKey(targetName);

        public void Write(AiProviderKind provider, string targetName, string apiKey)
        {
            mutations.Add($"write:{targetName}");
            if (WriteFailure is not null)
            {
                throw WriteFailure;
            }

            values[targetName] = apiKey;
        }

        public void Delete(AiProviderKind provider, string targetName)
        {
            mutations.Add($"delete:{targetName}");
            if (string.Equals(DeleteFailureTarget, targetName, StringComparison.Ordinal))
            {
                throw new Win32Exception(5, "simulated failure without a secret");
            }

            values.Remove(targetName);
        }
    }
}

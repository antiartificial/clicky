using Clicky.Windows.Configuration;
using Clicky.Windows.Networking;
using Clicky.Windows.Providers;

namespace Clicky.Windows.Tests;

[TestClass]
public sealed class OpenAiStoredCredentialLiveTests
{
    [TestMethod]
    [TestCategory("Live")]
    public async Task StoredCredential_CanCompleteMinimalResponsesRequest()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("CLICKY_LIVE_OPENAI"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var settings = new CompanionSettingsStore().Load();
        using var client = OpenAiResponsesClient.CreateProduction(
            new WindowsCredentialApiKeyStore(),
            settings);
        var request = new WorkerChatRequest(
            "ignored-by-direct-client",
            "You are a connection check. Reply with exactly OK.",
            "Reply with exactly OK.",
            maxTokens: 32);

        try
        {
            var responseText = new System.Text.StringBuilder();
            await foreach (var delta in client.StreamChatAsync(request))
            {
                responseText.Append(delta);
            }

            Assert.IsFalse(
                string.IsNullOrWhiteSpace(responseText.ToString()),
                "OpenAI returned no text for the minimal connection check.");
        }
        catch (OpenAiProviderException exception)
        {
            var httpStatus = exception.StatusCode is null
                ? "none"
                : ((int)exception.StatusCode.Value).ToString(System.Globalization.CultureInfo.InvariantCulture);
            Assert.Fail(
                $"OpenAI doctor failed: category={exception.FailureKind}; " +
                $"http={httpStatus}; code={exception.ProviderCode ?? "none"}; " +
                $"request={exception.RequestId ?? "none"}.");
        }
    }
}

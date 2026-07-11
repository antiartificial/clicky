using System.Runtime.CompilerServices;
using Clicky.Windows.Branding;
using Clicky.Windows.Capture;
using Clicky.Windows.Configuration;
using Clicky.Windows.Interaction;
using Clicky.Windows.Networking;
using Clicky.Windows.Overlay;
using Clicky.Windows.Pointing;
using Clicky.Windows.Providers;
using Clicky.Windows.Session;
using Clicky.Windows.ViewModels;

namespace Clicky.Windows.Tests;

[TestClass]
public sealed class ProviderSettingsViewModelTests
{
    [TestMethod]
    public async Task SelectProviderAsync_CancelsPreparedInteractionAndClearsHistory()
    {
        var keyStore = new FakeProviderApiKeyStore();
        var (viewModel, interactionService) = CreateViewModel(keyStore);

        await viewModel.BeginQuestionEntryAsync();
        viewModel.Question = "first";
        await viewModel.SubmitQuestionAsync();
        Assert.AreEqual(1, interactionService.ConversationTurnCount);
        Assert.HasCount(1, interactionService.GetConversationHistorySnapshot());
        Assert.HasCount(1, viewModel.ConversationTurns);
        Assert.AreEqual("first", viewModel.ConversationTurns[0].UserText);
        Assert.AreEqual("Try this.", viewModel.ConversationTurns[0].AssistantText);

        await viewModel.BeginQuestionEntryAsync();
        Assert.IsTrue(viewModel.IsQuestionEntryVisible);
        await viewModel.SelectProviderAsync(AiProviderKind.OpenAI);

        Assert.AreEqual(AiProviderKind.OpenAI, viewModel.SelectedProvider);
        Assert.AreEqual(CompanionSessionState.Idle, viewModel.State);
        Assert.IsFalse(viewModel.IsQuestionEntryVisible);
        Assert.AreEqual(0, interactionService.ConversationTurnCount);
        Assert.HasCount(0, interactionService.GetConversationHistorySnapshot());
        Assert.HasCount(0, viewModel.ConversationTurns);
        Assert.IsFalse(viewModel.HasConversation);
        Assert.IsTrue(viewModel.IsConversationEmpty);
    }

    [TestMethod]
    public async Task KeyStatusSaveAndRemove_ExposeNoSecretText()
    {
        const string secret = "secret-value-never-display";
        var keyStore = new FakeProviderApiKeyStore();
        var (viewModel, _) = CreateViewModel(keyStore);

        await viewModel.SelectProviderAsync(AiProviderKind.Anthropic);
        Assert.AreEqual(BrandText.ApiKeyNotStored, viewModel.ApiKeyStatusText);

        await viewModel.SaveSelectedProviderApiKeyAsync(secret);

        Assert.AreEqual(secret, keyStore.LastSavedKey);
        Assert.IsTrue(viewModel.HasStoredApiKey);
        Assert.AreEqual(BrandText.ApiKeyStored, viewModel.ApiKeyStatusText);
        Assert.IsFalse(viewModel.ApiKeyStatusText.Contains(secret, StringComparison.Ordinal));

        await viewModel.DeleteSelectedProviderApiKeyAsync();
        await viewModel.DeleteSelectedProviderApiKeyAsync();

        Assert.IsFalse(viewModel.HasStoredApiKey);
        Assert.AreEqual(BrandText.ApiKeyNotStored, viewModel.ApiKeyStatusText);
        Assert.AreEqual(2, keyStore.DeleteCount);
    }

    [TestMethod]
    public async Task ProviderSwitch_RefreshesProviderSpecificStoredStatus()
    {
        var keyStore = new FakeProviderApiKeyStore();
        keyStore.StoredProviders.Add(AiProviderKind.OpenAI);
        var (viewModel, _) = CreateViewModel(keyStore);

        await viewModel.SelectProviderAsync(AiProviderKind.Anthropic);
        Assert.IsFalse(viewModel.HasStoredApiKey);

        await viewModel.SelectProviderAsync(AiProviderKind.OpenAI);
        Assert.IsTrue(viewModel.HasStoredApiKey);
        Assert.AreEqual(BrandText.ApiKeyStored, viewModel.ApiKeyStatusText);
    }

    [TestMethod]
    public async Task SaveOpenAIKey_DiscoversValidatesAndPersistsReadyState()
    {
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "ClickyOnboardingTests", Guid.NewGuid().ToString("N"));
        var settingsPath = Path.Combine(temporaryDirectory, "settings.json");
        try
        {
            var keyStore = new FakeProviderApiKeyStore();
            var settings = new CompanionSettings();
            var settingsStore = new CompanionSettingsStore(settingsPath);
            var onboarding = new FakeOpenAiOnboardingClient((preferredModel, _) =>
            {
                Assert.IsNull(preferredModel);
                return Task.FromResult(new OpenAiOnboardingResult(
                    ["gpt-4.1-mini", "gpt-5.4-mini"],
                    "gpt-5.4-mini",
                    "gpt-5.4-mini",
                    new DateTimeOffset(2026, 7, 11, 18, 0, 0, TimeSpan.Zero)));
            });
            var (viewModel, _) = CreateViewModel(keyStore, settings, onboarding, settingsStore);

            await viewModel.SaveSelectedProviderApiKeyAsync("sk-test");

            Assert.AreEqual(1, onboarding.Calls);
            Assert.IsTrue(viewModel.HasStoredApiKey);
            Assert.IsTrue(viewModel.HasDiscoveredOpenAIModels);
            Assert.IsTrue(viewModel.IsOpenAIOnboarded);
            Assert.AreEqual(BrandText.OpenAIReady, viewModel.OpenAIOnboardingStatusText);
            Assert.AreEqual("gpt-5.4-mini", settings.OpenAIModelId);
            CollectionAssert.AreEqual(
                new[] { "gpt-4.1-mini", "gpt-5.4-mini" },
                viewModel.AvailableOpenAIModels.ToArray());

            var persisted = settingsStore.Load();
            Assert.AreEqual("gpt-5.4-mini", persisted.OpenAIValidatedModelId);
            Assert.AreEqual(settings.OpenAIValidatedAtUtc, persisted.OpenAIValidatedAtUtc);

            settings.OpenAIModelId = "gpt-4.1-mini";
            Assert.IsFalse(viewModel.IsOpenAIOnboarded);
            Assert.AreEqual(string.Empty, settings.OpenAIValidatedModelId);
            Assert.IsNull(settings.OpenAIValidatedAtUtc);
            Assert.AreEqual("Model changed", viewModel.OpenAIOnboardingStatusText);
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
    public async Task SaveOpenAIKey_PermissionFailureDoesNotClaimReady()
    {
        var keyStore = new FakeProviderApiKeyStore();
        var onboarding = new FakeOpenAiOnboardingClient((_, _) =>
            Task.FromException<OpenAiOnboardingResult>(new OpenAiProviderException(
                OpenAiProviderFailureKind.Permission,
                "safe failure")));
        var (viewModel, _) = CreateViewModel(
            keyStore,
            new CompanionSettings(),
            onboarding);

        await viewModel.SaveSelectedProviderApiKeyAsync("sk-test");

        Assert.IsTrue(viewModel.HasStoredApiKey);
        Assert.IsFalse(viewModel.IsOpenAIOnboarded);
        Assert.AreEqual("Connection needs attention", viewModel.OpenAIOnboardingStatusText);
        StringAssert.Contains(viewModel.OpenAIOnboardingDetailText, "Models read access");
    }

    [TestMethod]
    public async Task StoredOpenAIKey_EmptyConnectInputTestsWithoutReplacingCredential()
    {
        var keyStore = new FakeProviderApiKeyStore();
        keyStore.StoredProviders.Add(AiProviderKind.OpenAI);
        var onboarding = new FakeOpenAiOnboardingClient((preferredModel, _) =>
        {
            Assert.AreEqual(CompanionSettings.DefaultOpenAIModelId, preferredModel);
            return Task.FromResult(new OpenAiOnboardingResult(
                [CompanionSettings.DefaultOpenAIModelId],
                CompanionSettings.DefaultOpenAIModelId,
                CompanionSettings.DefaultOpenAIModelId,
                DateTimeOffset.UtcNow));
        });
        var (viewModel, _) = CreateViewModel(
            keyStore,
            new CompanionSettings(),
            onboarding);

        await viewModel.SelectProviderAsync(AiProviderKind.OpenAI);
        viewModel.SetSettingsVisible(true);
        Assert.AreEqual(BrandText.OpenAITestStoredLabel, viewModel.SaveApiKeyButtonText);

        viewModel.SetProviderApiKeyEntryAvailable(true);
        Assert.AreEqual(BrandText.OpenAIReplaceAndTestLabel, viewModel.SaveApiKeyButtonText);
        viewModel.SetProviderApiKeyEntryAvailable(false);

        await viewModel.ConnectOrTestSelectedProviderAsync(string.Empty);

        Assert.AreEqual(1, onboarding.Calls);
        Assert.IsNull(keyStore.LastSavedKey);
        Assert.IsTrue(viewModel.IsOpenAIOnboarded);
    }

    [TestMethod]
    public async Task Settings_CanOpenAfterResponseButNotDuringActiveWork()
    {
        var (viewModel, _) = CreateViewModel(new FakeProviderApiKeyStore());

        await viewModel.BeginQuestionEntryAsync();
        viewModel.SetSettingsVisible(true);
        Assert.IsFalse(viewModel.IsSettingsVisible);

        viewModel.Question = "done now";
        await viewModel.SubmitQuestionAsync();
        Assert.AreEqual(CompanionSessionState.Responding, viewModel.State);

        viewModel.SetSettingsVisible(true);

        Assert.IsTrue(viewModel.IsSettingsVisible);
        Assert.AreEqual(CompanionSessionState.Idle, viewModel.State);
        Assert.IsFalse(viewModel.IsQuestionEntryVisible);
    }

    [TestMethod]
    public void ConversationVisibility_ClosesSettingsAndRestoresAskCommand()
    {
        var (viewModel, _) = CreateViewModel(new FakeProviderApiKeyStore());

        viewModel.SetSettingsVisible(true);
        Assert.IsTrue(viewModel.IsSettingsVisible);
        Assert.IsFalse(viewModel.AdvanceSessionCommand.CanExecute(null));

        viewModel.SetConversationVisible(true);

        Assert.IsFalse(viewModel.IsSettingsVisible);
        Assert.IsTrue(viewModel.IsConversationVisible);
        Assert.IsTrue(viewModel.AdvanceSessionCommand.CanExecute(null));

        viewModel.SetConversationVisible(false);
        Assert.IsFalse(viewModel.IsConversationVisible);
    }

    private static (CompanionViewModel ViewModel, TutorInteractionService InteractionService)
        CreateViewModel(
            FakeProviderApiKeyStore keyStore,
            CompanionSettings? settings = null,
            IOpenAiOnboardingClient? openAiOnboardingClient = null,
            CompanionSettingsStore? settingsStore = null)
    {
        var interactionService = new TutorInteractionService(
            new FakeCaptureService(),
            new FakeWorkerClient());
        var viewModel = new CompanionViewModel(
            new CompanionSessionCoordinator(),
            settings ?? new CompanionSettings(),
            interactionService,
            new FakePointCuePresenter(),
            keyStore,
            openAiOnboardingClient: openAiOnboardingClient,
            settingsStore: settingsStore);
        return (viewModel, interactionService);
    }

    private sealed class FakeProviderApiKeyStore : IProviderApiKeyStore
    {
        public HashSet<AiProviderKind> StoredProviders { get; } = [];
        public string? LastSavedKey { get; private set; }
        public int DeleteCount { get; private set; }

        public Task<string?> GetApiKeyAsync(
            AiProviderKind provider,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(StoredProviders.Contains(provider) ? "stored" : null);

        public Task SaveApiKeyAsync(
            AiProviderKind provider,
            string apiKey,
            CancellationToken cancellationToken = default)
        {
            LastSavedKey = apiKey;
            StoredProviders.Add(provider);
            return Task.CompletedTask;
        }

        public Task DeleteApiKeyAsync(
            AiProviderKind provider,
            CancellationToken cancellationToken = default)
        {
            DeleteCount++;
            StoredProviders.Remove(provider);
            return Task.CompletedTask;
        }

        public Task<bool> HasApiKeyAsync(
            AiProviderKind provider,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(StoredProviders.Contains(provider));
    }

    private sealed class FakeOpenAiOnboardingClient(
        Func<string?, CancellationToken, Task<OpenAiOnboardingResult>> run)
        : IOpenAiOnboardingClient
    {
        public int Calls { get; private set; }

        public Task<OpenAiOnboardingResult> DiscoverAndValidateAsync(
            string? preferredModelId = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return run(preferredModelId, cancellationToken);
        }
    }

    private sealed class FakeCaptureService : IActiveWindowCaptureService
    {
        public Task<CaptureResult> CaptureAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new CaptureResult(
                [0xFF, 0xD8, 0xFF, 0xD9],
                800,
                600,
                new PhysicalPixelBounds(0, 0, 800, 600),
                "Rive"));
    }

    private sealed class FakeWorkerClient : IWorkerClient
    {
        public async IAsyncEnumerable<string> StreamChatAsync(
            WorkerChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return "Try this. [POINT:none]";
            await Task.CompletedTask;
        }
    }

    private sealed class FakePointCuePresenter : IPointCuePresenter
    {
        public Task ShowAsync(DesktopPoint point, string? label = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateAsync(DesktopPoint point, string? label = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task HideAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

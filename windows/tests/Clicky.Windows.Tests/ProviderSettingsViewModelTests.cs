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

        await viewModel.BeginQuestionEntryAsync();
        Assert.IsTrue(viewModel.IsQuestionEntryVisible);
        await viewModel.SelectProviderAsync(AiProviderKind.OpenAI);

        Assert.AreEqual(AiProviderKind.OpenAI, viewModel.SelectedProvider);
        Assert.AreEqual(CompanionSessionState.Idle, viewModel.State);
        Assert.IsFalse(viewModel.IsQuestionEntryVisible);
        Assert.AreEqual(0, interactionService.ConversationTurnCount);
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

    private static (CompanionViewModel ViewModel, TutorInteractionService InteractionService)
        CreateViewModel(FakeProviderApiKeyStore keyStore)
    {
        var interactionService = new TutorInteractionService(
            new FakeCaptureService(),
            new FakeWorkerClient());
        var viewModel = new CompanionViewModel(
            new CompanionSessionCoordinator(),
            new CompanionSettings(),
            interactionService,
            new FakePointCuePresenter(),
            keyStore);
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

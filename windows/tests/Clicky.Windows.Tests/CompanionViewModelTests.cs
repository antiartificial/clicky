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
public sealed class CompanionViewModelTests
{
    [TestMethod]
    public void BrandText_UsesGenericClickyLanguage()
    {
        Assert.AreEqual("Clicky", BrandText.ApplicationName);
        StringAssert.Contains(BrandText.CompanionWindowTitle, "Clicky");
        StringAssert.Contains(BrandText.IdleDetail, "active app");
        StringAssert.Contains(BrandText.QuestionInputLabel, "active app");
    }

    [TestMethod]
    public async Task BeginQuestionEntryAsync_CapturesBeforeRequestingWindowFocus()
    {
        var calls = new List<string>();
        var viewModel = CreateViewModel(
            new FakeCaptureService((_) =>
            {
                calls.Add("capture");
                return Task.FromResult(CreateCapture());
            }),
            new FakeWorkerClient((_, _) => Stream("unused [POINT:none]")));
        viewModel.QuestionEntryReady += (_, _) => calls.Add("focus");

        await viewModel.BeginQuestionEntryAsync();

        CollectionAssert.AreEqual(new[] { "capture", "focus" }, calls);
        Assert.AreEqual(CompanionSessionState.Listening, viewModel.State);
        Assert.IsTrue(viewModel.IsQuestionEntryVisible);
        Assert.AreEqual(new CompanionInteractionId(1), viewModel.CurrentInteractionId);
    }

    [TestMethod]
    public async Task SubmitQuestionAsync_UsesPreparedCaptureAndKeepsMappedPoint()
    {
        var captureCount = 0;
        WorkerChatRequest? request = null;
        var presenter = new FakePointCuePresenter();
        var viewModel = CreateViewModel(
            new FakeCaptureService((_) =>
            {
                captureCount++;
                return Task.FromResult(CreateCapture());
            }),
            new FakeWorkerClient((sentRequest, _) =>
            {
                request = sentRequest;
                return Stream("Open Solution Explorer. [POINT:400,300:solution explorer]");
            }),
            presenter);
        var compactRequestCount = 0;
        viewModel.CompactStateRequested += (_, _) => compactRequestCount++;

        await viewModel.BeginQuestionEntryAsync();
        viewModel.Question = "Where is Solution Explorer?";
        await viewModel.SubmitQuestionAsync();

        Assert.AreEqual(1, captureCount);
        Assert.AreEqual("Where is Solution Explorer?", request?.UserPrompt);
        Assert.AreEqual(CompanionSessionState.Responding, viewModel.State);
        Assert.IsFalse(viewModel.IsQuestionEntryVisible);
        Assert.AreEqual("Open Solution Explorer.", viewModel.ResponseText);
        Assert.AreEqual(new DesktopPoint(400, 300), viewModel.LastMappedDesktopPoint);
        Assert.AreEqual("solution explorer", viewModel.LastInteractionResult?.TargetLabel);
        CollectionAssert.AreEqual(
            new[] { "hide", "show:400,300:solution explorer" },
            presenter.Calls);
        Assert.IsGreaterThanOrEqualTo(1, compactRequestCount);
    }

    [TestMethod]
    public async Task NewInteractionNoPointAndCancel_HideExistingCue()
    {
        var responseNumber = 0;
        var presenter = new FakePointCuePresenter();
        var viewModel = CreateViewModel(
            new FakeCaptureService((_) => Task.FromResult(CreateCapture())),
            new FakeWorkerClient((_, _) => ++responseNumber == 1
                ? Stream("Use this. [POINT:200,150:toolbar button]")
                : Stream("Nothing to point at. [POINT:none]")),
            presenter);

        await viewModel.BeginQuestionEntryAsync();
        viewModel.Question = "first";
        await viewModel.SubmitQuestionAsync();
        Assert.AreEqual("show:200,150:toolbar button", presenter.Calls[^1]);

        await viewModel.BeginQuestionEntryAsync();
        Assert.AreEqual("hide", presenter.Calls[^1]);
        viewModel.Question = "second";
        await viewModel.SubmitQuestionAsync();
        Assert.AreEqual("hide", presenter.Calls[^1]);

        await viewModel.CancelCurrentInteractionAsync();
        Assert.AreEqual("hide", presenter.Calls[^1]);
    }

    [TestMethod]
    public async Task DelayedStaleCompletion_CannotOverwriteNewerInteraction()
    {
        var oldRequestStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOldRequest = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var presenter = new FakePointCuePresenter();
        var viewModel = CreateViewModel(
            new FakeCaptureService((_) => Task.FromResult(CreateCapture())),
            new FakeWorkerClient((request, _) => request.UserPrompt == "old"
                ? DelayedResponse(oldRequestStarted, releaseOldRequest)
                : Stream("new answer [POINT:300,225:new target]")),
            presenter);

        await viewModel.BeginQuestionEntryAsync();
        viewModel.Question = "old";
        var oldSubmission = viewModel.SubmitQuestionAsync();
        await oldRequestStarted.Task;

        await viewModel.CancelCurrentInteractionAsync();
        await viewModel.BeginQuestionEntryAsync();
        var currentInteractionId = viewModel.CurrentInteractionId;
        viewModel.Question = "new";
        await viewModel.SubmitQuestionAsync();
        var cueCallsBeforeStaleCompletion = presenter.Calls.ToArray();

        releaseOldRequest.SetResult();
        await oldSubmission;

        Assert.AreEqual(currentInteractionId, viewModel.CurrentInteractionId);
        Assert.AreEqual(CompanionSessionState.Responding, viewModel.State);
        Assert.AreEqual("new answer", viewModel.ResponseText);
        Assert.AreEqual("show:300,225:new target", presenter.Calls[^1]);
        CollectionAssert.AreEqual(cueCallsBeforeStaleCompletion, presenter.Calls);
    }

    [TestMethod]
    public async Task WorkerConfigurationFailure_ShowsUsefulSettingsState()
    {
        var viewModel = CreateViewModel(
            new FakeCaptureService((_) => Task.FromResult(CreateCapture())),
            new FakeWorkerClient((_, _) => MissingWorkerUrl()));

        await viewModel.BeginQuestionEntryAsync();
        viewModel.Question = "Help";
        await viewModel.SubmitQuestionAsync();

        Assert.AreEqual(CompanionSessionState.Responding, viewModel.State);
        Assert.AreEqual(BrandText.WorkerSetupStatus, viewModel.StatusText);
        StringAssert.Contains(viewModel.ResponseText, "Worker URL");
    }

    [TestMethod]
    public async Task OpenAIAuthenticationFailure_ShowsSafeKeyReplacementMessage()
    {
        const string providerBody = "raw-provider-secret-body";
        var viewModel = CreateViewModel(
            new FakeCaptureService((_) => Task.FromResult(CreateCapture())),
            new FakeWorkerClient((_, _) => OpenAIAuthenticationFailure(providerBody)));

        await viewModel.BeginQuestionEntryAsync();
        viewModel.Question = "Help";
        await viewModel.SubmitQuestionAsync();

        Assert.AreEqual(BrandText.ProviderSetupStatus, viewModel.StatusText);
        StringAssert.Contains(viewModel.ResponseText, "rejected the stored key");
        Assert.IsFalse(viewModel.ResponseText.Contains(providerBody, StringComparison.Ordinal));
    }

    private static CompanionViewModel CreateViewModel(
        IActiveWindowCaptureService captureService,
        IWorkerClient workerClient,
        IPointCuePresenter? pointCuePresenter = null)
    {
        var settings = new CompanionSettings();
        var coordinator = new CompanionSessionCoordinator();
        var interactionService = new TutorInteractionService(captureService, workerClient);
        return new CompanionViewModel(
            coordinator,
            settings,
            interactionService,
            pointCuePresenter ?? new FakePointCuePresenter(),
            new FakeProviderApiKeyStore());
    }

    private static CaptureResult CreateCapture() =>
        new(
            [0xFF, 0xD8, 0xFF, 0xD9],
            800,
            600,
            new PhysicalPixelBounds(0, 0, 800, 600),
            "Visual Studio");

    private static async IAsyncEnumerable<string> Stream(params string[] chunks)
    {
        foreach (var chunk in chunks)
        {
            yield return chunk;
        }

        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<string> DelayedResponse(
        TaskCompletionSource started,
        TaskCompletionSource release)
    {
        started.SetResult();
        await release.Task;
        yield return "old answer [POINT:100,75:old target]";
    }

    private static async IAsyncEnumerable<string> MissingWorkerUrl(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        throw new WorkerConfigurationException(
            "Set your Worker URL in Clicky settings before asking a question.");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    private static async IAsyncEnumerable<string> OpenAIAuthenticationFailure(
        string providerBody)
    {
        await Task.Yield();
        throw new OpenAiProviderException(
            OpenAiProviderFailureKind.Authentication,
            providerBody);
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    private sealed class FakeCaptureService(
        Func<CancellationToken, Task<CaptureResult>> capture) : IActiveWindowCaptureService
    {
        public Task<CaptureResult> CaptureAsync(CancellationToken cancellationToken = default) =>
            capture(cancellationToken);
    }

    private sealed class FakeWorkerClient(
        Func<WorkerChatRequest, CancellationToken, IAsyncEnumerable<string>> stream) : IWorkerClient
    {
        public IAsyncEnumerable<string> StreamChatAsync(
            WorkerChatRequest request,
            CancellationToken cancellationToken = default) =>
            stream(request, cancellationToken);
    }

    private sealed class FakePointCuePresenter : IPointCuePresenter
    {
        public List<string> Calls { get; } = [];

        public Task ShowAsync(
            DesktopPoint point,
            string? label = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"show:{point.X:0.##},{point.Y:0.##}:{label}");
            return Task.CompletedTask;
        }

        public Task UpdateAsync(
            DesktopPoint point,
            string? label = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"update:{point.X:0.##},{point.Y:0.##}:{label}");
            return Task.CompletedTask;
        }

        public Task HideAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("hide");
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeProviderApiKeyStore : IProviderApiKeyStore
    {
        public Task<string?> GetApiKeyAsync(
            AiProviderKind provider,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task SaveApiKeyAsync(
            AiProviderKind provider,
            string apiKey,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DeleteApiKeyAsync(
            AiProviderKind provider,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> HasApiKeyAsync(
            AiProviderKind provider,
            CancellationToken cancellationToken = default) => Task.FromResult(false);
    }
}

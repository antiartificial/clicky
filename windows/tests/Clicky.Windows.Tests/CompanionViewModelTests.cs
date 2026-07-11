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
using Clicky.Windows.Voice;

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
    public async Task SubmitFollowUpAsync_ReusesScreenAndAppendsToActiveConversation()
    {
        var captureCount = 0;
        var requests = new List<WorkerChatRequest>();
        var viewModel = CreateViewModel(
            new FakeCaptureService((_) =>
            {
                captureCount++;
                return Task.FromResult(CreateCapture());
            }),
            new FakeWorkerClient((request, _) =>
            {
                requests.Add(request);
                return Stream(requests.Count == 1
                    ? "Open Solution Explorer. [POINT:400,300:solution explorer]"
                    : "Right-click the project. [POINT:350,340:project]"
                );
            }));

        await viewModel.BeginQuestionEntryAsync();
        viewModel.Question = "Where is the project?";
        await viewModel.SubmitQuestionAsync();

        viewModel.SetConversationVisible(true);
        viewModel.FollowUpQuestion = "What do I do next?";
        Assert.IsTrue(viewModel.SubmitFollowUpCommand.CanExecute(null));

        await viewModel.SubmitFollowUpAsync();

        Assert.AreEqual(1, captureCount);
        Assert.HasCount(2, requests);
        Assert.HasCount(1, requests[1].ConversationHistory);
        Assert.AreEqual("Where is the project?", requests[1].ConversationHistory[0].UserText);
        Assert.AreEqual(string.Empty, viewModel.FollowUpQuestion);
        Assert.HasCount(2, viewModel.ConversationTurns);
        Assert.AreEqual("What do I do next?", viewModel.ConversationTurns[1].UserText);
        Assert.AreEqual("Right-click the project.", viewModel.ConversationTurns[1].AssistantText);
        Assert.AreEqual(CompanionSessionState.Responding, viewModel.State);
    }

    [TestMethod]
    public async Task SubmitFollowUpAsync_ProviderFailureRestoresDraft()
    {
        var requestCount = 0;
        var viewModel = CreateViewModel(
            new FakeCaptureService((_) => Task.FromResult(CreateCapture())),
            new FakeWorkerClient((_, _) => ++requestCount == 1
                ? Stream("Open Solution Explorer. [POINT:400,300:solution explorer]")
                : OpenAIAuthenticationFailure("provider failure")));

        await viewModel.BeginQuestionEntryAsync();
        viewModel.Question = "Where is the project?";
        await viewModel.SubmitQuestionAsync();
        viewModel.SetConversationVisible(true);
        viewModel.FollowUpQuestion = "What should I click next?";

        await viewModel.SubmitFollowUpAsync();

        Assert.AreEqual("What should I click next?", viewModel.FollowUpQuestion);
        Assert.HasCount(1, viewModel.ConversationTurns);
        Assert.AreEqual(BrandText.ProviderSetupStatus, viewModel.StatusText);
        Assert.IsTrue(viewModel.SubmitFollowUpCommand.CanExecute(null));
    }

    [TestMethod]
    public async Task SubmitFollowUpAsync_CueHideFailureStillCompletesResponse()
    {
        var requestCount = 0;
        var presenter = new FakePointCuePresenter(failOnHideCall: 2);
        var viewModel = CreateViewModel(
            new FakeCaptureService((_) => Task.FromResult(CreateCapture())),
            new FakeWorkerClient((_, _) => ++requestCount == 1
                ? Stream("Open Solution Explorer. [POINT:400,300:solution explorer]")
                : Stream("Select the project. [POINT:350,340:project]")),
            presenter);

        await viewModel.BeginQuestionEntryAsync();
        viewModel.Question = "Where is the project?";
        await viewModel.SubmitQuestionAsync();
        viewModel.SetConversationVisible(true);
        viewModel.FollowUpQuestion = "What next?";

        await viewModel.SubmitFollowUpAsync();

        Assert.HasCount(2, viewModel.ConversationTurns);
        Assert.AreEqual("Select the project.", viewModel.ResponseText);
        Assert.AreEqual(CompanionSessionState.Responding, viewModel.State);
    }

    [TestMethod]
    public async Task SubmitQuestionAsync_ProjectsCompletedTurnWhileActiveQuestionIsTransient()
    {
        var responseStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseResponse = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var viewModel = CreateViewModel(
            new FakeCaptureService((_) => Task.FromResult(CreateCapture())),
            new FakeWorkerClient((_, cancellationToken) => DelayedResponse(
                responseStarted,
                releaseResponse,
                "Open the command palette. [POINT:200,100:command palette]",
                cancellationToken)));

        Assert.IsTrue(viewModel.IsConversationEmpty);
        await viewModel.BeginQuestionEntryAsync();
        viewModel.Question = "  How do I run a command?  ";
        var submission = viewModel.SubmitQuestionAsync();
        await responseStarted.Task;

        Assert.IsTrue(viewModel.HasActiveQuestion);
        Assert.AreEqual("How do I run a command?", viewModel.ActiveQuestionText);
        Assert.HasCount(0, viewModel.ConversationTurns);
        Assert.IsFalse(viewModel.IsConversationEmpty);

        releaseResponse.SetResult();
        await submission;

        Assert.IsFalse(viewModel.HasActiveQuestion);
        Assert.AreEqual(string.Empty, viewModel.ActiveQuestionText);
        Assert.IsTrue(viewModel.HasConversation);
        Assert.HasCount(1, viewModel.ConversationTurns);
        Assert.AreEqual("How do I run a command?", viewModel.ConversationTurns[0].UserText);
        Assert.AreEqual("Open the command palette.", viewModel.ConversationTurns[0].AssistantText);
        Assert.IsFalse(viewModel.ConversationTurns[0].AssistantText.Contains(
            "[POINT",
            StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task CancelCurrentInteractionAsync_ClearsActiveQuestionWithoutCommittingTurn()
    {
        var responseStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseResponse = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var (viewModel, interactionService) = CreateViewModelWithService(
            new FakeCaptureService((_) => Task.FromResult(CreateCapture())),
            new FakeWorkerClient((_, cancellationToken) => DelayedResponse(
                responseStarted,
                releaseResponse,
                "This answer should be canceled. [POINT:none]",
                cancellationToken)));

        await viewModel.BeginQuestionEntryAsync();
        viewModel.Question = "Cancel this question";
        var submission = viewModel.SubmitQuestionAsync();
        await responseStarted.Task;
        Assert.AreEqual("Cancel this question", viewModel.ActiveQuestionText);

        await viewModel.CancelCurrentInteractionAsync();
        await submission;

        Assert.IsFalse(viewModel.HasActiveQuestion);
        Assert.HasCount(0, viewModel.ConversationTurns);
        Assert.HasCount(0, interactionService.GetConversationHistorySnapshot());
        Assert.IsTrue(viewModel.IsConversationEmpty);
        Assert.AreEqual(CompanionSessionState.Idle, viewModel.State);
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
        var responseStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseResponse = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var (viewModel, interactionService) = CreateViewModelWithService(
            new FakeCaptureService((_) => Task.FromResult(CreateCapture())),
            new FakeWorkerClient((_, cancellationToken) => MissingWorkerUrl(
                responseStarted,
                releaseResponse,
                cancellationToken)));

        await viewModel.BeginQuestionEntryAsync();
        viewModel.Question = "Help";
        var submission = viewModel.SubmitQuestionAsync();
        await responseStarted.Task;
        Assert.AreEqual("Help", viewModel.ActiveQuestionText);

        releaseResponse.SetResult();
        await submission;

        Assert.AreEqual(CompanionSessionState.Responding, viewModel.State);
        Assert.AreEqual(BrandText.WorkerSetupStatus, viewModel.StatusText);
        StringAssert.Contains(viewModel.ResponseText, "Worker URL");
        Assert.IsFalse(viewModel.HasActiveQuestion);
        Assert.HasCount(0, viewModel.ConversationTurns);
        Assert.HasCount(0, interactionService.GetConversationHistorySnapshot());
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

    [TestMethod]
    public async Task BeginVoiceInteractionAsync_StartsDictationAndCapturesWithoutRequestingFocus()
    {
        var calls = new List<string>();
        var dictationTranscriber = new FakeDictationTranscriber(
            start: (_) =>
            {
                calls.Add("start-dictation");
                return Task.CompletedTask;
            });
        using var viewModel = CreateViewModel(
            new FakeCaptureService((_) =>
            {
                calls.Add("capture");
                return Task.FromResult(CreateCapture());
            }),
            new FakeWorkerClient((_, _) => Stream("unused [POINT:none]")),
            dictationTranscriber: dictationTranscriber);
        var focusRequestCount = 0;
        viewModel.QuestionEntryReady += (_, _) => focusRequestCount++;

        await viewModel.BeginVoiceInteractionAsync();

        CollectionAssert.AreEqual(new[] { "start-dictation", "capture" }, calls);
        Assert.AreEqual(0, focusRequestCount);
        Assert.IsFalse(viewModel.IsQuestionEntryVisible);
        Assert.AreEqual(CompanionSessionState.Listening, viewModel.State);
        Assert.IsTrue(dictationTranscriber.IsTranscribing);

        await viewModel.CancelCurrentInteractionAsync();
    }

    [TestMethod]
    public async Task CompleteVoiceInteractionAsync_UsesOnePreparedCaptureAndTranscript()
    {
        var captureCount = 0;
        WorkerChatRequest? request = null;
        var responseStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseResponse = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var dictationTranscriber = new FakeDictationTranscriber(
            stop: (_) => Task.FromResult("  Where is the save button?  "));
        using var viewModel = CreateViewModel(
            new FakeCaptureService((_) =>
            {
                captureCount++;
                return Task.FromResult(CreateCapture());
            }),
            new FakeWorkerClient((sentRequest, cancellationToken) =>
            {
                request = sentRequest;
                return DelayedResponse(
                    responseStarted,
                    releaseResponse,
                    "Use the toolbar save button. [POINT:320,180:save button]",
                    cancellationToken);
            }),
            dictationTranscriber: dictationTranscriber);

        await viewModel.BeginVoiceInteractionAsync();
        var completion = viewModel.CompleteVoiceInteractionAsync();
        await responseStarted.Task;

        Assert.AreEqual("Where is the save button?", viewModel.ActiveQuestionText);
        Assert.IsTrue(viewModel.HasActiveQuestion);
        Assert.HasCount(0, viewModel.ConversationTurns);

        releaseResponse.SetResult();
        await completion;

        Assert.AreEqual(1, captureCount);
        Assert.AreEqual(1, dictationTranscriber.StartCount);
        Assert.AreEqual(1, dictationTranscriber.StopCount);
        Assert.AreEqual("Where is the save button?", request?.UserPrompt);
        Assert.AreEqual(CompanionSessionState.Responding, viewModel.State);
        Assert.AreEqual("Use the toolbar save button.", viewModel.ResponseText);
        Assert.IsFalse(viewModel.HasActiveQuestion);
        Assert.HasCount(1, viewModel.ConversationTurns);
        Assert.AreEqual("Where is the save button?", viewModel.ConversationTurns[0].UserText);
        Assert.AreEqual("Use the toolbar save button.", viewModel.ConversationTurns[0].AssistantText);
    }

    [TestMethod]
    public async Task CompleteVoiceInteractionAsync_ReleaseBeforeCaptureCompletesStillUsesCapture()
    {
        var captureStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var captureCompletion = new TaskCompletionSource<CaptureResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        WorkerChatRequest? request = null;
        var dictationTranscriber = new FakeDictationTranscriber(
            stop: (_) => Task.FromResult("What should I click?"));
        using var viewModel = CreateViewModel(
            new FakeCaptureService((_) =>
            {
                captureStarted.SetResult();
                return captureCompletion.Task;
            }),
            new FakeWorkerClient((sentRequest, _) =>
            {
                request = sentRequest;
                return Stream("Click Run. [POINT:500,100:run button]");
            }),
            dictationTranscriber: dictationTranscriber);

        await viewModel.BeginVoiceInteractionAsync();
        await captureStarted.Task;
        var completion = viewModel.CompleteVoiceInteractionAsync();

        Assert.IsFalse(completion.IsCompleted);
        Assert.AreEqual(0, dictationTranscriber.StopCount);

        captureCompletion.SetResult(CreateCapture());
        await completion;

        Assert.AreEqual(1, dictationTranscriber.StopCount);
        Assert.AreEqual("What should I click?", request?.UserPrompt);
        Assert.AreEqual("Click Run.", viewModel.ResponseText);
    }

    [TestMethod]
    public async Task CompleteVoiceInteractionAsync_EmptyTranscriptShowsTryAgainResponse()
    {
        var workerRequestCount = 0;
        var dictationTranscriber = new FakeDictationTranscriber(
            stop: (_) => Task.FromResult("   "));
        using var viewModel = CreateViewModel(
            new FakeCaptureService((_) => Task.FromResult(CreateCapture())),
            new FakeWorkerClient((_, _) =>
            {
                workerRequestCount++;
                return Stream("unused [POINT:none]");
            }),
            dictationTranscriber: dictationTranscriber);

        await viewModel.BeginVoiceInteractionAsync();
        await viewModel.CompleteVoiceInteractionAsync();

        Assert.AreEqual(0, workerRequestCount);
        Assert.AreEqual(CompanionSessionState.Responding, viewModel.State);
        Assert.AreEqual(BrandText.EmptyDictationStatus, viewModel.StatusText);
        Assert.AreEqual(BrandText.EmptyDictationDetail, viewModel.ResponseText);
    }

    [TestMethod]
    public async Task VoiceInteraction_DictationUnavailableShowsSpeechSetupResponse()
    {
        var startCompletion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var dictationTranscriber = new FakeDictationTranscriber(
            start: (_) => startCompletion.Task);
        using var viewModel = CreateViewModel(
            new FakeCaptureService((_) => Task.FromResult(CreateCapture())),
            new FakeWorkerClient((_, _) => Stream("unused [POINT:none]")),
            dictationTranscriber: dictationTranscriber);

        await viewModel.BeginVoiceInteractionAsync();
        var completion = viewModel.CompleteVoiceInteractionAsync();
        startCompletion.SetException(new DictationUnavailableException(
            DictationUnavailableReason.SpeechRecognizer,
            "Speech recognition is unavailable."));
        await completion;

        Assert.AreEqual(CompanionSessionState.Responding, viewModel.State);
        Assert.AreEqual(BrandText.DictationSetupStatus, viewModel.StatusText);
        Assert.AreEqual(BrandText.DictationSetupDetail, viewModel.ResponseText);
    }

    [TestMethod]
    public async Task CancelCurrentInteractionAsync_StopsDictationAndReturnsToIdle()
    {
        var dictationTranscriber = new FakeDictationTranscriber();
        using var viewModel = CreateViewModel(
            new FakeCaptureService((_) => Task.FromResult(CreateCapture())),
            new FakeWorkerClient((_, _) => Stream("unused [POINT:none]")),
            dictationTranscriber: dictationTranscriber);

        await viewModel.BeginVoiceInteractionAsync();
        Assert.IsTrue(dictationTranscriber.IsTranscribing);

        await viewModel.CancelCurrentInteractionAsync();

        Assert.AreEqual(1, dictationTranscriber.CancelCount);
        Assert.IsFalse(dictationTranscriber.IsTranscribing);
        Assert.AreEqual(CompanionSessionState.Idle, viewModel.State);
        Assert.IsNull(viewModel.CurrentInteractionId);
        Assert.AreEqual(string.Empty, viewModel.ResponseText);
    }

    [TestMethod]
    public async Task ConversationTurns_RespectServiceTenTurnCap()
    {
        var (viewModel, interactionService) = CreateViewModelWithService(
            new FakeCaptureService((_) => Task.FromResult(CreateCapture())),
            new FakeWorkerClient((request, _) =>
                Stream($"answer for {request.UserPrompt} [POINT:none]")));

        for (var questionNumber = 1; questionNumber <= 12; questionNumber++)
        {
            await viewModel.BeginQuestionEntryAsync();
            viewModel.Question = $"question {questionNumber}";
            await viewModel.SubmitQuestionAsync();
        }

        var canonicalHistory = interactionService.GetConversationHistorySnapshot();
        Assert.HasCount(10, canonicalHistory);
        Assert.HasCount(10, viewModel.ConversationTurns);
        CollectionAssert.AreEqual(canonicalHistory.ToArray(), viewModel.ConversationTurns.ToArray());
        Assert.AreEqual("question 3", viewModel.ConversationTurns[0].UserText);
        Assert.AreEqual("answer for question 3", viewModel.ConversationTurns[0].AssistantText);
        Assert.AreEqual("question 12", viewModel.ConversationTurns[9].UserText);
        Assert.AreEqual("answer for question 12", viewModel.ConversationTurns[9].AssistantText);
    }

    [TestMethod]
    public async Task ClearConversationCommand_ClearsVisibleAndServiceHistory()
    {
        var (viewModel, interactionService) = CreateViewModelWithService(
            new FakeCaptureService((_) => Task.FromResult(CreateCapture())),
            new FakeWorkerClient((_, _) => Stream("A finished answer. [POINT:none]")));

        await viewModel.BeginQuestionEntryAsync();
        viewModel.Question = "A finished question";
        await viewModel.SubmitQuestionAsync();
        Assert.IsTrue(viewModel.ClearConversationCommand.CanExecute(null));
        Assert.HasCount(1, viewModel.ConversationTurns);
        Assert.HasCount(1, interactionService.GetConversationHistorySnapshot());

        viewModel.ClearConversationCommand.Execute(null);

        Assert.HasCount(0, viewModel.ConversationTurns);
        Assert.HasCount(0, interactionService.GetConversationHistorySnapshot());
        Assert.IsFalse(viewModel.HasConversation);
        Assert.IsFalse(viewModel.HasActiveQuestion);
        Assert.IsTrue(viewModel.IsConversationEmpty);
        Assert.AreEqual(string.Empty, viewModel.ResponseText);
        Assert.AreEqual(CompanionSessionState.Idle, viewModel.State);
        Assert.IsFalse(viewModel.ClearConversationCommand.CanExecute(null));
    }

    private static CompanionViewModel CreateViewModel(
        IActiveWindowCaptureService captureService,
        IWorkerClient workerClient,
        IPointCuePresenter? pointCuePresenter = null,
        IDictationTranscriber? dictationTranscriber = null) =>
        CreateViewModelWithService(
            captureService,
            workerClient,
            pointCuePresenter,
            dictationTranscriber).ViewModel;

    private static (CompanionViewModel ViewModel, TutorInteractionService InteractionService)
        CreateViewModelWithService(
            IActiveWindowCaptureService captureService,
            IWorkerClient workerClient,
            IPointCuePresenter? pointCuePresenter = null,
            IDictationTranscriber? dictationTranscriber = null)
    {
        var settings = new CompanionSettings();
        var coordinator = new CompanionSessionCoordinator();
        var interactionService = new TutorInteractionService(captureService, workerClient);
        var viewModel = new CompanionViewModel(
            coordinator,
            settings,
            interactionService,
            pointCuePresenter ?? new FakePointCuePresenter(),
            new FakeProviderApiKeyStore(),
            dictationTranscriber);
        return (viewModel, interactionService);
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

    private static async IAsyncEnumerable<string> DelayedResponse(
        TaskCompletionSource started,
        TaskCompletionSource release,
        string response,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        started.SetResult();
        await release.Task.WaitAsync(cancellationToken);
        yield return response;
    }

    private static async IAsyncEnumerable<string> MissingWorkerUrl(
        TaskCompletionSource started,
        TaskCompletionSource release,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        started.SetResult();
        await release.Task.WaitAsync(cancellationToken);
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

    private sealed class FakeDictationTranscriber(
        Func<CancellationToken, Task>? start = null,
        Func<CancellationToken, Task<string>>? stop = null,
        Func<CancellationToken, Task>? cancel = null) : IDictationTranscriber
    {
        public bool IsTranscribing { get; private set; }

        public int StartCount { get; private set; }

        public int StopCount { get; private set; }

        public int CancelCount { get; private set; }

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            StartCount++;
            await (start?.Invoke(cancellationToken) ?? Task.CompletedTask);
            IsTranscribing = true;
        }

        public async Task<string> StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            var transcript = await (stop?.Invoke(cancellationToken) ?? Task.FromResult(string.Empty));
            IsTranscribing = false;
            return transcript;
        }

        public async Task CancelAsync(CancellationToken cancellationToken = default)
        {
            CancelCount++;
            await (cancel?.Invoke(cancellationToken) ?? Task.CompletedTask);
            IsTranscribing = false;
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakePointCuePresenter(int? failOnHideCall = null) : IPointCuePresenter
    {
        private int hideCallCount;

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
            hideCallCount++;
            if (hideCallCount == failOnHideCall)
            {
                throw new InvalidOperationException("Cue presenter unavailable.");
            }

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

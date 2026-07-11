using System.ComponentModel;
using System.Net;
using System.Windows;
using Clicky.Windows.Branding;
using Clicky.Windows.Configuration;
using Clicky.Windows.Input;
using Clicky.Windows.Interaction;
using Clicky.Windows.Motion;
using Clicky.Windows.Mvvm;
using Clicky.Windows.Networking;
using Clicky.Windows.Overlay;
using Clicky.Windows.Pointing;
using Clicky.Windows.Providers;
using Clicky.Windows.Session;
using Clicky.Windows.Voice;

namespace Clicky.Windows.ViewModels;

public sealed class CompanionViewModel : ObservableObject, IDisposable
{
    private const double CompactWindowHeight = 190;
    private const double SettingsWindowHeight = 536;
    private const double ConversationWindowHeight = 520;

    private readonly CompanionSessionCoordinator sessionCoordinator;
    private readonly TutorInteractionService tutorInteractionService;
    private readonly IPointCuePresenter pointCuePresenter;
    private readonly IProviderApiKeyStore apiKeyStore;
    private readonly IDictationTranscriber? dictationTranscriber;
    private readonly SemaphoreSlim cueGate = new(1, 1);

    private CancellationTokenSource? interactionCancellationSource;
    private PreparedTutorInteraction? preparedInteraction;
    private CompanionInteractionId? activeQuestionInteractionId;
    private bool isQuestionEntryVisible;
    private bool isSettingsVisible;
    private bool isConversationVisible;
    private string question = string.Empty;
    private string responseText = string.Empty;
    private string activeQuestionText = string.Empty;
    private IReadOnlyList<TutorConversationTurn> conversationTurns = [];
    private string responseStatusText = BrandText.RespondingStatus;
    private TutorInteractionResult? lastInteractionResult;
    private bool isProviderOperationBusy;
    private bool hasStoredApiKey;
    private string apiKeyStatusText = BrandText.ApiKeyNotStored;
    private VoiceInteraction? voiceInteraction;
    private int disposeState;

    public CompanionViewModel(
        CompanionSessionCoordinator sessionCoordinator,
        CompanionSettings settings,
        TutorInteractionService tutorInteractionService,
        IPointCuePresenter pointCuePresenter,
        IProviderApiKeyStore apiKeyStore,
        IDictationTranscriber? dictationTranscriber = null)
    {
        ArgumentNullException.ThrowIfNull(sessionCoordinator);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(tutorInteractionService);
        ArgumentNullException.ThrowIfNull(pointCuePresenter);
        ArgumentNullException.ThrowIfNull(apiKeyStore);

        this.sessionCoordinator = sessionCoordinator;
        this.tutorInteractionService = tutorInteractionService;
        this.pointCuePresenter = pointCuePresenter;
        this.apiKeyStore = apiKeyStore;
        this.dictationTranscriber = dictationTranscriber;
        Settings = settings;

        AdvanceSessionCommand = new RelayCommand(
            () => _ = BeginQuestionEntryAsync(),
            CanBeginQuestion);
        SubmitQuestionCommand = new RelayCommand(
            () => _ = SubmitQuestionAsync(),
            CanSubmitQuestion);
        CancelSessionCommand = new RelayCommand(
            CancelCurrentInteraction,
            () => State != CompanionSessionState.Idle);
        SelectWorkerProviderCommand = new RelayCommand(
            () => _ = SelectProviderAndObserveAsync(AiProviderKind.Worker),
            CanChangeProviderSettings);
        SelectAnthropicProviderCommand = new RelayCommand(
            () => _ = SelectProviderAndObserveAsync(AiProviderKind.Anthropic),
            CanChangeProviderSettings);
        SelectOpenAIProviderCommand = new RelayCommand(
            () => _ = SelectProviderAndObserveAsync(AiProviderKind.OpenAI),
            CanChangeProviderSettings);
        RemoveApiKeyCommand = new RelayCommand(
            () => _ = DeleteSelectedProviderApiKeyAndObserveAsync(),
            () => CanChangeProviderSettings() && IsDirectProvider && HasStoredApiKey);
        ClearConversationCommand = new RelayCommand(
            ClearConversation,
            CanClearConversation);

        sessionCoordinator.StateChanged += HandleSessionStateChanged;
        Settings.PropertyChanged += HandleSettingsPropertyChanged;
        SystemParameters.StaticPropertyChanged += HandleSystemParametersPropertyChanged;
    }

    public event EventHandler? QuestionEntryReady;

    public event EventHandler? CompactStateRequested;

    public CompanionSettings Settings { get; }

    public IReadOnlyList<PushToTalkKey> PushToTalkKeys { get; } =
        Enum.GetValues<PushToTalkKey>();

    public IReadOnlyList<TutorConversationTurn> ConversationTurns
    {
        get => conversationTurns;
        private set
        {
            if (SetProperty(ref conversationTurns, value))
            {
                OnPropertyChanged(nameof(HasConversation));
                OnPropertyChanged(nameof(IsConversationEmpty));
                ClearConversationCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string ActiveQuestionText
    {
        get => activeQuestionText;
        private set
        {
            if (SetProperty(ref activeQuestionText, value))
            {
                OnPropertyChanged(nameof(HasActiveQuestion));
                OnPropertyChanged(nameof(IsConversationEmpty));
            }
        }
    }

    public bool HasConversation => ConversationTurns.Count > 0;

    public bool HasActiveQuestion => !string.IsNullOrWhiteSpace(ActiveQuestionText);

    public bool IsConversationEmpty => !HasConversation && !HasActiveQuestion;

    public bool IsMotionEffectivelyEnabled => CompanionMotionPolicy.IsEnabled(Settings);

    public CompanionSessionState State => sessionCoordinator.State;

    public CompanionInteractionId? CurrentInteractionId => sessionCoordinator.CurrentInteractionId;

    public string StatusText => State switch
    {
        CompanionSessionState.Idle => BrandText.IdleStatus,
        CompanionSessionState.Listening => BrandText.ListeningStatus,
        CompanionSessionState.Processing => BrandText.ProcessingStatus,
        CompanionSessionState.Responding => responseStatusText,
        _ => BrandText.IdleStatus
    };

    public string StatusDetail => State switch
    {
        CompanionSessionState.Idle => BrandText.IdleDetail,
        CompanionSessionState.Listening => BrandText.ListeningDetail,
        CompanionSessionState.Processing => BrandText.ProcessingDetail,
        CompanionSessionState.Responding when !string.IsNullOrWhiteSpace(ResponseText) => ResponseText,
        CompanionSessionState.Responding => BrandText.RespondingDetail,
        _ => BrandText.IdleDetail
    };

    public string Question
    {
        get => question;
        set
        {
            if (SetProperty(ref question, value ?? string.Empty))
            {
                SubmitQuestionCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string ResponseText
    {
        get => responseText;
        private set
        {
            if (SetProperty(ref responseText, value))
            {
                OnPropertyChanged(nameof(StatusDetail));
            }
        }
    }

    public bool IsQuestionEntryVisible
    {
        get => isQuestionEntryVisible;
        private set
        {
            if (SetProperty(ref isQuestionEntryVisible, value))
            {
                AdvanceSessionCommand.RaiseCanExecuteChanged();
                SubmitQuestionCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public TutorInteractionResult? LastInteractionResult
    {
        get => lastInteractionResult;
        private set
        {
            if (SetProperty(ref lastInteractionResult, value))
            {
                OnPropertyChanged(nameof(LastMappedDesktopPoint));
            }
        }
    }

    public DesktopPoint? LastMappedDesktopPoint => LastInteractionResult?.MappedDesktopPoint;

    public bool IsSettingsVisible
    {
        get => isSettingsVisible;
        private set
        {
            if (SetProperty(ref isSettingsVisible, value))
            {
                OnPropertyChanged(nameof(WindowHeight));
            }
        }
    }

    public bool IsConversationVisible
    {
        get => isConversationVisible;
        private set
        {
            if (SetProperty(ref isConversationVisible, value))
            {
                OnPropertyChanged(nameof(WindowHeight));
            }
        }
    }

    public double WindowHeight => IsSettingsVisible
        ? SettingsWindowHeight
        : IsConversationVisible
            ? ConversationWindowHeight
            : CompactWindowHeight;

    public AiProviderKind SelectedProvider => Settings.SelectedProvider;

    public bool IsWorkerProvider => SelectedProvider == AiProviderKind.Worker;

    public bool IsAnthropicProvider => SelectedProvider == AiProviderKind.Anthropic;

    public bool IsOpenAIProvider => SelectedProvider == AiProviderKind.OpenAI;

    public bool IsDirectProvider => !IsWorkerProvider;

    public bool CanSaveApiKey => CanChangeProviderSettings() && IsDirectProvider;

    public bool IsProviderOperationBusy
    {
        get => isProviderOperationBusy;
        private set
        {
            if (SetProperty(ref isProviderOperationBusy, value))
            {
                OnPropertyChanged(nameof(CanSaveApiKey));
                RaiseProviderCommandCanExecuteChanged();
            }
        }
    }

    public bool HasStoredApiKey
    {
        get => hasStoredApiKey;
        private set
        {
            if (SetProperty(ref hasStoredApiKey, value))
            {
                OnPropertyChanged(nameof(SaveApiKeyButtonText));
                RemoveApiKeyCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string ApiKeyStatusText
    {
        get => apiKeyStatusText;
        private set => SetProperty(ref apiKeyStatusText, value);
    }

    public string SaveApiKeyButtonText => HasStoredApiKey
        ? BrandText.ReplaceApiKeyLabel
        : BrandText.SaveApiKeyLabel;

    public RelayCommand AdvanceSessionCommand { get; }

    public RelayCommand SubmitQuestionCommand { get; }

    public RelayCommand CancelSessionCommand { get; }

    public RelayCommand SelectWorkerProviderCommand { get; }

    public RelayCommand SelectAnthropicProviderCommand { get; }

    public RelayCommand SelectOpenAIProviderCommand { get; }

    public RelayCommand RemoveApiKeyCommand { get; }

    public RelayCommand ClearConversationCommand { get; }

    public void SetSettingsVisible(bool isVisible)
    {
        if (isVisible &&
            State is CompanionSessionState.Listening or CompanionSessionState.Processing)
        {
            return;
        }

        if (isVisible && State == CompanionSessionState.Responding)
        {
            CancelCurrentInteraction();
        }

        if (isVisible)
        {
            IsConversationVisible = false;
        }

        IsSettingsVisible = isVisible;
        AdvanceSessionCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanSaveApiKey));
        RaiseProviderCommandCanExecuteChanged();

        if (isVisible)
        {
            _ = RefreshSelectedProviderKeyStatusAndObserveAsync();
        }
    }

    public void SetConversationVisible(bool isVisible)
    {
        if (isVisible && IsSettingsVisible)
        {
            SetSettingsVisible(false);
        }

        IsConversationVisible = isVisible;
    }

    public void ClearConversation()
    {
        if (!CanClearConversation())
        {
            return;
        }

        tutorInteractionService.ClearHistory();
        RefreshConversationTurns();
        ClearActiveQuestion();
        ResponseText = string.Empty;
        if (State == CompanionSessionState.Responding)
        {
            sessionCoordinator.ResetToIdle();
        }

        _ = HideCueAsync();
    }

    public async Task SelectProviderAsync(AiProviderKind provider)
    {
        if (!Enum.IsDefined(provider))
        {
            throw new ArgumentOutOfRangeException(nameof(provider));
        }

        if (IsProviderOperationBusy)
        {
            return;
        }

        IsProviderOperationBusy = true;
        try
        {
            await CancelCurrentInteractionAsync();
            tutorInteractionService.ClearHistory();
            RefreshConversationTurns();
            Settings.SelectedProvider = provider;
            NotifyProviderChanged();
            await RefreshSelectedProviderKeyStatusCoreAsync();
        }
        finally
        {
            IsProviderOperationBusy = false;
        }
    }

    public async Task RefreshSelectedProviderKeyStatusAsync()
    {
        if (IsProviderOperationBusy)
        {
            return;
        }

        IsProviderOperationBusy = true;
        try
        {
            await RefreshSelectedProviderKeyStatusCoreAsync();
        }
        finally
        {
            IsProviderOperationBusy = false;
        }
    }

    public async Task SaveSelectedProviderApiKeyAsync(string apiKey)
    {
        if (!IsDirectProvider || IsProviderOperationBusy)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            ApiKeyStatusText = BrandText.ApiKeyRequired;
            return;
        }

        IsProviderOperationBusy = true;
        ApiKeyStatusText = BrandText.ApiKeySaving;
        try
        {
            await apiKeyStore.SaveApiKeyAsync(SelectedProvider, apiKey);
            HasStoredApiKey = true;
            ApiKeyStatusText = BrandText.ApiKeyStored;
        }
        catch
        {
            ApiKeyStatusText = BrandText.ApiKeySaveFailed;
        }
        finally
        {
            IsProviderOperationBusy = false;
        }
    }

    public async Task DeleteSelectedProviderApiKeyAsync()
    {
        if (!IsDirectProvider || IsProviderOperationBusy)
        {
            return;
        }

        IsProviderOperationBusy = true;
        ApiKeyStatusText = BrandText.ApiKeyRemoving;
        try
        {
            await apiKeyStore.DeleteApiKeyAsync(SelectedProvider);
            HasStoredApiKey = false;
            ApiKeyStatusText = BrandText.ApiKeyNotStored;
        }
        catch
        {
            ApiKeyStatusText = BrandText.ApiKeyRemoveFailed;
        }
        finally
        {
            IsProviderOperationBusy = false;
        }
    }

    public async Task BeginQuestionEntryAsync()
    {
        if (!CanBeginQuestion())
        {
            return;
        }

        CancelInteractionToken();
        ClearActiveQuestion();
        Question = string.Empty;
        ResponseText = string.Empty;
        responseStatusText = BrandText.RespondingStatus;
        IsQuestionEntryVisible = false;
        preparedInteraction = null;

        var interactionId = sessionCoordinator.BeginListening();
        var cancellationSource = new CancellationTokenSource();
        interactionCancellationSource = cancellationSource;

        try
        {
            await HideCueIfCurrentAsync(interactionId);
            var preparation = await tutorInteractionService.PrepareAsync(cancellationSource.Token);
            if (!IsCurrent(interactionId) || cancellationSource.IsCancellationRequested)
            {
                return;
            }

            preparedInteraction = preparation;
            IsQuestionEntryVisible = true;
            QuestionEntryReady?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) when (cancellationSource.IsCancellationRequested)
        {
            await ResetIfCurrentAsync(interactionId);
        }
        catch (Exception)
        {
            await ShowFailureAsync(
                interactionId,
                "I couldn't capture the active app. Keep it visible and try again.");
        }
    }

    public async Task SubmitQuestionAsync()
    {
        var interactionId = sessionCoordinator.CurrentInteractionId;
        var preparation = preparedInteraction;
        var cancellationSource = interactionCancellationSource;
        var submittedQuestion = Question.Trim();

        if (interactionId is null || preparation is null || cancellationSource is null ||
            string.IsNullOrWhiteSpace(submittedQuestion) || !IsQuestionEntryVisible)
        {
            return;
        }

        IsQuestionEntryVisible = false;
        CompactStateRequested?.Invoke(this, EventArgs.Empty);
        if (!sessionCoordinator.BeginProcessing(interactionId.Value))
        {
            return;
        }

        SetActiveQuestion(interactionId.Value, submittedQuestion);

        try
        {
            await RespondWithPreparedInteractionAsync(
                interactionId.Value,
                preparation,
                submittedQuestion,
                cancellationSource);
        }
        finally
        {
            if (ReferenceEquals(interactionCancellationSource, cancellationSource))
            {
                interactionCancellationSource = null;
                cancellationSource.Dispose();
            }
        }
    }

    public Task BeginVoiceInteractionAsync()
    {
        if (dictationTranscriber is null || !CanBeginQuestion())
        {
            return Task.CompletedTask;
        }

        CancelInteractionToken();
        ClearActiveQuestion();
        Question = string.Empty;
        ResponseText = string.Empty;
        responseStatusText = BrandText.RespondingStatus;
        IsQuestionEntryVisible = false;
        preparedInteraction = null;

        var interactionId = sessionCoordinator.BeginListening();
        var cancellationSource = new CancellationTokenSource();
        interactionCancellationSource = cancellationSource;
        var interaction = new VoiceInteraction(interactionId, cancellationSource);
        voiceInteraction = interaction;
        interaction.Workflow = RunVoiceInteractionAsync(interaction);
        return Task.CompletedTask;
    }

    public async Task CompleteVoiceInteractionAsync()
    {
        var interaction = voiceInteraction;
        if (interaction is null)
        {
            return;
        }

        if (IsCurrent(interaction.InteractionId) && State == CompanionSessionState.Listening)
        {
            _ = sessionCoordinator.BeginProcessing(interaction.InteractionId);
        }

        interaction.ReleaseSignal.TrySetResult();
        await interaction.Workflow;
    }

    public void CancelCurrentInteraction()
    {
        _ = CancelCurrentInteractionAndObserveAsync();
    }

    public async Task CancelCurrentInteractionAsync()
    {
        var activeVoiceInteraction = voiceInteraction;
        voiceInteraction = null;
        var cancellationSource = interactionCancellationSource;
        interactionCancellationSource = null;
        cancellationSource?.Cancel();

        if (activeVoiceInteraction is not null && dictationTranscriber is not null)
        {
            activeVoiceInteraction.ReleaseSignal.TrySetCanceled();
            try
            {
                await dictationTranscriber.CancelAsync();
            }
            catch (InvalidOperationException)
            {
                // Recognition may finish between the state check and cancellation.
            }

            await activeVoiceInteraction.Workflow;
        }
        else
        {
            cancellationSource?.Dispose();
        }

        preparedInteraction = null;
        ClearActiveQuestion();
        IsQuestionEntryVisible = false;
        Question = string.Empty;
        ResponseText = string.Empty;
        sessionCoordinator.ResetToIdle();
        CompactStateRequested?.Invoke(this, EventArgs.Empty);
        await HideCueAsync();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposeState, 1) != 0)
        {
            return;
        }

        sessionCoordinator.StateChanged -= HandleSessionStateChanged;
        Settings.PropertyChanged -= HandleSettingsPropertyChanged;
        SystemParameters.StaticPropertyChanged -= HandleSystemParametersPropertyChanged;
        voiceInteraction?.ReleaseSignal.TrySetCanceled();
        voiceInteraction = null;
        var cancellationSource = interactionCancellationSource;
        interactionCancellationSource = null;
        cancellationSource?.Cancel();
    }

    private async Task RunVoiceInteractionAsync(VoiceInteraction interaction)
    {
        var cancellationSource = interaction.CancellationSource;
        try
        {
            await HideCueIfCurrentAsync(interaction.InteractionId);
            await dictationTranscriber!.StartAsync(cancellationSource.Token);
            var preparation = await tutorInteractionService.PrepareAsync(cancellationSource.Token);
            await interaction.ReleaseSignal.Task.WaitAsync(cancellationSource.Token);
            var transcript = (await dictationTranscriber.StopAsync(cancellationSource.Token)).Trim();

            if (!IsCurrent(interaction.InteractionId) || cancellationSource.IsCancellationRequested)
            {
                return;
            }

            EnsureVoiceProcessing(interaction.InteractionId);
            if (string.IsNullOrWhiteSpace(transcript))
            {
                await HideCueIfCurrentAsync(interaction.InteractionId);
                ShowResponse(
                    interaction.InteractionId,
                    BrandText.EmptyDictationStatus,
                    BrandText.EmptyDictationDetail);
                return;
            }

            SetActiveQuestion(interaction.InteractionId, transcript);
            await RespondWithPreparedInteractionAsync(
                interaction.InteractionId,
                preparation,
                transcript,
                cancellationSource);
        }
        catch (OperationCanceledException) when (cancellationSource.IsCancellationRequested)
        {
            await ResetIfCurrentAsync(interaction.InteractionId);
        }
        catch (DictationUnavailableException)
        {
            EnsureVoiceProcessing(interaction.InteractionId);
            await HideCueIfCurrentAsync(interaction.InteractionId);
            ShowResponse(
                interaction.InteractionId,
                BrandText.DictationSetupStatus,
                BrandText.DictationSetupDetail);
        }
        catch (Exception)
        {
            EnsureVoiceProcessing(interaction.InteractionId);
            await HideCueIfCurrentAsync(interaction.InteractionId);
            ShowResponse(
                interaction.InteractionId,
                "Voice question missed",
                "I couldn't capture that voice question. Keep the app visible and try again.");
        }
        finally
        {
            if (ReferenceEquals(voiceInteraction, interaction))
            {
                voiceInteraction = null;
            }

            if (ReferenceEquals(interactionCancellationSource, cancellationSource))
            {
                interactionCancellationSource = null;
            }

            cancellationSource.Dispose();
        }
    }

    private async Task RespondWithPreparedInteractionAsync(
        CompanionInteractionId interactionId,
        PreparedTutorInteraction preparation,
        string prompt,
        CancellationTokenSource cancellationSource)
    {
        try
        {
            var result = await tutorInteractionService.RespondAsync(
                preparation,
                prompt,
                cancellationSource.Token);
            if (!IsCurrent(interactionId) || cancellationSource.IsCancellationRequested)
            {
                return;
            }

            LastInteractionResult = result;
            RefreshConversationTurns();
            await PresentResultCueIfCurrentAsync(interactionId, result);
            ShowResponse(interactionId, BrandText.RespondingStatus, result.SpokenText);
        }
        catch (OperationCanceledException) when (cancellationSource.IsCancellationRequested)
        {
            await ResetIfCurrentAsync(interactionId);
        }
        catch (WorkerConfigurationException exception)
        {
            await HideCueIfCurrentAsync(interactionId);
            ShowResponse(interactionId, BrandText.WorkerSetupStatus, exception.Message);
        }
        catch (AnthropicProviderException exception)
        {
            await HideCueIfCurrentAsync(interactionId);
            var (status, detail) = DescribeAnthropicFailure(exception);
            ShowResponse(interactionId, status, detail);
        }
        catch (OpenAiProviderException exception)
        {
            await HideCueIfCurrentAsync(interactionId);
            var (status, detail) = DescribeOpenAIFailure(exception);
            ShowResponse(interactionId, status, detail);
        }
        catch (Exception)
        {
            await HideCueIfCurrentAsync(interactionId);
            ShowResponse(
                interactionId,
                "Couldn't answer",
                "The selected AI provider didn't answer. Check its settings and try again.");
        }
        finally
        {
            ClearActiveQuestionIfCurrent(interactionId);
        }
    }

    private void EnsureVoiceProcessing(CompanionInteractionId interactionId)
    {
        if (IsCurrent(interactionId) && State == CompanionSessionState.Listening)
        {
            _ = sessionCoordinator.BeginProcessing(interactionId);
        }
    }

    private bool CanBeginQuestion() =>
        !IsSettingsVisible &&
        !IsQuestionEntryVisible &&
        (State == CompanionSessionState.Idle || State == CompanionSessionState.Responding);

    private bool CanSubmitQuestion() =>
        IsQuestionEntryVisible && !string.IsNullOrWhiteSpace(Question);

    private bool CanChangeProviderSettings() =>
        IsSettingsVisible && State == CompanionSessionState.Idle && !IsProviderOperationBusy;

    private bool CanClearConversation() =>
        HasConversation &&
        State is not CompanionSessionState.Listening and not CompanionSessionState.Processing;

    private async Task RefreshSelectedProviderKeyStatusCoreAsync()
    {
        if (!IsDirectProvider)
        {
            HasStoredApiKey = false;
            ApiKeyStatusText = BrandText.ApiKeyNotStored;
            return;
        }

        try
        {
            HasStoredApiKey = await apiKeyStore.HasApiKeyAsync(SelectedProvider);
            ApiKeyStatusText = HasStoredApiKey
                ? BrandText.ApiKeyStored
                : BrandText.ApiKeyNotStored;
        }
        catch
        {
            HasStoredApiKey = false;
            ApiKeyStatusText = BrandText.ApiKeyStatusFailed;
        }
    }

    private void NotifyProviderChanged()
    {
        OnPropertyChanged(nameof(SelectedProvider));
        OnPropertyChanged(nameof(IsWorkerProvider));
        OnPropertyChanged(nameof(IsAnthropicProvider));
        OnPropertyChanged(nameof(IsOpenAIProvider));
        OnPropertyChanged(nameof(IsDirectProvider));
        OnPropertyChanged(nameof(CanSaveApiKey));
        RaiseProviderCommandCanExecuteChanged();
    }

    private void RaiseProviderCommandCanExecuteChanged()
    {
        SelectWorkerProviderCommand.RaiseCanExecuteChanged();
        SelectAnthropicProviderCommand.RaiseCanExecuteChanged();
        SelectOpenAIProviderCommand.RaiseCanExecuteChanged();
        RemoveApiKeyCommand.RaiseCanExecuteChanged();
    }

    private async Task SelectProviderAndObserveAsync(AiProviderKind provider)
    {
        try
        {
            await SelectProviderAsync(provider);
        }
        catch
        {
            ApiKeyStatusText = BrandText.ApiKeyStatusFailed;
        }
    }

    private async Task RefreshSelectedProviderKeyStatusAndObserveAsync()
    {
        try
        {
            await RefreshSelectedProviderKeyStatusAsync();
        }
        catch
        {
            ApiKeyStatusText = BrandText.ApiKeyStatusFailed;
        }
    }

    private async Task DeleteSelectedProviderApiKeyAndObserveAsync()
    {
        try
        {
            await DeleteSelectedProviderApiKeyAsync();
        }
        catch
        {
            ApiKeyStatusText = BrandText.ApiKeyRemoveFailed;
        }
    }

    private bool IsCurrent(CompanionInteractionId interactionId) =>
        sessionCoordinator.CurrentInteractionId == interactionId;

    private void SetActiveQuestion(
        CompanionInteractionId interactionId,
        string text)
    {
        activeQuestionInteractionId = interactionId;
        ActiveQuestionText = text.Trim();
    }

    private void ClearActiveQuestionIfCurrent(CompanionInteractionId interactionId)
    {
        if (activeQuestionInteractionId == interactionId)
        {
            ClearActiveQuestion();
        }
    }

    private void ClearActiveQuestion()
    {
        activeQuestionInteractionId = null;
        ActiveQuestionText = string.Empty;
    }

    private void RefreshConversationTurns() =>
        ConversationTurns = tutorInteractionService.GetConversationHistorySnapshot();

    private static (string Status, string Detail) DescribeAnthropicFailure(
        AnthropicProviderException exception)
    {
        if (exception.ErrorKind == AnthropicProviderErrorKind.Configuration)
        {
            return (BrandText.ProviderSetupStatus, exception.Message);
        }

        if (exception.ErrorKind == AnthropicProviderErrorKind.Incomplete)
        {
            return (
                BrandText.ProviderBusyStatus,
                "Anthropic reached its response limit. Try a narrower question or raise the token limit.");
        }

        return exception.StatusCode switch
        {
            HttpStatusCode.Unauthorized =>
                (BrandText.ProviderSetupStatus, "Anthropic rejected the stored key. Replace it in settings."),
            HttpStatusCode.Forbidden =>
                (BrandText.ProviderBusyStatus, "This Anthropic key cannot use the selected model."),
            HttpStatusCode.TooManyRequests =>
                (BrandText.ProviderBusyStatus, "Anthropic is rate limiting this key. Give it a moment and try again."),
            >= HttpStatusCode.InternalServerError =>
                (BrandText.ProviderBusyStatus, "Anthropic is having trouble right now. Try again shortly."),
            _ =>
                (BrandText.ProviderBusyStatus, "Anthropic could not complete that request. Check the model and try again."),
        };
    }

    private static (string Status, string Detail) DescribeOpenAIFailure(
        OpenAiProviderException exception) => exception.FailureKind switch
        {
            OpenAiProviderFailureKind.MissingApiKey or
            OpenAiProviderFailureKind.InvalidConfiguration =>
                (BrandText.ProviderSetupStatus, "Save an OpenAI key and model in Clicky settings."),
            OpenAiProviderFailureKind.Authentication =>
                (BrandText.ProviderSetupStatus, "OpenAI rejected the stored key. Replace it in settings."),
            OpenAiProviderFailureKind.Permission =>
                (BrandText.ProviderBusyStatus, "This OpenAI key cannot use the selected model."),
            OpenAiProviderFailureKind.RateLimited =>
                (BrandText.ProviderBusyStatus, "OpenAI is rate limiting this key. Give it a moment and try again."),
            OpenAiProviderFailureKind.Server or OpenAiProviderFailureKind.Transport =>
                (BrandText.ProviderBusyStatus, "OpenAI is having trouble right now. Try again shortly."),
            OpenAiProviderFailureKind.Refusal =>
                ("Couldn't answer", "OpenAI declined that request. Try asking in a different way."),
            _ =>
                (BrandText.ProviderBusyStatus, "OpenAI could not complete that request. Check the model and try again."),
        };

    private async Task ShowFailureAsync(CompanionInteractionId interactionId, string message)
    {
        if (!IsCurrent(interactionId))
        {
            return;
        }

        await HideCueIfCurrentAsync(interactionId);
        _ = sessionCoordinator.BeginProcessing(interactionId);
        ShowResponse(interactionId, "Capture missed", message);
    }

    private void ShowResponse(
        CompanionInteractionId interactionId,
        string statusText,
        string detail)
    {
        if (!IsCurrent(interactionId) ||
            !sessionCoordinator.BeginResponding(interactionId))
        {
            return;
        }

        preparedInteraction = null;
        responseStatusText = statusText;
        ResponseText = detail.Trim();
        OnPropertyChanged(nameof(StatusText));
        CompactStateRequested?.Invoke(this, EventArgs.Empty);
    }

    private async Task ResetIfCurrentAsync(CompanionInteractionId interactionId)
    {
        if (!IsCurrent(interactionId))
        {
            return;
        }

        preparedInteraction = null;
        ClearActiveQuestionIfCurrent(interactionId);
        IsQuestionEntryVisible = false;
        sessionCoordinator.ResetToIdle();
        CompactStateRequested?.Invoke(this, EventArgs.Empty);
        await HideCueAsync();
    }

    private async Task PresentResultCueIfCurrentAsync(
        CompanionInteractionId interactionId,
        TutorInteractionResult result)
    {
        await cueGate.WaitAsync();
        try
        {
            if (!IsCurrent(interactionId))
            {
                return;
            }

            if (result.MappedDesktopPoint is { } mappedPoint)
            {
                await pointCuePresenter.ShowAsync(mappedPoint, result.TargetLabel);
            }
            else
            {
                await pointCuePresenter.HideAsync();
            }
        }
        finally
        {
            cueGate.Release();
        }
    }

    private async Task HideCueIfCurrentAsync(CompanionInteractionId interactionId)
    {
        await cueGate.WaitAsync();
        try
        {
            if (IsCurrent(interactionId))
            {
                await pointCuePresenter.HideAsync();
            }
        }
        finally
        {
            cueGate.Release();
        }
    }

    private async Task HideCueAsync()
    {
        await cueGate.WaitAsync();
        try
        {
            await pointCuePresenter.HideAsync();
        }
        finally
        {
            cueGate.Release();
        }
    }

    private async Task CancelCurrentInteractionAndObserveAsync()
    {
        try
        {
            await CancelCurrentInteractionAsync();
        }
        catch
        {
            // Cancellation must remain safe even if the presenter is shutting down.
        }
    }

    private void CancelInteractionToken()
    {
        var cancellationSource = interactionCancellationSource;
        interactionCancellationSource = null;
        if (cancellationSource is null)
        {
            return;
        }

        cancellationSource.Cancel();
        cancellationSource.Dispose();
    }

    private void HandleSessionStateChanged(object? sender, CompanionSessionStateChangedEventArgs eventArgs)
    {
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(CurrentInteractionId));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusDetail));
        AdvanceSessionCommand.RaiseCanExecuteChanged();
        SubmitQuestionCommand.RaiseCanExecuteChanged();
        CancelSessionCommand.RaiseCanExecuteChanged();
        ClearConversationCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanSaveApiKey));
        RaiseProviderCommandCanExecuteChanged();
    }

    private void HandleSettingsPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(CompanionSettings.MotionEffectsEnabled))
        {
            OnPropertyChanged(nameof(IsMotionEffectivelyEnabled));
        }
    }

    private void HandleSystemParametersPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (string.IsNullOrEmpty(eventArgs.PropertyName) ||
            eventArgs.PropertyName == nameof(SystemParameters.ClientAreaAnimation))
        {
            OnPropertyChanged(nameof(IsMotionEffectivelyEnabled));
        }
    }

    private sealed class VoiceInteraction(
        CompanionInteractionId interactionId,
        CancellationTokenSource cancellationSource)
    {
        public CompanionInteractionId InteractionId { get; } = interactionId;

        public CancellationTokenSource CancellationSource { get; } = cancellationSource;

        public TaskCompletionSource ReleaseSignal { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Workflow { get; set; } = Task.CompletedTask;
    }
}

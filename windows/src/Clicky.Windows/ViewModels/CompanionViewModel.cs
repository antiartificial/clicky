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
using Clicky.Windows.Persistence;
using Clicky.Windows.Pointing;
using Clicky.Windows.Providers;
using Clicky.Windows.Session;
using Clicky.Windows.Speech;
using Clicky.Windows.Voice;

namespace Clicky.Windows.ViewModels;

public sealed class CompanionViewModel : ObservableObject, IDisposable
{
    private const double CompactWindowHeight = 206;
    private const double SettingsWindowHeight = 680;
    private const double ConversationWindowHeight = 590;

    private readonly CompanionSessionCoordinator sessionCoordinator;
    private readonly TutorInteractionService tutorInteractionService;
    private readonly IPointCuePresenter pointCuePresenter;
    private readonly IProviderApiKeyStore apiKeyStore;
    private readonly IOpenAiOnboardingClient? openAiOnboardingClient;
    private readonly CompanionSettingsStore? settingsStore;
    private readonly IDictationTranscriber? dictationTranscriber;
    private readonly ITextToSpeechClient? textToSpeechClient;
    private readonly IAudioPlaybackService? audioPlaybackService;
    private readonly SemaphoreSlim cueGate = new(1, 1);

    private CancellationTokenSource? interactionCancellationSource;
    private CancellationTokenSource? openAIOnboardingCancellationSource;
    private PreparedTutorInteraction? preparedInteraction;
    private PreparedTutorInteraction? lastSuccessfulPreparation;
    private CompanionInteractionId? activeQuestionInteractionId;
    private bool isQuestionEntryVisible;
    private bool isSettingsVisible;
    private bool isConversationVisible;
    private string question = string.Empty;
    private string followUpQuestion = string.Empty;
    private string responseText = string.Empty;
    private string activeQuestionText = string.Empty;
    private IReadOnlyList<TutorConversationTurn> conversationTurns = [];
    private IReadOnlyList<StoredConversation> storedConversations = [];
    private StoredConversation? selectedStoredConversation;
    private string responseStatusText = BrandText.RespondingStatus;
    private TutorInteractionResult? lastInteractionResult;
    private bool isProviderOperationBusy;
    private bool hasStoredApiKey;
    private string apiKeyStatusText = BrandText.ApiKeyNotStored;
    private IReadOnlyList<string> availableOpenAIModels = [CompanionSettings.DefaultOpenAIModelId];
    private string openAIOnboardingStatusText = BrandText.OpenAIAddKey;
    private string openAIOnboardingDetailText = "Not connected";
    private string openAIModelRecommendationText = string.Empty;
    private bool hasDiscoveredOpenAIModels;
    private bool hasProviderApiKeyEntry;
    private bool hasStoredElevenLabsApiKey;
    private string elevenLabsApiKeyStatusText = BrandText.ApiKeyNotStored;
    private VoiceInteraction? voiceInteraction;
    private CancellationTokenSource? speechCancellationSource;
    private bool isSpeechOutputBusy;
    private string speechOutputStatusText = BrandText.SpeechReady;
    private long cuePresentationGeneration;
    private int disposeState;

    public CompanionViewModel(
        CompanionSessionCoordinator sessionCoordinator,
        CompanionSettings settings,
        TutorInteractionService tutorInteractionService,
        IPointCuePresenter pointCuePresenter,
        IProviderApiKeyStore apiKeyStore,
        IDictationTranscriber? dictationTranscriber = null,
        ITextToSpeechClient? textToSpeechClient = null,
        IAudioPlaybackService? audioPlaybackService = null,
        IOpenAiOnboardingClient? openAiOnboardingClient = null,
        CompanionSettingsStore? settingsStore = null)
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
        this.openAiOnboardingClient = openAiOnboardingClient;
        this.settingsStore = settingsStore;
        this.dictationTranscriber = dictationTranscriber;
        this.textToSpeechClient = textToSpeechClient;
        this.audioPlaybackService = audioPlaybackService;
        Settings = settings;
        availableOpenAIModels = string.IsNullOrWhiteSpace(settings.OpenAIModelId)
            ? [CompanionSettings.DefaultOpenAIModelId]
            : [settings.OpenAIModelId];

        AdvanceSessionCommand = new RelayCommand(
            () => _ = BeginQuestionEntryAsync(),
            CanBeginQuestion);
        SubmitQuestionCommand = new RelayCommand(
            () => _ = SubmitQuestionAsync(),
            CanSubmitQuestion);
        SubmitFollowUpCommand = new RelayCommand(
            () => _ = SubmitFollowUpAsync(),
            CanSubmitFollowUp);
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
        SelectGeminiProviderCommand = new RelayCommand(
            () => _ = SelectProviderAndObserveAsync(AiProviderKind.Gemini),
            CanChangeProviderSettings);
        RemoveApiKeyCommand = new RelayCommand(
            () => _ = DeleteSelectedProviderApiKeyAndObserveAsync(),
            () => CanChangeProviderSettings() && IsDirectProvider && HasStoredApiKey);
        TestOpenAIConnectionCommand = new RelayCommand(
            () => _ = TestOpenAIConnectionAndObserveAsync(),
            CanTestOpenAIConnection);
        TestSpeechOutputCommand = new RelayCommand(
            () => _ = TestSpeechOutputAsync(),
            CanTestSpeechOutput);
        ReplayPointCueCommand = new RelayCommand<TutorConversationTurn>(
            turn => _ = ReplayPointCueAsync(turn),
            turn => turn.HasInteractionTarget);
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

    public IReadOnlyList<TextToSpeechProviderKind> TextToSpeechProviders { get; } =
        Enum.GetValues<TextToSpeechProviderKind>();

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

    public IReadOnlyList<StoredConversation> StoredConversations
    {
        get => storedConversations;
        private set
        {
            if (SetProperty(ref storedConversations, value))
            {
                OnPropertyChanged(nameof(HasStoredConversations));
            }
        }
    }

    public bool HasStoredConversations => StoredConversations.Count > 0;

    public StoredConversation? SelectedStoredConversation
    {
        get => selectedStoredConversation;
        set
        {
            if (SetProperty(ref selectedStoredConversation, value))
            {
                OnPropertyChanged(nameof(CanContinueActiveConversation));
                SubmitFollowUpCommand.RaiseCanExecuteChanged();
                _ = LoadSelectedStoredConversationAndObserveAsync(value);
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
        CompanionSessionState.Processing when !string.IsNullOrWhiteSpace(ResponseText) => ResponseText,
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
                SubmitFollowUpCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string FollowUpQuestion
    {
        get => followUpQuestion;
        set
        {
            if (SetProperty(ref followUpQuestion, value ?? string.Empty))
            {
                SubmitFollowUpCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool CanContinueActiveConversation =>
        lastSuccessfulPreparation is not null && SelectedStoredConversation is null;

    public double WindowHeight => IsSettingsVisible
        ? SettingsWindowHeight
        : IsConversationVisible
            ? ConversationWindowHeight
            : CompactWindowHeight;

    public AiProviderKind SelectedProvider => Settings.SelectedProvider;

    public bool IsWorkerProvider => SelectedProvider == AiProviderKind.Worker;

    public bool IsAnthropicProvider => SelectedProvider == AiProviderKind.Anthropic;

    public bool IsOpenAIProvider => SelectedProvider == AiProviderKind.OpenAI;

    public bool IsGeminiProvider => SelectedProvider == AiProviderKind.Gemini;

    public bool IsDirectProvider => !IsWorkerProvider;

    public bool CanSaveApiKey => CanChangeProviderSettings() && IsDirectProvider;

    public bool CanSaveElevenLabsApiKey =>
        CanChangeProviderSettings() &&
        Settings.TextToSpeechProvider == TextToSpeechProviderKind.ElevenLabs;

    public bool IsElevenLabsSpeechProvider =>
        Settings.TextToSpeechProvider == TextToSpeechProviderKind.ElevenLabs;

    public bool IsOpenAISpeechProvider =>
        Settings.TextToSpeechProvider == TextToSpeechProviderKind.OpenAI;

    public IReadOnlyList<string> AvailableOpenAIModels
    {
        get => availableOpenAIModels;
        private set => SetProperty(ref availableOpenAIModels, value);
    }

    public bool HasDiscoveredOpenAIModels
    {
        get => hasDiscoveredOpenAIModels;
        private set => SetProperty(ref hasDiscoveredOpenAIModels, value);
    }

    public bool IsOpenAIOnboarded =>
        HasStoredApiKey &&
        Settings.OpenAIValidatedAtUtc is not null &&
        string.Equals(
            Settings.OpenAIValidatedModelId,
            Settings.OpenAIModelId,
            StringComparison.Ordinal);

    public string OpenAIOnboardingStatusText
    {
        get => openAIOnboardingStatusText;
        private set => SetProperty(ref openAIOnboardingStatusText, value);
    }

    public string OpenAIOnboardingDetailText
    {
        get => openAIOnboardingDetailText;
        private set => SetProperty(ref openAIOnboardingDetailText, value);
    }

    public string OpenAIModelRecommendationText
    {
        get => openAIModelRecommendationText;
        private set => SetProperty(ref openAIModelRecommendationText, value);
    }

    public bool IsProviderOperationBusy
    {
        get => isProviderOperationBusy;
        private set
        {
            if (SetProperty(ref isProviderOperationBusy, value))
            {
                OnPropertyChanged(nameof(CanSaveApiKey));
                OnPropertyChanged(nameof(CanSaveElevenLabsApiKey));
                TestOpenAIConnectionCommand.RaiseCanExecuteChanged();
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
                OnPropertyChanged(nameof(IsOpenAIOnboarded));
                TestOpenAIConnectionCommand.RaiseCanExecuteChanged();
                RemoveApiKeyCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string ApiKeyStatusText
    {
        get => apiKeyStatusText;
        private set => SetProperty(ref apiKeyStatusText, value);
    }

    public string SaveApiKeyButtonText => IsOpenAIProvider
        ? HasProviderApiKeyEntry
            ? HasStoredApiKey
                ? BrandText.OpenAIReplaceAndTestLabel
                : BrandText.OpenAIConnectLabel
            : HasStoredApiKey
                ? BrandText.OpenAITestStoredLabel
                : BrandText.OpenAIConnectLabel
        : HasStoredApiKey
            ? BrandText.ReplaceApiKeyLabel
            : BrandText.SaveApiKeyLabel;

    public bool HasProviderApiKeyEntry
    {
        get => hasProviderApiKeyEntry;
        private set
        {
            if (SetProperty(ref hasProviderApiKeyEntry, value))
            {
                OnPropertyChanged(nameof(SaveApiKeyButtonText));
            }
        }
    }

    public bool HasStoredElevenLabsApiKey
    {
        get => hasStoredElevenLabsApiKey;
        private set => SetProperty(ref hasStoredElevenLabsApiKey, value);
    }

    public string ElevenLabsApiKeyStatusText
    {
        get => elevenLabsApiKeyStatusText;
        private set => SetProperty(ref elevenLabsApiKeyStatusText, value);
    }

    public bool IsSpeechOutputBusy
    {
        get => isSpeechOutputBusy;
        private set
        {
            if (SetProperty(ref isSpeechOutputBusy, value))
            {
                TestSpeechOutputCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string SpeechOutputStatusText
    {
        get => Settings.SpeechOutputEnabled
            ? speechOutputStatusText
            : BrandText.SpeechDisabled;
        private set => SetProperty(ref speechOutputStatusText, value);
    }

    public RelayCommand AdvanceSessionCommand { get; }

    public RelayCommand SubmitQuestionCommand { get; }

    public RelayCommand SubmitFollowUpCommand { get; }

    public RelayCommand CancelSessionCommand { get; }

    public RelayCommand SelectWorkerProviderCommand { get; }

    public RelayCommand SelectAnthropicProviderCommand { get; }

    public RelayCommand SelectOpenAIProviderCommand { get; }

    public RelayCommand SelectGeminiProviderCommand { get; }

    public RelayCommand RemoveApiKeyCommand { get; }

    public RelayCommand TestOpenAIConnectionCommand { get; }

    public RelayCommand TestSpeechOutputCommand { get; }

    public RelayCommand<TutorConversationTurn> ReplayPointCueCommand { get; }

    public RelayCommand ClearConversationCommand { get; }

    public void SetProviderApiKeyEntryAvailable(bool isAvailable) =>
        HasProviderApiKeyEntry = isAvailable;

    public void SetSettingsVisible(bool isVisible)
    {
        if (isVisible &&
            State is CompanionSessionState.Listening or CompanionSessionState.Processing)
        {
            return;
        }

        if (isVisible && State == CompanionSessionState.Responding)
        {
            sessionCoordinator.ResetToIdle();
        }

        if (isVisible)
        {
            IsConversationVisible = false;
        }
        else
        {
            CancelOpenAIOnboarding();
        }

        IsSettingsVisible = isVisible;
        AdvanceSessionCommand.RaiseCanExecuteChanged();
        TestSpeechOutputCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanSaveApiKey));
        RaiseProviderCommandCanExecuteChanged();

        if (isVisible)
        {
            _ = RefreshSelectedProviderKeyStatusAndObserveAsync();
            _ = RefreshElevenLabsApiKeyStatusAndObserveAsync();
        }
    }

    public async Task SaveElevenLabsApiKeyAsync(string apiKey)
    {
        if (!CanSaveElevenLabsApiKey || IsProviderOperationBusy)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            ElevenLabsApiKeyStatusText = BrandText.ApiKeyRequired;
            return;
        }

        IsProviderOperationBusy = true;
        ElevenLabsApiKeyStatusText = BrandText.ApiKeySaving;
        try
        {
            await apiKeyStore.SaveApiKeyAsync(AiProviderKind.ElevenLabs, apiKey);
            HasStoredElevenLabsApiKey = true;
            ElevenLabsApiKeyStatusText = BrandText.ApiKeyStored;
        }
        catch
        {
            ElevenLabsApiKeyStatusText = BrandText.ApiKeySaveFailed;
        }
        finally
        {
            IsProviderOperationBusy = false;
        }
    }

    public async Task DeleteElevenLabsApiKeyAsync()
    {
        if (IsProviderOperationBusy)
        {
            return;
        }

        IsProviderOperationBusy = true;
        ElevenLabsApiKeyStatusText = BrandText.ApiKeyRemoving;
        try
        {
            await apiKeyStore.DeleteApiKeyAsync(AiProviderKind.ElevenLabs);
            HasStoredElevenLabsApiKey = false;
            ElevenLabsApiKeyStatusText = BrandText.ApiKeyNotStored;
        }
        catch
        {
            ElevenLabsApiKeyStatusText = BrandText.ApiKeyRemoveFailed;
        }
        finally
        {
            IsProviderOperationBusy = false;
        }
    }

    public void SetConversationVisible(bool isVisible)
    {
        if (isVisible && IsSettingsVisible)
        {
            SetSettingsVisible(false);
        }

        IsConversationVisible = isVisible;
        if (isVisible)
        {
            _ = RefreshStoredConversationsAndObserveAsync();
        }
    }

    public void ClearConversation()
    {
        if (!CanClearConversation())
        {
            return;
        }

        tutorInteractionService.ClearHistory();
        lastSuccessfulPreparation = null;
        FollowUpQuestion = string.Empty;
        OnPropertyChanged(nameof(CanContinueActiveConversation));
        SubmitFollowUpCommand.RaiseCanExecuteChanged();
        SelectedStoredConversation = null;
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
        if (!Enum.IsDefined(provider) || provider == AiProviderKind.ElevenLabs)
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
            lastSuccessfulPreparation = null;
            FollowUpQuestion = string.Empty;
            OnPropertyChanged(nameof(CanContinueActiveConversation));
            SubmitFollowUpCommand.RaiseCanExecuteChanged();
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
            if (IsOpenAIProvider && openAiOnboardingClient is not null)
            {
                await RunOpenAIOnboardingCoreAsync(preferredModelId: null);
            }
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

    public Task ConnectOrTestSelectedProviderAsync(string apiKey) =>
        IsOpenAIProvider && HasStoredApiKey && string.IsNullOrWhiteSpace(apiKey)
            ? TestOpenAIConnectionAsync()
            : SaveSelectedProviderApiKeyAsync(apiKey);

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
            if (IsOpenAIProvider)
            {
                ClearOpenAIOnboardingState();
            }
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

        CancelSpeechOutput();
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

    public async Task SubmitFollowUpAsync()
    {
        if (!CanSubmitFollowUp())
        {
            return;
        }

        var sourcePreparation = lastSuccessfulPreparation!;
        var submittedQuestion = FollowUpQuestion.Trim();
        var conversationTurnCountBeforeSubmission = ConversationTurns.Count;

        CancelSpeechOutput();
        CancelInteractionToken();
        ClearActiveQuestion();
        ResponseText = string.Empty;
        responseStatusText = BrandText.RespondingStatus;
        FollowUpQuestion = string.Empty;

        var interactionId = sessionCoordinator.BeginListening();
        var cancellationSource = new CancellationTokenSource();
        interactionCancellationSource = cancellationSource;

        try
        {
            try
            {
                await HideCueIfCurrentAsync(interactionId);
            }
            catch (Exception exception)
            {
                System.Diagnostics.Trace.TraceWarning(
                    "Clicky could not hide the previous cue before a follow-up: {0}",
                    exception.Message);
            }

            var preparation = tutorInteractionService.PrepareFollowUp(sourcePreparation);
            if (!sessionCoordinator.BeginProcessing(interactionId))
            {
                return;
            }

            SetActiveQuestion(interactionId, submittedQuestion);
            var responseSucceeded = await RespondWithPreparedInteractionAsync(
                interactionId,
                preparation,
                submittedQuestion,
                cancellationSource);
            if (!responseSucceeded &&
                !cancellationSource.IsCancellationRequested &&
                ConversationTurns.Count == conversationTurnCountBeforeSubmission)
            {
                FollowUpQuestion = submittedQuestion;
            }
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

    public async Task TestOpenAIConnectionAsync()
    {
        if (!CanTestOpenAIConnection())
        {
            return;
        }

        IsProviderOperationBusy = true;
        try
        {
            await RunOpenAIOnboardingCoreAsync(Settings.OpenAIModelId);
        }
        finally
        {
            IsProviderOperationBusy = false;
        }
    }

    public async Task TestSpeechOutputAsync()
    {
        if (!CanTestSpeechOutput())
        {
            return;
        }

        await StartSpeechOutputAsync(BrandText.TestVoicePhrase);
    }

    public async Task ReplayPointCueAsync(TutorConversationTurn turn)
    {
        ArgumentNullException.ThrowIfNull(turn);
        if (turn.MappedDesktopPoint is not { } mappedPoint)
        {
            return;
        }

        var expectedInteractionId = CurrentInteractionId;
        var replayGeneration = Interlocked.Increment(ref cuePresentationGeneration);

        await cueGate.WaitAsync();
        try
        {
            await pointCuePresenter.HideAsync();
        }
        finally
        {
            cueGate.Release();
        }

        await Task.Delay(90);

        if (Volatile.Read(ref cuePresentationGeneration) != replayGeneration ||
            CurrentInteractionId != expectedInteractionId)
        {
            return;
        }

        await cueGate.WaitAsync();
        try
        {
            if (Volatile.Read(ref cuePresentationGeneration) == replayGeneration &&
                CurrentInteractionId == expectedInteractionId)
            {
                await pointCuePresenter.ShowAsync(mappedPoint, turn.TargetLabel);
            }
        }
        finally
        {
            cueGate.Release();
        }
    }

    public Task BeginVoiceInteractionAsync()
    {
        if (dictationTranscriber is null || !CanBeginQuestion())
        {
            return Task.CompletedTask;
        }

        CancelSpeechOutput();
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
        CancelSpeechOutput();
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
        CancelSpeechOutput();
        CancelOpenAIOnboarding();
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

    private async Task<bool> RespondWithPreparedInteractionAsync(
        CompanionInteractionId interactionId,
        PreparedTutorInteraction preparation,
        string prompt,
        CancellationTokenSource cancellationSource)
    {
        var responseSucceeded = false;
        try
        {
            var streamedResponseText = new System.Text.StringBuilder();
            var responseProgress = new Progress<string>(textDelta =>
            {
                if (!IsCurrent(interactionId) || cancellationSource.IsCancellationRequested)
                {
                    return;
                }

                streamedResponseText.Append(textDelta);
                ResponseText = streamedResponseText.ToString().Trim();
            });
            var result = await tutorInteractionService.RespondAsync(
                preparation,
                prompt,
                responseProgress,
                cancellationSource.Token);
            if (!IsCurrent(interactionId) || cancellationSource.IsCancellationRequested)
            {
                return false;
            }

            LastInteractionResult = result;
            lastSuccessfulPreparation = preparation;
            selectedStoredConversation = null;
            OnPropertyChanged(nameof(SelectedStoredConversation));
            OnPropertyChanged(nameof(CanContinueActiveConversation));
            SubmitFollowUpCommand.RaiseCanExecuteChanged();
            RefreshConversationTurns(result);
            _ = RefreshStoredConversationsAndObserveAsync();
            await PresentResultCueIfCurrentAsync(interactionId, result);
            ShowResponse(interactionId, BrandText.RespondingStatus, result.SpokenText);
            _ = StartSpeechOutputAsync(result.SpokenText, interactionId, result);
            responseSucceeded = true;
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
        catch (GeminiProviderException exception)
        {
            await HideCueIfCurrentAsync(interactionId);
            var (status, detail) = DescribeGeminiFailure(exception);
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

        return responseSucceeded;
    }

    private Task StartSpeechOutputAsync(
        string responseText,
        CompanionInteractionId? interactionId = null,
        TutorInteractionResult? interactionResult = null)
    {
        CancelSpeechOutput();
        if (!Settings.SpeechOutputEnabled ||
            textToSpeechClient is null ||
            audioPlaybackService is null ||
            string.IsNullOrWhiteSpace(responseText))
        {
            return Task.CompletedTask;
        }

        var cancellationSource = new CancellationTokenSource();
        speechCancellationSource = cancellationSource;
        IsSpeechOutputBusy = true;
        SpeechOutputStatusText = BrandText.SpeechPreparing;
        return SynthesizeAndPlaySpeechAsync(
            responseText,
            cancellationSource,
            textToSpeechClient,
            audioPlaybackService,
            interactionId,
            interactionResult);
    }

    private async Task SynthesizeAndPlaySpeechAsync(
        string responseText,
        CancellationTokenSource cancellationSource,
        ITextToSpeechClient speechClient,
        IAudioPlaybackService playbackService,
        CompanionInteractionId? interactionId,
        TutorInteractionResult? interactionResult)
    {
        try
        {
            var speechAudio = await speechClient
                .SynthesizeAsync(responseText, cancellationSource.Token);
            await RefreshCueForNarrationIfCurrentAsync(
                interactionId,
                interactionResult,
                cancellationSource.Token);
            SpeechOutputStatusText = BrandText.SpeechPlaying;
            await playbackService.PlayAsync(speechAudio, cancellationSource.Token);
            SpeechOutputStatusText = BrandText.SpeechPlayed;
        }
        catch (OperationCanceledException) when (cancellationSource.IsCancellationRequested)
        {
            // A new question or explicit cancel stops speech without changing the text answer.
        }
        catch (Exception exception) when (
            exception is SpeechSynthesisException or AudioPlaybackException)
        {
            SpeechOutputStatusText = DescribeSpeechFailure(exception);
            System.Diagnostics.Trace.TraceWarning(
                "Clicky speech output was skipped: {0}",
                exception.Message);
        }
        catch (Exception exception)
        {
            SpeechOutputStatusText = "Voice failed unexpectedly. Try Test voice again.";
            System.Diagnostics.Trace.TraceWarning(
                "Clicky speech output failed unexpectedly: {0}",
                exception.Message);
        }
        finally
        {
            if (ReferenceEquals(speechCancellationSource, cancellationSource))
            {
                speechCancellationSource = null;
                IsSpeechOutputBusy = false;
            }

            cancellationSource.Dispose();
        }
    }

    private async Task RefreshCueForNarrationIfCurrentAsync(
        CompanionInteractionId? interactionId,
        TutorInteractionResult? interactionResult,
        CancellationToken cancellationToken)
    {
        if (interactionId is not { } currentInteractionId ||
            interactionResult?.MappedDesktopPoint is not { } mappedPoint ||
            !IsCurrent(currentInteractionId))
        {
            return;
        }

        await cueGate.WaitAsync(cancellationToken);
        try
        {
            if (IsCurrent(currentInteractionId))
            {
                Interlocked.Increment(ref cuePresentationGeneration);
                await pointCuePresenter.UpdateAsync(
                    mappedPoint,
                    interactionResult.TargetLabel,
                    cancellationToken);
            }
        }
        finally
        {
            cueGate.Release();
        }
    }

    private void CancelSpeechOutput()
    {
        var cancellationSource = speechCancellationSource;
        speechCancellationSource = null;
        cancellationSource?.Cancel();
        audioPlaybackService?.Stop();
        if (cancellationSource is not null)
        {
            IsSpeechOutputBusy = false;
            SpeechOutputStatusText = BrandText.SpeechReady;
        }
    }

    private bool CanTestSpeechOutput() =>
        IsSettingsVisible &&
        Settings.SpeechOutputEnabled &&
        State == CompanionSessionState.Idle &&
        !IsSpeechOutputBusy &&
        textToSpeechClient is not null &&
        audioPlaybackService is not null;

    private static string DescribeSpeechFailure(Exception exception)
    {
        if (exception is AudioPlaybackException)
        {
            return "Windows couldn't play the generated WAV audio.";
        }

        var synthesisFailure = (SpeechSynthesisException)exception;
        return synthesisFailure.FailureKind switch
        {
            SpeechSynthesisFailureKind.MissingApiKey =>
                "Voice needs the selected provider's API key.",
            SpeechSynthesisFailureKind.Authentication =>
                "The voice provider rejected the stored API key.",
            SpeechSynthesisFailureKind.Permission =>
                "This API key cannot use the configured voice model.",
            SpeechSynthesisFailureKind.RateLimited =>
                "Voice is rate limited or the account needs API credits.",
            SpeechSynthesisFailureKind.InvalidConfiguration =>
                "Check the voice model and voice name.",
            SpeechSynthesisFailureKind.Transport or SpeechSynthesisFailureKind.Server =>
                "The voice provider is temporarily unavailable.",
            _ => "The voice provider could not generate audio.",
        };
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

    private bool CanSubmitFollowUp() =>
        IsConversationVisible &&
        CanContinueActiveConversation &&
        !string.IsNullOrWhiteSpace(FollowUpQuestion) &&
        State is CompanionSessionState.Idle or CompanionSessionState.Responding;

    private bool CanChangeProviderSettings() =>
        IsSettingsVisible && State == CompanionSessionState.Idle && !IsProviderOperationBusy;

    private bool CanTestOpenAIConnection() =>
        CanChangeProviderSettings() &&
        IsOpenAIProvider &&
        HasStoredApiKey &&
        openAiOnboardingClient is not null;

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
            if (IsOpenAIProvider)
            {
                RefreshOpenAIOnboardingPresentation();
            }
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
        OnPropertyChanged(nameof(IsGeminiProvider));
        OnPropertyChanged(nameof(IsDirectProvider));
        OnPropertyChanged(nameof(CanSaveApiKey));
        OnPropertyChanged(nameof(SaveApiKeyButtonText));
        OnPropertyChanged(nameof(IsOpenAIOnboarded));
        TestOpenAIConnectionCommand.RaiseCanExecuteChanged();
        RaiseProviderCommandCanExecuteChanged();
    }

    private void RaiseProviderCommandCanExecuteChanged()
    {
        SelectWorkerProviderCommand.RaiseCanExecuteChanged();
        SelectAnthropicProviderCommand.RaiseCanExecuteChanged();
        SelectOpenAIProviderCommand.RaiseCanExecuteChanged();
        SelectGeminiProviderCommand.RaiseCanExecuteChanged();
        RemoveApiKeyCommand.RaiseCanExecuteChanged();
        TestOpenAIConnectionCommand.RaiseCanExecuteChanged();
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

    private async Task TestOpenAIConnectionAndObserveAsync()
    {
        try
        {
            await TestOpenAIConnectionAsync();
        }
        catch
        {
            OpenAIOnboardingStatusText = "Connection needs attention";
            OpenAIOnboardingDetailText = "Clicky could not complete the OpenAI check.";
        }
    }

    private async Task RunOpenAIOnboardingCoreAsync(string? preferredModelId)
    {
        if (openAiOnboardingClient is null)
        {
            return;
        }

        CancelOpenAIOnboarding();
        var cancellationSource = new CancellationTokenSource();
        openAIOnboardingCancellationSource = cancellationSource;

        OpenAIOnboardingStatusText = BrandText.OpenAIConnecting;
        OpenAIOnboardingDetailText = "Retrieving compatible models";
        HasDiscoveredOpenAIModels = false;
        Settings.OpenAIValidatedModelId = string.Empty;
        Settings.OpenAIValidatedAtUtc = null;
        OnPropertyChanged(nameof(IsOpenAIOnboarded));

        try
        {
            var result = await openAiOnboardingClient.DiscoverAndValidateAsync(
                preferredModelId,
                cancellationSource.Token);
            if (!ReferenceEquals(openAIOnboardingCancellationSource, cancellationSource))
            {
                return;
            }

            AvailableOpenAIModels = result.Models;
            HasDiscoveredOpenAIModels = result.Models.Count > 0;
            OpenAIModelRecommendationText =
                $"{BrandText.OpenAIModelRecommendation}: {result.RecommendedModelId}";
            Settings.OpenAIModelId = result.ValidatedModelId;
            Settings.OpenAIValidatedModelId = result.ValidatedModelId;
            Settings.OpenAIValidatedAtUtc = result.ValidatedAtUtc;
            if (settingsStore is not null)
            {
                await settingsStore.SaveAsync(Settings, cancellationSource.Token);
            }

            OpenAIOnboardingStatusText = BrandText.OpenAIReady;
            OpenAIOnboardingDetailText =
                $"{result.ValidatedModelId}  |  {result.Models.Count} compatible models";
            OnPropertyChanged(nameof(IsOpenAIOnboarded));
        }
        catch (OperationCanceledException) when (cancellationSource.IsCancellationRequested)
        {
        }
        catch (OpenAiProviderException exception)
        {
            OpenAIOnboardingStatusText = "Connection needs attention";
            OpenAIOnboardingDetailText = DescribeOpenAIOnboardingFailure(exception);
        }
        catch
        {
            OpenAIOnboardingStatusText = "Connection needs attention";
            OpenAIOnboardingDetailText = "Clicky could not complete the OpenAI check.";
        }
        finally
        {
            if (ReferenceEquals(openAIOnboardingCancellationSource, cancellationSource))
            {
                openAIOnboardingCancellationSource = null;
            }

            cancellationSource.Dispose();
        }
    }

    private void CancelOpenAIOnboarding()
    {
        var cancellationSource = openAIOnboardingCancellationSource;
        openAIOnboardingCancellationSource = null;
        cancellationSource?.Cancel();
    }

    private void RefreshOpenAIOnboardingPresentation()
    {
        if (!HasStoredApiKey)
        {
            ClearOpenAIOnboardingState();
            return;
        }

        if (IsOpenAIOnboarded)
        {
            OpenAIOnboardingStatusText = BrandText.OpenAIReady;
            OpenAIOnboardingDetailText =
                $"{Settings.OpenAIValidatedModelId}  |  checked {Settings.OpenAIValidatedAtUtc:MMM d}";
            return;
        }

        OpenAIOnboardingStatusText = "Key stored";
        OpenAIOnboardingDetailText = "Connection test pending";
    }

    private void ClearOpenAIOnboardingState()
    {
        AvailableOpenAIModels = [CompanionSettings.DefaultOpenAIModelId];
        HasDiscoveredOpenAIModels = false;
        OpenAIModelRecommendationText = string.Empty;
        Settings.OpenAIValidatedModelId = string.Empty;
        Settings.OpenAIValidatedAtUtc = null;
        OpenAIOnboardingStatusText = BrandText.OpenAIAddKey;
        OpenAIOnboardingDetailText = "Not connected";
        OnPropertyChanged(nameof(IsOpenAIOnboarded));
    }

    private static string DescribeOpenAIOnboardingFailure(OpenAiProviderException exception)
    {
        if (exception.ProviderCode is "insufficient_quota" or "billing_not_active")
        {
            return "API billing or credits are required for this account.";
        }

        if (exception.ProviderCode is "model_not_found" or "invalid_model" or "model_not_available")
        {
            return "The selected model is not available to this project.";
        }

        if (exception.ProviderCode == "no_compatible_models")
        {
            return "This project does not expose a compatible visual model. Check project model usage.";
        }

        if (exception.ProviderCode == "invalid_key_format")
        {
            return "Paste a fresh key without quotes or unsupported characters.";
        }

        return exception.FailureKind switch
        {
            OpenAiProviderFailureKind.MissingApiKey => "Add an OpenAI API key to continue.",
            OpenAiProviderFailureKind.Authentication => "OpenAI rejected this key. Replace it with an active API key.",
            OpenAiProviderFailureKind.Permission => "This key needs Models read access and Responses write access.",
            OpenAiProviderFailureKind.RateLimited => "OpenAI is rate limiting this project. Try again shortly.",
            OpenAiProviderFailureKind.Transport => "Clicky could not reach OpenAI from this Windows session.",
            OpenAiProviderFailureKind.Server => "OpenAI is temporarily unavailable. Try again shortly.",
            _ => string.IsNullOrWhiteSpace(exception.ProviderCode)
                ? "OpenAI could not validate this project."
                : $"OpenAI returned {exception.ProviderCode}.",
        };
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

    private async Task RefreshElevenLabsApiKeyStatusAndObserveAsync()
    {
        try
        {
            HasStoredElevenLabsApiKey = await apiKeyStore
                .HasApiKeyAsync(AiProviderKind.ElevenLabs);
            ElevenLabsApiKeyStatusText = HasStoredElevenLabsApiKey
                ? BrandText.ApiKeyStored
                : BrandText.ApiKeyNotStored;
        }
        catch
        {
            HasStoredElevenLabsApiKey = false;
            ElevenLabsApiKeyStatusText = BrandText.ApiKeyStatusFailed;
        }
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

    private void RefreshConversationTurns(TutorInteractionResult? latestResult = null)
    {
        var refreshedTurns = tutorInteractionService.GetConversationHistorySnapshot().ToArray();
        var previousTurnsByText = ConversationTurns
            .GroupBy(turn => (turn.UserText, turn.AssistantText))
            .ToDictionary(
                group => group.Key,
                group => new Queue<TutorConversationTurn>(group));

        for (var turnIndex = 0; turnIndex < refreshedTurns.Length; turnIndex++)
        {
            var refreshedTurn = refreshedTurns[turnIndex];
            var turnKey = (refreshedTurn.UserText, refreshedTurn.AssistantText);
            if (previousTurnsByText.TryGetValue(turnKey, out var matchingTurns) &&
                matchingTurns.Count > 0)
            {
                var previousTurn = matchingTurns.Dequeue();
                if (previousTurn.HasInteractionTarget)
                {
                    refreshedTurns[turnIndex] = refreshedTurn with
                    {
                        MappedDesktopPoint = previousTurn.MappedDesktopPoint,
                        TargetLabel = previousTurn.TargetLabel,
                    };
                }
            }
        }

        if (latestResult?.MappedDesktopPoint is { } latestPoint && refreshedTurns.Length > 0)
        {
            refreshedTurns[^1] = refreshedTurns[^1] with
            {
                MappedDesktopPoint = latestPoint,
                TargetLabel = latestResult.TargetLabel,
            };
        }

        ConversationTurns = refreshedTurns;
    }

    private async Task RefreshStoredConversationsAndObserveAsync()
    {
        try
        {
            StoredConversations = await tutorInteractionService
                .ListStoredConversationsAsync();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceWarning(
                "Clicky conversation history could not be loaded: {0}",
                exception.Message);
            StoredConversations = [];
        }
    }

    private async Task LoadSelectedStoredConversationAndObserveAsync(
        StoredConversation? storedConversation)
    {
        if (storedConversation is null)
        {
            RefreshConversationTurns();
            return;
        }

        try
        {
            var loadedTurns = await tutorInteractionService
                .LoadStoredConversationAsync(storedConversation.Id);
            if (SelectedStoredConversation?.Id == storedConversation.Id)
            {
                ConversationTurns = loadedTurns;
            }
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceWarning(
                "Clicky conversation could not be opened: {0}",
                exception.Message);
        }
    }

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

    private (string Status, string Detail) DescribeOpenAIFailure(
        OpenAiProviderException exception)
    {
        if (exception.ProviderCode is "insufficient_quota" or "billing_not_active")
        {
            return (
                BrandText.ProviderBusyStatus,
                "This OpenAI API account has no available credits. Check API billing, then try again.");
        }

        if (exception.ProviderCode is "model_not_found" or "invalid_model")
        {
            return (
                BrandText.ProviderSetupStatus,
                $"This key cannot use model {Settings.OpenAIModelId}. Choose another OpenAI model in Settings.");
        }

        if (exception.ProviderCode == "invalid_key_format")
        {
            return (
                BrandText.ProviderSetupStatus,
                "The stored OpenAI key contains an unsupported character. Paste it again and replace the saved key.");
        }

        return exception.FailureKind switch
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
                (BrandText.ProviderBusyStatus, DescribeUnknownOpenAIFailure(exception)),
        };
    }

    private static string DescribeUnknownOpenAIFailure(OpenAiProviderException exception) =>
        string.IsNullOrWhiteSpace(exception.ProviderCode)
            ? "OpenAI could not complete that request. Check the model and API billing, then try again."
            : $"OpenAI rejected that request ({exception.ProviderCode}). Check the model and API billing.";

    private static (string Status, string Detail) DescribeGeminiFailure(
        GeminiProviderException exception) => exception.FailureKind switch
        {
            GeminiProviderFailureKind.MissingApiKey or
            GeminiProviderFailureKind.InvalidConfiguration =>
                (BrandText.ProviderSetupStatus, "Save a Gemini key and model in Clicky settings."),
            GeminiProviderFailureKind.Authentication =>
                (BrandText.ProviderSetupStatus, "Gemini rejected the stored key. Replace it in settings."),
            GeminiProviderFailureKind.Permission =>
                (BrandText.ProviderBusyStatus, "This Gemini key cannot use the selected model."),
            GeminiProviderFailureKind.RateLimited =>
                (BrandText.ProviderBusyStatus, "Gemini is rate limiting this key. Give it a moment and try again."),
            GeminiProviderFailureKind.Safety =>
                ("Couldn't answer", "Gemini declined that request. Try asking in a different way."),
            GeminiProviderFailureKind.Server or GeminiProviderFailureKind.Transport =>
                (BrandText.ProviderBusyStatus, "Gemini is having trouble right now. Try again shortly."),
            _ =>
                (BrandText.ProviderBusyStatus, "Gemini could not complete that request. Check the model and try again."),
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
            Interlocked.Increment(ref cuePresentationGeneration);
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
            Interlocked.Increment(ref cuePresentationGeneration);
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
            Interlocked.Increment(ref cuePresentationGeneration);
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
        SubmitFollowUpCommand.RaiseCanExecuteChanged();
        CancelSessionCommand.RaiseCanExecuteChanged();
        TestSpeechOutputCommand.RaiseCanExecuteChanged();
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

        if (eventArgs.PropertyName == nameof(CompanionSettings.TextToSpeechProvider))
        {
            CancelSpeechOutput();
            OnPropertyChanged(nameof(IsOpenAISpeechProvider));
            OnPropertyChanged(nameof(IsElevenLabsSpeechProvider));
            OnPropertyChanged(nameof(CanSaveElevenLabsApiKey));
            SpeechOutputStatusText = BrandText.SpeechReady;
            TestSpeechOutputCommand.RaiseCanExecuteChanged();
        }

        if (eventArgs.PropertyName == nameof(CompanionSettings.SpeechOutputEnabled))
        {
            if (!Settings.SpeechOutputEnabled)
            {
                CancelSpeechOutput();
            }

            OnPropertyChanged(nameof(SpeechOutputStatusText));
            TestSpeechOutputCommand.RaiseCanExecuteChanged();
        }

        if (eventArgs.PropertyName is
            nameof(CompanionSettings.OpenAIModelId) or
            nameof(CompanionSettings.OpenAIValidatedModelId) or
            nameof(CompanionSettings.OpenAIValidatedAtUtc))
        {
            OnPropertyChanged(nameof(IsOpenAIOnboarded));
        }

        if (eventArgs.PropertyName == nameof(CompanionSettings.OpenAIModelId) &&
            !IsProviderOperationBusy &&
            IsOpenAIProvider &&
            HasStoredApiKey &&
            !IsOpenAIOnboarded)
        {
            Settings.OpenAIValidatedModelId = string.Empty;
            Settings.OpenAIValidatedAtUtc = null;
            OpenAIOnboardingStatusText = "Model changed";
            OpenAIOnboardingDetailText = "Connection test pending";
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

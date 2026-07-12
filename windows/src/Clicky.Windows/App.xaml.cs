using System.ComponentModel;
using System.Net.Http;
using Clicky.Windows.Capture;
using Clicky.Windows.Configuration;
using Clicky.Windows.Input;
using Clicky.Windows.Interaction;
using Clicky.Windows.Motion;
using Clicky.Windows.Networking;
using Clicky.Windows.Overlay;
using Clicky.Windows.Persistence;
using Clicky.Windows.Pointing;
using Clicky.Windows.Providers;
using Clicky.Windows.Session;
using Clicky.Windows.Shell;
using Clicky.Windows.ViewModels;
using Clicky.Windows.Voice;
using Clicky.Windows.Speech;

namespace Clicky.Windows;

public partial class App : System.Windows.Application
{
    private static readonly TimeSpan SplashDisplayDuration = TimeSpan.FromMilliseconds(1130);

    private CompanionWindow? companionWindow;
    private StartupSplashWindow? startupSplashWindow;
    private TrayIconHost? trayIconHost;
    private HttpClient? workerHttpClient;
    private AnthropicDirectApiClient? anthropicClient;
    private OpenAiResponsesClient? openAIClient;
    private OpenAiOnboardingClient? openAIOnboardingClient;
    private GeminiDirectApiClient? geminiClient;
    private OpenAiSpeechClient? openAISpeechClient;
    private ElevenLabsSpeechClient? elevenLabsSpeechClient;
    private WaveAudioPlaybackService? audioPlaybackService;
    private SqliteConversationRepository? conversationRepository;
    private IPointCuePresenter? pointCuePresenter;
    private CompanionSettings? settings;
    private CompanionSettingsStore? settingsStore;
    private CompanionViewModel? companionViewModel;
    private IGlobalPushToTalkMonitor? pushToTalkMonitor;
    private IDictationTranscriber? dictationTranscriber;
    private CancellationTokenSource? startupCancellationSource;
    private Task? startupSequence;

    protected override void OnStartup(System.Windows.StartupEventArgs startupEventArgs)
    {
        base.OnStartup(startupEventArgs);

        startupSplashWindow = new StartupSplashWindow();
        startupSplashWindow.Show();

        try
        {
            InitializeApplication();
            startupCancellationSource = new CancellationTokenSource();
            startupSequence = CompleteStartupAsync(startupCancellationSource.Token);
        }
        catch
        {
            startupSplashWindow.Close();
            startupSplashWindow = null;
            DisposeServices();
            throw;
        }
    }

    private void InitializeApplication()
    {
        settingsStore = new CompanionSettingsStore();
        settings = settingsStore.Load();
        settings.PropertyChanged += HandleSettingsPropertyChanged;
        var sessionCoordinator = new CompanionSessionCoordinator();
        var apiKeyStore = new WindowsCredentialApiKeyStore();
        workerHttpClient = new HttpClient(
            new HttpClientHandler { AllowAutoRedirect = false },
            disposeHandler: true);
        var workerClient = new SettingsAwareWorkerClient(workerHttpClient, settings);
        anthropicClient = AnthropicDirectApiClient.CreateProduction(apiKeyStore, settings);
        openAIClient = OpenAiResponsesClient.CreateProduction(apiKeyStore, settings);
        openAIOnboardingClient = OpenAiOnboardingClient.CreateProduction(apiKeyStore);
        geminiClient = GeminiDirectApiClient.CreateProduction(apiKeyStore, settings);
        var providerClient = new ProviderRoutingChatClient(
            settings,
            workerClient,
            anthropicClient,
            openAIClient,
            geminiClient);
        conversationRepository = new SqliteConversationRepository();
        var tutorInteractionService = new TutorInteractionService(
            new ActiveWindowCaptureService(settings),
            providerClient,
            new TutorInteractionOptions
            {
                ProviderContextAccessor = () => new ConversationProviderContext(
                    settings.SelectedProvider.ToString(),
                    GetSelectedModelId(settings)),
            },
            conversationRepository);
        openAISpeechClient = OpenAiSpeechClient.CreateProduction(apiKeyStore, settings);
        elevenLabsSpeechClient = ElevenLabsSpeechClient.CreateProduction(apiKeyStore, settings);
        var textToSpeechClient = new ProviderRoutingTextToSpeechClient(
            settings,
            openAISpeechClient,
            elevenLabsSpeechClient);
        audioPlaybackService = new WaveAudioPlaybackService();
        pointCuePresenter = new PointCuePresenter(
            new PointCuePresenterOptions
            {
                MotionEnabled = () => CompanionMotionPolicy.IsEnabled(settings),
                MotionOrigin = () => companionWindow?.GetPointCueOrigin(),
                PointerOrigin = DesktopCursorPositionProvider.TryGetCurrentPosition,
            });
        dictationTranscriber = new SystemSpeechDictationTranscriber();
        companionViewModel = new CompanionViewModel(
            sessionCoordinator,
            settings,
            tutorInteractionService,
            pointCuePresenter,
            apiKeyStore,
            dictationTranscriber,
            textToSpeechClient,
            audioPlaybackService,
            openAIOnboardingClient,
            settingsStore);

        companionWindow = new CompanionWindow(companionViewModel);
        trayIconHost = new TrayIconHost(
            showCompanion: ToggleCompanionWindow,
            openSettings: ShowSettings,
            exitApplication: ExitApplication);
        TryInitializePushToTalkMonitor();
    }

    private static string GetSelectedModelId(CompanionSettings settings) =>
        settings.SelectedProvider switch
        {
            AiProviderKind.Anthropic => settings.AnthropicModelId,
            AiProviderKind.OpenAI => settings.OpenAIModelId,
            AiProviderKind.Gemini => settings.GeminiModelId,
            _ => TutorInteractionOptions.DefaultModel,
        };

    private void TryInitializePushToTalkMonitor()
    {
        if (settings is null)
        {
            return;
        }

        try
        {
            pushToTalkMonitor = new GlobalPushToTalkMonitor(settings.PushToTalkKey);
            pushToTalkMonitor.Transitioned += HandlePushToTalkTransition;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceWarning(
                "Clicky push-to-talk is unavailable; typed questions remain enabled: {0}",
                exception.Message);
            pushToTalkMonitor?.Dispose();
            pushToTalkMonitor = null;
        }
    }

    private void HandlePushToTalkTransition(
        object? sender,
        PushToTalkTransitionEventArgs eventArgs)
    {
        var transition = eventArgs.Transition;
        _ = Dispatcher.BeginInvoke(() =>
        {
            var viewModel = companionViewModel;
            if (viewModel is null)
            {
                return;
            }

            var operation = transition.Kind == PushToTalkTransitionKind.Pressed
                ? viewModel.BeginVoiceInteractionAsync()
                : viewModel.CompleteVoiceInteractionAsync();
            _ = ObservePushToTalkOperationAsync(operation);
        });
    }

    private void HandleSettingsPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        _ = SaveSettingsAndObserveAsync();

        if (eventArgs.PropertyName != nameof(CompanionSettings.PushToTalkKey) ||
            settings is null || pushToTalkMonitor is null)
        {
            return;
        }

        pushToTalkMonitor.SelectedKey = settings.PushToTalkKey;
    }

    private async Task SaveSettingsAndObserveAsync()
    {
        if (settingsStore is null || settings is null)
        {
            return;
        }

        try
        {
            await settingsStore.SaveAsync(settings);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceWarning(
                "Clicky settings could not be saved: {0}",
                exception.Message);
        }
    }

    private static async Task ObservePushToTalkOperationAsync(Task operation)
    {
        try
        {
            await operation;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError(
                "Clicky push-to-talk interaction failed: {0}",
                exception);
        }
    }

    protected override void OnExit(System.Windows.ExitEventArgs exitEventArgs)
    {
        startupCancellationSource?.Cancel();
        startupCancellationSource?.Dispose();
        startupCancellationSource = null;
        startupSplashWindow?.Close();
        startupSplashWindow = null;
        DisposeServices();

        base.OnExit(exitEventArgs);
    }

    private async Task CompleteStartupAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(SplashDisplayDuration, cancellationToken);
            if (startupSplashWindow is not null)
            {
                await startupSplashWindow.CloseWithAnimationAsync();
                startupSplashWindow = null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (settings?.SelectedProvider == AiProviderKind.OpenAI &&
                (settings.OpenAIValidatedAtUtc is null ||
                 !string.Equals(
                     settings.OpenAIValidatedModelId,
                     settings.OpenAIModelId,
                     StringComparison.Ordinal)))
            {
                companionWindow?.ShowSettings();
            }
            else
            {
                companionWindow?.ShowNearPrimaryWorkArea();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError("Clicky startup failed: {0}", exception);
            Shutdown(-1);
        }
    }

    private void ToggleCompanionWindow()
    {
        if (companionWindow is null)
        {
            return;
        }

        if (companionWindow.IsVisible)
        {
            companionWindow.HideCompanion();
        }
        else
        {
            companionWindow.ShowNearPrimaryWorkArea();
        }
    }

    private void ShowSettings()
    {
        companionWindow?.ShowSettings();
    }

    private void ExitApplication()
    {
        startupCancellationSource?.Cancel();
        trayIconHost?.Dispose();
        trayIconHost = null;
        startupSplashWindow?.Close();
        startupSplashWindow = null;
        companionWindow?.ClosePermanently();
        Shutdown();
    }

    private void DisposeServices()
    {
        if (settings is not null)
        {
            settings.PropertyChanged -= HandleSettingsPropertyChanged;
            try
            {
                settingsStore?.SaveAsync(settings).GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                System.Diagnostics.Trace.TraceWarning(
                    "Clicky settings could not be saved during shutdown: {0}",
                    exception.Message);
            }
        }

        if (pushToTalkMonitor is not null)
        {
            pushToTalkMonitor.Transitioned -= HandlePushToTalkTransition;
            pushToTalkMonitor.Dispose();
            pushToTalkMonitor = null;
        }

        companionViewModel?.Dispose();
        companionViewModel = null;
        dictationTranscriber?.Dispose();
        dictationTranscriber = null;
        settings = null;
        trayIconHost?.Dispose();
        trayIconHost = null;
        try
        {
            pointCuePresenter?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            pointCuePresenter = null;
        }
        finally
        {
            anthropicClient?.Dispose();
            anthropicClient = null;
            openAIClient?.Dispose();
            openAIClient = null;
            openAIOnboardingClient?.Dispose();
            openAIOnboardingClient = null;
            geminiClient?.Dispose();
            geminiClient = null;
            openAISpeechClient?.Dispose();
            openAISpeechClient = null;
            elevenLabsSpeechClient?.Dispose();
            elevenLabsSpeechClient = null;
            audioPlaybackService?.Dispose();
            audioPlaybackService = null;
            conversationRepository?.Dispose();
            conversationRepository = null;
            workerHttpClient?.Dispose();
            workerHttpClient = null;
        }
    }
}

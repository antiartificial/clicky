using Clicky.Windows.Mvvm;
using Clicky.Windows.Input;
using Clicky.Windows.Providers;

namespace Clicky.Windows.Configuration;

public sealed class CompanionSettings : ObservableObject
{
    public static readonly Uri DefaultWorkerBaseUrl =
        new("https://your-worker-name.your-subdomain.workers.dev");

    public const string DefaultAnthropicModelId = "claude-sonnet-4-6";
    public const string DefaultOpenAIModelId = "gpt-5.4-mini";

    private Uri workerBaseUrl = DefaultWorkerBaseUrl;
    private AiProviderKind selectedProvider = AiProviderKind.Worker;
    private string anthropicModelId = DefaultAnthropicModelId;
    private string openAIModelId = DefaultOpenAIModelId;
    private bool captureAllDisplays;
    private bool captureOnlyDuringActiveRequest = true;
    private bool excludeCompanionWindows = true;
    private bool includePointerInCaptures;
    private bool optimizeLargeCaptures = true;
    private bool retainCapturesLocally;
    private bool motionEffectsEnabled = true;
    private PushToTalkKey pushToTalkKey = PushToTalkKey.F13;

    public Uri WorkerBaseUrl
    {
        get => workerBaseUrl;
        set => SetProperty(ref workerBaseUrl, value ?? throw new ArgumentNullException(nameof(value)));
    }

    public AiProviderKind SelectedProvider
    {
        get => selectedProvider;
        set
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "The AI provider is not supported.");
            }

            SetProperty(ref selectedProvider, value);
        }
    }

    public string AnthropicModelId
    {
        get => anthropicModelId;
        set => SetProperty(
            ref anthropicModelId,
            value ?? throw new ArgumentNullException(nameof(value)));
    }

    public string OpenAIModelId
    {
        get => openAIModelId;
        set => SetProperty(
            ref openAIModelId,
            value ?? throw new ArgumentNullException(nameof(value)));
    }

    public bool CaptureAllDisplays
    {
        get => captureAllDisplays;
        set => SetProperty(ref captureAllDisplays, value);
    }

    public bool CaptureOnlyDuringActiveRequest
    {
        get => captureOnlyDuringActiveRequest;
        set => SetProperty(ref captureOnlyDuringActiveRequest, value);
    }

    public bool ExcludeCompanionWindows
    {
        get => excludeCompanionWindows;
        set => SetProperty(ref excludeCompanionWindows, value);
    }

    public bool IncludePointerInCaptures
    {
        get => includePointerInCaptures;
        set => SetProperty(ref includePointerInCaptures, value);
    }

    public bool OptimizeLargeCaptures
    {
        get => optimizeLargeCaptures;
        set => SetProperty(ref optimizeLargeCaptures, value);
    }

    public bool RetainCapturesLocally
    {
        get => retainCapturesLocally;
        set => SetProperty(ref retainCapturesLocally, value);
    }

    public bool MotionEffectsEnabled
    {
        get => motionEffectsEnabled;
        set => SetProperty(ref motionEffectsEnabled, value);
    }

    public PushToTalkKey PushToTalkKey
    {
        get => pushToTalkKey;
        set
        {
            PushToTalkKeyValidator.Validate(value, nameof(value));
            SetProperty(ref pushToTalkKey, value);
        }
    }
}

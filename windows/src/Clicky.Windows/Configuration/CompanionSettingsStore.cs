using System.IO;
using System.Text.Json;

namespace Clicky.Windows.Configuration;

public sealed class CompanionSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string settingsFilePath;
    private readonly SemaphoreSlim saveGate = new(1, 1);

    public CompanionSettingsStore(string? settingsFilePath = null)
    {
        this.settingsFilePath = settingsFilePath ?? GetDefaultSettingsFilePath();
    }

    public CompanionSettings Load()
    {
        var settings = new CompanionSettings();
        if (!File.Exists(settingsFilePath))
        {
            return settings;
        }

        try
        {
            var json = File.ReadAllText(settingsFilePath);
            var persistedSettings = JsonSerializer.Deserialize<PersistedSettings>(
                json,
                SerializerOptions);
            persistedSettings?.ApplyTo(settings);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            System.Diagnostics.Trace.TraceWarning(
                "Clicky settings could not be loaded; defaults will be used: {0}",
                exception.Message);
        }

        return settings;
    }

    public async Task SaveAsync(
        CompanionSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directoryPath = Path.GetDirectoryName(settingsFilePath);
            if (string.IsNullOrWhiteSpace(directoryPath))
            {
                throw new InvalidOperationException("The Clicky settings path has no parent directory.");
            }

            Directory.CreateDirectory(directoryPath);
            var temporaryPath = settingsFilePath + ".tmp";
            var json = JsonSerializer.Serialize(PersistedSettings.From(settings), SerializerOptions);
            await File.WriteAllTextAsync(temporaryPath, json, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, settingsFilePath, overwrite: true);
        }
        finally
        {
            saveGate.Release();
        }
    }

    public static string GetDefaultSettingsFilePath()
    {
        var localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localApplicationData, "Clicky", "settings.json");
    }

    private sealed record PersistedSettings(
        string? SelectedProvider,
        string? WorkerBaseUrl,
        string? AnthropicModelId,
        string? OpenAIModelId,
        string? GeminiModelId,
        bool CaptureAllDisplays,
        bool CaptureOnlyDuringActiveRequest,
        bool ExcludeCompanionWindows,
        bool IncludePointerInCaptures,
        bool OptimizeLargeCaptures,
        bool RetainCapturesLocally,
        bool MotionEffectsEnabled,
        string? PushToTalkKey,
        bool SpeechOutputEnabled,
        string? TextToSpeechProvider,
        string? OpenAITtsModelId,
        string? OpenAITtsVoice,
        string? ElevenLabsVoiceId,
        string? OpenAIValidatedModelId,
        DateTimeOffset? OpenAIValidatedAtUtc)
    {
        public static PersistedSettings From(CompanionSettings settings) =>
            new(
                settings.SelectedProvider.ToString(),
                settings.WorkerBaseUrl.AbsoluteUri,
                settings.AnthropicModelId,
                settings.OpenAIModelId,
                settings.GeminiModelId,
                settings.CaptureAllDisplays,
                settings.CaptureOnlyDuringActiveRequest,
                settings.ExcludeCompanionWindows,
                settings.IncludePointerInCaptures,
                settings.OptimizeLargeCaptures,
                settings.RetainCapturesLocally,
                settings.MotionEffectsEnabled,
                settings.PushToTalkKey.ToString(),
                settings.SpeechOutputEnabled,
                settings.TextToSpeechProvider.ToString(),
                settings.OpenAITtsModelId,
                settings.OpenAITtsVoice,
                settings.ElevenLabsVoiceId,
                settings.OpenAIValidatedModelId,
                settings.OpenAIValidatedAtUtc);

        public void ApplyTo(CompanionSettings settings)
        {
            if (Enum.TryParse<Providers.AiProviderKind>(SelectedProvider, out var selectedProvider) &&
                selectedProvider != Providers.AiProviderKind.ElevenLabs)
            {
                settings.SelectedProvider = selectedProvider;
            }

            if (Uri.TryCreate(WorkerBaseUrl, UriKind.Absolute, out var workerBaseUrl))
            {
                settings.WorkerBaseUrl = workerBaseUrl;
            }

            settings.AnthropicModelId = AnthropicModelId ?? CompanionSettings.DefaultAnthropicModelId;
            settings.OpenAIModelId = OpenAIModelId ?? CompanionSettings.DefaultOpenAIModelId;
            settings.GeminiModelId = GeminiModelId ?? CompanionSettings.DefaultGeminiModelId;
            settings.CaptureAllDisplays = CaptureAllDisplays;
            settings.CaptureOnlyDuringActiveRequest = CaptureOnlyDuringActiveRequest;
            settings.ExcludeCompanionWindows = ExcludeCompanionWindows;
            settings.IncludePointerInCaptures = IncludePointerInCaptures;
            settings.OptimizeLargeCaptures = OptimizeLargeCaptures;
            settings.RetainCapturesLocally = RetainCapturesLocally;
            settings.MotionEffectsEnabled = MotionEffectsEnabled;
            if (Enum.TryParse<Input.PushToTalkKey>(PushToTalkKey, out var pushToTalkKey))
            {
                settings.PushToTalkKey = pushToTalkKey;
            }

            settings.SpeechOutputEnabled = SpeechOutputEnabled;
            if (Enum.TryParse<TextToSpeechProviderKind>(TextToSpeechProvider, out var speechProvider))
            {
                settings.TextToSpeechProvider = speechProvider;
            }

            settings.OpenAITtsModelId = OpenAITtsModelId ?? CompanionSettings.DefaultOpenAITtsModelId;
            settings.OpenAITtsVoice = OpenAITtsVoice ?? CompanionSettings.DefaultOpenAITtsVoice;
            settings.ElevenLabsVoiceId = ElevenLabsVoiceId ?? string.Empty;
            settings.OpenAIValidatedModelId = OpenAIValidatedModelId ?? string.Empty;
            settings.OpenAIValidatedAtUtc = OpenAIValidatedAtUtc;
        }
    }
}

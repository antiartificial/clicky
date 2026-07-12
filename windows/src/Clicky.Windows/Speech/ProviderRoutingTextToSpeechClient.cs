using Clicky.Windows.Configuration;

namespace Clicky.Windows.Speech;

public sealed class ProviderRoutingTextToSpeechClient : ITextToSpeechClient
{
    private readonly CompanionSettings settings;
    private readonly ITextToSpeechClient openAiClient;
    private readonly ITextToSpeechClient elevenLabsClient;

    public ProviderRoutingTextToSpeechClient(
        CompanionSettings settings,
        ITextToSpeechClient openAiClient,
        ITextToSpeechClient elevenLabsClient)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(openAiClient);
        ArgumentNullException.ThrowIfNull(elevenLabsClient);

        this.settings = settings;
        this.openAiClient = openAiClient;
        this.elevenLabsClient = elevenLabsClient;
    }

    public Task<SpeechAudio> SynthesizeAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        var provider = settings.TextToSpeechProvider;
        return provider switch
        {
            TextToSpeechProviderKind.OpenAI =>
                openAiClient.SynthesizeAsync(text, cancellationToken),
            TextToSpeechProviderKind.ElevenLabs =>
                elevenLabsClient.SynthesizeAsync(text, cancellationToken),
            _ => throw new SpeechSynthesisException(
                SpeechSynthesisProvider.OpenAI,
                SpeechSynthesisFailureKind.InvalidConfiguration,
                "The text-to-speech provider is not supported."),
        };
    }
}

using Clicky.Windows.Configuration;
using Clicky.Windows.Providers;

namespace Clicky.Windows.Networking;

public sealed class ProviderRoutingChatClient : IWorkerClient
{
    private readonly CompanionSettings settings;
    private readonly IWorkerClient workerClient;
    private readonly IWorkerClient anthropicClient;
    private readonly IWorkerClient openAIClient;

    public ProviderRoutingChatClient(
        CompanionSettings settings,
        IWorkerClient workerClient,
        IWorkerClient anthropicClient,
        IWorkerClient openAIClient)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(workerClient);
        ArgumentNullException.ThrowIfNull(anthropicClient);
        ArgumentNullException.ThrowIfNull(openAIClient);

        this.settings = settings;
        this.workerClient = workerClient;
        this.anthropicClient = anthropicClient;
        this.openAIClient = openAIClient;
    }

    public IAsyncEnumerable<string> StreamChatAsync(
        WorkerChatRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Resolve before enumeration begins so one request cannot jump providers mid-stream.
        var selectedClient = settings.SelectedProvider switch
        {
            AiProviderKind.Worker => workerClient,
            AiProviderKind.Anthropic => anthropicClient,
            AiProviderKind.OpenAI => openAIClient,
            _ => throw new InvalidOperationException("The selected AI provider is not supported."),
        };

        return selectedClient.StreamChatAsync(request, cancellationToken);
    }
}

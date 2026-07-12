using System.Net.Http;
using System.Runtime.CompilerServices;
using Clicky.Windows.Configuration;

namespace Clicky.Windows.Networking;

public sealed class SettingsAwareWorkerClient : IWorkerClient
{
    private readonly HttpClient httpClient;
    private readonly CompanionSettings settings;

    public SettingsAwareWorkerClient(HttpClient httpClient, CompanionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(settings);

        this.httpClient = httpClient;
        this.settings = settings;
    }

    public async IAsyncEnumerable<string> StreamChatAsync(
        WorkerChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var workerBaseUrl = settings.WorkerBaseUrl;
        if (IsPlaceholder(workerBaseUrl))
        {
            throw new WorkerConfigurationException(
                "Set your Worker URL in Clicky settings before asking a question.");
        }

        var workerClient = new CloudflareWorkerClient(httpClient, workerBaseUrl);
        await foreach (var chunk in workerClient
            .StreamChatAsync(request, cancellationToken)
            .ConfigureAwait(false))
        {
            yield return chunk;
        }
    }

    public static bool IsPlaceholder(Uri workerBaseUrl)
    {
        ArgumentNullException.ThrowIfNull(workerBaseUrl);

        return string.Equals(
            workerBaseUrl.Host,
            CompanionSettings.DefaultWorkerBaseUrl.Host,
            StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class WorkerConfigurationException(string message) : InvalidOperationException(message);

namespace Clicky.Windows.Networking;

public interface IWorkerClient
{
    IAsyncEnumerable<string> StreamChatAsync(
        WorkerChatRequest request,
        CancellationToken cancellationToken = default);
}

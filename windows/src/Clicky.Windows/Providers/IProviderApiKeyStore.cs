namespace Clicky.Windows.Providers;

public interface IProviderApiKeyStore
{
    Task<string?> GetApiKeyAsync(
        AiProviderKind provider,
        CancellationToken cancellationToken = default);

    Task SaveApiKeyAsync(
        AiProviderKind provider,
        string apiKey,
        CancellationToken cancellationToken = default);

    Task DeleteApiKeyAsync(
        AiProviderKind provider,
        CancellationToken cancellationToken = default);

    Task<bool> HasApiKeyAsync(
        AiProviderKind provider,
        CancellationToken cancellationToken = default);
}

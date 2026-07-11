namespace Clicky.Windows.Persistence;

public interface ILocalConversationRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<StoredConversation> CreateConversationAsync(
        ConversationCreateRequest request,
        CancellationToken cancellationToken = default);

    Task<StoredConversation?> GetConversationAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StoredConversation>> ListConversationsAsync(
        CancellationToken cancellationToken = default);

    Task<StoredConversationTurn> AppendTurnAsync(
        Guid conversationId,
        ConversationTurnWrite turn,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StoredConversationMessage>> LoadMessagesAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StoredConversationTurn>> LoadTurnsAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default);

    Task<bool> UpdateSummaryAsync(
        Guid conversationId,
        ConversationSummaryUpdate update,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteConversationAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default);

    Task<int> ClearAsync(CancellationToken cancellationToken = default);
}

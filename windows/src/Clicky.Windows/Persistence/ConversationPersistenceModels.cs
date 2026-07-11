namespace Clicky.Windows.Persistence;

public enum ConversationMessageRole
{
    User,
    Assistant,
}

public sealed record ConversationCreateRequest
{
    public string? Title { get; init; }

    public string? RollingSummary { get; init; }

    public string? Provider { get; init; }

    public string? Model { get; init; }

    public string? ApplicationName { get; init; }

    public string? WindowTitle { get; init; }

    public DateTimeOffset? CreatedAtUtc { get; init; }
}

public sealed record ConversationTurnWrite
{
    public required string UserText { get; init; }

    public required string AssistantText { get; init; }

    public string? Provider { get; init; }

    public string? Model { get; init; }

    public string? ApplicationName { get; init; }

    public string? WindowTitle { get; init; }

    public DateTimeOffset? CreatedAtUtc { get; init; }
}

public sealed record ConversationSummaryUpdate
{
    public string? Title { get; init; }

    public string? RollingSummary { get; init; }

    public DateTimeOffset? UpdatedAtUtc { get; init; }
}

public sealed record StoredConversation(
    Guid Id,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string? Title,
    string? RollingSummary,
    string? Provider,
    string? Model,
    string? ApplicationName,
    string? WindowTitle,
    int TurnCount);

public sealed record StoredConversationMessage(
    long Id,
    Guid ConversationId,
    int TurnIndex,
    ConversationMessageRole Role,
    string Content,
    DateTimeOffset CreatedAtUtc,
    string? Provider,
    string? Model,
    string? ApplicationName,
    string? WindowTitle);

public sealed record StoredConversationTurn(
    int TurnIndex,
    StoredConversationMessage UserMessage,
    StoredConversationMessage AssistantMessage);

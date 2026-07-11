namespace Clicky.Windows.Networking;

public sealed class WorkerChatRequest
{
    public WorkerChatRequest(
        string model,
        string systemPrompt,
        string userPrompt,
        IReadOnlyList<WorkerChatImage>? images = null,
        IReadOnlyList<WorkerConversationTurn>? conversationHistory = null,
        int maxTokens = 1024)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(systemPrompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(userPrompt);

        if (maxTokens <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTokens), "Maximum tokens must be greater than zero.");
        }

        Model = model;
        SystemPrompt = systemPrompt;
        UserPrompt = userPrompt;
        Images = images?.ToArray() ?? [];
        ConversationHistory = conversationHistory?.ToArray() ?? [];
        MaxTokens = maxTokens;
    }

    public string Model { get; }

    public string SystemPrompt { get; }

    public string UserPrompt { get; }

    public IReadOnlyList<WorkerChatImage> Images { get; }

    public IReadOnlyList<WorkerConversationTurn> ConversationHistory { get; }

    public int MaxTokens { get; }
}

public sealed class WorkerChatImage
{
    private readonly byte[] imageBytes;

    public WorkerChatImage(
        byte[] imageBytes,
        string label,
        string mediaType = "image/jpeg")
    {
        ArgumentNullException.ThrowIfNull(imageBytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);

        if (imageBytes.Length == 0)
        {
            throw new ArgumentException("Image data cannot be empty.", nameof(imageBytes));
        }

        this.imageBytes = imageBytes.ToArray();
        Label = label;
        MediaType = mediaType;
    }

    public ReadOnlyMemory<byte> ImageBytes => imageBytes;

    public string Label { get; }

    public string MediaType { get; }
}

public sealed class WorkerConversationTurn
{
    public WorkerConversationTurn(string userText, string assistantText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userText);
        ArgumentException.ThrowIfNullOrWhiteSpace(assistantText);

        UserText = userText;
        AssistantText = assistantText;
    }

    public string UserText { get; }

    public string AssistantText { get; }
}

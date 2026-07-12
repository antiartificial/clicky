using Clicky.Windows.Capture;
using Clicky.Windows.Networking;
using Clicky.Windows.Pointing;

namespace Clicky.Windows.Interaction;

public sealed record TutorCaptureMetadata(
    string WindowTitle,
    int EncodedPixelWidth,
    int EncodedPixelHeight,
    PhysicalPixelBounds PhysicalPixelBounds)
{
    public CaptureImagePixelSize EncodedImageSize =>
        new(EncodedPixelWidth, EncodedPixelHeight);
}

public sealed class PreparedTutorInteraction
{
    internal PreparedTutorInteraction(
        long interactionId,
        long historyGeneration,
        CaptureResult capture,
        IReadOnlyList<WorkerConversationTurn> conversationHistory)
    {
        InteractionId = interactionId;
        HistoryGeneration = historyGeneration;
        Capture = capture;
        ConversationHistory = conversationHistory;
        CaptureMetadata = new TutorCaptureMetadata(
            capture.WindowTitle,
            capture.PixelWidth,
            capture.PixelHeight,
            capture.PhysicalPixelBounds);
    }

    public long InteractionId { get; }

    public TutorCaptureMetadata CaptureMetadata { get; }

    internal long HistoryGeneration { get; }

    internal CaptureResult Capture { get; }

    internal IReadOnlyList<WorkerConversationTurn> ConversationHistory { get; }
}

public sealed record TutorInteractionResult(
    string SpokenText,
    string FullAssistantText,
    TutorCaptureMetadata CaptureMetadata,
    DesktopPoint? MappedDesktopPoint,
    string? TargetLabel);

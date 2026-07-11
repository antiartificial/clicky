using System.IO;
using System.Text;
using Clicky.Windows.Capture;
using Clicky.Windows.Networking;
using Clicky.Windows.Pointing;
using Clicky.Windows.Tutoring;

namespace Clicky.Windows.Interaction;

public sealed class TutorInteractionService
{
    private const int MaximumHistoryTurns = 10;

    private readonly IActiveWindowCaptureService captureService;
    private readonly IWorkerClient workerClient;
    private readonly string model;
    private readonly object historyLock = new();
    private readonly List<WorkerConversationTurn> conversationHistory = [];

    private long nextInteractionId;
    private long currentInteractionId;
    private long historyGeneration;

    public TutorInteractionService(
        IActiveWindowCaptureService captureService,
        IWorkerClient workerClient,
        TutorInteractionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(captureService);
        ArgumentNullException.ThrowIfNull(workerClient);

        options ??= new TutorInteractionOptions();
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Model);

        this.captureService = captureService;
        this.workerClient = workerClient;
        model = options.Model;
    }

    public int ConversationTurnCount
    {
        get
        {
            lock (historyLock)
            {
                return conversationHistory.Count;
            }
        }
    }

    public IReadOnlyList<TutorConversationTurn> GetConversationHistorySnapshot()
    {
        lock (historyLock)
        {
            return conversationHistory
                .Select(turn => new TutorConversationTurn(
                    turn.UserText,
                    turn.AssistantText))
                .ToArray();
        }
    }

    public async Task<TutorInteractionResult> RespondAsync(
        string userPrompt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userPrompt);
        cancellationToken.ThrowIfCancellationRequested();

        var interaction = await PrepareAsync(cancellationToken).ConfigureAwait(false);
        return await RespondAsync(interaction, userPrompt, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PreparedTutorInteraction> PrepareAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var interaction = BeginInteraction();
        var capture = await captureService.CaptureAsync(cancellationToken).ConfigureAwait(false);

        // A capture provider may finish normally even when cancellation was requested.
        cancellationToken.ThrowIfCancellationRequested();

        return new PreparedTutorInteraction(
            interaction.Id,
            interaction.HistoryGeneration,
            capture,
            interaction.History);
    }

    public async Task<TutorInteractionResult> RespondAsync(
        PreparedTutorInteraction interaction,
        string userPrompt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(interaction);
        ArgumentException.ThrowIfNullOrWhiteSpace(userPrompt);
        cancellationToken.ThrowIfCancellationRequested();

        var capture = interaction.Capture;

        var request = new WorkerChatRequest(
            model,
            VisualGuideTutor.SystemPrompt,
            userPrompt,
            images:
            [
                new WorkerChatImage(
                    capture.JpegBytes.ToArray(),
                    BuildImageLabel(capture))
            ],
            conversationHistory: interaction.ConversationHistory);

        var responseBuilder = new StringBuilder();
        await foreach (var textChunk in workerClient
            .StreamChatAsync(request, cancellationToken)
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            responseBuilder.Append(textChunk);
        }

        cancellationToken.ThrowIfCancellationRequested();

        var fullAssistantText = responseBuilder.ToString();
        var pointResponse = PointResponseParser.Parse(fullAssistantText);
        if (!pointResponse.HasPointDirective)
        {
            throw new InvalidDataException(
                "The visual guide response must end with a POINT directive.");
        }

        var mappedDesktopPoint = MapPoint(pointResponse.Target, capture);

        RememberInteractionIfCurrent(
            interaction,
            userPrompt,
            pointResponse.SpokenText,
            cancellationToken);

        return new TutorInteractionResult(
            pointResponse.SpokenText,
            fullAssistantText,
            interaction.CaptureMetadata,
            mappedDesktopPoint,
            pointResponse.Target?.ElementLabel);
    }

    public void ClearHistory()
    {
        lock (historyLock)
        {
            conversationHistory.Clear();
            historyGeneration++;
        }
    }

    private InteractionContext BeginInteraction()
    {
        lock (historyLock)
        {
            currentInteractionId = ++nextInteractionId;
            return new InteractionContext(
                currentInteractionId,
                historyGeneration,
                conversationHistory.ToArray());
        }
    }

    private void RememberInteractionIfCurrent(
        PreparedTutorInteraction interaction,
        string userPrompt,
        string assistantText,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(assistantText))
        {
            return;
        }

        lock (historyLock)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (interaction.InteractionId != currentInteractionId ||
                interaction.HistoryGeneration != historyGeneration)
            {
                return;
            }

            conversationHistory.Add(new WorkerConversationTurn(userPrompt, assistantText));
            if (conversationHistory.Count > MaximumHistoryTurns)
            {
                conversationHistory.RemoveRange(
                    0,
                    conversationHistory.Count - MaximumHistoryTurns);
            }
        }
    }

    private static string BuildImageLabel(CaptureResult capture) =>
        $"Primary screen, foreground window \"{capture.WindowTitle}\" " +
        $"(image dimensions: {capture.PixelWidth}x{capture.PixelHeight} pixels)";

    private static DesktopPoint? MapPoint(PointTarget? target, CaptureResult capture)
    {
        if (target is null)
        {
            return null;
        }

        var coordinateSpaces = new[]
        {
            new ScreenCaptureCoordinateSpace(
                screenNumber: 1,
                isCursorScreen: true,
                capture.ImageSize,
                capture.PhysicalPixelBounds)
        };

        return CoordinateMapper.ResolveAndMapToDesktop(target, coordinateSpaces);
    }

    private sealed record InteractionContext(
        long Id,
        long HistoryGeneration,
        IReadOnlyList<WorkerConversationTurn> History);
}

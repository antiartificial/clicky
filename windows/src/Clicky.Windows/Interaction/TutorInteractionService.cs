using System.IO;
using System.Text;
using Clicky.Windows.Capture;
using Clicky.Windows.Networking;
using Clicky.Windows.Pointing;
using Clicky.Windows.Persistence;
using Clicky.Windows.Tutoring;

namespace Clicky.Windows.Interaction;

public sealed class TutorInteractionService
{
    private const int MaximumHistoryTurns = 10;
    private const int MaximumConversationTitleLength = 72;
    private const int MaximumConversationSummaryLength = 220;

    private readonly IActiveWindowCaptureService captureService;
    private readonly IWorkerClient workerClient;
    private readonly string model;
    private readonly object historyLock = new();
    private readonly List<WorkerConversationTurn> conversationHistory = [];
    private readonly ILocalConversationRepository? conversationRepository;
    private readonly Func<ConversationProviderContext>? providerContextAccessor;

    private long nextInteractionId;
    private long currentInteractionId;
    private long historyGeneration;
    private Guid? activeStoredConversationId;

    public TutorInteractionService(
        IActiveWindowCaptureService captureService,
        IWorkerClient workerClient,
        TutorInteractionOptions? options = null,
        ILocalConversationRepository? conversationRepository = null)
    {
        ArgumentNullException.ThrowIfNull(captureService);
        ArgumentNullException.ThrowIfNull(workerClient);

        options ??= new TutorInteractionOptions();
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Model);

        this.captureService = captureService;
        this.workerClient = workerClient;
        model = options.Model;
        providerContextAccessor = options.ProviderContextAccessor;
        this.conversationRepository = conversationRepository;
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
        return await RespondAsync(
            userPrompt,
            responseProgress: null,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<TutorInteractionResult> RespondAsync(
        string userPrompt,
        IProgress<string>? responseProgress,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userPrompt);
        cancellationToken.ThrowIfCancellationRequested();

        var interaction = await PrepareAsync(cancellationToken).ConfigureAwait(false);
        return await RespondAsync(
            interaction,
            userPrompt,
            responseProgress,
            cancellationToken).ConfigureAwait(false);
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
        return await RespondAsync(
            interaction,
            userPrompt,
            responseProgress: null,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<TutorInteractionResult> RespondAsync(
        PreparedTutorInteraction interaction,
        string userPrompt,
        IProgress<string>? responseProgress,
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
        var streamingFilter = new PointDirectiveStreamingFilter();
        await foreach (var textChunk in workerClient
            .StreamChatAsync(request, cancellationToken)
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            responseBuilder.Append(textChunk);
            var visibleTextDelta = streamingFilter.Append(textChunk);
            if (visibleTextDelta.Length > 0)
            {
                responseProgress?.Report(visibleTextDelta);
            }
        }

        var finalVisibleTextDelta = streamingFilter.Complete();
        if (finalVisibleTextDelta.Length > 0)
        {
            responseProgress?.Report(finalVisibleTextDelta);
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

        var interactionWasRemembered = RememberInteractionIfCurrent(
            interaction,
            userPrompt,
            pointResponse.SpokenText,
            cancellationToken);

        if (interactionWasRemembered)
        {
            await PersistInteractionAsync(
                interaction,
                userPrompt,
                pointResponse.SpokenText,
                cancellationToken).ConfigureAwait(false);
        }

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
            activeStoredConversationId = null;
            historyGeneration++;
        }
    }

    public async Task<IReadOnlyList<StoredConversation>> ListStoredConversationsAsync(
        CancellationToken cancellationToken = default)
    {
        if (conversationRepository is null)
        {
            return [];
        }

        return await conversationRepository
            .ListConversationsAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TutorConversationTurn>> LoadStoredConversationAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        if (conversationRepository is null)
        {
            return [];
        }

        var storedTurns = await conversationRepository
            .LoadTurnsAsync(conversationId, cancellationToken)
            .ConfigureAwait(false);
        return storedTurns
            .Select(turn => new TutorConversationTurn(
                turn.UserMessage.Content,
                turn.AssistantMessage.Content))
            .ToArray();
    }

    public async Task<bool> DeleteStoredConversationAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        if (conversationRepository is null)
        {
            return false;
        }

        var deleted = await conversationRepository
            .DeleteConversationAsync(conversationId, cancellationToken)
            .ConfigureAwait(false);
        if (deleted)
        {
            lock (historyLock)
            {
                if (activeStoredConversationId == conversationId)
                {
                    activeStoredConversationId = null;
                    conversationHistory.Clear();
                    historyGeneration++;
                }
            }
        }

        return deleted;
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

    private bool RememberInteractionIfCurrent(
        PreparedTutorInteraction interaction,
        string userPrompt,
        string assistantText,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(assistantText))
        {
            return false;
        }

        lock (historyLock)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (interaction.InteractionId != currentInteractionId ||
                interaction.HistoryGeneration != historyGeneration)
            {
                return false;
            }

            conversationHistory.Add(new WorkerConversationTurn(userPrompt, assistantText));
            if (conversationHistory.Count > MaximumHistoryTurns)
            {
                conversationHistory.RemoveRange(
                    0,
                    conversationHistory.Count - MaximumHistoryTurns);
            }

            return true;
        }
    }

    private async Task PersistInteractionAsync(
        PreparedTutorInteraction interaction,
        string userPrompt,
        string assistantText,
        CancellationToken cancellationToken)
    {
        if (conversationRepository is null)
        {
            return;
        }

        try
        {
            Guid? storedConversationId;
            lock (historyLock)
            {
                if (interaction.HistoryGeneration != historyGeneration)
                {
                    return;
                }

                storedConversationId = activeStoredConversationId;
            }

            var providerContext = providerContextAccessor?.Invoke() ??
                new ConversationProviderContext("Unknown", model);
            if (storedConversationId is null)
            {
                var createdConversation = await conversationRepository
                    .CreateConversationAsync(
                        new ConversationCreateRequest
                        {
                            Title = CreateTitle(userPrompt),
                            RollingSummary = CreateSummary(assistantText),
                            Provider = providerContext.Provider,
                            Model = providerContext.Model,
                            WindowTitle = interaction.Capture.WindowTitle,
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
                storedConversationId = createdConversation.Id;
                lock (historyLock)
                {
                    if (interaction.HistoryGeneration != historyGeneration)
                    {
                        return;
                    }

                    activeStoredConversationId = storedConversationId;
                }
            }

            await conversationRepository.AppendTurnAsync(
                storedConversationId.Value,
                new ConversationTurnWrite
                {
                    UserText = userPrompt,
                    AssistantText = assistantText,
                    Provider = providerContext.Provider,
                    Model = providerContext.Model,
                    WindowTitle = interaction.Capture.WindowTitle,
                },
                cancellationToken).ConfigureAwait(false);
            await conversationRepository.UpdateSummaryAsync(
                storedConversationId.Value,
                new ConversationSummaryUpdate
                {
                    RollingSummary = CreateSummary(assistantText),
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceWarning(
                "Clicky could not persist the completed conversation turn: {0}",
                exception.Message);
        }
    }

    private static string CreateTitle(string userPrompt) =>
        LimitText(userPrompt, MaximumConversationTitleLength);

    private static string CreateSummary(string assistantText)
    {
        var normalizedText = string.Join(
            ' ',
            assistantText.Split(
                ['\r', '\n', '\t'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return LimitText(normalizedText, MaximumConversationSummaryLength);
    }

    private static string LimitText(string text, int maximumLength)
    {
        var normalizedText = text.Trim();
        if (normalizedText.Length <= maximumLength)
        {
            return normalizedText;
        }

        return normalizedText[..(maximumLength - 1)].TrimEnd() + "\u2026";
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

using System.IO;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using Clicky.Windows.Capture;
using Clicky.Windows.Configuration;
using Clicky.Windows.Interaction;
using Clicky.Windows.Networking;
using Clicky.Windows.Pointing;
using Clicky.Windows.Tutoring;

namespace Clicky.Windows.Tests;

[TestClass]
public sealed class TutorInteractionServiceTests
{
    [TestMethod]
    public async Task PreparedResponse_CapturesBeforeEntryAndBuildsTypedRequestOnSubmit()
    {
        var calls = new List<string>();
        var capture = CreateCapture(
            width: 1280,
            height: 720,
            bounds: new PhysicalPixelBounds(100, 200, 1920, 1080),
            title: "Adobe Photoshop - poster.psd");
        var captureService = new FakeCaptureService((_) =>
        {
            calls.Add("capture");
            return Task.FromResult(capture);
        });
        WorkerChatRequest? sentRequest = null;
        var workerClient = new FakeWorkerClient((request, _) =>
        {
            calls.Add("worker");
            sentRequest = request;
            return Stream("Select the ", "Brush tool. ", "[POINT:640,360:brush tool]");
        });
        var service = new TutorInteractionService(
            captureService,
            workerClient,
            new TutorInteractionOptions { Model = "claude-test-model" });

        var preparation = await service.PrepareAsync();

        CollectionAssert.AreEqual(new[] { "capture" }, calls);
        Assert.AreEqual(1L, preparation.InteractionId);
        Assert.AreEqual("Adobe Photoshop - poster.psd", preparation.CaptureMetadata.WindowTitle);

        var result = await service.RespondAsync(preparation, "Where do I start?");

        CollectionAssert.AreEqual(new[] { "capture", "worker" }, calls);
        Assert.IsNotNull(sentRequest);
        Assert.AreEqual("claude-test-model", sentRequest.Model);
        Assert.AreEqual(VisualGuideTutor.SystemPrompt, sentRequest.SystemPrompt);
        Assert.AreEqual("Where do I start?", sentRequest.UserPrompt);
        Assert.HasCount(1, sentRequest.Images);
        Assert.HasCount(0, sentRequest.ConversationHistory);
        StringAssert.Contains(sentRequest.Images[0].Label, "Adobe Photoshop - poster.psd");
        StringAssert.Contains(sentRequest.Images[0].Label, "1280x720");
        CollectionAssert.AreEqual(
            capture.JpegBytes.ToArray(),
            sentRequest.Images[0].ImageBytes.ToArray());

        Assert.AreEqual("Select the Brush tool.", result.SpokenText);
        Assert.AreEqual(
            "Select the Brush tool. [POINT:640,360:brush tool]",
            result.FullAssistantText);
        Assert.AreEqual("Adobe Photoshop - poster.psd", result.CaptureMetadata.WindowTitle);
        Assert.AreEqual(1280, result.CaptureMetadata.EncodedPixelWidth);
        Assert.AreEqual(720, result.CaptureMetadata.EncodedPixelHeight);
        Assert.AreEqual(new DesktopPoint(1060, 740), result.MappedDesktopPoint);
        Assert.AreEqual("brush tool", result.TargetLabel);

        var history = service.GetConversationHistorySnapshot();
        Assert.HasCount(1, history);
        Assert.AreEqual("Where do I start?", history[0].UserText);
        Assert.AreEqual("Select the Brush tool.", history[0].AssistantText);
        Assert.IsFalse(history[0].AssistantText.Contains("[POINT", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task PrepareFollowUp_ReusesCaptureWithLatestConversationHistory()
    {
        var captureCount = 0;
        var requests = new List<WorkerChatRequest>();
        var service = new TutorInteractionService(
            new FakeCaptureService((_) =>
            {
                captureCount++;
                return Task.FromResult(CreateCapture(title: "Visual Studio - App.xaml"));
            }),
            new FakeWorkerClient((request, _) =>
            {
                requests.Add(request);
                return Stream(requests.Count == 1
                    ? "Open the designer. [POINT:200,100:designer]"
                    : "Use the properties panel. [POINT:600,300:properties panel]");
            }));

        var firstPreparation = await service.PrepareAsync();
        await service.RespondAsync(firstPreparation, "Where should I start?");
        var followUpPreparation = service.PrepareFollowUp(firstPreparation);
        await service.RespondAsync(followUpPreparation, "What should I change there?");

        Assert.AreEqual(1, captureCount);
        Assert.HasCount(2, requests);
        Assert.HasCount(1, requests[1].ConversationHistory);
        Assert.AreEqual("Where should I start?", requests[1].ConversationHistory[0].UserText);
        Assert.AreEqual("Open the designer.", requests[1].ConversationHistory[0].AssistantText);
        CollectionAssert.AreEqual(
            requests[0].Images[0].ImageBytes.ToArray(),
            requests[1].Images[0].ImageBytes.ToArray());
        Assert.HasCount(2, service.GetConversationHistorySnapshot());
    }

    [TestMethod]
    public async Task SettingsAwareWorkerClient_UsesCurrentUrlForEveryRequest()
    {
        var settings = new CompanionSettings
        {
            WorkerBaseUrl = new Uri("https://first.example")
        };
        var handler = new RecordingHttpMessageHandler();
        using var httpClient = new HttpClient(handler);
        var client = new SettingsAwareWorkerClient(httpClient, settings);
        var request = new WorkerChatRequest("model", "system", "question");

        await DrainAsync(client.StreamChatAsync(request));
        settings.WorkerBaseUrl = new Uri("https://second.example/base");
        await DrainAsync(client.StreamChatAsync(request));

        CollectionAssert.AreEqual(
            new[]
            {
                new Uri("https://first.example/chat"),
                new Uri("https://second.example/chat")
            },
            handler.RequestUris);
    }

    [TestMethod]
    public async Task SettingsAwareWorkerClient_PlaceholderUrlDoesNotCallNetwork()
    {
        var settings = new CompanionSettings();
        var handler = new RecordingHttpMessageHandler();
        using var httpClient = new HttpClient(handler);
        var client = new SettingsAwareWorkerClient(httpClient, settings);
        var request = new WorkerChatRequest("model", "system", "question");

        var exception = await Assert.ThrowsExactlyAsync<WorkerConfigurationException>(
            () => DrainAsync(client.StreamChatAsync(request)));

        StringAssert.Contains(exception.Message, "Worker URL");
        Assert.HasCount(0, handler.RequestUris);
    }

    [TestMethod]
    public async Task RespondAsync_UsesDefaultModelAndRejectsMissingTerminalDirective()
    {
        WorkerChatRequest? sentRequest = null;
        var service = new TutorInteractionService(
            new FakeCaptureService((_) => Task.FromResult(CreateCapture())),
            new FakeWorkerClient((request, _) =>
            {
                sentRequest = request;
                return Stream("This response forgot the directive.");
            }));

        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => service.RespondAsync("Help"));

        Assert.AreEqual(TutorInteractionOptions.DefaultModel, sentRequest?.Model);
        Assert.AreEqual(0, service.ConversationTurnCount);
        Assert.HasCount(0, service.GetConversationHistorySnapshot());
    }

    [TestMethod]
    public async Task RespondAsync_CapsHistoryAtTenTurnsAndClearHistoryInvalidatesIt()
    {
        var requests = new List<WorkerChatRequest>();
        var responseNumber = 0;
        var service = new TutorInteractionService(
            new FakeCaptureService((_) => Task.FromResult(CreateCapture())),
            new FakeWorkerClient((request, _) =>
            {
                requests.Add(request);
                responseNumber++;
                return Stream($"answer {responseNumber} [POINT:none]");
            }));

        for (var questionNumber = 1; questionNumber <= 12; questionNumber++)
        {
            await service.RespondAsync($"question {questionNumber}");
        }

        Assert.AreEqual(10, service.ConversationTurnCount);
        Assert.HasCount(10, requests[11].ConversationHistory);
        Assert.AreEqual("question 2", requests[11].ConversationHistory[0].UserText);
        Assert.AreEqual("answer 2", requests[11].ConversationHistory[0].AssistantText);
        Assert.AreEqual("question 11", requests[11].ConversationHistory[9].UserText);

        var cappedHistory = service.GetConversationHistorySnapshot();
        Assert.HasCount(10, cappedHistory);
        Assert.AreEqual("question 3", cappedHistory[0].UserText);
        Assert.AreEqual("answer 3", cappedHistory[0].AssistantText);
        Assert.AreEqual("question 12", cappedHistory[9].UserText);
        Assert.AreEqual("answer 12", cappedHistory[9].AssistantText);
        Assert.IsTrue(cappedHistory.All(
            turn => !turn.AssistantText.Contains("[POINT", StringComparison.Ordinal)));

        service.ClearHistory();
        Assert.HasCount(0, service.GetConversationHistorySnapshot());
        await service.RespondAsync("fresh question");

        Assert.HasCount(0, requests[12].ConversationHistory);
        Assert.AreEqual(1, service.ConversationTurnCount);
        Assert.AreEqual(
            "fresh question",
            service.GetConversationHistorySnapshot().Single().UserText);
    }

    [TestMethod]
    public async Task RespondAsync_CancellationAfterCapturePreventsWorkerRequestAndHistoryCommit()
    {
        using var cancellationSource = new CancellationTokenSource();
        var workerCallCount = 0;
        var service = new TutorInteractionService(
            new FakeCaptureService((_) =>
            {
                cancellationSource.Cancel();
                return Task.FromResult(CreateCapture());
            }),
            new FakeWorkerClient((_, _) =>
            {
                workerCallCount++;
                return Stream("unused [POINT:none]");
            }));

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => service.RespondAsync("Help", cancellationSource.Token));

        Assert.AreEqual(0, workerCallCount);
        Assert.AreEqual(0, service.ConversationTurnCount);
        Assert.HasCount(0, service.GetConversationHistorySnapshot());
    }

    [TestMethod]
    public async Task RespondAsync_OutOfOrderCompletionDoesNotCommitStaleHistory()
    {
        var firstRequestStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstRequest = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = new List<WorkerChatRequest>();
        var service = new TutorInteractionService(
            new FakeCaptureService((_) => Task.FromResult(CreateCapture())),
            new FakeWorkerClient(HandleRequest));

        var staleResponseTask = service.RespondAsync("stale question");
        await firstRequestStarted.Task;

        var currentResult = await service.RespondAsync("current question");
        releaseFirstRequest.SetResult();
        var staleResult = await staleResponseTask;

        await service.RespondAsync("follow-up question");

        Assert.AreEqual("current answer", currentResult.SpokenText);
        Assert.AreEqual("stale answer", staleResult.SpokenText);
        Assert.HasCount(1, requests[2].ConversationHistory);
        Assert.AreEqual("current question", requests[2].ConversationHistory[0].UserText);
        Assert.AreEqual("current answer", requests[2].ConversationHistory[0].AssistantText);
        Assert.AreEqual(2, service.ConversationTurnCount);

        var history = service.GetConversationHistorySnapshot();
        Assert.HasCount(2, history);
        Assert.AreEqual("current question", history[0].UserText);
        Assert.AreEqual("follow-up question", history[1].UserText);
        Assert.IsFalse(history.Any(turn => turn.UserText == "stale question"));

        IAsyncEnumerable<string> HandleRequest(
            WorkerChatRequest request,
            CancellationToken cancellationToken)
        {
            requests.Add(request);
            return request.UserPrompt switch
            {
                "stale question" => DelayedResponse(
                    firstRequestStarted,
                    releaseFirstRequest,
                    cancellationToken),
                "current question" => Stream("current answer [POINT:none]"),
                _ => Stream("follow-up answer [POINT:none]")
            };
        }
    }

    private static async IAsyncEnumerable<string> DelayedResponse(
        TaskCompletionSource started,
        TaskCompletionSource release,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        started.SetResult();
        await release.Task.WaitAsync(cancellationToken);
        yield return "stale answer [POINT:none]";
    }

    private static async IAsyncEnumerable<string> Stream(params string[] chunks)
    {
        foreach (var chunk in chunks)
        {
            yield return chunk;
        }

        await Task.CompletedTask;
    }

    private static async Task DrainAsync(IAsyncEnumerable<string> stream)
    {
        await foreach (var _ in stream)
        {
        }
    }

    private static CaptureResult CreateCapture(
        int width = 800,
        int height = 600,
        PhysicalPixelBounds? bounds = null,
        string title = "Visual Studio") =>
        new(
            [0xFF, 0xD8, 0xFF, 0xD9],
            width,
            height,
            bounds ?? new PhysicalPixelBounds(0, 0, 800, 600),
            title);

    private sealed class FakeCaptureService(
        Func<CancellationToken, Task<CaptureResult>> capture) : IActiveWindowCaptureService
    {
        public Task<CaptureResult> CaptureAsync(CancellationToken cancellationToken = default) =>
            capture(cancellationToken);
    }

    private sealed class FakeWorkerClient(
        Func<WorkerChatRequest, CancellationToken, IAsyncEnumerable<string>> stream) : IWorkerClient
    {
        public IAsyncEnumerable<string> StreamChatAsync(
            WorkerChatRequest request,
            CancellationToken cancellationToken = default) =>
            stream(request, cancellationToken);
    }

    private sealed class RecordingHttpMessageHandler : HttpMessageHandler
    {
        private const string ResponseSse =
            "data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"ok\"}}\n\n" +
            "data: [DONE]\n\n";

        public List<Uri> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri!);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(
                    new MemoryStream(Encoding.UTF8.GetBytes(ResponseSse)))
            };
            response.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
            return Task.FromResult(response);
        }
    }
}

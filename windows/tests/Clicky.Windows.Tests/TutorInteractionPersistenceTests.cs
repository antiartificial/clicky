using System.Runtime.CompilerServices;
using Clicky.Windows.Capture;
using Clicky.Windows.Interaction;
using Clicky.Windows.Networking;
using Clicky.Windows.Persistence;
using Clicky.Windows.Pointing;

namespace Clicky.Windows.Tests;

[TestClass]
public sealed class TutorInteractionPersistenceTests
{
    [TestMethod]
    public async Task RespondAsync_PersistsSuccessfulTurnsAndPreservesPastConversations()
    {
        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "ClickyInteractionPersistenceTests",
            Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(temporaryDirectory, "clicky.db");

        try
        {
            using var repository = new SqliteConversationRepository(databasePath);
            var service = new TutorInteractionService(
                new CaptureService(),
                new AnswerClient(),
                new TutorInteractionOptions
                {
                    ProviderContextAccessor = () =>
                        new ConversationProviderContext("OpenAI", "openai-test-model"),
                },
                repository);

            await service.RespondAsync("Where is the export button?");
            service.ClearHistory();
            await service.RespondAsync("How do I open settings?");

            var conversations = await service.ListStoredConversationsAsync();

            Assert.HasCount(2, conversations);
            Assert.AreEqual("OpenAI", conversations[0].Provider);
            Assert.AreEqual("openai-test-model", conversations[0].Model);
            Assert.AreEqual("How do I open settings?", conversations[0].Title);
            Assert.IsFalse(string.IsNullOrWhiteSpace(conversations[0].RollingSummary));
            var loadedTurns = await service.LoadStoredConversationAsync(conversations[0].Id);
            Assert.HasCount(1, loadedTurns);
            Assert.AreEqual("How do I open settings?", loadedTurns[0].UserText);
            Assert.AreEqual("Choose the visible control.", loadedTurns[0].AssistantText);
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    private sealed class CaptureService : IActiveWindowCaptureService
    {
        public Task<CaptureResult> CaptureAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new CaptureResult(
                [0xFF, 0xD8, 0xFF, 0xD9],
                800,
                600,
                new PhysicalPixelBounds(0, 0, 800, 600),
                "Rive"));
    }

    private sealed class AnswerClient : IWorkerClient
    {
        public async IAsyncEnumerable<string> StreamChatAsync(
            WorkerChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return "Choose the visible control. [POINT:none]";
            await Task.CompletedTask;
        }
    }
}

using Clicky.Windows.Persistence;
using Microsoft.Data.Sqlite;
using System.IO;

namespace Clicky.Windows.Tests;

[TestClass]
public sealed class SqliteConversationRepositoryTests
{
    [TestMethod]
    public void DefaultDatabasePath_UsesClickyLocalApplicationDataDirectory()
    {
        var expectedPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Clicky",
            "clicky.db");

        Assert.AreEqual(expectedPath, ConversationDatabasePath.GetDefaultPath());
    }

    [TestMethod]
    public async Task InitializeAsync_CreatesVersionedWalDatabaseWithoutBinaryOrSecretColumns()
    {
        using var temporaryDatabase = new TemporaryDatabase();
        using var repository = new SqliteConversationRepository(temporaryDatabase.DatabasePath);

        await repository.InitializeAsync();

        await using var connection = await temporaryDatabase.OpenConnectionAsync();
        Assert.AreEqual(
            SqliteConversationRepository.CurrentSchemaVersion,
            await ExecuteScalarIntAsync(connection, "PRAGMA user_version;"));
        Assert.AreEqual(
            "wal",
            (await ExecuteScalarStringAsync(connection, "PRAGMA journal_mode;"))?.ToLowerInvariant());

        var columns = await ReadSchemaColumnsAsync(connection);
        Assert.IsFalse(columns.Any(column => column.Contains("screenshot", StringComparison.Ordinal)));
        Assert.IsFalse(columns.Any(column => column.Contains("secret", StringComparison.Ordinal)));
        Assert.IsFalse(columns.Any(column => column.EndsWith(":BLOB", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task ConversationLifecycle_PersistsMetadataMessagesSummariesAndNewestFirstOrdering()
    {
        using var temporaryDatabase = new TemporaryDatabase();
        using var repository = new SqliteConversationRepository(temporaryDatabase.DatabasePath);
        var olderTime = new DateTimeOffset(2026, 7, 10, 15, 0, 0, TimeSpan.Zero);
        var newerTime = olderTime.AddHours(1);

        var olderConversation = await repository.CreateConversationAsync(
            new ConversationCreateRequest
            {
                Title = "Older conversation",
                Provider = "worker",
                Model = "claude-sonnet",
                ApplicationName = "Rive",
                WindowTitle = "Animation.riv",
                CreatedAtUtc = olderTime,
            });
        var newerConversation = await repository.CreateConversationAsync(
            new ConversationCreateRequest
            {
                Title = "Newer conversation",
                CreatedAtUtc = newerTime,
            });

        var appendedTurn = await repository.AppendTurnAsync(
            olderConversation.Id,
            new ConversationTurnWrite
            {
                UserText = "Where is the export button?",
                AssistantText = "Use Export in the toolbar.",
                Provider = "openai",
                Model = "gpt-5.4-mini",
                ApplicationName = "Adobe Photoshop",
                WindowTitle = "poster.psd",
                CreatedAtUtc = newerTime.AddHours(1),
            });
        var summaryUpdated = await repository.UpdateSummaryAsync(
            olderConversation.Id,
            new ConversationSummaryUpdate
            {
                Title = "Export help",
                RollingSummary = "The user is learning how to export a design.",
                UpdatedAtUtc = newerTime.AddHours(2),
            });

        var conversations = await repository.ListConversationsAsync();
        var messages = await repository.LoadMessagesAsync(olderConversation.Id);
        var turns = await repository.LoadTurnsAsync(olderConversation.Id);
        var reloadedConversation = await repository.GetConversationAsync(olderConversation.Id);

        Assert.IsTrue(summaryUpdated);
        CollectionAssert.AreEqual(
            new[] { olderConversation.Id, newerConversation.Id },
            conversations.Select(conversation => conversation.Id).ToArray());
        Assert.HasCount(2, messages);
        Assert.AreEqual(ConversationMessageRole.User, messages[0].Role);
        Assert.AreEqual("Where is the export button?", messages[0].Content);
        Assert.AreEqual(ConversationMessageRole.Assistant, messages[1].Role);
        Assert.AreEqual("Use Export in the toolbar.", messages[1].Content);
        Assert.AreEqual("openai", messages[1].Provider);
        Assert.AreEqual("gpt-5.4-mini", messages[1].Model);
        Assert.AreEqual("Adobe Photoshop", messages[1].ApplicationName);
        Assert.AreEqual("poster.psd", messages[1].WindowTitle);
        Assert.HasCount(1, turns);
        Assert.AreEqual(appendedTurn, turns[0]);
        Assert.IsNotNull(reloadedConversation);
        Assert.AreEqual("Export help", reloadedConversation.Title);
        Assert.AreEqual(
            "The user is learning how to export a design.",
            reloadedConversation.RollingSummary);
        Assert.AreEqual("openai", reloadedConversation.Provider);
        Assert.AreEqual(1, reloadedConversation.TurnCount);
    }

    [TestMethod]
    public async Task DeleteAndClear_CascadeMessagesAndAreIdempotent()
    {
        using var temporaryDatabase = new TemporaryDatabase();
        using var repository = new SqliteConversationRepository(temporaryDatabase.DatabasePath);
        var firstConversation = await repository.CreateConversationAsync(new ConversationCreateRequest());
        var secondConversation = await repository.CreateConversationAsync(new ConversationCreateRequest());
        await repository.AppendTurnAsync(
            firstConversation.Id,
            new ConversationTurnWrite
            {
                UserText = "First question",
                AssistantText = "First answer",
            });
        await repository.AppendTurnAsync(
            secondConversation.Id,
            new ConversationTurnWrite
            {
                UserText = "Second question",
                AssistantText = "Second answer",
            });

        Assert.IsTrue(await repository.DeleteConversationAsync(firstConversation.Id));
        Assert.IsFalse(await repository.DeleteConversationAsync(firstConversation.Id));
        Assert.HasCount(0, await repository.LoadMessagesAsync(firstConversation.Id));
        Assert.AreEqual(1, await repository.ClearAsync());
        Assert.AreEqual(0, await repository.ClearAsync());
        Assert.HasCount(0, await repository.ListConversationsAsync());

        await using var connection = await temporaryDatabase.OpenConnectionAsync();
        Assert.AreEqual(
            0,
            await ExecuteScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM conversation_messages;"));
    }

    [TestMethod]
    public async Task InitializeAsync_RejectsNewerSchemaVersion()
    {
        using var temporaryDatabase = new TemporaryDatabase();
        await using (var connection = await temporaryDatabase.OpenConnectionAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 999;";
            await command.ExecuteNonQueryAsync();
        }

        using var repository = new SqliteConversationRepository(temporaryDatabase.DatabasePath);

        await Assert.ThrowsExactlyAsync<NotSupportedException>(
            async () => await repository.InitializeAsync());
    }

    private static async Task<int> ExecuteScalarIntAsync(
        SqliteConnection connection,
        string commandText)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<string?> ExecuteScalarStringAsync(
        SqliteConnection connection,
        string commandText)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        return Convert.ToString(await command.ExecuteScalarAsync());
    }

    private static async Task<List<string>> ReadSchemaColumnsAsync(SqliteConnection connection)
    {
        const string commandText = """
            SELECT lower(name) || ':' || upper(type)
            FROM pragma_table_info('conversations')
            UNION ALL
            SELECT lower(name) || ':' || upper(type)
            FROM pragma_table_info('conversation_messages');
            """;
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await using var reader = await command.ExecuteReaderAsync();
        var columns = new List<string>();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(0));
        }

        return columns;
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        private readonly string directoryPath = Path.Combine(
            Path.GetTempPath(),
            "Clicky.Windows.Tests",
            Guid.NewGuid().ToString("N"));

        public TemporaryDatabase()
        {
            Directory.CreateDirectory(directoryPath);
            DatabasePath = Path.Combine(directoryPath, "conversation-tests.db");
        }

        public string DatabasePath { get; }

        public async Task<SqliteConnection> OpenConnectionAsync()
        {
            var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = DatabasePath,
                    Pooling = false,
                }.ToString());
            await connection.OpenAsync();
            return connection;
        }

        public void Dispose()
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
    }
}

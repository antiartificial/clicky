using System.Data;
using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;

namespace Clicky.Windows.Persistence;

public sealed class SqliteConversationRepository : ILocalConversationRepository, IDisposable
{
    public const int CurrentSchemaVersion = 1;

    private const string TimestampFormat = "O";
    private readonly string databasePath;
    private readonly SemaphoreSlim initializationGate = new(1, 1);
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private bool initialized;
    private bool disposed;

    public SqliteConversationRepository(string? databasePath = null)
    {
        var selectedDatabasePath = databasePath ?? ConversationDatabasePath.GetDefaultPath();
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedDatabasePath);
        this.databasePath = Path.GetFullPath(selectedDatabasePath);
    }

    public string DatabasePath => databasePath;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (initialized)
        {
            return;
        }

        await initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (initialized)
            {
                return;
            }

            var databaseDirectory = Path.GetDirectoryName(databasePath);
            if (string.IsNullOrWhiteSpace(databaseDirectory))
            {
                throw new InvalidOperationException("The database path must include a directory.");
            }

            Directory.CreateDirectory(databaseDirectory);
            await using var connection = await OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await ExecuteNonQueryAsync(
                    connection,
                    "PRAGMA journal_mode = WAL;",
                    cancellationToken)
                .ConfigureAwait(false);

            var schemaVersion = await GetSchemaVersionAsync(connection, cancellationToken)
                .ConfigureAwait(false);
            if (schemaVersion > CurrentSchemaVersion)
            {
                throw new NotSupportedException(
                    $"The conversation database schema version {schemaVersion} is newer than " +
                    $"the supported version {CurrentSchemaVersion}.");
            }

            if (schemaVersion < 1)
            {
                await MigrateToVersionOneAsync(connection, cancellationToken).ConfigureAwait(false);
            }

            initialized = true;
        }
        finally
        {
            initializationGate.Release();
        }
    }

    public async Task<StoredConversation> CreateConversationAsync(
        ConversationCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var conversationId = Guid.NewGuid();
        var createdAtUtc = NormalizeTimestamp(request.CreatedAtUtc ?? DateTimeOffset.UtcNow);
        var title = NormalizeOptionalText(request.Title);
        var rollingSummary = NormalizeOptionalText(request.RollingSummary);
        var provider = NormalizeOptionalText(request.Provider);
        var model = NormalizeOptionalText(request.Model);
        var applicationName = NormalizeOptionalText(request.ApplicationName);
        var windowTitle = NormalizeOptionalText(request.WindowTitle);
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO conversations (
                    id,
                    created_at_utc,
                    updated_at_utc,
                    title,
                    rolling_summary,
                    provider,
                    model,
                    application_name,
                    window_title,
                    turn_count)
                VALUES (
                    @id,
                    @createdAtUtc,
                    @updatedAtUtc,
                    @title,
                    @rollingSummary,
                    @provider,
                    @model,
                    @applicationName,
                    @windowTitle,
                    0);
                """;
            AddParameter(command, "@id", conversationId.ToString("D"));
            AddParameter(command, "@createdAtUtc", FormatTimestamp(createdAtUtc));
            AddParameter(command, "@updatedAtUtc", FormatTimestamp(createdAtUtc));
            AddParameter(command, "@title", title);
            AddParameter(command, "@rollingSummary", rollingSummary);
            AddParameter(command, "@provider", provider);
            AddParameter(command, "@model", model);
            AddParameter(command, "@applicationName", applicationName);
            AddParameter(command, "@windowTitle", windowTitle);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writeGate.Release();
        }

        return new StoredConversation(
            conversationId,
            createdAtUtc,
            createdAtUtc,
            title,
            rollingSummary,
            provider,
            model,
            applicationName,
            windowTitle,
            TurnCount: 0);
    }

    public async Task<StoredConversation?> GetConversationAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        ValidateConversationId(conversationId);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = CreateConversationSelectCommand(
            connection,
            "WHERE id = @id");
        AddParameter(command, "@id", conversationId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadConversation(reader)
            : null;
    }

    public async Task<IReadOnlyList<StoredConversation>> ListConversationsAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = CreateConversationSelectCommand(
            connection,
            "ORDER BY updated_at_utc DESC, created_at_utc DESC, id DESC");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        var conversations = new List<StoredConversation>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            conversations.Add(ReadConversation(reader));
        }

        return conversations;
    }

    public async Task<StoredConversationTurn> AppendTurnAsync(
        Guid conversationId,
        ConversationTurnWrite turn,
        CancellationToken cancellationToken = default)
    {
        ValidateConversationId(conversationId);
        ArgumentNullException.ThrowIfNull(turn);
        ArgumentException.ThrowIfNullOrWhiteSpace(turn.UserText);
        ArgumentException.ThrowIfNullOrWhiteSpace(turn.AssistantText);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var createdAtUtc = NormalizeTimestamp(turn.CreatedAtUtc ?? DateTimeOffset.UtcNow);
        var provider = NormalizeOptionalText(turn.Provider);
        var model = NormalizeOptionalText(turn.Model);
        var applicationName = NormalizeOptionalText(turn.ApplicationName);
        var windowTitle = NormalizeOptionalText(turn.WindowTitle);

        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
                    IsolationLevel.Serializable,
                    cancellationToken)
                .ConfigureAwait(false);
            var turnIndex = await ReserveTurnIndexAsync(
                    connection,
                    transaction,
                    conversationId,
                    createdAtUtc,
                    provider,
                    model,
                    applicationName,
                    windowTitle,
                    cancellationToken)
                .ConfigureAwait(false);

            if (turnIndex is null)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                throw new KeyNotFoundException(
                    $"Conversation '{conversationId:D}' does not exist.");
            }

            var userMessage = await InsertMessageAsync(
                    connection,
                    transaction,
                    conversationId,
                    turnIndex.Value,
                    ConversationMessageRole.User,
                    turn.UserText,
                    createdAtUtc,
                    provider,
                    model,
                    applicationName,
                    windowTitle,
                    cancellationToken)
                .ConfigureAwait(false);
            var assistantMessage = await InsertMessageAsync(
                    connection,
                    transaction,
                    conversationId,
                    turnIndex.Value,
                    ConversationMessageRole.Assistant,
                    turn.AssistantText,
                    createdAtUtc,
                    provider,
                    model,
                    applicationName,
                    windowTitle,
                    cancellationToken)
                .ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new StoredConversationTurn(
                turnIndex.Value,
                userMessage,
                assistantMessage);
        }
        finally
        {
            writeGate.Release();
        }
    }

    public async Task<IReadOnlyList<StoredConversationMessage>> LoadMessagesAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        ValidateConversationId(conversationId);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                id,
                conversation_id,
                turn_index,
                role,
                content,
                created_at_utc,
                provider,
                model,
                application_name,
                window_title
            FROM conversation_messages
            WHERE conversation_id = @conversationId
            ORDER BY turn_index ASC,
                     CASE role WHEN 'user' THEN 0 ELSE 1 END ASC,
                     id ASC;
            """;
        AddParameter(command, "@conversationId", conversationId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        var messages = new List<StoredConversationMessage>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            messages.Add(ReadMessage(reader));
        }

        return messages;
    }

    public async Task<IReadOnlyList<StoredConversationTurn>> LoadTurnsAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        var messages = await LoadMessagesAsync(conversationId, cancellationToken)
            .ConfigureAwait(false);
        var turns = new List<StoredConversationTurn>();
        foreach (var turnGroup in messages.GroupBy(message => message.TurnIndex))
        {
            var userMessage = turnGroup.SingleOrDefault(
                message => message.Role == ConversationMessageRole.User);
            var assistantMessage = turnGroup.SingleOrDefault(
                message => message.Role == ConversationMessageRole.Assistant);
            if (userMessage is null || assistantMessage is null)
            {
                throw new InvalidDataException(
                    $"Conversation '{conversationId:D}' has an incomplete turn at index " +
                    $"{turnGroup.Key}.");
            }

            turns.Add(new StoredConversationTurn(
                turnGroup.Key,
                userMessage,
                assistantMessage));
        }

        return turns;
    }

    public async Task<bool> UpdateSummaryAsync(
        Guid conversationId,
        ConversationSummaryUpdate update,
        CancellationToken cancellationToken = default)
    {
        ValidateConversationId(conversationId);
        ArgumentNullException.ThrowIfNull(update);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var updatedAtUtc = NormalizeTimestamp(update.UpdatedAtUtc ?? DateTimeOffset.UtcNow);
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE conversations
                SET title = COALESCE(@title, title),
                    rolling_summary = COALESCE(@rollingSummary, rolling_summary),
                    updated_at_utc = @updatedAtUtc
                WHERE id = @id;
                """;
            AddParameter(command, "@title", NormalizeOptionalText(update.Title));
            AddParameter(command, "@rollingSummary", NormalizeOptionalText(update.RollingSummary));
            AddParameter(command, "@updatedAtUtc", FormatTimestamp(updatedAtUtc));
            AddParameter(command, "@id", conversationId.ToString("D"));
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        }
        finally
        {
            writeGate.Release();
        }
    }

    public async Task<bool> DeleteConversationAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        ValidateConversationId(conversationId);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM conversations WHERE id = @id;";
            AddParameter(command, "@id", conversationId.ToString("D"));
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        }
        finally
        {
            writeGate.Release();
        }
    }

    public async Task<int> ClearAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM conversations;";
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writeGate.Release();
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        initializationGate.Dispose();
        writeGate.Dispose();
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (!initialized)
        {
            await InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<SqliteConnection> OpenConnectionAsync(
        CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
            Pooling = false,
            DefaultTimeout = 5,
        }.ToString();
        var connection = new SqliteConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteNonQueryAsync(
                    connection,
                    "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;",
                    cancellationToken)
                .ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<int> GetSchemaVersionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        var schemaVersion = await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false);
        return Convert.ToInt32(schemaVersion, CultureInfo.InvariantCulture);
    }

    private static async Task MigrateToVersionOneAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            CREATE TABLE conversations (
                id TEXT PRIMARY KEY NOT NULL,
                created_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL,
                title TEXT NULL,
                rolling_summary TEXT NULL,
                provider TEXT NULL,
                model TEXT NULL,
                application_name TEXT NULL,
                window_title TEXT NULL,
                turn_count INTEGER NOT NULL DEFAULT 0 CHECK (turn_count >= 0)
            );

            CREATE TABLE conversation_messages (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                conversation_id TEXT NOT NULL,
                turn_index INTEGER NOT NULL CHECK (turn_index >= 0),
                role TEXT NOT NULL CHECK (role IN ('user', 'assistant')),
                content TEXT NOT NULL CHECK (length(trim(content)) > 0),
                created_at_utc TEXT NOT NULL,
                provider TEXT NULL,
                model TEXT NULL,
                application_name TEXT NULL,
                window_title TEXT NULL,
                FOREIGN KEY (conversation_id) REFERENCES conversations(id) ON DELETE CASCADE,
                UNIQUE (conversation_id, turn_index, role)
            );

            CREATE INDEX conversation_messages_conversation_order
                ON conversation_messages (conversation_id, turn_index, id);

            CREATE INDEX conversations_newest_first
                ON conversations (updated_at_utc DESC, created_at_utc DESC);

            PRAGMA user_version = 1;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static SqliteCommand CreateConversationSelectCommand(
        SqliteConnection connection,
        string clause)
    {
        var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT
                id,
                created_at_utc,
                updated_at_utc,
                title,
                rolling_summary,
                provider,
                model,
                application_name,
                window_title,
                turn_count
            FROM conversations
            {clause};
            """;
        return command;
    }

    private static StoredConversation ReadConversation(SqliteDataReader reader) =>
        new(
            Guid.Parse(reader.GetString(0)),
            ParseTimestamp(reader.GetString(1)),
            ParseTimestamp(reader.GetString(2)),
            GetOptionalString(reader, 3),
            GetOptionalString(reader, 4),
            GetOptionalString(reader, 5),
            GetOptionalString(reader, 6),
            GetOptionalString(reader, 7),
            GetOptionalString(reader, 8),
            reader.GetInt32(9));

    private static StoredConversationMessage ReadMessage(SqliteDataReader reader) =>
        new(
            reader.GetInt64(0),
            Guid.Parse(reader.GetString(1)),
            reader.GetInt32(2),
            ParseRole(reader.GetString(3)),
            reader.GetString(4),
            ParseTimestamp(reader.GetString(5)),
            GetOptionalString(reader, 6),
            GetOptionalString(reader, 7),
            GetOptionalString(reader, 8),
            GetOptionalString(reader, 9));

    private static async Task<int?> ReserveTurnIndexAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid conversationId,
        DateTimeOffset updatedAtUtc,
        string? provider,
        string? model,
        string? applicationName,
        string? windowTitle,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE conversations
            SET turn_count = turn_count + 1,
                updated_at_utc = @updatedAtUtc,
                provider = COALESCE(@provider, provider),
                model = COALESCE(@model, model),
                application_name = COALESCE(@applicationName, application_name),
                window_title = COALESCE(@windowTitle, window_title)
            WHERE id = @conversationId
            RETURNING turn_count - 1;
            """;
        AddParameter(command, "@updatedAtUtc", FormatTimestamp(updatedAtUtc));
        AddParameter(command, "@provider", provider);
        AddParameter(command, "@model", model);
        AddParameter(command, "@applicationName", applicationName);
        AddParameter(command, "@windowTitle", windowTitle);
        AddParameter(command, "@conversationId", conversationId.ToString("D"));
        var turnIndex = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return turnIndex is null
            ? null
            : Convert.ToInt32(turnIndex, CultureInfo.InvariantCulture);
    }

    private static async Task<StoredConversationMessage> InsertMessageAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid conversationId,
        int turnIndex,
        ConversationMessageRole role,
        string content,
        DateTimeOffset createdAtUtc,
        string? provider,
        string? model,
        string? applicationName,
        string? windowTitle,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO conversation_messages (
                conversation_id,
                turn_index,
                role,
                content,
                created_at_utc,
                provider,
                model,
                application_name,
                window_title)
            VALUES (
                @conversationId,
                @turnIndex,
                @role,
                @content,
                @createdAtUtc,
                @provider,
                @model,
                @applicationName,
                @windowTitle)
            RETURNING id;
            """;
        AddParameter(command, "@conversationId", conversationId.ToString("D"));
        AddParameter(command, "@turnIndex", turnIndex);
        AddParameter(command, "@role", FormatRole(role));
        AddParameter(command, "@content", content);
        AddParameter(command, "@createdAtUtc", FormatTimestamp(createdAtUtc));
        AddParameter(command, "@provider", provider);
        AddParameter(command, "@model", model);
        AddParameter(command, "@applicationName", applicationName);
        AddParameter(command, "@windowTitle", windowTitle);
        var messageId = Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
        return new StoredConversationMessage(
            messageId,
            conversationId,
            turnIndex,
            role,
            content,
            createdAtUtc,
            provider,
            model,
            applicationName,
            windowTitle);
    }

    private static async Task ExecuteNonQueryAsync(
        SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddParameter(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static string? NormalizeOptionalText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DateTimeOffset NormalizeTimestamp(DateTimeOffset timestamp) =>
        timestamp.ToUniversalTime();

    private static string FormatTimestamp(DateTimeOffset timestamp) =>
        timestamp.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string timestamp) =>
        DateTimeOffset.ParseExact(
            timestamp,
            TimestampFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);

    private static string FormatRole(ConversationMessageRole role) =>
        role switch
        {
            ConversationMessageRole.User => "user",
            ConversationMessageRole.Assistant => "assistant",
            _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown message role."),
        };

    private static ConversationMessageRole ParseRole(string role) =>
        role switch
        {
            "user" => ConversationMessageRole.User,
            "assistant" => ConversationMessageRole.Assistant,
            _ => throw new InvalidDataException($"The stored message role '{role}' is invalid."),
        };

    private static string? GetOptionalString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static void ValidateConversationId(Guid conversationId)
    {
        if (conversationId == Guid.Empty)
        {
            throw new ArgumentException("The conversation ID cannot be empty.", nameof(conversationId));
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }
}

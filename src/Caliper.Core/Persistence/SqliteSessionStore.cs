// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Globalization;
using System.Text.Json;
using Caliper.Core.Abstractions;
using Caliper.Core.Configuration;
using Caliper.Core.Context;
using Caliper.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Caliper.Core.Persistence;

internal sealed class SqliteSessionStore(
    IOptions<PersistenceOptions> opts,
    IOptions<CaliperOptions> caliperOptions,
    ILogger<SqliteSessionStore> logger) : SqliteStoreBase(opts, logger), ISessionStore
{
    protected override string StoreName => "session store";

    public async Task<string> CreateAsync(string? title, CancellationToken ct)
    {
        await EnsureCreatedAsync(ct).ConfigureAwait(false);

        var id = Guid.NewGuid().ToString("N");
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sessions (id, title, model, created_at)
            VALUES ($id, $title, $model, $created_at);
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$title", (object?)title ?? DBNull.Value);
        command.Parameters.AddWithValue("$model", caliperOptions.Value.Model);
        command.Parameters.AddWithValue("$created_at", NowString());
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        return id;
    }

    public async Task AppendAsync(string sessionId, ChatMessage message, CancellationToken ct)
    {
        await EnsureCreatedAsync(ct).ConfigureAwait(false);
        await ThrowIfSessionMissingAsync(sessionId, ct).ConfigureAwait(false);

        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await InsertMessageAsync(connection, transaction: null, sessionId, message, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ChatMessage>> LoadAsync(string sessionId, CancellationToken ct)
    {
        await EnsureCreatedAsync(ct).ConfigureAwait(false);
        await ThrowIfSessionMissingAsync(sessionId, ct).ConfigureAwait(false);

        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT role, kind, content, tool_call_id, tool_name, payload_json
            FROM messages
            WHERE session_id = $session_id
            ORDER BY id ASC;
            """;
        command.Parameters.AddWithValue("$session_id", sessionId);

        var messages = new List<ChatMessage>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            messages.Add(new ChatMessage(
                FromRoleStorage(reader.GetString(0)),
                FromKindStorage(reader.GetString(1)),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : JsonDocument.Parse(reader.GetString(5)).RootElement.Clone()));
        }

        return messages;
    }

    public async Task<IReadOnlyList<SessionSummary>> ListAsync(CancellationToken ct)
    {
        await EnsureCreatedAsync(ct).ConfigureAwait(false);

        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, title, created_at
            FROM sessions
            ORDER BY created_at DESC;
            """;

        var sessions = new List<SessionSummary>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            sessions.Add(new SessionSummary(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
        }

        return sessions;
    }

    protected override async Task CreateSchemaAsync(SqliteConnection connection, CancellationToken ct)
    {
        await ExecuteNonQueryAsync(connection, """
                CREATE TABLE IF NOT EXISTS sessions (
                  id         TEXT PRIMARY KEY,
                  title      TEXT,
                  model      TEXT,
                  created_at TEXT NOT NULL
                );
                """, ct).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, """
                CREATE TABLE IF NOT EXISTS messages (
                  id         INTEGER PRIMARY KEY AUTOINCREMENT,
                  session_id TEXT NOT NULL REFERENCES sessions(id),
                  role       TEXT NOT NULL,
                  kind       TEXT NOT NULL,
                  tool_call_id TEXT,
                  tool_name    TEXT,
                  content    TEXT NOT NULL,
                  payload_json TEXT,
                  token_estimate INTEGER,
                  created_at TEXT NOT NULL
                );
                """, ct).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, """
                CREATE INDEX IF NOT EXISTS ix_messages_session ON messages(session_id, id);
                """, ct).ConfigureAwait(false);
    }

    public async Task ReplaceWithCompactionAsync(string sessionId, ContextFit fit, CancellationToken ct)
    {
        await EnsureCreatedAsync(ct).ConfigureAwait(false);
        await ThrowIfSessionMissingAsync(sessionId, ct).ConfigureAwait(false);

        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            var firstDeleteId = await FirstMessageIdAtOffsetAsync(connection, (SqliteTransaction)transaction, sessionId, fit.ActiveStartIndex, ct)
                .ConfigureAwait(false);
            await using (var delete = connection.CreateCommand())
            {
                delete.Transaction = (SqliteTransaction)transaction;
                delete.CommandText = firstDeleteId is null
                    ? "DELETE FROM messages WHERE session_id = $session_id AND 1 = 0;"
                    : "DELETE FROM messages WHERE session_id = $session_id AND id >= $first_id;";
                delete.Parameters.AddWithValue("$session_id", sessionId);
                if (firstDeleteId is not null)
                    delete.Parameters.AddWithValue("$first_id", firstDeleteId.Value);
                await delete.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            foreach (var message in fit.Messages)
                await InsertMessageAsync(connection, (SqliteTransaction)transaction, sessionId, message, ct).ConfigureAwait(false);

            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task ThrowIfSessionMissingAsync(string sessionId, CancellationToken ct)
    {
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sessions WHERE id = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", sessionId);
        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (result is null)
            throw new KeyNotFoundException($"Session '{sessionId}' not found.");
    }

    private static async Task InsertMessageAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sessionId,
        ChatMessage message,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO messages (session_id, role, kind, tool_call_id, tool_name, content, payload_json, token_estimate, created_at)
            VALUES ($session_id, $role, $kind, $tool_call_id, $tool_name, $content, $payload_json, $token_estimate, $created_at);
            """;
        command.Parameters.AddWithValue("$session_id", sessionId);
        command.Parameters.AddWithValue("$role", ToStorage(message.Role));
        command.Parameters.AddWithValue("$kind", ToStorage(message.Kind));
        command.Parameters.AddWithValue("$tool_call_id", (object?)message.ToolCallId ?? DBNull.Value);
        command.Parameters.AddWithValue("$tool_name", (object?)message.ToolName ?? DBNull.Value);
        command.Parameters.AddWithValue("$content", message.Content);
        command.Parameters.AddWithValue("$payload_json", message.Payload is { } payload ? payload.GetRawText() : DBNull.Value);
        command.Parameters.AddWithValue("$token_estimate", DBNull.Value);
        command.Parameters.AddWithValue("$created_at", NowString());
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<long?> FirstMessageIdAtOffsetAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        int offset,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id
            FROM messages
            WHERE session_id = $session_id
            ORDER BY id ASC
            LIMIT 1 OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$session_id", sessionId);
        command.Parameters.AddWithValue("$offset", Math.Max(0, offset));
        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is null ? null : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static string ToStorage(ChatRole role) =>
        role switch
        {
            ChatRole.System => "system",
            ChatRole.User => "user",
            ChatRole.Assistant => "assistant",
            ChatRole.Tool => "tool",
            _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
        };

    private static ChatRole FromRoleStorage(string role) =>
        role switch
        {
            "system" => ChatRole.System,
            "user" => ChatRole.User,
            "assistant" => ChatRole.Assistant,
            "tool" => ChatRole.Tool,
            _ => throw new InvalidOperationException($"Unknown stored chat role: {role}"),
        };

    private static string ToStorage(MessageKind kind) =>
        kind switch
        {
            MessageKind.Text => "text",
            MessageKind.ToolCall => "tool_call",
            MessageKind.ToolResult => "tool_result",
            MessageKind.Summary => "summary",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };

    private static MessageKind FromKindStorage(string kind) =>
        kind switch
        {
            "text" => MessageKind.Text,
            "tool_call" => MessageKind.ToolCall,
            "tool_result" => MessageKind.ToolResult,
            "summary" => MessageKind.Summary,
            _ => throw new InvalidOperationException($"Unknown stored message kind: {kind}"),
        };

}

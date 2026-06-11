// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Globalization;
using System.Text;
using Caliper.Core.Abstractions;
using Caliper.Core.Configuration;
using Caliper.Core.Persistence;
using Caliper.Core.Tools;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Caliper.Core.Memory;

internal sealed class SqliteMemoryStore(
    IOptions<PersistenceOptions> opts,
    IOptions<CaliperOptions> caliperOptions,
    ILogger<SqliteMemoryStore> logger) : SqliteStoreBase(opts, logger), IMemoryStore
{
    private const int DefaultRenderCap = 4096;
    protected override string StoreName => "memory store";

    public async Task RememberAsync(string scope, string key, string value, CancellationToken ct)
    {
        await EnsureCreatedAsync(ct).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO memory (scope, key, value, updated_at)
            VALUES ($scope, $key, $value, $updated_at)
            ON CONFLICT(scope, key) DO UPDATE SET
              value = excluded.value,
              updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$scope", scope);
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.Parameters.AddWithValue("$updated_at", NowString());
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MemoryEntry>> RecallAsync(string scope, string? query, CancellationToken ct)
    {
        await EnsureCreatedAsync(ct).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);

        var scopes = scope == MemoryScope.Global
            ? [MemoryScope.Global]
            : new[] { MemoryScope.Global, scope };

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT scope, key, value, updated_at
            FROM memory
            WHERE scope IN ($global, $scope)
              AND ($query IS NULL OR key LIKE $like_query ESCAPE '\' OR value LIKE $like_query ESCAPE '\')
            ORDER BY updated_at DESC;
            """;
        command.Parameters.AddWithValue("$global", scopes[0]);
        command.Parameters.AddWithValue("$scope", scopes[^1]);
        command.Parameters.AddWithValue("$query", string.IsNullOrWhiteSpace(query) ? DBNull.Value : query);
        command.Parameters.AddWithValue("$like_query", string.IsNullOrWhiteSpace(query) ? DBNull.Value : $"%{EscapeLike(query)}%");

        var entries = new List<MemoryEntry>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            entries.Add(ReadEntry(reader));

        return entries;
    }

    public async Task ForgetAsync(string scope, string key, CancellationToken ct)
    {
        await EnsureCreatedAsync(ct).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM memory WHERE scope = $scope AND key = $key;";
        command.Parameters.AddWithValue("$scope", scope);
        command.Parameters.AddWithValue("$key", key);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<string> RenderForPromptAsync(string scope, CancellationToken ct)
    {
        var entries = await RecallAsync(scope, query: null, ct).ConfigureAwait(false);
        if (entries.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        foreach (var entry in entries)
            sb.AppendLine($"- {entry.Key}: {entry.Value}");

        return ToolOutput.Truncate(sb.ToString().Trim(), RenderCap);
    }

    private int RenderCap =>
        Math.Max(0, Math.Min(DefaultRenderCap, caliperOptions.Value.ToolOutputMaxChars));

    protected override async Task CreateSchemaAsync(SqliteConnection connection, CancellationToken ct)
    {
        await ExecuteNonQueryAsync(connection, """
                CREATE TABLE IF NOT EXISTS memory (
                  scope      TEXT NOT NULL,
                  key        TEXT NOT NULL,
                  value      TEXT NOT NULL,
                  updated_at TEXT NOT NULL,
                  PRIMARY KEY (scope, key)
                );
                """, ct).ConfigureAwait(false);
    }

    private static MemoryEntry ReadEntry(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));

    private static string EscapeLike(string value) =>
        value.Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal);

}

// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Globalization;
using Caliper.Core.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Caliper.Core.Persistence;

internal abstract class SqliteStoreBase(
    IOptions<PersistenceOptions> opts,
    ILogger logger) : IDisposable
{
    static SqliteStoreBase()
    {
        SQLitePCL.Batteries_V2.Init();
    }

    private readonly SemaphoreSlim _schemaGate = new(1, 1);
    private bool _schemaCreated;

    protected string SqlitePath { get; } = ResolveSqlitePath(opts.Value.SqlitePath);
    protected abstract string StoreName { get; }

    protected async Task EnsureCreatedAsync(CancellationToken ct)
    {
        if (_schemaCreated)
            return;

        await _schemaGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_schemaCreated)
                return;

            var directory = Path.GetDirectoryName(SqlitePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
            // WAL is a persistent, database-level property; set it once here rather than on
            // every connection open.
            await ExecuteNonQueryAsync(connection, "PRAGMA journal_mode=WAL;", ct).ConfigureAwait(false);
            await CreateSchemaAsync(connection, ct).ConfigureAwait(false);
            // Runs once per store instance, under the same schema gate as CreateSchemaAsync — the
            // seam SqliteRunStore uses for its startup sweep (roadmap §3.4): any row still `running`
            // when the store first initializes belongs to a process that is not this one (single
            // local writer), so it gets flipped to `interrupted` before any other IRunStore method
            // can run. A no-op for every other store.
            await AfterSchemaCreatedAsync(connection, ct).ConfigureAwait(false);

            if (logger.IsEnabled(LogLevel.Debug))
                logger.LogDebug("SQLite {StoreName} schema is ready at '{SqlitePath}'.", StoreName, SqlitePath);
            _schemaCreated = true;
        }
        finally
        {
            _schemaGate.Release();
        }
    }

    protected abstract Task CreateSchemaAsync(SqliteConnection connection, CancellationToken ct);

    /// <summary>
    /// Hook run exactly once, immediately after <see cref="CreateSchemaAsync"/>, while still under
    /// the schema-creation gate. Default no-op; override for one-time-per-process initialization
    /// that must happen before any other method on the store runs (see <c>SqliteRunStore</c>'s
    /// startup sweep).
    /// </summary>
    protected virtual Task AfterSchemaCreatedAsync(SqliteConnection connection, CancellationToken ct) =>
        Task.CompletedTask;

    protected SqliteConnection CreateConnection() =>
        new($"Data Source={SqlitePath};Default Timeout=5");

    protected async Task<SqliteConnection> OpenConnectionAsync(CancellationToken ct)
    {
        var connection = CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, "PRAGMA busy_timeout=5000;", ct).ConfigureAwait(false);
        return connection;
    }

    /// <summary>Adds a column if the table does not already have it (idempotent migration).</summary>
    protected static async Task EnsureColumnAsync(
        SqliteConnection connection,
        string table,
        string column,
        string definition,
        CancellationToken ct)
    {
        await using (var check = connection.CreateCommand())
        {
            check.CommandText = $"SELECT 1 FROM pragma_table_info('{table}') WHERE name = $column LIMIT 1;";
            check.Parameters.AddWithValue("$column", column);
            if (await check.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null)
                return;
        }

        await ExecuteNonQueryAsync(connection, $"ALTER TABLE {table} ADD COLUMN {column} {definition};", ct).ConfigureAwait(false);
    }

    protected static async Task ExecuteNonQueryAsync(
        SqliteConnection connection,
        string commandText,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    protected static string NowString() =>
        DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    private static string ResolveSqlitePath(string configuredPath)
    {
        return CaliperHome.ResolveStatePath(configuredPath);
    }

    public void Dispose() =>
        _schemaGate.Dispose();
}

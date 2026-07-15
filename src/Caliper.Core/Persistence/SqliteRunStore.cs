// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Globalization;
using Caliper.Core.Abstractions;
using Caliper.Core.Configuration;
using Caliper.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Caliper.Core.Persistence;

/// <summary>
/// SQLite-backed <see cref="IRunStore"/> (roadmap §3.4). Timestamps use <see cref="TimeProvider"/>
/// rather than <c>SqliteStoreBase.NowString</c> (which is <c>DateTimeOffset.UtcNow</c> directly) —
/// the CLAUDE.md house rule that new time-sensitive code introduces <see cref="TimeProvider"/>
/// instead of calling <c>DateTime</c>/<c>DateTimeOffset.Now/UtcNow</c>.
/// </summary>
internal sealed class SqliteRunStore(
    IOptions<PersistenceOptions> opts,
    TimeProvider timeProvider,
    ILogger<SqliteRunStore> logger) : SqliteStoreBase(opts, logger), IRunStore
{
    protected override string StoreName => "run store";

    public async Task<string> StartAsync(string sessionId, string? jobName, int maxSteps, bool unattended, CancellationToken ct)
    {
        await EnsureCreatedAsync(ct).ConfigureAwait(false);

        var runId = Guid.NewGuid().ToString("N");
        var now = Now();
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO runs (run_id, session_id, job_name, status, reason, step, max_steps, unattended, started_at, updated_at)
            VALUES ($run_id, $session_id, $job_name, $status, NULL, 0, $max_steps, $unattended, $now, $now);
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        command.Parameters.AddWithValue("$session_id", sessionId);
        command.Parameters.AddWithValue("$job_name", (object?)jobName ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", ToStorage(RunStatus.Running));
        command.Parameters.AddWithValue("$max_steps", maxSteps);
        command.Parameters.AddWithValue("$unattended", unattended ? 1 : 0);
        command.Parameters.AddWithValue("$now", now);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        return runId;
    }

    public async Task UpdateStepAsync(string runId, int stepNumber, CancellationToken ct)
    {
        await EnsureCreatedAsync(ct).ConfigureAwait(false);

        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE runs SET step = $step, updated_at = $now WHERE run_id = $run_id;";
        command.Parameters.AddWithValue("$step", stepNumber);
        command.Parameters.AddWithValue("$now", Now());
        command.Parameters.AddWithValue("$run_id", runId);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task CompleteAsync(string runId, RunStatus status, string? reason, CancellationToken ct)
    {
        await EnsureCreatedAsync(ct).ConfigureAwait(false);

        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE runs SET status = $status, reason = $reason, updated_at = $now WHERE run_id = $run_id;";
        command.Parameters.AddWithValue("$status", ToStorage(status));
        command.Parameters.AddWithValue("$reason", (object?)reason ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", Now());
        command.Parameters.AddWithValue("$run_id", runId);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task MarkResumedAsync(string runId, int maxSteps, CancellationToken ct)
    {
        await EnsureCreatedAsync(ct).ConfigureAwait(false);

        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE runs SET status = $status, reason = NULL, max_steps = $max_steps, resumed = 1, updated_at = $now
            WHERE run_id = $run_id;
            """;
        command.Parameters.AddWithValue("$status", ToStorage(RunStatus.Running));
        command.Parameters.AddWithValue("$max_steps", maxSteps);
        command.Parameters.AddWithValue("$now", Now());
        command.Parameters.AddWithValue("$run_id", runId);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<RunRecord?> GetAsync(string runId, CancellationToken ct)
    {
        await EnsureCreatedAsync(ct).ConfigureAwait(false);

        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT run_id, session_id, job_name, status, reason, step, max_steps, unattended, started_at, updated_at, resumed
            FROM runs WHERE run_id = $run_id LIMIT 1;
            """;
        command.Parameters.AddWithValue("$run_id", runId);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadRecord(reader) : null;
    }

    public async Task<IReadOnlyList<RunRecord>> ListRecentAsync(int limit, CancellationToken ct)
    {
        await EnsureCreatedAsync(ct).ConfigureAwait(false);

        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT run_id, session_id, job_name, status, reason, step, max_steps, unattended, started_at, updated_at, resumed
            FROM runs ORDER BY updated_at DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", Math.Max(0, limit));

        var results = new List<RunRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            results.Add(ReadRecord(reader));

        return results;
    }

    protected override async Task CreateSchemaAsync(SqliteConnection connection, CancellationToken ct)
    {
        await ExecuteNonQueryAsync(connection, """
                CREATE TABLE IF NOT EXISTS runs (
                  run_id      TEXT PRIMARY KEY,
                  session_id  TEXT NOT NULL REFERENCES sessions(id),
                  job_name    TEXT,
                  status      TEXT NOT NULL,
                  reason      TEXT,
                  step        INTEGER NOT NULL DEFAULT 0,
                  max_steps   INTEGER NOT NULL,
                  unattended  INTEGER NOT NULL DEFAULT 0,
                  resumed     INTEGER NOT NULL DEFAULT 0,
                  started_at  TEXT NOT NULL,
                  updated_at  TEXT NOT NULL
                );
                """, ct).ConfigureAwait(false);
        // A6 migration: the column is in CREATE TABLE for fresh databases, but the runs table
        // shipped this release without it — heal any database created before the column existed.
        await EnsureColumnAsync(connection, "runs", "resumed", "INTEGER NOT NULL DEFAULT 0", ct).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, """
                CREATE INDEX IF NOT EXISTS ix_runs_status ON runs(status);
                """, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Startup sweep (roadmap §3.4): runs exactly once per store instance, immediately after schema
    /// creation and before any other <see cref="IRunStore"/> method can execute (both gated by the
    /// same <c>SqliteStoreBase</c> schema-creation semaphore). Single local writer/process model: a
    /// row still <see cref="RunStatus.Running"/> at the moment this store initializes belongs to a
    /// process that is not this one, so it is flipped to <see cref="RunStatus.Interrupted"/>.
    /// Terminal rows (completed/failed/cancelled/interrupted) are left untouched by the WHERE clause.
    /// </summary>
    protected override async Task AfterSchemaCreatedAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE runs SET status = $interrupted, reason = $sweep_reason, updated_at = $now
            WHERE status = $running;
            """;
        command.Parameters.AddWithValue("$interrupted", ToStorage(RunStatus.Interrupted));
        command.Parameters.AddWithValue("$running", ToStorage(RunStatus.Running));
        command.Parameters.AddWithValue("$sweep_reason", "Interrupted by startup sweep: process ended without a clean shutdown.");
        command.Parameters.AddWithValue("$now", Now());
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private string Now() => timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);

    private static RunRecord ReadRecord(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            FromStorage(reader.GetString(3)),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetInt32(5),
            reader.GetInt32(6),
            reader.GetInt32(7) != 0,
            DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            reader.GetInt32(10) != 0);

    private static string ToStorage(RunStatus status) =>
        status switch
        {
            RunStatus.Running => "running",
            RunStatus.Completed => "completed",
            RunStatus.Failed => "failed",
            RunStatus.Cancelled => "cancelled",
            RunStatus.Interrupted => "interrupted",
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
        };

    private static RunStatus FromStorage(string status) =>
        status switch
        {
            "running" => RunStatus.Running,
            "completed" => RunStatus.Completed,
            "failed" => RunStatus.Failed,
            "cancelled" => RunStatus.Cancelled,
            "interrupted" => RunStatus.Interrupted,
            _ => throw new InvalidOperationException($"Unknown stored run status: {status}"),
        };
}

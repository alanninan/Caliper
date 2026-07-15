// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Configuration;
using Caliper.Core.Models;
using Caliper.Core.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Caliper.Core.Tests.Persistence;

public sealed class SqliteRunStoreTests : IDisposable
{
    private readonly List<string> _paths = [];

    [Fact]
    public async Task StartAsync_creates_a_running_row_with_the_resolved_step_budget()
    {
        var path = NewDbPath();
        var store = Build(path);

        var runId = await store.StartAsync("session-1", jobName: null, maxSteps: 10, unattended: false, CancellationToken.None);
        var run = await store.GetAsync(runId, CancellationToken.None);

        Assert.NotNull(run);
        Assert.Equal("session-1", run!.SessionId);
        Assert.Null(run.JobName);
        Assert.Equal(RunStatus.Running, run.Status);
        Assert.Null(run.Reason);
        Assert.Equal(0, run.Step);
        Assert.Equal(10, run.MaxSteps);
        Assert.False(run.Unattended);
    }

    [Fact]
    public async Task UpdateStepAsync_bumps_the_recorded_step()
    {
        var path = NewDbPath();
        var store = Build(path);
        var runId = await store.StartAsync("session-1", null, 10, false, CancellationToken.None);

        await store.UpdateStepAsync(runId, 4, CancellationToken.None);

        var run = await store.GetAsync(runId, CancellationToken.None);
        Assert.Equal(4, run!.Step);
    }

    [Fact]
    public async Task CompleteAsync_writes_terminal_status_and_reason()
    {
        var path = NewDbPath();
        var store = Build(path);
        var runId = await store.StartAsync("session-1", "nightly", 10, true, CancellationToken.None);

        await store.CompleteAsync(runId, RunStatus.Failed, "Streaming error: boom", CancellationToken.None);

        var run = await store.GetAsync(runId, CancellationToken.None);
        Assert.Equal(RunStatus.Failed, run!.Status);
        Assert.Equal("Streaming error: boom", run.Reason);
        Assert.Equal("nightly", run.JobName);
        Assert.True(run.Unattended);
    }

    [Fact]
    public async Task MarkResumedAsync_flips_an_interrupted_row_back_to_running_with_a_new_budget()
    {
        var path = NewDbPath();
        var store = Build(path);
        var runId = await store.StartAsync("session-1", null, 10, false, CancellationToken.None);
        await store.UpdateStepAsync(runId, 4, CancellationToken.None);
        await store.CompleteAsync(runId, RunStatus.Interrupted, "Interrupted by startup sweep.", CancellationToken.None);

        await store.MarkResumedAsync(runId, maxSteps: 6, CancellationToken.None);

        var run = await store.GetAsync(runId, CancellationToken.None);
        Assert.Equal(RunStatus.Running, run!.Status);
        Assert.Null(run.Reason);
        Assert.Equal(6, run.MaxSteps);
        // Step itself is left as recorded; AgentRunner's first TurnStarted of the resumed leg
        // overwrites it via UpdateStepAsync once the loop actually starts.
        Assert.Equal(4, run.Step);
    }

    [Fact]
    public async Task GetAsync_for_an_unknown_run_id_returns_null()
    {
        var store = Build(NewDbPath());

        Assert.Null(await store.GetAsync("missing", CancellationToken.None));
    }

    [Fact]
    public async Task ListRecentAsync_orders_by_most_recently_updated_first()
    {
        var path = NewDbPath();
        var store = Build(path);
        var first = await store.StartAsync("session-1", null, 10, false, CancellationToken.None);
        await Task.Delay(20);
        var second = await store.StartAsync("session-2", null, 10, false, CancellationToken.None);
        await Task.Delay(20);
        // Touching the first row again should move it back to the front.
        await store.UpdateStepAsync(first, 1, CancellationToken.None);

        var recent = await store.ListRecentAsync(10, CancellationToken.None);

        Assert.Equal(first, recent[0].RunId);
        Assert.Equal(second, recent[1].RunId);
    }

    [Fact]
    public async Task ListRecentAsync_honors_the_limit()
    {
        var path = NewDbPath();
        var store = Build(path);
        for (var i = 0; i < 5; i++)
            await store.StartAsync($"session-{i}", null, 10, false, CancellationToken.None);

        var recent = await store.ListRecentAsync(2, CancellationToken.None);

        Assert.Equal(2, recent.Count);
    }

    [Fact]
    public async Task Persists_across_store_instances()
    {
        var path = NewDbPath();
        var firstStore = Build(path);
        var runId = await firstStore.StartAsync("session-1", null, 10, false, CancellationToken.None);

        var secondStore = Build(path);
        var run = await secondStore.GetAsync(runId, CancellationToken.None);

        Assert.NotNull(run);
        Assert.Equal("session-1", run!.SessionId);
    }

    // ── Startup sweep (roadmap §3.4) ────────────────────────────────────────
    //
    // A killed process cannot run any more code, so "kill mid-tool" cannot be simulated by
    // cancelling something in-process — the row it left behind just never got a terminal write.
    // Simulate that directly: write a `running` row via raw SQL (bypassing the store, the way a
    // now-dead process would have left it), then let a *new* store instance observe it — its
    // schema-creation gate runs the sweep before anything else can touch the table.

    [Fact]
    public async Task Startup_sweep_flips_a_row_left_running_by_a_killed_process_to_interrupted()
    {
        var path = NewDbPath();
        var firstStore = Build(path);
        // Force schema creation, then insert a "running" row directly — simulating a process that
        // started a run and was killed before it could write any terminal status.
        _ = await firstStore.StartAsync("dummy", null, 1, false, CancellationToken.None);
        var killedRunId = await InsertRawRunAsync(path, sessionId: "session-killed", status: "running", step: 4, maxSteps: 10);

        // A fresh store instance simulates the next process start.
        var secondStore = Build(path);
        var run = await secondStore.GetAsync(killedRunId, CancellationToken.None);

        Assert.NotNull(run);
        Assert.Equal(RunStatus.Interrupted, run!.Status);
        Assert.NotNull(run.Reason);
        // The sweep only changes status/reason/updated_at; the step recorded before the kill survives.
        Assert.Equal(4, run.Step);
    }

    [Fact]
    public async Task Startup_sweep_leaves_terminal_rows_alone()
    {
        var path = NewDbPath();
        var firstStore = Build(path);
        var completedId = await firstStore.StartAsync("session-a", null, 10, false, CancellationToken.None);
        await firstStore.CompleteAsync(completedId, RunStatus.Completed, "Completed", CancellationToken.None);
        var failedId = await firstStore.StartAsync("session-b", null, 10, false, CancellationToken.None);
        await firstStore.CompleteAsync(failedId, RunStatus.Failed, "boom", CancellationToken.None);
        var cancelledId = await firstStore.StartAsync("session-c", null, 10, false, CancellationToken.None);
        await firstStore.CompleteAsync(cancelledId, RunStatus.Cancelled, "Cancelled", CancellationToken.None);
        var interruptedId = await InsertRawRunAsync(path, "session-d", status: "interrupted", step: 2, maxSteps: 10);

        // A fresh instance runs the sweep again; none of the terminal rows should change.
        var secondStore = Build(path);
        _ = await secondStore.GetAsync(completedId, CancellationToken.None);

        Assert.Equal(RunStatus.Completed, (await secondStore.GetAsync(completedId, CancellationToken.None))!.Status);
        Assert.Equal(RunStatus.Failed, (await secondStore.GetAsync(failedId, CancellationToken.None))!.Status);
        Assert.Equal(RunStatus.Cancelled, (await secondStore.GetAsync(cancelledId, CancellationToken.None))!.Status);
        Assert.Equal(RunStatus.Interrupted, (await secondStore.GetAsync(interruptedId, CancellationToken.None))!.Status);
    }

    private static SqliteRunStore Build(string path) =>
        new(
            Options.Create(new PersistenceOptions { SqlitePath = path }),
            new FakeTimeProvider(),
            NullLogger<SqliteRunStore>.Instance);

    /// <summary>
    /// Inserts a run row directly via raw SQL, bypassing <see cref="SqliteRunStore"/> entirely — the
    /// only faithful way to simulate a row left behind by a process that no longer exists to call
    /// any store method itself. Requires the `runs` table to already exist (call a store method on
    /// the same path first).
    /// </summary>
    private static async Task<string> InsertRawRunAsync(string path, string sessionId, string status, int step, int maxSteps)
    {
        var runId = Guid.NewGuid().ToString("N");
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO runs (run_id, session_id, job_name, status, reason, step, max_steps, unattended, started_at, updated_at)
            VALUES ($run_id, $session_id, NULL, $status, NULL, $step, $max_steps, 0, $now, $now);
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        command.Parameters.AddWithValue("$session_id", sessionId);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$step", step);
        command.Parameters.AddWithValue("$max_steps", maxSteps);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync();
        return runId;
    }

    private string NewDbPath()
    {
        var path = Path.Combine(Path.GetTempPath(), "caliper-runs-" + Guid.NewGuid().ToString("N") + ".db");
        _paths.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var path in _paths.SelectMany(path => new[] { path, $"{path}-wal", $"{path}-shm" }))
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (IOException)
            {
            }
        }
    }
}

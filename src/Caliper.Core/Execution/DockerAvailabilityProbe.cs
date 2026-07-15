// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.ComponentModel;

namespace Caliper.Core.Execution;

/// <summary>
/// Caches whether <c>docker info</c> succeeds, so a run of container-backed shell calls doesn't
/// re-shell to docker on every single command. Probed lazily on first container execution rather
/// than at host startup: <c>Execution.Backend</c> is a live config seam (roadmap §3.3 item 7), so a
/// startup-only probe would go stale the moment a host flips <c>Backend</c> from Host to Container
/// mid-session and would also do pointless work for hosts that never use the container backend at
/// all. A probe on every call would work too but re-shells to <c>docker info</c> far more than
/// needed. A short <see cref="TimeProvider"/>-driven cache is the simplest design that stays
/// correct either way: a negative ("unavailable") result self-heals within the cache window once
/// Docker Desktop starts, and a positive result is re-confirmed periodically rather than trusted
/// forever (so a `docker` daemon that dies mid-session is noticed within the TTL, not never).
/// </summary>
internal sealed class DockerAvailabilityProbe(IProcessRunner processRunner, TimeProvider timeProvider) : IDisposable
{
    private static readonly TimeSpan s_cacheTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan s_probeTimeout = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    // A7: single-flight for concurrent cold probes. Without this, N callers racing in before the
    // cache is populated (e.g. several ShellTool calls dispatched at once right after startup)
    // would each shell out to `docker info` independently. The semaphore serializes the actual
    // probe; every waiter re-checks the cache once it acquires the gate, so only the first caller
    // ever runs the process — the rest observe the freshly-populated cache and return immediately.
    private readonly SemaphoreSlim _probeGate = new(1, 1);
    private (bool Available, string? Error, DateTimeOffset ExpiresAt)? _cached;

    public async Task<(bool Available, string? Error)> ProbeAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_cached is { } cached && cached.ExpiresAt > timeProvider.GetUtcNow())
                return (cached.Available, cached.Error);
        }

        await _probeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-check inside the gate: another caller may have just finished probing while we
            // were waiting for the semaphore, in which case we should use its (now-cached) result
            // rather than shelling out again.
            lock (_gate)
            {
                if (_cached is { } cached && cached.ExpiresAt > timeProvider.GetUtcNow())
                    return (cached.Available, cached.Error);
            }

            var (available, error) = await RunProbeAsync(ct).ConfigureAwait(false);

            lock (_gate)
                _cached = (available, error, timeProvider.GetUtcNow() + s_cacheTtl);

            return (available, error);
        }
        finally
        {
            _probeGate.Release();
        }
    }

    private async Task<(bool Available, string? Error)> RunProbeAsync(CancellationToken ct)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(s_probeTimeout);
            var spec = new ProcessRunSpec("docker", ["info"], WorkingDirectory: null, EnvironmentScrubPrefixes: [], OutputBufferCapChars: 4096);
            var result = await processRunner.RunAsync(spec, timeoutCts.Token).ConfigureAwait(false);
            if (result.ExitCode == 0)
                return (true, null);

            var detail = result.Output.Trim();
            var detailSuffix = detail.Length == 0 ? "" : $": {Truncate(detail, 300)}";
            return (false, $"'docker info' exited with code {result.ExitCode}{detailSuffix}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Our own s_probeTimeout fired, not the caller's token — treat it as "unavailable",
            // not as a real cancellation to propagate.
            return (false, "'docker info' did not respond in time.");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Win32Exception)
        {
            // Most commonly: docker isn't installed (Win32Exception, "file not found").
            return (false, ex.Message);
        }
    }

    private static string Truncate(string text, int maxChars) =>
        text.Length <= maxChars ? text : string.Concat(text.AsSpan(0, maxChars), "…");

    public void Dispose() => _probeGate.Dispose();
}

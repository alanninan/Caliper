// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Execution;

namespace Caliper.Core.Tests.Execution;

/// <summary>
/// Records every <see cref="ProcessRunSpec"/> passed to <see cref="RunAsync"/> and replays a
/// scripted sequence of results/exceptions, so backend tests can assert on exact argument
/// construction (network/memory/cpus/user/mount/workdir mapping, ArgumentList shape) without
/// spawning real processes. Accessible from tests only via
/// <c>InternalsVisibleTo("Caliper.Core.Tests")</c> — see <see cref="IProcessRunner"/>'s doc comment.
/// </summary>
internal sealed class FakeProcessRunner : IProcessRunner
{
    private readonly Queue<Func<ProcessRunSpec, ProcessRunResult>> _scripted = new();

    public List<ProcessRunSpec> Calls { get; } = [];

    public void Enqueue(ProcessRunResult result) => _scripted.Enqueue(_ => result);

    public void Enqueue(Exception exception) => _scripted.Enqueue(_ => throw exception);

    public Task<ProcessRunResult> RunAsync(ProcessRunSpec spec, CancellationToken ct)
    {
        Calls.Add(spec);
        var next = _scripted.Count > 0 ? _scripted.Dequeue() : _ => new ProcessRunResult(0, "");
        return Task.FromResult(next(spec));
    }
}

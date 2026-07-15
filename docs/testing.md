# Testing and evals

## Layout

| Project | Scope |
| --- | --- |
| `tests/Caliper.Core.Tests` | Engine: loop guardrails, permissions, tools, subagents, scheduling, execution backends, persistence, durable runs, config validation |
| `tests/Caliper.Console.Tests` | Pure console logic: flag composition, exit codes, rendering |
| `tests/Caliper.App.Tests` | View models, event mapping, transcript factory, preferences |
| `tests/Caliper.Evals` | Scenario harness: hermetic (no model) and model-in-the-loop |

xUnit **v2** (2.9.3, not v3). Naming: `Method_Condition_Expected`. Warnings are errors
everywhere, including tests.

## House patterns

- **Hermetic first.** Scripted model clients / turn strategies stand in for the model; tests
  drive the real `AgentRunner`/`ConversationOrchestrator`/`PermissionGate`/SQLite stores,
  not mocks of them. The subagent suite builds the same recursive DI graph production uses,
  so a parent run really spawns a child run.
- **Time is always faked.** Anything time-dependent (scheduler, probes, run timestamps) is
  driven by `FakeTimeProvider` (`Microsoft.Extensions.TimeProvider.Testing`). The scheduler
  exposes an internal `DelayArmed` seam so tests know exactly when the loop's timer exists
  before advancing the clock — the delay task is created *before* the seam fires
  (contract: "armed" means the timer is real; the reverse ordering was a
  timeout-under-load flake).
- **Determinism over sleeps.** Concurrency tests gate on `TaskCompletionSource`, never
  `Task.Delay`; wall-clock assertions exist only as generous upper bounds on
  cancellation/timeout paths.
- **Process seams.** Execution backends take an internal `IProcessRunner`, so container
  tests assert on exact `docker` argument construction and kill behavior with a fake — no
  Docker needed. One opt-in integration test does a real `docker` round-trip and self-skips
  when the daemon is unavailable.
- **Safety invariants are named tests.** Every guarantee in
  [permissions.md](permissions.md) / [subagents.md](subagents.md) (deny+report, denylist
  union, restrict-only inheritance, fail-closed container, no-regrant under
  `RememberApprovals`, wildcard-requires-container…) has a test with that meaning in its
  name. Don't merge a safety-relevant change without one.

## The eval harness

```powershell
dotnet run --project tests/Caliper.Evals -- --suite all --out eval-report.json      # hermetic
dotnet run --project tests/Caliper.Evals -- --suite tool-calling --model "<slug>"   # live model
```

Suites (`Suites/SuiteRegistry`): tool-calling, permissions, compaction, subagents, and more;
`all` runs everything hermetically via `FakeModelClient`. The report scores task completion,
correct-tool selection, argument validity, permission behavior, loop incidence, and step
counts. Passing `--model` swaps the fake for a live OpenRouter model to evaluate real model
behavior against the same scenarios.

## CI expectations

`dotnet build Caliper.slnx` must be warning-clean and `dotnet test Caliper.slnx` fully green
before any commit. For changes near the scheduler or other timing-sensitive code, run the
full suite a few times — pass-in-isolation/fail-under-load is exactly how the one historical
flake presented.

# Contributing to Caliper

Thanks for your interest in Caliper. This guide covers what you need to build, test, and land
a change. The full design documentation lives in [`docs/`](docs/README.md); the day-to-day
agent/contributor conventions live in [`CLAUDE.md`](CLAUDE.md) / [`AGENTS.md`](AGENTS.md)
(mirrors — edit both together).

## Prerequisites

- .NET SDK 10.0+
- Windows for the WinUI app (`Caliper.App`); Core and Console build anywhere .NET 10 does
- Optional: Docker Desktop (WSL2 Linux containers) for the container-backend integration test
- Optional: an OpenRouter key (`CALIPER_OPENROUTER_KEY`) for model-in-the-loop evals — never
  needed for the regular test suite, which is fully hermetic

## Build, test, verify

```powershell
dotnet build Caliper.slnx      # must be warning-clean — warnings are errors
dotnet test  Caliper.slnx      # must be fully green
dotnet run --project tests/Caliper.Evals -- --suite all --out eval-report.json   # hermetic evals
```

For changes near timing-sensitive code (the scheduler, probes), run the full suite a few
times — the failure mode to catch is pass-in-isolation / fail-under-parallel-load.

## Hard rules (enforced by build or review)

1. **Native AOT.** Console publishes AOT; Core is `IsAotCompatible`. JSON is source-gen only —
   register every serialized type in `Protocol/CaliperJsonContext.cs`. New dependencies must
   be trim/AOT-safe.
2. **Central package management.** Versions go in `Directory.Packages.props`, never on a
   `<PackageReference>`.
3. **Warnings are errors.** Analyzers at `latest-recommended`. Don't add suppressions without
   a comment and a reason.
4. **`TimeProvider` for all new time math.** Never `DateTime.Now`/`DateTimeOffset.UtcNow` in
   new code; test with `FakeTimeProvider`.
5. **Safety defaults are non-negotiable.** Unattended paths deny + report (never
   silent-allow, never silent-drop); the global shell denylist merges into every overlay;
   permission inheritance is restrict-only; the container backend fails closed. A change that
   weakens one of these needs an explicit design discussion first.
6. **Tool schemas stay flat** (no nested objects), and tool outcomes are `ToolResult`, not
   exceptions.
7. **New `AgentEvent`s must be handled in all three consumers** (Console renderer, App
   mapper/transcript factory, eval harness).
8. **Every safety invariant gets a named test.** See [docs/testing.md](docs/testing.md) for
   the house patterns (hermetic-first, faked time, `TaskCompletionSource` over sleeps).

## Style

Standard modern C#: file-scoped namespaces, nullable enabled, records for DTOs/events,
primary constructors for mutation-free services, `CancellationToken` on every async method,
`ConfigureAwait(false)` in Core (it's a library). Tests are named
`Method_Condition_Expected`. Match the comment density and idiom of the surrounding code.

## Commits and PRs

- Keep commits scoped to one logical change with a message that explains *why*, not just
  what. Bisectable history is a feature here.
- Don't commit secrets — keys come from environment variables (console) or Windows
  Credential Manager (app). The `CALIPER_*` env scrub exists so they can't leak to child
  processes; keep it that way.
- Update the relevant `docs/` page, `README.md`, and the `CLAUDE.md`/`AGENTS.md` mirrors when
  behavior or commands change — byte-identical mirrors are checked in review.
- Don't touch generated files or the compile-excluded strategy files (see `CLAUDE.md`).

## Code ownership

See [`CODEOWNERS`](CODEOWNERS). All changes are reviewed by the code owner.

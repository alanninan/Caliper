# Caliper — project guide

> `CLAUDE.md` and `AGENTS.md` are mirrors — edit both together.

Caliper is a **.NET 10 agent runtime + terminal app**. The model proposes work; the **host
owns** tool dispatch, permissions, retries, context management, persistence, and safety.
The engine is a reusable library (`Caliper.Core`); the terminal is one front-end on top
of it (`Caliper.Console`).

## Commands

```powershell
dotnet build Caliper.slnx                         # build (solution is .slnx)
dotnet test  Caliper.slnx                          # all unit tests (xUnit)
dotnet run --project src/Caliper.Console           # interactive REPL

# one-shot, read-only (defaults to Plan mode + read-only tools)
dotnet run --project src/Caliper.Console -- --prompt "Summarize this repo" --print

# unattended one-shot (Auto mode; prompts are denied + reported, never asked)
dotnet run --project src/Caliper.Console -- --prompt "Check outdated packages" --unattended

# headless cron scheduler (runs Caliper:Schedules until Ctrl+C; rejects --prompt/--unattended)
dotnet run --project src/Caliper.Console -- --serve

# evals — hermetic (no model) / model-in-the-loop
dotnet run --project tests/Caliper.Evals -- --suite all --out eval-report.json
dotnet run --project tests/Caliper.Evals -- --suite tool-calling --model "<openrouter-slug>"

# AOT publish
dotnet publish src/Caliper.Console -c Release -r win-x64
```

Credentials come from env vars: `CALIPER_OPENROUTER_KEY` (required for model runs),
`CALIPER_SEARCH_KEY` (Tavily). Config seeds at `~/.caliper/config.json` on first run.

## Layout

- `src/Caliper.Core/` — engine library. Agent loop (`Agents/AgentRunner.cs`), tools
  (`Tools/`, built-ins in `Tools/BuiltIn/`, MCP in `Tools/Mcp/`), model clients (`Models/`),
  persistence (`Persistence/`, SQLite), context/compaction (`Context/`), skills, memory,
  permissions (`Permissions/`), cron scheduling (`Scheduling/`, Cronos-based), sandboxed shell
  execution (`Execution/` — `IExecutionBackend`, Host/Container backends, `docker` CLI),
  JSON source-gen (`Protocol/CaliperJsonContext.cs`).
- `src/Caliper.Console/` — terminal host. `Program.cs` (entry + slash commands), `Rendering/`,
  `Commands/`, and `ConsolePermissionPrompt`.
- `src/Caliper.App/` — WinUI 3 desktop host on the same engine. MVVM (`ViewModels/`, `Views/`),
  `ApprovalService`/`IPermissionPrompt`, Windows Credential Manager store (`Security/`), and
  app-local UI prefs in `~/.caliper/app-ui.json` (`Preferences/`). Not trimmed/AOT.
- `tests/Caliper.Core.Tests`, `tests/Caliper.Console.Tests`, `tests/Caliper.App.Tests` — xUnit.
- `tests/Caliper.Evals` — hermetic + model-in-the-loop eval harness (`FakeModelClient`).

## Architecture

- **Layered runtime, not Clean/VSA/DDD.** Core is a reusable engine; Console is a front-end.
- **DI:** everything is wired in `ServiceCollectionExtensions.AddCaliperCore`. Host is
  `Microsoft.Extensions.Hosting` (`Host.CreateApplicationBuilder`).
- **Tools:** implement `ITool` (`Name`, `Description`, `ParameterSchema`, `SideEffect`,
  `InvokeAsync`), registered as singletons, gated by `CaliperOptions.EnabledTools`, merged
  with MCP tools in `ToolRegistry`. Keep schemas **flat** (no nested objects — degrades
  small-model GBNF conversion).
- **Permissions:** `IPermissionGate` → `Plan` denies non-read-only; `Auto` uses shell
  allow/deny lists + outside-root file checks; `AskAlways` prompts via `IPermissionPrompt`
  (null prompt ⇒ Deny; non-interactive console ⇒ Deny).
- **Turn strategy:** default is `NativeToolStrategy` (chosen by `TurnStrategySelector`).
- **Scheduling:** `Caliper:Schedules` (list; live — re-read every tick) drives
  `SchedulerHostedService`, registered only under `--serve`. Jobs run via `ScheduleJobRunner`
  with `RunSpec { Unattended = true }` — prompts deny + report (`UnattendedPermissionPrompt`;
  in the REPL a `RoutingPermissionPrompt` routes by `PermissionRequest.Unattended`). Overlap:
  skip; misfire: no catch-up. `/schedule list|run <name>` manages jobs interactively. A job's
  `Permissions` overlay should set `Mode: Auto` for its allowlists to matter; the global shell
  denylist is always merged in.
- **Config precedence:** `~/.caliper/config.json` → `CALIPER_` env vars → CLI overrides.
  The live options class is **`CaliperOptions`** (the `Caliper:` section).
- **Sandboxed shell execution:** `ShellTool` (bash/powershell) delegates process launch to an
  `IExecutionBackend`, picked per call from the live `Caliper:Execution:Backend` (`Host` default |
  `Container`) — never at construction time, since both backends are always-constructed DI
  singletons. `HostExecutionBackend` is a behavior-preserving extraction of the pre-existing
  process logic. `ContainerExecutionBackend` runs `docker run` (bash only; a `powershell` call
  under `Container` fails with a clear message) with the run's working root bind-mounted at
  `/workspace`, `--network none` by default, and resource limits from `Caliper:Execution`. **Fails
  closed:** if `docker info` doesn't succeed (probed lazily, cached briefly via `TimeProvider`),
  every container-backend call returns a failed `ToolResult` — it never silently falls back to the
  host. **Wildcard allowlist requires Container:** a bare `"*"` in `ShellAutoAllowlist` (global
  `Permissions` section or a schedule job's overlay) is rejected by validation unless
  `Execution.Backend == Container` — enforced in `CaliperOptionsValidator`,
  `PermissionsOptionsValidator`, and the corresponding `ConfigWriter.SaveXAsync` cross-checks.

## Conventions / exceptions to the global .NET defaults

- **Central Package Management:** all versions live in `Directory.Packages.props`. **Never**
  put `Version=` on a `<PackageReference>` in a csproj.
- **Native AOT is a hard constraint:** Console `PublishAot=true`, Core `IsAotCompatible=true`.
  JSON is **source-gen only** (`JsonSerializerIsReflectionEnabledByDefault=false`) — register
  every serialized type in `Protocol/CaliperJsonContext`. New dependencies must be trim/AOT-safe
  (e.g. Cronos ✅, Quartz ❌, `docker` CLI ✅ over Docker.DotNet ⚠️).
- **Warnings are errors** (`TreatWarningsAsErrors=true`, analyzers at `latest-recommended`).
  Keep builds warning-clean. Intentional suppressions: `CA1305`, `CA1848` (global); `IL2104`
  (Console); `CA1707` (tests).
- **Tests use xUnit v2 (2.9.3), not v3.** Method naming: `Method_Condition_Expected`.
- **Tool outcomes use `ToolResult`** (success + output), not a generic `Result<T>`. Exceptions
  are reserved for genuinely exceptional store/IO errors.
- **`TimeProvider` is wired in for new time math** — the scheduler (`Scheduling/`) does all
  timing via `TimeProvider` (`GetUtcNow()` + `Task.Delay(delay, timeProvider, ct)`, tested with
  `FakeTimeProvider`), and Core registers `TimeProvider.System` via `TryAddSingleton`. Legacy
  persistence timestamps still use `DateTimeOffset.UtcNow` (`SqliteStoreBase.NowString`) until
  touched. Any **new** time-sensitive code must use `TimeProvider`, never
  `DateTime/DateTimeOffset.Now/UtcNow`.
- Followed from the global defaults: file-scoped namespaces, nullable, primary constructors,
  records for DTOs/events, async + `CancellationToken` everywhere, `ConfigureAwait(false)` in
  Core (it's a library; the Console app doesn't need it).

## Don't

- Don't edit or re-add the compile-excluded files (`Agents/SingleEnvelopeStrategy.cs`,
  `Agents/TwoPhaseStrategy.cs`, and the matching test) — they're removed via `<Compile Remove>`.
- Don't add knobs to `AgentOptions` — it's legacy/preserved and unused by the native strategy.
  Use `CaliperOptions`.
- Don't commit secrets; keys come from `CALIPER_OPENROUTER_KEY` / `CALIPER_SEARCH_KEY`.

## Roadmap

Future capability plans live in `docs/agent-capability-roadmap.md` (the `docs/` folder is
gitignored / local-only).

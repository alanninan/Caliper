# Caliper documentation

Caliper is a .NET 10 agent runtime with three fronts: an interactive console REPL, a WinUI 3
desktop app, and headless modes (unattended one-shots, a cron scheduler). The reusable engine
lives in `Caliper.Core`; every host sits on top of it. The design premise throughout: **the
model proposes work; the host owns** tool dispatch, permissions, retries, context management,
persistence, and safety.

## Where to start

| Doc | Covers |
| --- | --- |
| [architecture.md](architecture.md) | Engine/host split, the agent loop, `RunSpec`, events, turn strategies, DI |
| [cli.md](cli.md) | Console flags, slash commands, exit codes |
| [configuration.md](configuration.md) | `config.json` sections, precedence, live vs restart-required settings |
| [permissions.md](permissions.md) | Permission modes, the gate, allow/deny lists, unattended policy, overlays |
| [tools.md](tools.md) | The `ITool` contract, built-in tools, MCP, schema rules, timeouts |
| [subagents.md](subagents.md) | The `task` tool, profiles, guards, restrict-only inheritance |
| [scheduling.md](scheduling.md) | Cron jobs, `--serve`, overlap/misfire policy, `/schedule` |
| [sandboxed-execution.md](sandboxed-execution.md) | Execution backends, Docker container sandbox, fail-closed rules |
| [durable-runs.md](durable-runs.md) | The `runs` table, interruption, `--resume`, `/runs` |
| [desktop-app.md](desktop-app.md) | The WinUI 3 host: approvals, sessions pane, credential storage |
| [testing.md](testing.md) | Test layout, hermetic patterns, the eval harness |

## Quick start

```powershell
dotnet build Caliper.slnx                                    # build
dotnet test  Caliper.slnx                                    # all tests
$env:CALIPER_OPENROUTER_KEY = "<key>"                        # credentials via env vars
dotnet run --project src/Caliper.Console                     # interactive REPL
```

Config seeds at `~/.caliper/config.json` on first run. See [cli.md](cli.md) for the one-shot,
unattended, scheduler, and resume entry points.

## Safety posture (the short version)

- Three permission modes: `Plan` (read-only), `AskAlways` (prompt for every side effect),
  `Auto` (allowlists + in-root writes auto-allowed, prompt otherwise). The global shell
  denylist always applies and can never be lifted by any overlay.
- Headless runs **deny + report, never silently allow, never silently drop**: anything that
  would prompt a human is denied, logged, and surfaced in the run result and exit code.
- Shell commands can be sandboxed into a disposable, network-isolated Docker container;
  if Docker is unavailable the backend **fails closed** — it never silently falls back to
  the host.
- Subagent permission inheritance is restrict-only: a child can be tighter than its parent,
  never looser.
- Every orchestrator-driven run is journaled in a `runs` table and can be resumed after a
  crash; side-effecting tool calls are never automatically re-dispatched.

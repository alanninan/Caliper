# Caliper documentation

Caliper is a .NET 10 agent runtime you can drive three ways: an interactive console REPL, a
Windows desktop app, and headless modes (one-shot prompts, unattended automation, a cron
scheduler). The design premise throughout: **the model proposes work; Caliper decides what
actually runs** — every side effect passes a permission gate you configure.

## Quick start

```powershell
$env:CALIPER_OPENROUTER_KEY = "<key>"          # or use /auth set-key
dotnet run --project src/Caliper.Console       # interactive REPL
dotnet run --project src/Caliper.App           # desktop app (Windows)
```

Configuration seeds at `~/.caliper/config.json` on first run. See [cli.md](cli.md) for
one-shot, unattended, scheduler, and resume entry points.

## Using Caliper

| Doc | Covers |
| --- | --- |
| [cli.md](cli.md) | Console flags, slash commands, exit codes |
| [desktop-app.md](desktop-app.md) | The desktop app: chat workspace, approvals, schedules, runs, settings |
| [configuration.md](configuration.md) | `config.json` sections, precedence, what applies live vs after restart |
| [provider-authentication.md](provider-authentication.md) | Four providers, credentials, OAuth, and the manual acceptance pass |
| [permissions.md](permissions.md) | Permission modes, allow/deny lists, approvals, the unattended policy |
| [tools.md](tools.md) | What each built-in tool does, enabling/disabling, MCP servers |
| [subagents.md](subagents.md) | Delegating work to child agents, profiles, safety limits |
| [scheduling.md](scheduling.md) | Cron jobs, `--serve`, the app's Schedules page |
| [sandboxed-execution.md](sandboxed-execution.md) | Running shell commands in a Docker sandbox |
| [durable-runs.md](durable-runs.md) | Crash recovery: run tracking, interruption, resume |

## For contributors

| Doc | Covers |
| --- | --- |
| [architecture.md](architecture.md) | Engine/host split, the agent loop, events, tool contract, AOT rules |
| [testing.md](testing.md) | Test layout, hermetic patterns, the eval harness |
| [../CONTRIBUTING.md](../CONTRIBUTING.md) | Contribution workflow and repo conventions |

## Safety posture (the short version)

- Three permission modes: `Plan` (read-only), `AskAlways` (approve every side effect),
  `Auto` (allowlisted commands and in-project writes run without asking; everything else
  still prompts). The global shell denylist always applies and can never be lifted by any
  per-job or per-agent override.
- Headless runs **deny + report, never silently allow, never silently drop**: anything that
  would have asked you is denied, logged, and surfaced in the run summary and exit code.
- Shell commands can be sandboxed into a disposable, network-isolated Docker container. If
  Docker is unavailable the sandbox **fails closed** — commands fail rather than silently
  running on your machine.
- A delegated child agent can only ever be *more* restricted than its parent, never less.
- Long-running work is journaled and survives a crash: interrupted runs can be resumed, and
  side-effecting actions are never automatically re-run.

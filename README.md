# Caliper

Caliper is a .NET agent runtime with two front-ends — an interactive console app and a WinUI 3 desktop app — for running model-assisted workflows against a local workspace. It combines OpenRouter and Google Gemini chat clients, guarded local tools, MCP server support, session persistence, memory, skills, and context compaction. The reusable engine lives in `Caliper.Core`; the console and the WinUI app are two hosts on top of it.

The project is designed around deterministic host-side control: model output proposes work, while Caliper owns tool dispatch, permissions, retries, context management, persistence, and safety checks.

## Features

- Console-first agent loop with streaming responses, plus a WinUI 3 desktop app (`Caliper.App`)
  with a three-pane workbench, inline tool/diff inspector, docked permission approvals, and
  structured settings pages.
- OpenRouter and Google Gemini provider integration through `Microsoft.Extensions.AI` (Gemini via
  its OpenAI-compatible endpoint).
- Runtime model switching with `/model <slug>` and catalog inspection with `/models`; runtime
  provider switching with `/set provider <OpenRouter|Gemini>`.
- Tool permission modes: `AskAlways`, `Auto`, and `Plan`.
- Built-in tools for search, URL fetch, file reads/writes/edits, glob, grep, shell, memory, and skill loading.
- MCP client support for stdio and HTTP servers.
- Unattended runs (`--unattended` one-shot, `--serve` cron scheduler) with a deny+report
  permission policy — no prompt is ever silently allowed or silently dropped.
- Cron-scheduled jobs (`Caliper.Schedules` + `--serve`), each with its own prompt, model, step
  budget, working root, and permission overlay; `/schedule list` and `/schedule run <name>` manage
  them from the REPL.
- Sandboxed shell execution (`Caliper.Execution.Backend: Container`): shell commands run inside a
  disposable, network-isolated `docker` container instead of on the host; fails closed (never
  falls back to the host) when Docker is unavailable.
- SQLite-backed sessions, memory, and resumable transcripts.
- Context-window fitting with optional automatic compaction.
- Skill discovery from `~/.caliper/skills`.
- Hermetic and model-in-the-loop eval harnesses.
- Native AOT-oriented project settings for the console app.

## Repository Layout

```text
Caliper.slnx
src/
  Caliper.Console/      Console host, rendering, slash commands, sample config
  Caliper.Core/         Agent loop, tools, model clients, persistence, memory
  Caliper.App/          WinUI 3 desktop host (view models, XAML, Credential Manager store)
tests/
  Caliper.Console.Tests/
  Caliper.Core.Tests/
  Caliper.App.Tests/    View-model and pure-layer tests for the WinUI app
  Caliper.Evals/        Hermetic and model-in-the-loop evaluation suites
```

## Desktop app (`Caliper.App`)

The WinUI 3 desktop app is a second host on the same `Caliper.Core` engine.

- Run it with `dotnet run --project src/Caliper.App` (or F5 in Visual Studio); requires the Windows
  App SDK and a Windows 10.0.19041+ target.
- API keys are stored in **Windows Credential Manager** (not `config.json`) — enter them under
  Settings → Models &amp; providers. Provider endpoint and key changes apply after a restart, which
  the app offers as a one-click "Restart Caliper" button.
- UI preferences (theme, window placement, sessions-pane state) live in `~/.caliper/app-ui.json`;
  runtime settings still come from `~/.caliper/config.json`.

## Prerequisites

- .NET SDK 10.0 or newer.
- An API key for model-backed runs: an OpenRouter key (default provider), or a Google Gemini API
  key if you plan to use `Provider: "Gemini"`.
- A model slug from your chosen provider. Pick a model appropriate for your task; tool-calling
  models work best with Caliper's native tool strategy.

## Setup

1. Restore and build the solution.

   ```powershell
   dotnet restore Caliper.slnx
   dotnet build Caliper.slnx
   ```

2. Configure credentials. The recommended local setup is to keep the API key in an environment variable.

   ```powershell
   $env:CALIPER_OPENROUTER_KEY = "<your-openrouter-api-key>"
   ```

3. Configure the model slug. Caliper seeds `~/.caliper/config.json` on first run. Set `Caliper.Model` to the OpenRouter model slug you want to use.

   ```json
   {
     "Caliper": {
       "Provider": "OpenRouter",
       "Model": "<your-openrouter-model-slug>"
     }
   }
   ```

   You can also override the model for a single shell session:

   ```powershell
   $env:CALIPER_Caliper__Model = "<your-openrouter-model-slug>"
   ```

   To use Gemini instead, set `CALIPER_GEMINI_KEY` and switch the provider and model:

   ```powershell
   $env:CALIPER_GEMINI_KEY = "<your-gemini-api-key>"
   ```

   ```json
   {
     "Caliper": {
       "Provider": "Gemini",
       "Model": "gemini-2.5-flash"
     }
   }
   ```

   Or switch providers at runtime from the console with `/set provider Gemini` followed by
   `/model gemini-2.5-flash`.

4. Run Caliper.

   ```powershell
   dotnet run --project src/Caliper.Console
   ```

## Usage

Interactive mode starts a persisted session and accepts natural-language prompts. Useful slash commands:

```text
/help                         Show command help
/new                          Start a new session
/sessions                     List saved sessions
/resume <session-id>          Resume a previous session
/model <slug>                 Switch the active model at runtime
/models                       List available model metadata from the provider
/tools                        Show enabled tools
/permissions <mode>           Set AskAlways, Auto, or Plan
/mcp                          Show MCP server status
/mcp reconnect                Reconnect configured MCP servers
/memory                       Show project memory
/compact                      Force context compaction
/clear                        Clear active context while keeping transcript
/schedule list                List configured schedules, next occurrence, last result
/schedule run <name>          Trigger a schedule now via the unattended path
/runs                         List recent runs and their status
/quit                         Exit
```

One-shot read-only mode is useful for quick inspections. Without an explicit permissions override, one-shot prompts run in `Plan` mode with read-only tools.

```powershell
dotnet run --project src/Caliper.Console -- --prompt "Summarize this repository" --print
```

To allow a different permission mode:

```powershell
dotnet run --project src/Caliper.Console -- --prompt "Run the test suite" --permissions AskAlways
```

### Unattended one-shot runs

`--unattended` runs a one-shot prompt with no human in the loop: permission mode defaults to
`Auto`, and anything the gate would normally ask about is **denied and reported** (never silently
allowed, never silently dropped). Denials are logged at Warning and summarized on stderr.

```powershell
dotnet run --project src/Caliper.Console -- --prompt "Check for outdated packages" --unattended
```

### Scheduled jobs (`--serve`)

`--serve` starts the headless scheduler host: no REPL, jobs from `Caliper.Schedules` run on their
cron expressions through the same unattended deny+report policy, until Ctrl+C. It cannot be
combined with `--prompt` or `--unattended`.

```powershell
dotnet run --project src/Caliper.Console -- --serve
```

Schedules live in `~/.caliper/config.json` (config is the source of truth; the list is re-read
live, so edits apply without restarting `--serve`):

```json
{
  "Caliper": {
    "Schedules": [
      {
        "Name": "nightly-deps-report",
        "Cron": "0 6 * * *",
        "TimeZone": "local",
        "Prompt": "Check for outdated NuGet packages and summarize.",
        "WorkingRoot": "~/source/repos/Caliper",
        "Model": null,
        "MaxSteps": 20,
        "Enabled": true,
        "Permissions": {
          "Mode": "Auto",
          "ShellAutoAllowlist": [ "dotnet list", "dotnet restore" ]
        }
      }
    ]
  }
}
```

Notes: job names are unique (case-insensitive); `TimeZone` is `"local"` or a system time zone id;
the per-job `Permissions` overlay must set `"Mode": "Auto"` for its allowlists to apply — validation
rejects a saved overlay that sets `ShellAutoAllowlist`/`AutoAllowFileRoots` under any other `Mode`,
since those lists are silently inert under an unattended run (the global shell denylist is always
merged in and can never be lifted by a job); overlapping occurrences are
skipped, and occurrences missed while the process was down are not replayed. Each run is stored as
a normal session titled `[job] {name}`. From the interactive REPL, `/schedule run <name>` triggers
the identical unattended path — useful for testing a job's allowlist before trusting the cron.

### Durable runs and resume

Every orchestrator-driven run (one-shot, `--unattended`, scheduled jobs, subagent children) is
tracked in a SQLite `runs` table with a live status (`running`, then `completed`, `failed`,
`cancelled`, or `interrupted`) and a per-turn step counter. If the process dies mid-run, a startup
sweep marks the stale row `interrupted`. `/runs` lists recent runs;
`caliper --resume <run-id>` continues an interrupted run: the stored transcript is healed
(any dangling tool call gets a synthetic "interrupted" result), a system note tells the model a
tool call may have partially applied and to verify before repeating side effects, and the loop
continues with the remaining step budget. Side-effecting tool calls are never re-dispatched
automatically, and the scheduler never auto-resumes an interrupted job — its next cron tick simply
starts fresh.

### Sandboxed shell execution

The `bash`/`powershell` tools run through a pluggable execution backend
(`Caliper.Execution`), configured under `Caliper:Execution`:

```json
{
  "Caliper": {
    "Execution": {
      "Backend": "Host",
      "Image": "mcr.microsoft.com/dotnet/sdk:10.0",
      "Network": "none",
      "Cpus": 2,
      "MemoryMb": 4096,
      "User": "1000"
    }
  }
}
```

- `Backend: "Host"` (default) runs commands directly on the host, exactly as before this option
  existed. `Backend: "Container"` runs them via `docker run` — bind-mounting the run's working
  root at `/workspace`, applying `Network`/`Cpus`/`MemoryMb`/`User` as container limits, and never
  string-concatenating the command (it's passed through `ArgumentList`). All five fields are a
  live setting: a change (including flipping `Backend`) applies to the very next shell call, no
  restart required.
- **Windows reality:** the container backend requires Docker Desktop with the WSL2/Linux
  containers backend — Windows containers are not supported. The container always runs `bash`
  regardless of host OS; the `powershell` tool is rejected outright under `Backend: "Container"`
  with a clear error (bash-only in v1). File tools (`read_file`, `write_file`, etc.) are **not**
  sandboxed by this feature — they remain host-side, confined by `WorkingRoot`/
  `AutoAllowFileRoots` as always.
- **Fail-closed:** if Docker isn't available (`docker info` fails), every container-backend shell
  call returns a failed result — it never silently falls back to running on the host. Availability
  is probed lazily on first container-backed call and cached briefly, so a Docker Desktop restart
  is noticed again within seconds, not only at Caliper startup.
- **Wildcard allowlist requires the container backend.** A bare `"*"` entry in
  `ShellAutoAllowlist` (the global `Permissions` section, or a schedule job's own overlay) is
  rejected by config validation unless `Execution.Backend` is `"Container"` — the idea being that
  "allow all shell" is only a safe unattended policy once the blast radius is a disposable,
  network-isolated container, not the host. This is enforced both at startup (config binding) and
  at every relevant `/set --save`-style config write.

## Configuration

Caliper reads configuration from:

1. `~/.caliper/config.json`
2. Environment variables with the `CALIPER_` prefix
3. Command-line overrides for supported host options

Important settings:

| Setting | Purpose |
| --- | --- |
| `Caliper.Provider` | Active provider: `OpenRouter` (default) or `Gemini`. Switchable at runtime with `/set provider <name>`. |
| `Caliper.Model` | Active provider model slug. Configure this before model-backed use. |
| `Providers.OpenRouter.Endpoint` | OpenRouter-compatible API endpoint. |
| `Providers.OpenRouter.ApiKey` | API key, usually supplied through `CALIPER_OPENROUTER_KEY`. |
| `Providers.Gemini.Endpoint` | Gemini's OpenAI-compatible API endpoint. |
| `Providers.Gemini.ApiKey` | API key, usually supplied through `CALIPER_GEMINI_KEY`. |
| `Caliper.WorkingRoot` | Workspace root exposed to file tools. |
| `Caliper.EnabledTools` | Tool allowlist surfaced to the agent. |
| `Permissions.Mode` | Permission mode for side-effecting tools. |
| `Caliper.Schedules` | Cron job definitions for `--serve` / `/schedule` (name, cron, prompt, per-job overlay). |
| `Caliper.Scheduler.MaxConcurrentJobs` | How many scheduled jobs may run at once (default 1; applies at `--serve` start). |
| `Caliper.Execution.Backend` | `Host` (default) or `Container` — where `bash`/`powershell` commands run. Live. |
| `Caliper.Execution.Image` / `Network` / `Cpus` / `MemoryMb` / `User` | Container-backend knobs (image, `none`\|`bridge` network, CPU/memory limits, `docker run --user`). Live. |
| `Mcp.Servers` | MCP server definitions. |
| `Search.Backend` | `Stub` for local/dev use or `Tavily` when configured. |
| `Persistence.SqlitePath` | SQLite database path for sessions and memory. |

Environment variable examples:

```powershell
$env:CALIPER_OPENROUTER_KEY = "<your-openrouter-api-key>"
$env:CALIPER_GEMINI_KEY = "<your-gemini-api-key>"
$env:CALIPER_Caliper__Model = "<your-openrouter-model-slug>"
$env:CALIPER_Permissions__Mode = "AskAlways"
```

## Testing

Run all unit tests:

```powershell
dotnet test Caliper.slnx
```

Run hermetic evals without a model:

```powershell
dotnet run --project tests/Caliper.Evals -- --suite all --out eval-report.json
```

Run model-in-the-loop evals by supplying the model slug explicitly:

```powershell
dotnet run --project tests/Caliper.Evals -- --suite tool-calling --model "<your-openrouter-model-slug>"
```

## Publishing

The console project is configured for Native AOT publishing.

```powershell
dotnet publish src/Caliper.Console -c Release -r win-x64
```

Published output includes `config.example.json`.

## Security Notes

Caliper can execute local tools, read and write files, and launch shell commands when enabled. Use `AskAlways` for normal interactive work, keep `WorkingRoot` narrow, and review tool prompts before approving side effects. `Plan` mode is the safest mode for read-only analysis.

URL fetching blocks private, loopback, and link-local addresses. Shell commands are checked against configurable allowlists and denylists, but host permissions remain the final boundary.

## License

Caliper is licensed under the Apache License, Version 2.0. See [LICENSE](LICENSE).

# Console CLI reference

## Entry points

```powershell
# Interactive REPL (persisted session, slash commands, streaming output)
dotnet run --project src/Caliper.Console

# One-shot, attended. Without --permissions: Plan mode + read-only tool subset.
dotnet run --project src/Caliper.Console -- --prompt "Summarize this repo" --print

# One-shot with an explicit permission mode (AskAlways | Auto | Plan)
dotnet run --project src/Caliper.Console -- --prompt "Run the tests" --permissions AskAlways

# Unattended one-shot: Auto mode + deny+report prompt (see permissions.md)
dotnet run --project src/Caliper.Console -- --prompt "Check outdated packages" --unattended

# Headless cron scheduler: runs Caliper:Schedules until Ctrl+C
dotnet run --project src/Caliper.Console -- --serve

# Resume an interrupted run (see durable-runs.md)
dotnet run --project src/Caliper.Console -- --resume <run-id> --print
```

### Flag composition rules

| Combination | Result |
| --- | --- |
| `--prompt` alone | Plan mode + read-only tools (safe inspection default) |
| `--prompt --permissions <mode>` | Explicit mode wins, full tool surface |
| `--prompt --unattended` | Auto mode, full tools, deny+report prompt |
| `--prompt --unattended --permissions <mode>` | Explicit mode wins (even `AskAlways`, which then denies nearly everything) |
| `--unattended` without `--prompt` | Rejected — there is no "unattended REPL" |
| `--serve` with `--prompt` or `--unattended` | Rejected |
| `--resume` with `--prompt` or `--serve` | Rejected; composes with `--print` |

### Exit codes (`--prompt --unattended` and `--resume`)

| Code | Meaning |
| --- | --- |
| `0` | Clean run (attended runs also exit 0 on human-made denials) |
| `1` | The run reported an error (wins over denials) |
| `2` | Run completed but one or more actions were denied — automation can alert without parsing stderr |

## Slash commands (REPL)

```text
/help                         Show command help
/new                          Start a new session
/sessions                     List saved sessions
/resume <session-id>          Resume a previous session's transcript
/model <slug>                 Switch the active model at runtime
/models                       List available model metadata from the provider
/set provider <name>          Switch provider (OpenRouter | Gemini) at runtime
/tools                        Show enabled tools
/permissions <mode>           Set AskAlways, Auto, or Plan
/mcp                          Show MCP server status
/mcp reconnect                Reconnect configured MCP servers
/memory                       Show project memory
/compact                      Force context compaction
/clear                        Clear active context while keeping the transcript
/schedule list                Configured schedules, next occurrence, last result
/schedule run <name>          Trigger a schedule now via the unattended path
/runs                         Recent runs with status (resumed runs are marked)
/quit                         Exit
```

`/schedule run` deliberately uses the identical unattended path a `--serve` tick would — it
doubles as a dry-run harness for a job's allowlist before you trust the cron (see
[scheduling.md](scheduling.md)).

## Interrupts and logging

- First Ctrl+C cancels the current run; the process keeps running. In `--serve`, Ctrl+C
  stops the scheduler after in-flight jobs unwind.
- Warning-and-above logs go to `~/.caliper/logs/caliper.log` (shared with the desktop app —
  see [desktop-app.md](desktop-app.md)). Unattended denials are logged as Warnings
  individually and summarized per run.

## Credentials

Keys come from environment variables, never config: `CALIPER_OPENROUTER_KEY`,
`CALIPER_GEMINI_KEY`, `CALIPER_SEARCH_KEY` (Tavily search tool). The desktop app instead uses
Windows Credential Manager ([desktop-app.md](desktop-app.md)).

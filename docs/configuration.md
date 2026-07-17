# Configuration

## Sources and precedence

Lowest to highest:

1. `~/.caliper/config.json` — seeded on first run; `config.example.json` documents the shape
2. `CALIPER_`-prefixed environment variables — `CALIPER_Caliper__Model=...` (double
   underscore for nesting)
3. CLI flags — e.g. `--permissions` overrides `Permissions:Mode` for that process

The desktop app's Settings pages and the console both read and write the same file, so a
change made in one is picked up by the other.

## What applies immediately vs after restart

Most settings are **live** — they apply to the next run, tool call, or scheduler tick
without restarting: model and provider selection, permission mode and lists, subagent
profiles, the schedule list, the execution backend and container knobs, context/memory
tuning.

A few bind once at startup and are **restart-required**: the enabled-tool set
(`EnabledTools`), provider endpoints and keys, MCP servers, persistence paths, and
`Scheduler:MaxConcurrentJobs`. The app's Settings pages tell you which is which after each
save and offer a one-click restart.

## Section overview

```jsonc
{
  "Caliper": {
    "Provider": "OpenRouter",            // or "Gemini"
    "Model": "<slug>",
    "EnabledTools": ["search", "fetch_url", "read_file", "list_dir", "glob", "grep",
                     "write_file", "edit_file", "bash", "powershell", "memory",
                     "load_skill", "task"],
    "WorkingRoot": ".",                  // the root file writes are scoped to
    "MaxSteps": 25,                      // step budget per run
    "ToolTimeoutSeconds": 60,
    "ToolOutputMaxChars": 16000,
    "SkillsDirectory": "~/.caliper/skills",
    "Context":   { /* window fitting + auto-compaction */ },
    "Memory":    { /* persistent memory; project file defaults to "CALIPER.md" */ },
    "Subagents": { /* delegation profiles — see subagents.md */ },
    "Scheduler": { "MaxConcurrentJobs": 1 },
    "Schedules": [ /* cron jobs — see scheduling.md */ ],
    "Execution": { /* shell sandbox — see sandboxed-execution.md */ }
  },
  "Permissions": {
    "Mode": "AskAlways",                 // AskAlways | Auto | Plan — see permissions.md
    "RememberApprovals": true,
    "ShellAutoAllowlist": [],
    "ShellDenylist": [ /* always enforced, in every mode */ ],
    "AutoAllowFileRoots": []
  }
}
```

Enums are written as strings (`"Mode": "Auto"`); integer values from older files still load.

## Validation

Caliper validates configuration at startup and on every save (console or app), rejecting
invalid states with a specific message instead of accepting them silently. The rules that
span sections:

- A bare `"*"` in any `ShellAutoAllowlist` (global or per-job) requires
  `Execution.Backend: Container` — an unrestricted shell grant is only accepted inside the
  Docker sandbox ([sandboxed-execution.md](sandboxed-execution.md)).
- A schedule's permission override that sets allowlists without `Mode: Auto` is rejected —
  under the unattended policy those lists would silently do nothing.
- Schedules need unique names, a parsable cron, a resolvable timezone, an existing working
  root, and a non-empty prompt ([scheduling.md](scheduling.md)).

## Secrets

Never in `config.json`.

- **Console**: environment variables — `CALIPER_OPENROUTER_KEY`, `CALIPER_GEMINI_KEY`,
  `CALIPER_SEARCH_KEY`.
- **Desktop app**: Windows Credential Manager, managed from Settings → Models & providers.

Every `CALIPER_*` variable is stripped from shell and docker child processes, so keys can't
leak into tool commands.

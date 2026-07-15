# Configuration

## Sources and precedence

Lowest to highest:

1. `~/.caliper/config.json` (seeded on first run; `config.example.json` documents the shape)
2. `CALIPER_`-prefixed environment variables (`CALIPER_Caliper__Model=...` — double
   underscore for nesting)
3. CLI overrides (e.g. `--permissions` writes `Permissions:Mode` for the process)

The live options class is **`CaliperOptions`** (the `Caliper:` section) plus
`PermissionsOptions` (the `Permissions:` section). `AgentOptions` is legacy and unused by the
native turn strategy — never add knobs to it.

## Live vs restart-required

`IRuntimeSettings` holds mutable clones of the options behind a lock; the agent loop re-reads
it at run start *and every step*, and the permission gate re-reads it on every evaluation.
`IConfigWriter` persists typed sections back to `config.json` (source-gen JSON), updates the
live seams, validates before writing, and reports `RestartRequired` for fields that bind once.

Live (apply immediately): model, provider, permission mode/lists, subagent profiles, the
schedule list, execution backend + container knobs, context/memory tuning.
Restart-required: `EnabledTools`, provider endpoints/keys, MCP servers, persistence paths,
`Scheduler:MaxConcurrentJobs` (the cross-job semaphore is sized at service start).

Each `ConfigWriter.SaveXAsync` method documents which bucket its section is in — keep that
comment accurate when adding fields.

## Section overview

```jsonc
{
  "Caliper": {
    "Provider": "OpenRouter",            // or "Gemini"
    "Model": "<slug>",
    "TurnStrategy": "Auto",
    "EnabledTools": ["search", "fetch_url", "read_file", "list_dir", "glob", "grep",
                     "write_file", "edit_file", "bash", "powershell", "memory",
                     "load_skill", "task"],
    "WorkingRoot": ".",
    "MaxSteps": 25,
    "ToolTimeoutSeconds": 60,
    "ToolOutputMaxChars": 16000,
    "SkillsDirectory": "~/.caliper/skills",
    "Context":   { /* window fitting + auto-compaction */ },
    "Memory":    { /* project memory + ProjectFile (default "CALIPER.md") */ },
    "Subagents": { /* see subagents.md */ },
    "Scheduler": { "MaxConcurrentJobs": 1 },
    "Schedules": [ /* see scheduling.md */ ],
    "Execution": { /* see sandboxed-execution.md */ }
  },
  "Permissions": {
    "Mode": "AskAlways",                 // AskAlways | Auto | Plan
    "RememberApprovals": true,
    "ShellAutoAllowlist": [],
    "ShellDenylist": [ /* always merged into every overlay */ ],
    "AutoAllowFileRoots": []
  }
}
```

Enums serialize as **strings** in `config.json` (`"Mode": "Auto"`); integer values from older
files still load.

## Validation

`CaliperOptionsValidator` and `PermissionsOptionsValidator` run at options binding **and**
through every `ConfigWriter` save. Notable cross-section rules:

- A bare `"*"` in any `ShellAutoAllowlist` (global or per-job) is rejected unless
  `Execution.Backend` is `Container` — broad shell grants require the sandbox.
- A schedule's `Permissions` overlay that sets `ShellAutoAllowlist`/`AutoAllowFileRoots` with
  `Mode` other than `Auto` is rejected: unattended runs never prompt, so those lists would be
  silently inert.
- Schedules: unique names (case-insensitive), Cronos-parsable cron, resolvable timezone,
  existing working root, non-empty prompt.

## AOT and JSON

The console is Native AOT; reflection-based serialization is disabled
(`JsonSerializerIsReflectionEnabledByDefault=false`). Every serialized type must be registered
in `Protocol/CaliperJsonContext.cs`. New dependencies must be trim/AOT-safe (Cronos ✅, the
`docker` CLI ✅; Quartz and Docker.DotNet are not acceptable). Package versions live centrally
in `Directory.Packages.props` — never on a `<PackageReference>`.

## Secrets

Never in config. Console: environment variables (`CALIPER_OPENROUTER_KEY`,
`CALIPER_GEMINI_KEY`, `CALIPER_SEARCH_KEY`). Desktop app: Windows Credential Manager. The
`CALIPER_*` prefix is scrubbed from every child shell/docker process environment so keys
can't leak into tool commands.

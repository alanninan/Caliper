# Scheduling

Saved prompts run on cron schedules with per-job permission overlays, always through the
unattended deny+report policy ([permissions.md](permissions.md)).

## Configuration (`Caliper:Schedules`)

```jsonc
"Schedules": [
  {
    "Name": "nightly-deps-report",          // unique, case-insensitive
    "Cron": "0 6 * * *",                    // Cronos syntax
    "TimeZone": "local",                    // or a system timezone id
    "Prompt": "Check for outdated NuGet packages and summarize.",
    "WorkingRoot": "~/source/repos/Caliper",
    "Model": null,                          // null = current default model
    "MaxSteps": 20,
    "Enabled": true,
    "Permissions": {                        // per-job overlay
      "Mode": "Auto",                       // REQUIRED for the lists below to matter (validated)
      "ShellAutoAllowlist": ["dotnet list", "dotnet restore"],
      "AutoAllowFileRoots": []
    }
  }
]
```

Config is the source of truth; the list is **live** — the scheduler re-reads it every tick,
and a save wakes the loop immediately, so edits apply without restarting `--serve`.

Validation (at binding and at save): unique names, parsable cron, resolvable timezone,
existing working root, non-empty prompt; an overlay with allowlists but `Mode != Auto` is
rejected (provably inert under unattended); a bare `"*"` allowlist requires the container
backend.

## The scheduler (`--serve`, or the App's opt-in toggle)

`SchedulerHostedService` drives everything through `TimeProvider` — fully testable with
`FakeTimeProvider`. Core deliberately never registers it itself; the console's `--serve` flag is
one host that does (`Program.cs`), and the desktop App is the other — a "Run scheduler while the
app is open" toggle on its Schedules page starts/stops the same service in-process via
`Caliper.App.Scheduling.AppSchedulerController`
([desktop-app.md](desktop-app.md#schedules-page)), for as long as the window stays open. Loop
shape per tick: run whatever came due while sleeping → prune state for deleted/renamed jobs →
recompute every enabled job's next occurrence **from now** → sleep until the earliest (capped at
1 day; parked indefinitely when idle; woken by config saves).

Policies (deliberately boring and safe):

| Concern | Policy |
| --- | --- |
| Overlap | **Skip.** Per-job `SemaphoreSlim(1)` try-acquire; if the previous occurrence is still running, this one logs and skips — never queues. |
| Misfire | **No catch-up.** Occurrences missed while the process was down are skipped; next-from-now on startup. |
| Cross-job concurrency | `Scheduler:MaxConcurrentJobs` (default 1; restart-required). |
| Never-firing cron (e.g. `0 0 30 2 *`) | Logged once, treated as disabled, no spinning. |
| DST fall-back | Interval expressions may legitimately fire twice in the repeated hour (Cronos semantics); the overlap guard serializes the job regardless. |
| Shutdown | Ctrl+C cancels the loop and **awaits in-flight jobs**, which unwind through the normal cancelled-run path. |
| Interrupted jobs | Never auto-resumed — the next tick starts fresh ([durable-runs.md](durable-runs.md)). |

## How a job runs

Each occurrence: a fresh session titled `[job] {name}` → a `RunSpec` carrying the job's
prompt/model/step budget/overlay/working root, `JobName`, and `Unattended = true` →
`IConversationOrchestrator.RunToCompletionAsync`. Prompts deny + report. One-line summary +
denial count go to the file log (Warning when denials > 0); the full transcript is an
ordinary session, browsable from any host; the run is journaled in the `runs` table.

## Management

`/schedule list` shows each job with its next occurrence and last observed result;
`/schedule run <name>` triggers the identical unattended path from the REPL (works on
disabled jobs, with a notice — it's the dry-run harness for a job's allowlist). Add/edit/
remove jobs by editing config (or via `IConfigWriter.SaveSchedulesAsync`).

The desktop App's Schedules page is the same management surface with a GUI: a master-detail
list/edit view over `IConfigWriter.LoadSchedulesAsync`/`SaveSchedulesAsync` (add, edit, remove,
enable/disable), and a "Run now" per job that's the App's equivalent of `/schedule run` — see
[desktop-app.md](desktop-app.md#schedules-page) for the full page walkthrough.

# Scheduling

Saved prompts run on cron schedules — a nightly dependency report, a weekly cleanup pass —
always through the unattended deny+report policy ([permissions.md](permissions.md)): a
scheduled run never waits on a human, and anything it wasn't allowed to do is denied and
reported.

## Defining jobs (`Caliper:Schedules`)

```jsonc
"Schedules": [
  {
    "Name": "nightly-deps-report",          // unique, case-insensitive
    "Cron": "0 6 * * *",                    // standard 5-field cron
    "TimeZone": "local",                    // or a system timezone id
    "Prompt": "Check for outdated NuGet packages and summarize.",
    "WorkingRoot": "~/source/repos/Caliper",
    "Model": null,                          // null = current default model
    "MaxSteps": 20,
    "Enabled": true,
    "Permissions": {                        // optional per-job override
      "Mode": "Auto",                       // required for the lists below to matter
      "ShellAutoAllowlist": ["dotnet list", "dotnet restore"],
      "AutoAllowFileRoots": []
    }
  }
]
```

The list is **live**: edits apply without restarting the scheduler. Invalid jobs are
rejected when saved — duplicate names, unparsable cron, an unknown timezone, a missing
working root, an empty prompt, an override whose allowlists would be inert without
`Mode: Auto`, or a `"*"` allowlist without the container sandbox
([sandboxed-execution.md](sandboxed-execution.md)).

## Running the scheduler

Two ways to have jobs fire on schedule:

- **Headless**: `dotnet run --project src/Caliper.Console -- --serve` ticks until Ctrl+C.
- **Desktop app**: the Schedules page's "Run scheduler while the app is open" toggle runs
  the same scheduler in-process for as long as the window is open
  ([desktop-app.md](desktop-app.md#schedules-page)).

Scheduling policies are deliberately boring and safe:

| Concern | Policy |
| --- | --- |
| Overlap | **Skip.** If a job's previous occurrence is still running, the new one is skipped, never queued. |
| Missed occurrences | **No catch-up.** Occurrences missed while the scheduler was down are skipped; scheduling restarts from "now". |
| Concurrency | `Scheduler:MaxConcurrentJobs` (default 1) caps how many jobs run at once. Restart-required. |
| A cron that can never fire (e.g. `0 0 30 2 *`) | Logged once and treated as disabled. |
| DST fall-back | An interval cron may legitimately fire twice in the repeated hour; the overlap rule still serializes the job. |
| Shutdown | Ctrl+C (or closing the app) waits for in-flight jobs to unwind cleanly. |
| Interrupted jobs | Never auto-resumed — the next tick starts fresh ([durable-runs.md](durable-runs.md)). |

## What a job run looks like

Each occurrence gets a fresh session titled `[job] {name}` — browsable afterwards from the
console or the app like any other session. The run uses the job's prompt, model, step
budget, working root, and permission override, and runs unattended. A one-line summary with
the denial count goes to the log; the run is journaled for crash recovery.

## Managing jobs

- **Console**: `/schedule list` shows each job with its next occurrence and last result;
  `/schedule run <name>` triggers one now through the identical unattended path — useful as
  a dry run to test a job's allowlist before trusting the cron (it works on disabled jobs
  too, with a notice). Add/edit jobs by editing `config.json`.
- **Desktop app**: the Schedules page is the full management surface — add/edit/remove with
  live cron validation, per-job "Run now", and the in-app scheduler toggle
  ([desktop-app.md](desktop-app.md#schedules-page)).

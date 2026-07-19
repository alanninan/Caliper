# Durable runs and resume

If Caliper is killed mid-run — a crash, a closed terminal, a reboot — the work isn't lost.
Long-running runs are journaled with a live status, interrupted runs are detected on the
next start, and an interrupted run can be resumed from where it died.

## What is tracked

One-shot runs (`--prompt`), unattended runs, scheduled jobs, and subagent children are
tracked in a local runs journal. Interactive chat turns (console REPL, the app's chat) are
**not** — you're watching those; the feature targets long or unattended work where nobody
is.

Each tracked run records its session, originating job (if any), status
(`running → completed | failed | cancelled | interrupted`), and step progress against its
budget.

## Interruption

A process that dies mid-run leaves its journal entry saying `running`. On the next start,
Caliper sweeps those stale entries to `interrupted`. Interrupted scheduled runs appear in
the desktop Schedules page's History mode; all interrupted run types remain visible through
the console's `/runs` command.

## Resuming

```powershell
dotnet run --project src/Caliper.Console -- --resume <run-id> --print
```

For a scheduled run, the desktop Schedules page's History mode also provides **Resume**.
Only interrupted runs can be resumed. What resume does:

1. If the run died mid-tool-call, the transcript is healed so the model knows that call
   returned no result — nothing is replayed.
2. A note is added: *"a tool call may have partially applied — verify before repeating side
   effects."* Side-effecting actions are **never re-run automatically**; the model verifies
   and decides. Read-only work is simply re-derived.
3. The run continues with its **remaining** step budget (a run interrupted at step 18 of 25
   gets 7 more), under its original settings — a resumed scheduled job picks up the job's
   *current* configuration, and an unattended run stays unattended (deny + report).

The scheduler never auto-resumes an interrupted job; its next tick starts fresh. Resume is
always an explicit human action.

## Inspecting runs

- **Console**: `/runs` lists recent runs — id, session, job, status (resumed runs are
  marked, e.g. `Completed (resumed)`), step/budget, last update.
- **Desktop app**: Schedules → History shows the 20 most recent scheduled runs. It includes
  status, originating schedule, step progress, update time, completion/failure reason,
  **Open chat**, and **Resume** for interrupted rows
  ([desktop-app.md](desktop-app.md#schedules-page)). Subagent sessions remain in Chat behind
  the "Show subagent runs" toggle; one-shot and unattended CLI records are console-only.
- Exit codes for `--resume` match the unattended convention: `0` clean, `1` error,
  `2` completed with denials ([cli.md](cli.md)).

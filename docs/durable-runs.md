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
Caliper sweeps those stale entries to `interrupted` — and the desktop app shows a launch
banner when any exist ("N run(s) were interrupted — view Runs").

## Resuming

```powershell
dotnet run --project src/Caliper.Console -- --resume <run-id> --print
```

or the **Resume** button on an interrupted row in the app's Runs page. Only interrupted
runs can be resumed. What resume does:

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
- **Desktop app**: the Runs page shows the same list with interrupted rows highlighted and
  resumable in place ([desktop-app.md](desktop-app.md#runs-page)).
- Exit codes for `--resume` match the unattended convention: `0` clean, `1` error,
  `2` completed with denials ([cli.md](cli.md)).

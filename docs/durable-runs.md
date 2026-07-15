# Durable runs and resume

Caliper survives a crash or kill mid-run: every orchestrator-driven run is journaled with a
live status, stale runs are detected at startup, and an interrupted run can be resumed from
exactly where it died.

## What is tracked

Every run that goes through `ConversationOrchestrator` gets a row in the SQLite `runs` table:
one-shot (`--prompt`), `--unattended`, scheduled jobs, `/schedule run`, and subagent
children. Interactive REPL/App streaming runs (which consume `IAgentRunner` directly) are
**not** tracked — the feature targets crash recovery for long/unattended work.

```
run_id · session_id · job_name? · status · reason? · step · max_steps · unattended · resumed · started_at · updated_at
```

Status lifecycle: `running → completed | failed | cancelled | interrupted`. The row is
created before the run starts (with the resolved step budget, so later resume math never
depends on config that may have changed); `step`/`updated_at` bump on every turn; the
terminal status maps from the run's `CompletionReason` (an error always maps to `failed`).
Timestamps come from `TimeProvider`.

## Interruption

A process that dies mid-run can't write a terminal status — its row stays `running`. On the
next startup, a **sweep** (run under the store's schema-initialization gate, before any other
run-store call) flips every `running` row to `interrupted` with an explanatory reason.
Single-writer local model: if a row says `running` while this process is just starting, it
isn't.

## Resume

```powershell
dotnet run --project src/Caliper.Console -- --resume <run-id> --print
```

`ResumeAsync` (orchestrator) accepts only `interrupted` runs (anything else is a clear
error). The sequence:

1. **Heal.** The stored transcript may end with a dangling tool call (killed mid-tool).
   `NativeToolStrategy`'s healing gives it a synthetic `[no result — run was interrupted]`
   result before the model ever sees it — resume is "load, heal, continue", not replay.
2. **Note.** A system-role message is appended:
   `[run interrupted at step N; a tool call may have partially applied — verify before
   repeating side effects]`.
3. **Rebuild the spec** from the run row — and, for job runs, the *current* schedule config
   (model/overlay/working root); if the job no longer exists, resume proceeds with defaults
   and a logged warning. `Unattended` is carried over, so a resumed job still denies+reports.
4. **Remaining budget.** The resumed run is bounded to `max_steps − step` (min 1), and the
   row's `max_steps` is rewritten to that remainder — so the arithmetic stays correct even
   across a second interruption. The row is marked `resumed`.
5. **Continue.** `RunSpec.ResumeExisting` skips the normal append of the prompt as a new
   user message — the transcript continues where it left off.

**Idempotency is coarse by design:** side-effecting tool calls are never automatically
re-dispatched. The healed transcript plus the resume note tell the model what was in flight;
it decides how to verify or redo. Read-only work is simply re-derived.

The scheduler **never auto-resumes** an interrupted job — its next cron tick starts fresh.

## Inspection

`/runs` lists recent runs: id prefix, session, job, status (resumed runs marked as e.g.
`Completed (resumed)`), `step/budget`, last update. Exit codes for `--resume` match the
unattended convention: `0` clean, `1` error, `2` completed-with-denials
([cli.md](cli.md)).

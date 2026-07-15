# Subagents

A parent run delegates a scoped task to a child agent that runs its own bounded loop in an
isolated session and returns only a folded summary. The parent's context never receives the
child's transcript — just the final message plus a stats trailer.

## The `task` tool

```json
{ "prompt": "…required…", "profile": "research", "title": "≤60 chars, optional" }
```

`profile` selects a **host-defined** profile by name. The model can never compose its own
tool grant — free-form per-spawn tool lists are deliberately rejected, otherwise a
prompt-injected instruction could hand a "research" child a shell. An unknown profile fails
with the list of valid names.

The folded result: the child's final assistant message +
`[subagent stats] steps: N, tools invoked: N, denials: N` (plus timeout/reason/error when
applicable), truncated to `ToolOutputMaxChars`. `Success` = child completed without error.

## Configuration (`Caliper:Subagents`)

```jsonc
"Subagents": {
  "MaxDepth": 2,               // parent = depth 0; a depth-2 child cannot spawn
  "MaxChildrenPerRun": 8,      // per parent run; failed spawn attempts don't consume slots
  "DefaultProfile": "research",
  "TimeoutSeconds": 600,       // per child run
  "Profiles": {
    "research": { "EnabledTools": ["read_file","list_dir","glob","grep","search","fetch_url","load_skill"], "MaxSteps": 15 },
    "worker":   { "EnabledTools": ["read_file","list_dir","glob","grep","edit_file","write_file","bash","powershell","load_skill"], "MaxSteps": 25 }
  }
}
```

Profiles are live config — a change applies to the very next spawn. A profile may also set
`Mode` to tighten the child's permission mode (it can never loosen; see below).

## Guards

- **Depth:** a run at `SubagentDepth == MaxDepth` cannot spawn; clear failure message.
- **Children per run:** counted in a per-run `SubagentRunState` threaded through
  `ToolContext` — scoped exactly to one run, nothing accumulates process-wide. Attempts that
  fail before the child run starts (unknown profile, over-limit rejection, session-creation
  error) hand their slot back; a child that started and then failed still counts.
- **Timeout:** the child gets `Subagents.TimeoutSeconds` via both the tool's
  `ToolTimeoutOverride` and its own linked cancellation token; a timeout returns a failed
  `ToolResult`, never a hang.
- **Cancellation** chains parent → child → child's tools.

## Permission inheritance — restrict-only

Child overlay = parent's *effective* options (its own overlay if it has one, else global)
with `Mode = Min(parent, profile)` under the ordering `Plan < AskAlways < Auto`. A profile
can tighten, never loosen: a Plan-mode parent's child stays Plan even if the profile says
Auto — and this holds recursively for grandchildren. The global shell denylist union applies
to children like everyone else. Child prompts flow to the same host prompt as the parent
(approval cards show the child session's title); under unattended they deny + report.
Session approvals are keyed by session id, so a child never inherits or pollutes parent
grants.

## Isolation details

- Child sessions are ordinary sessions titled `Subagent: {title|truncated prompt}` with
  `parent_session_id` set — inspectable in the App behind the "Show subagent runs" toggle.
- Children inherit the working root and skills. The project memory *block* renders into the
  child prompt (cheap, useful), but default profiles exclude the `memory` tool so children
  can't write shared memory; a host can opt a profile in deliberately.
- `SubagentStarted`/`SubagentCompleted` events surface in the parent's stream (buffered via
  `ToolContext.Emit`, drained after the dispatch — so they appear when the child finishes;
  live child-progress streaming is deliberately deferred).
- Child runs get their own `runs`-table rows ([durable-runs.md](durable-runs.md)).

## The DI cycle

`SubagentTool` must not constructor-inject `IConversationOrchestrator`
(`ToolRegistry` enumerates all tools; the orchestrator depends on the registry). It resolves
the orchestrator from `IServiceProvider` inside `InvokeAsync` — the codebase's one blessed
service-locator, documented at the site.

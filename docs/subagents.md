# Subagents

The `task` tool lets a run delegate a scoped piece of work to a child agent. The child runs
its own bounded loop in its own session and hands back only a final summary — the parent's
context never absorbs the child's full transcript, which keeps long research or build tasks
from flooding the parent's window.

## How delegation works

The model calls `task` with a prompt, an optional short title, and a **profile name**:

```json
{ "prompt": "Find every place the retry policy is configured", "profile": "research" }
```

Profiles are defined by you, not the model. The model can only pick a profile by name —
it can never compose its own tool grant, so a prompt-injected instruction can't hand a
"research" child a shell. The parent receives the child's final message plus a stats line
(steps used, tools invoked, denials).

## Configuration (`Caliper:Subagents`)

```jsonc
"Subagents": {
  "MaxDepth": 2,               // children may spawn grandchildren until this depth
  "MaxChildrenPerRun": 8,      // per parent run
  "DefaultProfile": "research",
  "TimeoutSeconds": 600,       // per child run
  "Profiles": {
    "research": { "EnabledTools": ["read_file","list_dir","glob","grep","search","fetch_url","load_skill"], "MaxSteps": 15 },
    "worker":   { "EnabledTools": ["read_file","list_dir","glob","grep","edit_file","write_file","bash","powershell","load_skill"], "MaxSteps": 25 }
  }
}
```

Changes are live — the next `task` call uses the new settings. The app has a Settings →
Subagents page for all of this ([desktop-app.md](desktop-app.md#settings)); no need to
hand-edit JSON.

A profile lists the child's tools and step budget, and may set a permission `Mode` to
tighten the child further.

## Safety limits

- **Depth and fan-out** are capped (`MaxDepth`, `MaxChildrenPerRun`); exceeding either fails
  the spawn with a clear message. Attempts that never started (unknown profile, over the
  limit) don't consume a slot.
- **Timeout**: a child that exceeds `TimeoutSeconds` is cancelled and reported as a failed
  call — never a hang.
- **Cancelling the parent cancels its children**, and their tools.
- **Permissions are restrict-only**: the child's effective mode is the *stricter* of the
  parent's mode and the profile's — a Plan-mode parent can never spawn an Auto child, and
  the rule applies recursively to grandchildren. The global shell denylist applies to
  children like everyone else. When a child does need approval, the request reaches you
  labeled with the child's title; in unattended runs it's denied and reported like any other
  ([permissions.md](permissions.md)).

## Where child work lands

- Child sessions are ordinary sessions titled `Subagent: {title}`. The console lists them
  with `/sessions`; the app hides them behind the sessions pane's "Show subagent runs"
  toggle.
- Children see the project memory but, by default, can't write it (the default profiles
  exclude the `memory` tool — add it to a profile deliberately if you want that).
- Session approvals never cross the parent/child boundary in either direction.
- Child runs are tracked in the runs journal like any other
  ([durable-runs.md](durable-runs.md)); child progress streams into the parent as start and
  finish markers (live inner streaming is deliberately deferred).

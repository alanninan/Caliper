# Permissions

Every side-effecting tool call passes through `IPermissionGate.EvaluateAsync` before dispatch.
The gate is the single enforcement point; prompts are pluggable per host.

## Modes

| Mode | Behavior |
| --- | --- |
| `Plan` | Read-only calls allowed; every side effect denied outright. |
| `AskAlways` | Read-only allowed; every side effect prompts. |
| `Auto` | Allowlisted un-chained shell commands and in-working-root file writes auto-allowed; everything else prompts. |

Restrictiveness ordering (used by subagent inheritance): `Plan` < `AskAlways` < `Auto`.

## Gate mechanics

- **Trusted read-only short-circuit:** built-in read-only calls are allowed without
  consulting the prompt. MCP servers' self-declared read-only annotations are **untrusted**
  (`PermissionRequest.TrustedReadOnly` is false for MCP tools) — they prompt like writes.
- **Shell allowlist** (`ShellAutoAllowlist`, Auto mode): prefix match on the normalized
  command, guarded against chaining metacharacters (`;`, `&&`, pipes…) so `git status; rm -rf`
  can't ride an allowlisted prefix.
- **Shell denylist** (`ShellDenylist`): anchored sub-command matching (no substring false
  positives). A denylist hit always surfaces with a `[denylist]` reason, is excluded from
  "remember approval" scope, and — with no human present — is denied.
- **File policy** (`FileAccessPolicy`): symlink-resolving, per-OS case normalization; writes
  inside the working root (or a configured `AutoAllowFileRoots` entry) auto-allow under Auto.
  File tools may cross the working root only after gate approval.
- **Per-call effect:** tools can classify per invocation via `EffectiveSideEffect(arguments)`
  (e.g. `memory` reads are read-only; `remember`/`forget` are writes).
- **Session approvals:** "allow for session" grants are keyed by `(SessionId, Signature)` —
  one session's approvals never leak into another (subagent children and job runs are
  isolated for free). `ResetSessionApprovals(sessionId?)` clears them.

## Prompts (per host)

| Prompt | Host | Behavior |
| --- | --- | --- |
| `ConsolePermissionPrompt` | Console REPL | Interactive selection; denies when non-interactive/redirected. |
| `ApprovalService` | Desktop app | Docked approval cards, 5-minute auto-deny, per-request correlation. |
| `UnattendedPermissionPrompt` | Headless | **Always denies, never grants**; logs each denial at Warning. |
| `RoutingPermissionPrompt` | Console REPL wrapper | Routes per request on `PermissionRequest.Unattended`: unattended → deny+report, else → interactive. |

A null prompt means deny. There is deliberately **no `Unattended` permission mode** — Auto
mode *is* the unattended policy engine; unattended just swaps the "ask" fallback for
"deny + record".

## The unattended contract

**Deny + report, never silent-allow, never silent-drop.** Under `--unattended`, `--serve`
jobs, and `/schedule run`:

- Read-only + trusted ⇒ allowed (unchanged).
- Denylist hit ⇒ denied (no human to see the prompt).
- Anything that would prompt ⇒ denied, logged at Warning, collected into
  `ConversationRunResult.Denials`, summarized on stderr, and reflected in the exit code
  (`2` — see [cli.md](cli.md)).
- `RememberApprovals` grants nothing (no approvals are ever made).

## Overlays (per-run permissions)

`RunSpec.PermissionsOverlay` gives one run its own permission options (scheduled jobs and
subagent children use this). Two invariants hold for every overlay:

1. **The global `ShellDenylist` is always unioned in** — an overlay can tighten but can never
   un-ban a globally denied command.
2. **Subagent overlays are restrict-only** — child mode = `Min(parent effective mode,
   profile mode)`; see [subagents.md](subagents.md).

Per-run `WorkingRoot` (`RunSpec.WorkingRoot`) scopes the file policy to the run's own root
without touching global settings.

## Validation guardrails

- A bare `"*"` allowlist entry requires `Execution.Backend = Container`
  ([sandboxed-execution.md](sandboxed-execution.md)) — enforced at options binding and every
  config save path. (Today's matcher is prefix-based, so `"*"` isn't even functional as a
  wildcard; the guard exists so a future matcher change can't silently combine with host
  execution.)
- A job overlay with allowlists but `Mode != Auto` is rejected as provably inert
  ([scheduling.md](scheduling.md)).

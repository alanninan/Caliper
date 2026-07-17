# Permissions

Nothing side-effecting runs without passing Caliper's permission gate. Reading files and
searching are allowed freely; writing files, editing, and running shell commands are governed
by the mode you choose.

## Modes

| Mode | Behavior |
| --- | --- |
| `Plan` | Read-only. Every side effect is denied outright — safe for "look, don't touch". |
| `AskAlways` | Every side effect asks you first. The default. |
| `Auto` | Allowlisted shell commands and file writes inside the working root run without asking; everything else still asks. |

Set the mode in `config.json` (`Permissions:Mode`), with `/permissions <mode>` in the REPL,
via the app's Settings → Permissions page, or per one-shot run with `--permissions`
([cli.md](cli.md)). The REPL command and the app's header quick-switcher change the mode for
the current session only.

## How Auto mode decides

- **Shell allowlist** (`ShellAutoAllowlist`): a command runs without asking when it starts
  with an allowlisted prefix — e.g. `"git status"` allows `git status -sb`. Chained commands
  are not auto-allowed: `git status; rm -rf /` can't ride an allowlisted prefix past the `;`,
  `&&`, or a pipe.
- **File writes** inside the working root (or any configured `AutoAllowFileRoots` entry) are
  auto-allowed. Writes outside always ask.
- Everything else falls back to asking — Auto never silently allows something unlisted.

## The denylist

`ShellDenylist` (defaults include `rm -rf`, `sudo`, `mkfs`, …) always wins: a matching
command is denied in every mode, is never covered by "remember approval", and the denial
reason says `[denylist]`. Matching is against actual sub-commands, so `firm -rfs` doesn't
false-positive on `rm -rf`. No per-job or per-agent override can remove a global denylist
entry — overrides can only add restrictions.

## Approvals

When Caliper asks, you can approve once or **allow for the rest of the session**
(`RememberApprovals`). Session approvals are scoped to that one session — they never carry
over to other sessions, scheduled jobs, or subagent children. In the console the prompt is
inline; in the desktop app it's a docked approval card with a 5-minute auto-deny timeout
([desktop-app.md](desktop-app.md#approvals)).

One nuance: tools from MCP servers always ask, even if the server claims a tool is
read-only — Caliper doesn't trust third-party read-only declarations.

## Unattended runs: deny + report

Headless runs (`--unattended`, scheduled jobs, `/schedule run`, the app's "Run now") have no
human to ask, so anything that would have asked is **denied** — never silently allowed,
never silently dropped. Each denial is logged, collected into the run's summary
("N action(s) denied"), and reflected in the exit code (`2`, see [cli.md](cli.md)).

There is deliberately no separate "unattended mode": give an unattended run an `Auto`-mode
allowlist for what it *should* be able to do, and the deny+report policy covers the rest.

## Per-job and per-agent overrides

Scheduled jobs and subagent profiles can carry their own permission settings
([scheduling.md](scheduling.md), [subagents.md](subagents.md)). Two rules always hold:

1. The global `ShellDenylist` is always merged in — an override can tighten, never un-ban.
2. Subagent children are **restrict-only**: a child's mode is at most its parent's. A
   Plan-mode parent's children stay Plan no matter what the profile says.

Caliper also rejects configurations that look permissive but can't work:

- An allowlist containing a bare `"*"` requires the Docker sandbox
  (`Execution.Backend = Container`, see [sandboxed-execution.md](sandboxed-execution.md)) —
  an unrestricted shell grant is only accepted inside a container.
- A job override that sets allowlists without `Mode: Auto` is rejected outright, because
  under the unattended policy those lists would silently do nothing.

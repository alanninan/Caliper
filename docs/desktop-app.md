# Desktop app

The Windows desktop app is a full Caliper host: everything the console can do interactively,
plus a visual workspace for approvals, schedules, and settings.

```powershell
dotnet run --project src/Caliper.App     # requires Windows 10.0.19041+ and Windows App SDK
```

## The chat workspace

Three panes: **sessions** on the left, the **transcript** in the middle, and an **inspector**
on the right showing any tool call's arguments, output, and — for file edits — a side-by-side
or inline diff.

- **Streaming**: assistant replies render as Markdown from the first token. Reasoning,
  tool activity, and permission events appear inline as they happen. Context compactions show
  as expandable dividers marking where earlier turns stopped being sent to the model.
- **Quick switchers**: the model name and permission mode shown in the workspace header are
  clickable. The model switcher searches the provider's catalog; the permission switcher
  offers Ask always / Auto / Plan. Both apply **for this app session only** — use Settings to
  make a change permanent.
- **Transcript search** (`Ctrl+F`): case-insensitive search across messages and tool output,
  with next/previous stepping and a "N of M" indicator.
- **Copy conversation** exports the transcript as plain text (reasoning omitted, tool
  activity collapsed to one line each); the inspector has its own copy button for a single
  tool's output.
- **Follow-up queueing**: you can type and send while a run is active; the message is queued
  and sent when the run finishes.

### Sessions

Sessions persist automatically and are searchable and grouped by date. Rename or delete from
each row's "…" menu or right-click menu (deleting confirms with the message count). Sessions
created by scheduled jobs appear as `[job] {name}`; sessions created by subagents are hidden
behind the "Show subagent runs" toggle.

### Keyboard shortcuts

| Shortcut | Action |
| --- | --- |
| `Ctrl+Enter` | Send message |
| `Ctrl+N` | New session |
| `Ctrl+F` | Search the transcript |
| `Ctrl+B` | Collapse/expand the sessions pane |
| `Ctrl+Shift+B` | Collapse/expand the inspector pane |

Pane widths and collapsed states persist across launches.

The workspace adapts at two desktop breakpoints. At 1360 epx and above, Sessions and the
Inspector can remain inline at their saved widths. Between 1008 and 1359 epx, Sessions starts
closed while the Inspector can remain inline. Below 1008 epx, opening one side pane closes the
other so the composer and transcript retain usable space. Responsive changes do not replace the
saved wide-layout choices.

## Settings

Settings use adaptive left navigation grouped into Basics, Agent, Safety, Integrations, and
Advanced. Configuration changes are staged until Save; Discard restores the last loaded values.
Moving between Settings sections keeps staged edits in memory. Theme is the exception and is
clearly marked as applying immediately.

Labels describe outcomes rather than configuration enum names: permission choices explain what
Caliper may do, execution choices distinguish this PC from a Docker sandbox, and Search shows
"Search off" or "Tavily web search".

## Approvals

When a run wants to do something your permission mode doesn't auto-allow, a docked approval
card appears with the tool, its arguments, and approve/deny actions (keyboard accessible).
Unanswered approvals **auto-deny after 5 minutes**. Concurrent requests queue oldest-first
with an "Approval 1 of N" indicator. Requests from subagent children are labeled with the
child session's title so it's clear who is asking.

Scheduled-job runs (a "Run now" click or an in-app scheduler tick) never raise approval
cards: they follow the unattended policy — denied, logged, and counted in the run summary —
exactly as they would under the headless scheduler ([permissions.md](permissions.md)).

If the window is in the background, Windows toasts notify you of a new pending approval and
of a run finishing ("Run finished" / "Run failed").

## Schedules page

Manage cron jobs without editing `config.json` ([scheduling.md](scheduling.md) covers job
semantics):

- The list shows each job's name, enabled state, cron, **next occurrence**, and last result.
  Cron validity is checked live as you type, showing the actual next fire time or the parse
  error. Save is disabled while a job is missing a required field or duplicates a name, and
  a rejected save (e.g. an invalid permissions override) shows the validator's message.
- Each job can override the working root, model, step budget, and permissions (an
  "Override permissions" toggle exposes the mode and allowlists — the allowlists only take
  effect with Mode set to Auto, and the page says so inline).
- **Run now** triggers a job immediately through the same unattended path a cron tick uses.
  The outcome line reports the finish reason, any error, and how many actions were denied;
  the job's transcript lands in a `[job] {name}` session you can open from Chat. Triggering
  a job that's already running is skipped, never queued.
- **Run scheduler while the app is open** (opt-in toggle): jobs fire on their cron schedule
  for as long as the window stays open. The preference persists and re-activates on the next
  launch. Headless scheduling (no window at all) remains the console's `--serve`.
- The **History** mode shows the 20 most recent scheduled runs in a table with status,
  schedule, progress, and update time. **Open chat** selects the run's `[job] {name}`
  transcript and returns to Chat. Interrupted scheduled runs expose **Resume** in the same
  row. One-shot, unattended CLI, and subagent run records remain inspectable through the
  console's `/runs` command rather than occupying a separate desktop destination.

## Skills and Memory pages

- **Skills** lists discovered skills with a refresh button and shows each skill's body.
- **Memory** lists remembered facts with per-entry edit and forget (forget asks inline
  first), and a form to remember a new fact at global or project scope. Saving an existing
  key updates it in place.

## Settings

Structured pages — General, Models & providers, Permissions, Tools, Agent behavior,
Context & memory, Subagents, Execution, MCP servers, Search, Advanced — edit the same
`~/.caliper/config.json` the console reads. Each page tells you whether a saved change
applies immediately or needs a restart, and offers a one-click **Restart Caliper** when it
does. Most sections are live; endpoints/keys, the enabled-tool set, MCP servers, and search
need a restart.

- **Models & providers** stores API keys in **Windows Credential Manager**, never in
  `config.json`.
- **Subagents** edits delegation profiles ([subagents.md](subagents.md)): global limits, a
  per-profile tool list (one per line), step budget, and permission mode with an
  "(inherit)" option. Renaming the default profile follows it; removing it is blocked until
  you pick another default.
- **Execution** picks where shell commands run ([sandboxed-execution.md](sandboxed-execution.md)):
  Host or Container, plus the container image, network, and resource limits (dimmed while
  Host is selected). If switching to Host would conflict with a wildcard allowlist, the page
  warns you before you save.
- **Advanced** includes a raw JSON editor with live validation as you type — for the rare
  case a setting has no structured page.

## Where the app keeps things

| What | Where |
| --- | --- |
| Runtime configuration | `~/.caliper/config.json` (shared with the console) |
| API keys | Windows Credential Manager |
| UI preferences (theme, window, panes, toggles) | `~/.caliper/app-ui.json` |
| Per-session token usage | `~/.caliper/app-usage.json` |
| Warning+ logs | `~/.caliper/logs/caliper.log` (shared with the console) |

Window placement restores onto a live display; closing maximized restores maximized.

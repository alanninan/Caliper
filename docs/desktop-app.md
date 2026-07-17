# Desktop app (`Caliper.App`)

A WinUI 3 host on the same `Caliper.Core` engine — MVVM (CommunityToolkit), not trimmed/AOT.
Run with `dotnet run --project src/Caliper.App` (requires Windows App SDK, Windows
10.0.19041+).

## Layout

Three-pane workbench: sessions pane, transcript, and an inspector for tool calls (arguments
as pretty-printed JSON, output, and file diffs for write/edit operations). Context
compactions render as full-width expandable dividers so you can see where earlier turns
stopped being sent to the model.

- **Diff rendering**: side-by-side and inline diff rows mark added/modified/removed cells with
  a background fill; kind also reads without color via the inline view's `+`/`-`/`~` prefix
  column and the side-by-side view's positional side (removed = old side only, added = new
  side only, modified = both). In a high-contrast theme all three kinds share the same
  WindowColor/WindowText fill (Hotlight/GrayText aren't valid fill colors against the default
  row-text foreground) plus a WindowText underline on the changed cell.
- **Sessions pane** hides subagent child sessions (`parent_session_id` set) behind a
  "Show subagent runs" toggle; job runs (`[job] {name}`) appear like any session. Right-clicking
  a session row opens the same Rename/Delete menu as its "..." button (a duplicated
  `ContextFlyout`, not a shared one — a resource-level `MenuFlyout` would sit outside the row's
  `DataTemplate` `x:DataType` scope and couldn't `x:Bind` to the row's commands). Deleting a
  session asks for confirmation with a message count ("This will permanently delete 12
  messages.") counting the user/assistant text bubbles — from the cached transcript when the
  session is loaded, otherwise from the store's `MessageKind.Text` entries (no view models built
  just to count); a failed count falls back to the plain confirmation wording.
- **Live events** map through `AgentEventMapper` (streamed run → view models);
  `PersistedTranscriptFactory` rebuilds the same view models from stored messages on reload.
  Assistant bubbles render as Markdown from the first streamed token — there's no separate
  plain-text streaming state, so finishing a run no longer causes a visible reflow jump.
  Live (not reloaded) user/assistant bubbles show their send/receive time as a hover tooltip.
  Streaming content is pushed into a bubble on a calmer cadence than the raw flush tick — throttled
  to a minimum interval unless the newly streamed text crosses a paragraph break or code fence, so
  a long response doesn't force a full Markdown re-parse every tick; the transcript autoscroll that
  follows a streaming run is also unanimated and coalesced to at most one scroll per dispatcher
  pass, keeping the `ItemsRepeater` layout stable under concurrent streaming bubbles.
- **Transcript search** (`Ctrl+F`) reveals a search box in the workspace header; Enter/Shift+Enter
  or the next/previous buttons step through case-insensitive matches across user messages,
  assistant messages, and tool call headlines/output (a "N of M" / "No matches" indicator tracks
  position), scrolling the transcript to the current match; Escape or the close button clears the
  query and closes it. A "Copy conversation" toolbar button copies a plain-text export of the
  transcript (reasoning omitted, tool activity and status/compaction markers collapsed to one
  line each); the inspector's Output tab has its own copy button for just the selected tool's
  output.
- The workspace header's model and permission-mode captions are quick-switchers: each is a
  `DropDownButton` styled to keep the caption look. The model switcher's flyout hosts a search
  box over the model catalog (fetched lazily on first open; a failed fetch degrades to an empty
  list with the error as the hint text, never a crash); the permission-mode switcher's flyout is
  three `RadioMenuFlyoutItem`s (Ask always / Auto / Plan). Both write only the live
  `IRuntimeSettings` clone via `SetModel`/`SetPermissionMode` — **session-scoped, not
  persisted** — so Settings → Models & providers / Permissions remain the place to make a change
  stick across restarts.
- Both the sessions pane (`Ctrl+B`) and the inspector pane (`Ctrl+Shift+B`) can be collapsed
  from the workspace header toolbar; each state persists (`SessionsPaneCollapsed` /
  `InspectorPaneCollapsed`) and restores on next launch. Widths dragged with either
  `GridSplitter` also persist (`SessionsPaneWidth` / `InspectorPaneWidth`, in DIPs) — saved on
  every drag's `ManipulationCompleted` and whenever a pane collapses, so a resized pane snaps
  back to its own remembered width, not a fixed default, when re-expanded. The main window
  enforces an 800×560 DIP minimum size (`OverlappedPresenter.PreferredMinimumWidth/Height`) so
  both panes stay clip-free at once; their column `MinWidth`s (180/360/240) were sized to fit
  under it.

## Skills and Memory pages

- **Skills**: a header "Refresh" button re-lists from `ISkillStore` on demand (not just on page
  navigation), and a caption near the header shows the discovered count. A refresh that finds
  the previously selected skill still present re-selects it by name and keeps its already-loaded
  body rather than re-fetching; otherwise selection resets to the placeholder state.
- **Memory**: each row has a per-entry Forget action — an inline confirmation `Flyout` ("Forget
  '{key}'?"), not an immediate delete or a per-row `ContentDialog` — and a small Edit action that
  copies the row's scope/key/value into a "Remember a new fact" expander below the list. The
  expander's scope picker chooses between `MemoryScope.Global` and
  `MemoryScope.Project(WorkingRoot)`; saving calls `IMemoryStore.RememberAsync`, which **upserts**
  on scope+key, so there's no separate typed update — editing a row is just prefilling the form
  and saving again. Key/value fields clear only after a successful save; a failed forget or
  remember leaves the list and the user's input exactly as they were and reports the error in the
  page's status message.

## Schedules page

A top-level "Schedules" nav item (`Views/SchedulesPage.xaml`, `ViewModels/SchedulesViewModel.cs`)
gives the App parity with the console's `/schedule list|run` and the headless `--serve`
scheduler, editing `Caliper:Schedules` through `IConfigWriter.LoadSchedulesAsync`/
`SaveSchedulesAsync` — the same load-mutate-save contract every other settings page uses, so a
failed save (an invalid cron, a duplicate name, a permissions overlay the validator rejects) shows
its error in an `InfoBar` instead of throwing.

- **List + detail**, master-detail like MCP servers: the left list shows each job's name,
  enabled/disabled state, cron, next occurrence, and last result — mirroring the console table
  exactly (`ScheduleCron.GetNextOccurrence` for the next-occurrence column: disabled → "—", a
  cron that can never fire again → "never", a parse/time-zone failure → "invalid — {error}",
  otherwise a localized time; `ScheduleJobRunner.GetLastResult` for the last-result column). The
  right pane edits one job's full field set (name, cron, time zone, prompt, working root, model,
  max steps, enabled) plus an optional permissions overlay — an "Override permissions" toggle
  that, when off, saves `Permissions: null`; when on, exposes Mode/ShellAutoAllowlist/
  AutoAllowFileRoots with inline text noting the same Mode-must-be-Auto rule
  `CaliperOptionsValidator` enforces. Cron validity is checked live as you type (the same
  `ScheduleCron.GetNextOccurrence` call, shown as a hint under the cron box), and Save is disabled
  while any job is missing a required field or duplicates another job's name. Add/remove get an
  empty state ("No schedules yet") and a confirmation dialog respectively; a `ProgressRing` covers
  the initial load.
- **Run now**: each job has a "Run now" button (list row and detail pane) that calls
  `ScheduleJobRunner.RunJobAsync` on a background thread — the identical unattended path a cron
  tick or the console's `/schedule run` takes. While running, the button disables and shows a
  spinner; on completion an inline status line reports the finish reason, any error, the denial
  count ("N action(s) denied (unattended policy)"), and a pointer to the new session ("Transcript
  saved to session '[job] {name}' — open it from Chat"); `RunJobAsync`'s own overlap guard means a
  second trigger while one is still in flight comes back `Skipped` instead of queuing. The Chat
  page's sessions pane is refreshed (`SessionsViewModel.RefreshAsync`) right after, so the job's
  `[job] {name}` session shows up there without a restart — opening it is still a manual step from
  Chat in this v1 (no new cross-page navigation was added for it).
- **In-app scheduler (opt-in)**: a "Run scheduler while the app is open" toggle starts/stops
  `SchedulerHostedService` — the same `BackgroundService` `--serve` runs — inside this process via
  `Scheduling/AppSchedulerController.cs`. Core deliberately never registers this service itself, so
  the App is the one giving it a lifecycle: a fresh instance is created
  (`ActivatorUtilities.CreateInstance`) on each start and discarded (not restarted) on each stop,
  bounded by a ~3s shutdown timeout mirroring the MCP hub's own shutdown bound in
  `Window_Closed`. The preference persists in `~/.caliper/app-ui.json`
  (`AppPreferences.RunSchedulerInApp`) — host-local behavior, not a `config.json` engine setting —
  and auto-starts on the next launch if it was on at last close. Jobs it fires are unattended, the
  same as "Run now"; the page shows the live state ("Scheduler active — N enabled schedule(s)")
  and the caption is explicit that this only ticks while the window stays open — headless
  scheduling remains `--serve`.

## Runs page

A top-level "Runs" nav item (`Views/RunsPage.xaml`, `ViewModels/RunsViewModel.cs`) gives the App
parity with the console's `/runs` and `--resume <run-id>` — see [durable-runs.md](durable-runs.md)
for the underlying tracking/resume mechanics this page surfaces.

- **List**: the 20 most-recently-updated rows from `IRunStore.ListRecentAsync`, each showing a
  short run id, job name ("—" when it wasn't a scheduled job), status (with a "(resumed)" suffix
  once a run has been resumed at least once), step/budget ("Step N/M"), and a localized "updated"
  time — mirroring the console's `RenderRunsListAsync` table exactly. A row's `Reason` (the
  completion reason, an error, or the startup-sweep's interruption note) shows as secondary text
  when present, and an "unattended" caption appears when the run's `RunSpec.Unattended` was true.
  `Interrupted` rows are visually distinguished with a caution-colored (`SystemFillColorCautionBrush`)
  status badge and icon, never a hardcoded color. A header "Refresh" button reloads the list on
  demand; a `ProgressRing` covers the load, and the empty state repeats the console's own wording
  ("One-shot, --unattended, scheduled, and subagent runs are tracked; interactive chat turns are
  not.") so it's clear why an ordinary chat session never shows up here.
- **Resume**: only `Interrupted` rows show a "Resume" button, which drives
  `IConversationOrchestrator.ResumeAsync` on a background thread — the identical path
  `--resume <run-id>` takes, including the remaining-step-budget rebase and the dangling-call
  healing note. The button disables and shows a per-row spinner while in flight, and a run can never
  be resumed twice concurrently from this UI. On completion, a page-level `InfoBar` reports the
  outcome: the error if the resume failed, otherwise "Finished: {reason}", plus a denial count
  ("N action(s) denied (unattended policy)") when the unattended policy denied anything, and a
  closing pointer to the session ("Transcript is in session '{short id}' — open it from Chat"). The
  list is then reloaded, so the row reflects the run's real terminal status/reason rather than a
  transient message. As cheap v1 transcript freshness (same scope cut as the Schedules page's own
  "Run now"), the Chat page's sessions pane is refreshed right after, and if the resumed run's
  session happens to be the one currently open in Chat, it's re-selected so the transcript doesn't
  go stale — no live event streaming into an open transcript and no new cross-page navigation were
  added; both are deferred.
- **Startup sweep surfacing**: right after launch, `MainPage` checks `IRunStore.ListRecentAsync`
  for any `Interrupted` rows (the ones the startup sweep just produced) independent of whether the
  user ever opens the Runs page. If any exist, a dismissible `InfoBar` ("N run(s) were interrupted —
  view Runs.", Warning severity) appears above the content frame, with a "View Runs" action that
  selects the Runs nav item. Dismissing it (either the close button or the action) is session-only —
  it doesn't persist and reappears fresh on the next launch if the count is still non-zero. The
  check runs off the UI thread and is wrapped in the same top-level A11 try/catch boundary every
  other page uses, so a run-store failure can never crash startup.

## Approvals

`ApprovalService` implements `IPermissionPrompt` as docked approval cards with keyboard
accelerators, a **5-minute auto-deny timeout**, and per-request correlation
(`PermissionRequested`/`PermissionResolved` by request id). Approval countdowns run on the
injected `TimeProvider`. Concurrent requests (e.g. from subagent runs) **queue FIFO**: the
docked card always shows the oldest pending approval, an "Approval 1 of N" indicator appears
when more are waiting, and resolving the current one (approve, deny, or timeout) promotes
the next. A queued approval that resolves externally — timeout auto-deny, run cancellation,
or a `PermissionResolved` event — is dropped from the queue without disturbing the current
card. The queue survives session switches, like the single docked card always has. Subagent
children's requests render under the child session's title so it's clear who is asking.

The App's `IPermissionPrompt` registration is actually a `RoutingPermissionPrompt` wrapping
`ApprovalService` — the same split the console REPL uses. Interactive runs go straight to the
docked approval cards above; a schedule run (a "Run now" click, or a tick of the in-app scheduler
described below) builds its `RunSpec` with `Unattended = true`, and `RoutingPermissionPrompt` sends
those requests to `UnattendedPermissionPrompt` instead — denied and logged, never an approval card,
identical to the console's `--serve`/`/schedule run` behavior.

If the window is inactive (not the foreground/active window), the App also raises a Windows
notification (toast) for: a new pending approval, and a run finishing — "Run finished" for a
clean completion or "Run failed" for any other terminal outcome (cancelled, step limit, loop
detected, or failed), with the session title as the second line where available. The
run-finished toast fires only for a genuine transition out of an in-flight run (never a session
switch or reload); no taskbar flash or badge is implemented. Both are best-effort — a WinRT/COM
notification failure is logged, never surfaced or thrown.

## Secrets and preferences

- API keys live in **Windows Credential Manager** (Settings → Models & providers), never in
  `config.json`. Endpoint/key changes are restart-required; the app offers a one-click
  "Restart Caliper".
- UI preferences (theme, window placement, sessions/inspector pane collapsed state and
  widths, subagent-runs toggle) persist in `~/.caliper/app-ui.json` (`AppPreferencesStore`).
  Window placement restores clamped back onto a live display; closing while maximized keeps
  the saved *floating* bounds and an `IsMaximized` flag, so the next launch restores the
  floating rect and then re-maximizes (older prefs files without the flag, or without the
  inspector/pane-width keys, load with the old defaults: inspector expanded, widths null).
  Runtime settings still come from `~/.caliper/config.json` via the same `IConfigWriter`
  seam the console uses.
- The chat token-usage footer (cumulative prompt/completion counts) persists per session in
  `~/.caliper/app-usage.json` (`SessionUsageStore`), so it survives an app restart. Core's
  session store keeps no usage data, so this is App-side only, keyed by session id, and
  removed alongside the session in `RemoveSession`.

## Logging

Like the Console, the App logs Warning-and-above through `ILogger` to the shared
`~/.caliper/logs/caliper.log` (`FileLoggerProvider`, now in `Caliper.Core.Logging` so both
hosts use the same implementation) — this is the only place degraded states (Core's
respond-only fallback, tokenizer fallback, MCP errors, summarization fallback, and the App's
own top-level "A11" resilience-boundary catches) are recorded outside a debugger. `AddDebug()`
still runs alongside it for the Visual Studio Output window, filtered to the same Warning+
minimum level.

An `Application.UnhandledException` handler logs a Critical breadcrumb (message + exception) to
the same log file before the process dies on an otherwise-unrecoverable XAML/WinRT crash (e.g. a
layout cycle) — it never marks the exception handled, since the runtime treats that class of
failure as non-recoverable regardless. DEBUG builds also turn on XAML layout-cycle tracing
(`DebugSettings.LayoutCycleTracingLevel`), which names the looping elements in a native debugger
or crash dump on the next repro.

## Settings pages

Structured pages (General, Models & providers, Permissions, Tools, Agent behavior,
Context & memory, Subagents, Execution, MCP servers, Search, Advanced) edit typed config
sections through `IConfigWriter`, which reports which changes are live vs restart-required. The
settings `NavigationView` uses top (`PaneDisplayMode="Top"`) navigation, not a second left rail
nested inside the app's root nav — items that don't fit collapse into a "More" overflow menu.

Every page follows the same restart-affordance pattern: a `RestartRequired` view-model flag, set
from the save result (`result.Success && result.RestartRequired`, cleared at the start of the
next save — never claimed for a save that didn't happen), backs an `InfoBar.ActionButton`
("Restart Caliper") next to the status message, shown only while the flag is true. In practice
the flag only ever goes true on Models & providers (endpoint/key changes), Tools (the enabled-tool
set actually changed), MCP servers and Search (every save), and Advanced (both the persistence
path and the raw-JSON escape hatch, the latter unconditionally since it bypasses `IConfigWriter`'s
typed restart computation entirely) — General, Agent behavior, Context & memory, Permissions,
Subagents, and Execution save fields that are live seams, so `IConfigWriter` never reports true
for them; the flag and action button are still wired identically there for uniformity. All ten
non-Models pages reuse the same restart call via the internal `AppRestart.Restart()` helper.

**Subagents** (`ViewModels/Settings/SubagentsSettingsViewModel.cs`,
`Views/Settings/SubagentsSettingsPage.xaml`) edits `Caliper:Subagents` — global limits
(MaxDepth, MaxChildrenPerRun, TimeoutSeconds, DefaultProfile) plus a master-detail editor over
the named `Profiles` dictionary, mirroring MCP servers' shape. Each profile's `EnabledTools` is a
one-tool-per-line text box (same convention as the Schedules page's allowlist fields, not a
checkbox grid); `Mode` is a ComboBox with an extra "(inherit)" option mapping to a null
`SubagentProfileOptions.Mode`. Renaming the profile that is currently `DefaultProfile` carries
the rename along (tracked by object identity, not name, so renaming some *other* profile to the
old default name can't hijack it); removing the profile currently set as `DefaultProfile` is
blocked with an inline message rather than allowed through to a Save that the validator would
reject. Client-side validation (non-empty unique names, a `DefaultProfile` that exists among the
profiles, at least one tool per profile) disables Save before it's ever attempted. The section is
a live seam — `SubagentTool` reads it fresh per `task` call — so saved changes apply to the very
next subagent invocation with no restart.

**Execution** (`ViewModels/Settings/ExecutionSettingsViewModel.cs`,
`Views/Settings/ExecutionSettingsPage.xaml`) edits `Caliper:Execution` (see
[sandboxed-execution.md](sandboxed-execution.md)) — the Host/Container backend picker, plus the
container-only knobs (Image, Network, CPUs, Memory, User), which are disabled and visually dimmed
while Backend is Host since they're only consulted under Container. Because a bare `"*"` entry in
a shell auto-allowlist requires the Container backend, the page proactively checks the global
Permissions allowlist and every schedule's permissions overlay (on load and whenever the Backend
picker changes) and shows a Warning `InfoBar` *before* Save is even clicked if switching to Host
would make one of those wildcards invalid — rather than letting `SaveExecutionAsync`'s validator
rejection be the first signal. A failed read of either section while computing that warning is
treated as "no warning," never a page crash. The whole section is a live seam, so saved changes
apply to the very next shell tool call with no restart.

Advanced's raw JSON editor also validates inline as you type: each edit debounces ~500ms (via the
injected `TimeProvider`, so it's exercised deterministically in tests with a `FakeTimeProvider`)
before a `JsonDocument.Parse` check sets an inline error caption under the editor; a cancelled,
superseded debounce never overwrites a newer edit's result. This is advisory UI only — "Save raw
JSON" still runs its own independent validation (via `IConfigFileStore.WriteAsync`) and remains
the actual gate on what gets written.

## Conventions

`async void` is confined to WinUI lifecycle/event-handler signatures (unavoidable); bodies
delegate to view models. Broad `catch (Exception)` appears only at top-level UI-resilience
boundaries, each carrying a justification comment. View-model logic is tested in
`tests/Caliper.App.Tests` (no UI automation).

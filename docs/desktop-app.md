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
children's requests render under the child session's title so it's clear who is asking. The
App never runs unattended specs, so it needs no routing prompt.

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
Context & memory, MCP servers, Search, Advanced) edit typed config sections through
`IConfigWriter`, which reports which changes are live vs restart-required. The settings
`NavigationView` uses top (`PaneDisplayMode="Top"`) navigation, not a second left rail nested
inside the app's root nav — items that don't fit collapse into a "More" overflow menu.

Every page follows the same restart-affordance pattern: a `RestartRequired` view-model flag, set
from the save result (`result.Success && result.RestartRequired`, cleared at the start of the
next save — never claimed for a save that didn't happen), backs an `InfoBar.ActionButton`
("Restart Caliper") next to the status message, shown only while the flag is true. In practice
the flag only ever goes true on Models & providers (endpoint/key changes), Tools (the enabled-tool
set actually changed), MCP servers and Search (every save), and Advanced (both the persistence
path and the raw-JSON escape hatch, the latter unconditionally since it bypasses `IConfigWriter`'s
typed restart computation entirely) — General, Agent behavior, Context & memory, and Permissions
save fields that are live seams, so `IConfigWriter` never reports true for them; the flag and
action button are still wired identically there for uniformity. All eight non-Models pages reuse
the same restart call via the internal `AppRestart.Restart()` helper.

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

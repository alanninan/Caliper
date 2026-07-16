# Desktop app (`Caliper.App`)

A WinUI 3 host on the same `Caliper.Core` engine — MVVM (CommunityToolkit), not trimmed/AOT.
Run with `dotnet run --project src/Caliper.App` (requires Windows App SDK, Windows
10.0.19041+).

## Layout

Three-pane workbench: sessions pane, transcript, and an inspector for tool calls (arguments
as pretty-printed JSON, output, and file diffs for write/edit operations). Context
compactions render as full-width expandable dividers so you can see where earlier turns
stopped being sent to the model.

- **Sessions pane** hides subagent child sessions (`parent_session_id` set) behind a
  "Show subagent runs" toggle; job runs (`[job] {name}`) appear like any session.
- **Live events** map through `AgentEventMapper` (streamed run → view models);
  `PersistedTranscriptFactory` rebuilds the same view models from stored messages on reload.
- Both the sessions pane (`Ctrl+B`) and the inspector pane (`Ctrl+Shift+B`) can be collapsed
  from the workspace header toolbar; each state persists (`SessionsPaneCollapsed` /
  `InspectorPaneCollapsed`) and restores on next launch. Widths dragged with either
  `GridSplitter` also persist (`SessionsPaneWidth` / `InspectorPaneWidth`, in DIPs) — saved on
  every drag's `ManipulationCompleted` and whenever a pane collapses, so a resized pane snaps
  back to its own remembered width, not a fixed default, when re-expanded. The main window
  enforces an 800×560 DIP minimum size (`OverlappedPresenter.PreferredMinimumWidth/Height`) so
  both panes stay clip-free at once; their column `MinWidth`s (180/360/240) were sized to fit
  under it.

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

## Settings pages

Structured pages (General, Models & providers, Permissions, Tools, Agent behavior,
Context & memory, MCP servers, Search, Advanced) edit typed config sections through
`IConfigWriter`, which reports which changes are live vs restart-required. The settings
`NavigationView` uses top (`PaneDisplayMode="Top"`) navigation, not a second left rail nested
inside the app's root nav — items that don't fit collapse into a "More" overflow menu.

## Conventions

`async void` is confined to WinUI lifecycle/event-handler signatures (unavoidable); bodies
delegate to view models. Broad `catch (Exception)` appears only at top-level UI-resilience
boundaries, each carrying a justification comment. View-model logic is tested in
`tests/Caliper.App.Tests` (no UI automation).

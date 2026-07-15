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
- UI preferences (theme, window placement — clamped back on-screen, sessions-pane state,
  subagent-runs toggle) persist in `~/.caliper/app-ui.json` (`AppPreferencesStore`).
  Runtime settings still come from `~/.caliper/config.json` via the same `IConfigWriter`
  seam the console uses.

## Settings pages

Structured pages (General, Models & providers, Permissions, Tools, Agent behavior,
Context & memory, MCP servers, Search, Advanced) edit typed config sections through
`IConfigWriter`, which reports which changes are live vs restart-required.

## Conventions

`async void` is confined to WinUI lifecycle/event-handler signatures (unavoidable); bodies
delegate to view models. Broad `catch (Exception)` appears only at top-level UI-resilience
boundaries, each carrying a justification comment. View-model logic is tested in
`tests/Caliper.App.Tests` (no UI automation).

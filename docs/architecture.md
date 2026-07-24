# Architecture

## Engine and hosts

Caliper is a layered runtime (deliberately not Clean/VSA/DDD):

- **`Caliper.Core`** — the engine library: agent loop, tools, model clients, permissions,
  persistence, context management, scheduling, execution backends, memory, and skills.
  Native-AOT-compatible (`IsAotCompatible=true`), JSON via source generation only.
- **`Caliper.Console`** — terminal host. Interactive REPL with slash commands, plus the
  headless entry points (`--prompt`, `--unattended`, `--serve`, `--resume`). Published
  Native AOT (`PublishAot=true`).
- **`Caliper.App`** — WinUI 3 desktop host on the same engine (see
  [desktop-app.md](desktop-app.md)). Not trimmed/AOT.

Everything is wired in `ServiceCollectionExtensions.AddCaliperCore`; hosts use
`Microsoft.Extensions.Hosting` (`Host.CreateApplicationBuilder`).

## The agent loop

`AgentRunner.RunAsync(RunSpec, CancellationToken)` (`Agents/AgentRunner.cs`, behind
`IAgentRunner`) returns `IAsyncEnumerable<AgentEvent>`. All per-run state is method-local, so
the singleton runner is re-entrant — several runs can be in flight at once (App session A
running while B is viewed; scheduler jobs; subagent children).

Each step: re-read live options → build the system prompt (runtime identity with the Caliper
harness, selected provider, and effective per-run model; skills menu; loaded skill bodies;
memory block; project document; current task) → fit history to the context window (compacting
if needed) → stream one model turn → persist and dispatch tool calls through the permission
gate → repeat. The loop is bounded by `MaxSteps`, `DuplicateCallLimit`, per-tool timeouts, and
windowed duplicate-signature loop detection (catches A-B-A-B oscillation, not just immediate
repeats).

## `RunSpec` — per-run scoping

`RunSpec` (`Agents/RunSpec.cs`) scopes a single run without mutating process-global settings:

| Member | Meaning (null ⇒ fall back to runtime settings) |
| --- | --- |
| `SessionId`, `Prompt` | Required identity + task |
| `Model` | Per-run model slug |
| `ToolFilter` | Restrict the tool surface (filtered-out tools vanish from schemas *and* resolve as unknown) |
| `PermissionsOverlay` | Per-run permission options (global shell denylist is always unioned in) |
| `MaxSteps` | Per-run step budget |
| `SubagentDepth` | 0 for top-level; incremented per nesting level |
| `WorkingRoot` | Per-run working root (jobs run in their own root) |
| `JobName` | Set by the scheduler |
| `Unattended` | Routes permission prompts to the deny+report path |
| `ResumeExisting` | Resume seam: skip the initial user-message append (see [durable-runs.md](durable-runs.md)) |

The legacy `RunAsync(sessionId, message)` overload forwards with defaults, so simple callers
never see `RunSpec`.

## The orchestrator

`ConversationOrchestrator.RunToCompletionAsync(RunSpec, onEvent, ct)` (behind
`IConversationOrchestrator`) drains a run to a `ConversationRunResult(AssistantMessage, Error,
Reason, Denials)`. It is the shared entry point for one-shot runs, unattended runs, scheduled
jobs, and subagent children — and the layer that writes `runs`-table bookkeeping
([durable-runs.md](durable-runs.md)) and collects permission denials (by correlating the
`PermissionRequested`/`PermissionResolved` event pair per call id). It also owns
`ForceCompactAsync`, `ResumeAsync`, and the `[context reset]` active-history logic.

Interactive REPL/App streaming consume `IAgentRunner` directly (they render events live);
everything headless goes through the orchestrator.

## Events — three consumers

`AgentEvent` records (`Events/AgentEvent.cs`) are the loop's output vocabulary:
`TurnStarted`, streamed content, `ToolInvoked`/`ToolFailed`, `PermissionRequested`/
`PermissionResolved`, `SubagentStarted`/`SubagentCompleted`, `ContextCompacted`,
`AssistantMessage`, `RunCompleted`/`RunFailed`, and friends.

**Every new event must be handled (or explicitly defaulted) in three consumers:** the Console
`EventRenderer`, the App `AgentEventMapper` (plus `PersistedTranscriptFactory` if its effect
should survive reload), and the eval harness. A checklist comment on the `AgentEvent` base
type records this.

Tools cannot yield events directly (their signature returns `ToolResult`); a tool that needs
to raise events buffers them via `ToolContext.Emit`, and `AgentRunner` drains the buffer into
the run's stream right after the dispatch returns — regardless of success, failure, or
cancellation. The subagent tool's start/complete events use this channel.

## Turn strategies

`TurnStrategySelector` picks a strategy per turn; the default is **`NativeToolStrategy`**
(native tool-calling via `Microsoft.Extensions.AI`). `NativeToolStrategy.BuildMessages` also
performs **dangling-tool-call healing**: any assistant tool call in history without a matching
result (a run killed mid-tool) gets a synthetic `[no result — run was interrupted]` result
before the model sees it. This healing is what makes crash-resume "load, heal, continue"
(see [durable-runs.md](durable-runs.md)). A constrained-envelope strategy exists for models
without native tool calling.

`Microsoft.Extensions.AI`'s streaming adapter can hand back a tool call whose argument JSON
failed to parse mid-stream without throwing (provider-dependent, intermittent). `NativeToolStrategy`
detects this (`ToolCall.MalformedReason`) and logs a warning instead of silently treating it as
`{}`; `AgentRunner` never dispatches a malformed call or requests permission for it, and instead
feeds the model a failed tool result naming the parse error so it re-issues the call. The same
short-circuit covers a tool that "succeeds" with an empty `{}` when its schema has required
fields. Loop detection still applies if the model repeats the identical malformed call.

## Models

Four `IModelProvider` registrations sit behind `ModelProviderRouter`
(`IChatClientProvider` + `IModelCapabilityProvider` + `IModelCatalog`): OpenRouter, Gemini,
OpenAI Platform (API key + Responses API), and OpenAI Codex (ChatGPT OAuth + Codex Responses
backend). Authentication, transport, catalog, and capabilities remain provider-scoped; an
unknown provider fails explicitly rather than falling back to OpenRouter. The router re-reads
the configured provider per call, so `/set provider` switches live. `RunSpec.Model` reaches
capability lookup and client selection through `TurnContext.Model` in every strategy.

OpenAI Codex uses PKCE browser login or device-code login and refreshes short-lived access
tokens through `auth.openai.com`. Caliper uses the HTTP Responses transport so the host retains
ownership of tools, permissions, persistence, and the agent loop.

## Persistence

SQLite stores (`Persistence/`): `sessions` + `messages` (with `parent_session_id` linking
subagent child sessions), plus the `runs` table. Compaction is **non-destructive**:
`ReplaceWithCompactionAsync` marks superseded rows with `superseded_at` instead of deleting,
so the full original journal always survives. Schema migrations use the `EnsureColumnAsync`
pattern. Store timestamps: legacy stores still use `DateTimeOffset.UtcNow`
(`SqliteStoreBase.NowString`); the run store — and all new time-sensitive code — uses
`TimeProvider`.

## The tool contract

```csharp
public interface ITool
{
    string Name { get; }
    string Description { get; }
    JsonElement ParameterSchema { get; }
    SideEffect SideEffect { get; }
    TimeSpan? ToolTimeoutOverride => null;   // default interface member
    Task<ToolResult> InvokeAsync(JsonElement arguments, ToolContext ctx, CancellationToken ct);
}
```

- Tools are DI singletons, gated by `CaliperOptions.EnabledTools`, merged with MCP tools in
  `ToolRegistry`, and further narrowed per run by `RunSpec.ToolFilter`
  (`FilteredToolRegistry`: a filtered-out tool is absent from schemas *and* resolves as
  unknown).
- **Outcomes are `ToolResult` (success + output)**, not exceptions and not a generic
  `Result<T>`. Exceptions are reserved for genuinely exceptional store/IO errors.
- **Schemas stay flat** — no nested objects. Nested schemas degrade GBNF conversion for
  small models. (Nested *config* is fine; the constraint is tool parameter schemas only.)
- Tools can classify per invocation via `EffectiveSideEffect(arguments)` (e.g. `memory`
  reads are read-only; `remember`/`forget` are writes).
- `ToolContext` is the per-dispatch context: HTTP client factory, logger, skills root,
  working root, outside-root permission flag, cancellation token, `SessionId`, `CallId`,
  `SubagentDepth`, per-run `SubagentRunState`, the run's `PermissionsOverlay`, and the
  `Emit`/`DrainEmittedEvents` channel for tools that raise `AgentEvent`s.

Adding a tool:

1. Implement `ITool` in `Tools/BuiltIn/` (flat schema; classify `SideEffect`; honor `ct`).
2. Register it in `ServiceCollectionExtensions` and add its name to the `EnabledTools`
   default if it should ship enabled.
3. If it emits new `AgentEvent`s, wire all three consumers (see above).
4. If it needs longer than `ToolTimeoutSeconds`, override `ToolTimeoutOverride` — don't
   raise the global timeout.
5. Hermetic tests in `tests/Caliper.Core.Tests/Tools/`; gate-interaction tests if the tool
   has novel permission semantics.

## AOT and JSON

The console is Native AOT; reflection-based serialization is disabled
(`JsonSerializerIsReflectionEnabledByDefault=false`). Every serialized type must be
registered in `Protocol/CaliperJsonContext.cs`. New dependencies must be trim/AOT-safe
(Cronos ✅, the `docker` CLI ✅; Quartz and Docker.DotNet are not acceptable). Package
versions live centrally in `Directory.Packages.props` — never on a `<PackageReference>`.
`AgentOptions` is legacy and unused by the native turn strategy — never add knobs to it;
use `CaliperOptions`.

## The one blessed service-locator

`ToolRegistry`'s constructor enumerates every `ITool` singleton, and the orchestrator depends
on the registry — so a tool that runs child conversations (`SubagentTool`) cannot
constructor-inject `IConversationOrchestrator` without a DI cycle. It injects
`IServiceProvider` and resolves inside `InvokeAsync`, documented at the site. Do not copy the
pattern anywhere that lacks the same cyclic constraint.

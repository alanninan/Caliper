# Tools

## The `ITool` contract

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
- Dispatch wraps every call in `ToolTimeoutSeconds` unless the tool sets
  `ToolTimeoutOverride` (the subagent tool does — a child run routinely outlives the generic
  timeout). Side-effecting tools are never retried, so a timeout can't double-apply.
- Output is truncated to `ToolOutputMaxChars` for display; buffering is bounded upstream so a
  runaway process can't consume unbounded memory first.

## `ToolContext`

Per-dispatch context handed to every tool: HTTP client factory, logger, skills root, working
root (per-run when `RunSpec.WorkingRoot` is set), outside-root permission flag, cancellation
token, `SessionId`, `CallId`, `SubagentDepth`, per-run `SubagentRunState`, the run's
`PermissionsOverlay`, and the `Emit`/`DrainEmittedEvents` channel for tools that need to
raise `AgentEvent`s (see [architecture.md](architecture.md)).

## Built-in tools

| Tool | Side effect | Notes |
| --- | --- | --- |
| `read_file` | Read | 1-based line-number prefixes |
| `list_dir`, `glob`, `grep` | Read | Safe traversal (skips reparse points/inaccessible dirs); glob supports `**/`; grep has case sensitivity + path-aware glob |
| `search` | Read | Tavily web search (`CALIPER_SEARCH_KEY`) |
| `fetch_url` | Read | HTTP fetch |
| `write_file`, `edit_file` | Write | In-root writes auto-allow under Auto mode |
| `bash`, `powershell` | Execute | Via execution backends — see [sandboxed-execution.md](sandboxed-execution.md); `CALIPER_*` env scrub, stdin closed, kill-tree on cancel |
| `memory` | Read/Write per call | `recall` is read-only; `remember`/`forget` are writes |
| `load_skill` | Read | Special-cased in the loop (persists call/result, loads the body into the prompt); still subject to `ToolFilter` |
| `task` | Execute | Spawns a subagent — see [subagents.md](subagents.md) |

Default enabled set: all of the above.

## MCP tools

Stdio and HTTP MCP servers are configured in the `Mcp` section; their tools merge into the
registry alongside built-ins. Their self-declared read-only annotations are **not trusted**
by the permission gate — an MCP "read-only" tool still prompts like a write
([permissions.md](permissions.md)). `/mcp` and `/mcp reconnect` manage connections in the
REPL.

## Adding a tool (checklist)

1. Implement `ITool` in `Tools/BuiltIn/` (flat schema; classify `SideEffect`; honor `ct`).
2. Register it in `ServiceCollectionExtensions` and add its name to the `EnabledTools`
   default if it should ship enabled.
3. If it emits new `AgentEvent`s, wire all three consumers
   ([architecture.md](architecture.md)).
4. If it needs longer than `ToolTimeoutSeconds`, override `ToolTimeoutOverride` — don't
   raise the global timeout.
5. Hermetic tests in `tests/Caliper.Core.Tests/Tools/`; gate-interaction tests if the tool
   has novel permission semantics.

# Tools

Tools are the actions the model can take. Which ones are available is yours to control; every
side-effecting call still passes the permission gate ([permissions.md](permissions.md)).

## Built-in tools

| Tool | Kind | What it does |
| --- | --- | --- |
| `read_file` | Read | Reads a file with line numbers |
| `list_dir`, `glob`, `grep` | Read | Directory listing, glob matching, content search (safe traversal — skips reparse points and inaccessible directories) |
| `search` | Read | Web search via Tavily (needs `CALIPER_SEARCH_KEY`) |
| `fetch_url` | Read | Fetches a URL |
| `write_file`, `edit_file` | Write | Creates/edits files; in-root writes auto-allow under Auto mode |
| `bash`, `powershell` | Execute | Runs shell commands — on the host or in a Docker sandbox ([sandboxed-execution.md](sandboxed-execution.md)) |
| `memory` | Read/Write | Recalls, remembers, and forgets persistent facts (recall is read-only; remember/forget count as writes) |
| `load_skill` | Read | Loads a skill's instructions into the run |
| `task` | Execute | Delegates work to a subagent ([subagents.md](subagents.md)) |

All of the above ship enabled.

## Enabling and disabling

`Caliper:EnabledTools` in `config.json` (or the app's Settings → Tools page) controls the
available set. Changing it requires a restart. Subagent profiles and one-shot Plan mode
narrow the set further per run automatically — a tool outside the run's set is invisible to
the model, not just refused.

## Behavior you'll notice

- **Timeouts**: each call is bounded by `ToolTimeoutSeconds` (default 60s). Long-running
  tools like `task` carry their own larger bound. Side-effecting calls are never retried, so
  a timeout can't apply a change twice.
- **Output limits**: tool output is truncated at `ToolOutputMaxChars` (default 16,000)
  before the model sees it, and runaway process output is bounded as it streams — a noisy
  command can't exhaust memory.
- **Secrets don't leak into commands**: every `CALIPER_*` environment variable is stripped
  from shell and docker child processes.

## MCP servers

External tool servers plug in via the `Mcp` config section (or the app's Settings → MCP
servers page) — stdio or HTTP:

```jsonc
"Mcp": {
  "Servers": {
    "github": { "Type": "http", "Url": "https://…", "BearerToken": null },
    "local":  { "Type": "stdio", "Command": "npx", "Args": ["-y", "some-mcp-server"] }
  }
}
```

MCP tools appear alongside built-ins. Two things to know:

- Their "read-only" self-declarations are **not trusted** — an MCP tool always asks like a
  write ([permissions.md](permissions.md)).
- `/mcp` shows connection status; `/mcp reconnect` retries. In the app, bearer tokens are
  stored in Windows Credential Manager, and MCP changes are restart-required.

Writing a new built-in tool is a contributor topic — see
[architecture.md](architecture.md#the-tool-contract).

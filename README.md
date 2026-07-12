# Caliper

Caliper is a .NET agent runtime with two front-ends — an interactive console app and a WinUI 3 desktop app — for running model-assisted workflows against a local workspace. It combines OpenRouter and Google Gemini chat clients, guarded local tools, MCP server support, session persistence, memory, skills, and context compaction. The reusable engine lives in `Caliper.Core`; the console and the WinUI app are two hosts on top of it.

The project is designed around deterministic host-side control: model output proposes work, while Caliper owns tool dispatch, permissions, retries, context management, persistence, and safety checks.

## Features

- Console-first agent loop with streaming responses, plus a WinUI 3 desktop app (`Caliper.App`)
  with a three-pane workbench, inline tool/diff inspector, docked permission approvals, and
  structured settings pages.
- OpenRouter and Google Gemini provider integration through `Microsoft.Extensions.AI` (Gemini via
  its OpenAI-compatible endpoint).
- Runtime model switching with `/model <slug>` and catalog inspection with `/models`; runtime
  provider switching with `/set provider <OpenRouter|Gemini>`.
- Tool permission modes: `AskAlways`, `Auto`, and `Plan`.
- Built-in tools for search, URL fetch, file reads/writes/edits, glob, grep, shell, memory, and skill loading.
- MCP client support for stdio and HTTP servers.
- SQLite-backed sessions, memory, and resumable transcripts.
- Context-window fitting with optional automatic compaction.
- Skill discovery from `~/.caliper/skills`.
- Hermetic and model-in-the-loop eval harnesses.
- Native AOT-oriented project settings for the console app.

## Repository Layout

```text
Caliper.slnx
src/
  Caliper.Console/      Console host, rendering, slash commands, sample config
  Caliper.Core/         Agent loop, tools, model clients, persistence, memory
  Caliper.App/          WinUI 3 desktop host (view models, XAML, Credential Manager store)
tests/
  Caliper.Console.Tests/
  Caliper.Core.Tests/
  Caliper.App.Tests/    View-model and pure-layer tests for the WinUI app
  Caliper.Evals/        Hermetic and model-in-the-loop evaluation suites
```

## Desktop app (`Caliper.App`)

The WinUI 3 desktop app is a second host on the same `Caliper.Core` engine.

- Run it with `dotnet run --project src/Caliper.App` (or F5 in Visual Studio); requires the Windows
  App SDK and a Windows 10.0.19041+ target.
- API keys are stored in **Windows Credential Manager** (not `config.json`) — enter them under
  Settings → Models &amp; providers. Provider endpoint and key changes apply after a restart, which
  the app offers as a one-click "Restart Caliper" button.
- UI preferences (theme, window placement, sessions-pane state) live in `~/.caliper/app-ui.json`;
  runtime settings still come from `~/.caliper/config.json`.

## Prerequisites

- .NET SDK 10.0 or newer.
- An API key for model-backed runs: an OpenRouter key (default provider), or a Google Gemini API
  key if you plan to use `Provider: "Gemini"`.
- A model slug from your chosen provider. Pick a model appropriate for your task; tool-calling
  models work best with Caliper's native tool strategy.

## Setup

1. Restore and build the solution.

   ```powershell
   dotnet restore Caliper.slnx
   dotnet build Caliper.slnx
   ```

2. Configure credentials. The recommended local setup is to keep the API key in an environment variable.

   ```powershell
   $env:CALIPER_OPENROUTER_KEY = "<your-openrouter-api-key>"
   ```

3. Configure the model slug. Caliper seeds `~/.caliper/config.json` on first run. Set `Caliper.Model` to the OpenRouter model slug you want to use.

   ```json
   {
     "Caliper": {
       "Provider": "OpenRouter",
       "Model": "<your-openrouter-model-slug>"
     }
   }
   ```

   You can also override the model for a single shell session:

   ```powershell
   $env:CALIPER_Caliper__Model = "<your-openrouter-model-slug>"
   ```

   To use Gemini instead, set `CALIPER_GEMINI_KEY` and switch the provider and model:

   ```powershell
   $env:CALIPER_GEMINI_KEY = "<your-gemini-api-key>"
   ```

   ```json
   {
     "Caliper": {
       "Provider": "Gemini",
       "Model": "gemini-2.5-flash"
     }
   }
   ```

   Or switch providers at runtime from the console with `/set provider Gemini` followed by
   `/model gemini-2.5-flash`.

4. Run Caliper.

   ```powershell
   dotnet run --project src/Caliper.Console
   ```

## Usage

Interactive mode starts a persisted session and accepts natural-language prompts. Useful slash commands:

```text
/help                         Show command help
/new                          Start a new session
/sessions                     List saved sessions
/resume <session-id>          Resume a previous session
/model <slug>                 Switch the active model at runtime
/models                       List available model metadata from the provider
/tools                        Show enabled tools
/permissions <mode>           Set AskAlways, Auto, or Plan
/mcp                          Show MCP server status
/mcp reconnect                Reconnect configured MCP servers
/memory                       Show project memory
/compact                      Force context compaction
/clear                        Clear active context while keeping transcript
/quit                         Exit
```

One-shot read-only mode is useful for quick inspections. Without an explicit permissions override, one-shot prompts run in `Plan` mode with read-only tools.

```powershell
dotnet run --project src/Caliper.Console -- --prompt "Summarize this repository" --print
```

To allow a different permission mode:

```powershell
dotnet run --project src/Caliper.Console -- --prompt "Run the test suite" --permissions AskAlways
```

## Configuration

Caliper reads configuration from:

1. `~/.caliper/config.json`
2. Environment variables with the `CALIPER_` prefix
3. Command-line overrides for supported host options

Important settings:

| Setting | Purpose |
| --- | --- |
| `Caliper.Provider` | Active provider: `OpenRouter` (default) or `Gemini`. Switchable at runtime with `/set provider <name>`. |
| `Caliper.Model` | Active provider model slug. Configure this before model-backed use. |
| `Providers.OpenRouter.Endpoint` | OpenRouter-compatible API endpoint. |
| `Providers.OpenRouter.ApiKey` | API key, usually supplied through `CALIPER_OPENROUTER_KEY`. |
| `Providers.Gemini.Endpoint` | Gemini's OpenAI-compatible API endpoint. |
| `Providers.Gemini.ApiKey` | API key, usually supplied through `CALIPER_GEMINI_KEY`. |
| `Caliper.WorkingRoot` | Workspace root exposed to file tools. |
| `Caliper.EnabledTools` | Tool allowlist surfaced to the agent. |
| `Permissions.Mode` | Permission mode for side-effecting tools. |
| `Mcp.Servers` | MCP server definitions. |
| `Search.Backend` | `Stub` for local/dev use or `Tavily` when configured. |
| `Persistence.SqlitePath` | SQLite database path for sessions and memory. |

Environment variable examples:

```powershell
$env:CALIPER_OPENROUTER_KEY = "<your-openrouter-api-key>"
$env:CALIPER_GEMINI_KEY = "<your-gemini-api-key>"
$env:CALIPER_Caliper__Model = "<your-openrouter-model-slug>"
$env:CALIPER_Permissions__Mode = "AskAlways"
```

## Testing

Run all unit tests:

```powershell
dotnet test Caliper.slnx
```

Run hermetic evals without a model:

```powershell
dotnet run --project tests/Caliper.Evals -- --suite all --out eval-report.json
```

Run model-in-the-loop evals by supplying the model slug explicitly:

```powershell
dotnet run --project tests/Caliper.Evals -- --suite tool-calling --model "<your-openrouter-model-slug>"
```

## Publishing

The console project is configured for Native AOT publishing.

```powershell
dotnet publish src/Caliper.Console -c Release -r win-x64
```

Published output includes `config.example.json`.

## Security Notes

Caliper can execute local tools, read and write files, and launch shell commands when enabled. Use `AskAlways` for normal interactive work, keep `WorkingRoot` narrow, and review tool prompts before approving side effects. `Plan` mode is the safest mode for read-only analysis.

URL fetching blocks private, loopback, and link-local addresses. Shell commands are checked against configurable allowlists and denylists, but host permissions remain the final boundary.

## License

Caliper is licensed under the Apache License, Version 2.0. See [LICENSE](LICENSE).

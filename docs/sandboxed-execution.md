# Sandboxed shell execution

Shell commands (`bash`/`powershell`) normally run directly on your machine. Switch the
execution backend to `Container` and they run inside a disposable Docker container instead:
no network by default, capped CPU and memory, and the only part of your machine it can see
is the working root. That contained blast radius is what makes broad unattended shell
allowlists reasonable.

## Configuration (`Caliper:Execution`)

```jsonc
"Execution": {
  "Backend": "Host",                        // Host (default) | Container
  "Image": "mcr.microsoft.com/dotnet/sdk:10.0",
  "Network": "None",                        // None (default) | Bridge
  "Cpus": 2,
  "MemoryMb": 4096,
  "User": "1000"                            // non-root by default
}
```

Everything here is live — flipping `Backend` applies to the very next shell command, no
restart. The app has a Settings → Execution page for this section
([desktop-app.md](desktop-app.md#settings)).

## What each backend does

**Host** — commands run as normal local processes, with guardrails: `CALIPER_*` environment
variables (your API keys) are stripped, commands that wait for stdin fail fast instead of
hanging, output is bounded, and cancellation kills the whole process tree.

**Container** — commands run via `docker run` in a fresh container per call:

```
docker run --rm --network none --memory 4096m --cpus 2 --user 1000
           -v <workingRoot>:/workspace -w /workspace <image> bash -lc <command>
```

The working root is mounted read-write at `/workspace` (builds and installs need writes);
a working directory outside the working root is rejected. Cancellation kills the container
itself, not just the local client. Container execution is **bash only** — a `powershell`
call under the container backend fails with a clear message.

## Fails closed

If Docker isn't available (not installed, not running), container-backend commands **fail
with an error** — they never silently fall back to running on your machine. Caliper probes
Docker lazily and re-checks about every 30 seconds, so starting Docker Desktop heals the
backend without a restart, and a daemon that dies mid-session is noticed.

## Interplay with permissions

The sandbox is a second wall, not a replacement — the permission gate still evaluates every
command ([permissions.md](permissions.md)). The one rule that ties them together: an
unrestricted allowlist (a bare `"*"` entry) is only accepted when the backend is
`Container`. Caliper rejects that combination with Host execution at save time, and the
app's Execution page warns you before you even try.

## Windows notes

Requires Docker Desktop with the WSL2 (Linux containers) backend — Windows containers are
not supported. Commands run under `bash` inside the container regardless of your host shell.
The single working-root mount sidesteps most Windows↔Linux path translation pain.

Scope: the sandbox covers the **shell tools only**. File tools (`write_file`, `edit_file`)
always operate host-side, confined to the working root by the file permission policy.

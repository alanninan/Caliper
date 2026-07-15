# Sandboxed shell execution

Shell commands (`bash`/`powershell`) run through a pluggable `IExecutionBackend`. The
container backend is what makes broad unattended shell allowlists trustworthy: the blast
radius of an arbitrary command is a disposable, network-isolated container whose only view
of the machine is the bind-mounted working root.

## Configuration (`Caliper:Execution`)

```jsonc
"Execution": {
  "Backend": "Host",                        // Host (default) | Container
  "Image": "mcr.microsoft.com/dotnet/sdk:10.0",
  "Network": "None",                        // None (default) | Bridge
  "Cpus": 2,
  "MemoryMb": 4096,
  "User": "1000"
}
```

All knobs are live — `ShellTool` selects the backend per call, so flipping `Backend` applies
to the very next command without a restart (both backends are always-constructed singletons;
there is no rewiring step).

## Backends

**`HostExecutionBackend`** — the pre-existing process behavior, extracted verbatim:
`CALIPER_*` environment scrub (secrets never reach child processes), stdin closed (commands
waiting for input fail fast), bounded output buffering, drain-after-exit, kill-process-tree
on cancellation.

**`ContainerExecutionBackend`** — drives the `docker` CLI (never Docker.DotNet; AOT):

```
docker run --rm --name caliper-<guid> --network none --memory 4096m --cpus 2 --user 1000
           -v <workingRoot>:/workspace -w /workspace/<relative-cwd> <image> bash -lc <command>
```

- Arguments always via `ArgumentList` — the command is never string-concatenated into a
  shell-parsed argument.
- The request cwd is mapped under `/workspace`; a cwd outside the working root is rejected.
- Cancellation kills the local docker client **and** fires an explicit `docker kill` on the
  named container (on a fresh short-lived token — the caller's is already cancelled).
- Bash only in v1. A `powershell` call under the container backend fails with a clear
  message (Windows containers are out of scope).

## Fail-closed rules

Docker availability is probed lazily via `docker info` (5s timeout), cached for 30 seconds
(`TimeProvider`-driven, single-flighted so concurrent cold calls share one probe). If the
probe fails, **every container-backend call returns a failed `ToolResult`** ("container
backend unavailable: …"). There is no code path that silently falls back to host execution —
not for interactive runs and especially not for unattended ones. Negative probe results
self-heal within the TTL once Docker Desktop starts; positive results are re-confirmed
periodically so a daemon that dies mid-session is noticed.

## Permission interplay

The permission gate still evaluates every call (defense in depth) — the sandbox is a second
wall, not a replacement. The payoff is encoded as validation, not convention: a bare `"*"`
entry in any `ShellAutoAllowlist` (global or per-job) is **rejected unless
`Execution.Backend = Container`** — enforced at options binding and every config save path.
(Today's allowlist matching is prefix-based, so `"*"` isn't functional as a wildcard yet;
the guard exists so a future matcher change can't silently combine with host execution.)

Scope note: v1 sandboxes the **shell only**. File tools stay host-side, confined by the
symlink-resolving `FileAccessPolicy` (working root + auto-allow roots). The mount is
read-write by design — most agent shell work (builds, restores, installs) needs writes; the
blast radius is the working root itself.

## Windows reality

Requires Docker Desktop with WSL2 Linux containers. It's always `bash` inside the container
regardless of host shell. Path translation is just the single working-root mount, which
sidesteps most Windows↔Linux path pain.

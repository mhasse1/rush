# rush-ps-bridge

Standalone PowerShell bridge binary. Lets any JSON-speaking client
drive a persistent PowerShell runspace, so Rush (and anything else
that wants to) can script Windows / Exchange / AD / Azure in PS
without embedding PowerShell itself.

Tracking issue: [#267](https://github.com/mhasse1/rush/issues/267).

## Status

**Phase 1 — scaffolding only.** The binary builds and has `--help`
and `--version`. Both functional modes (`--bridge`, `--mcp`) are
stubbed and will print a "not yet implemented" message.

Phases 2–6 wire up the modes, the session semantics, packaging, and
retirement of the legacy `dotnet/` tree. See the tracking issue for
the plan.

## Modes

```
rush-ps-bridge --bridge     plugin JSON-lines protocol (Rush's plugin.ps wire)
rush-ps-bridge --mcp        MCP JSON-RPC 2.0 server (any MCP client)
rush-ps-bridge --version
rush-ps-bridge --help
```

Same binary, picked at startup. Both share a persistent PowerShell
runspace (see `PsRunner.cs`), so variables and function definitions
survive across calls within a single invocation.

## Why .NET

Hosting PowerShell is what `System.Management.Automation` is for.
Writing a PS host in Rust would mean either re-implementing the
protocol (large surface, moving target) or pinvoke'ing into .NET
anyway. Keeping this component in .NET and talking to everything else
over JSON is the right split.

## Build

```
dotnet build            # requires .NET 10 SDK
dotnet publish -c Release -r linux-x64 --self-contained
```

The self-contained publish produces a single-file binary
(~25 MB) that can ship alongside rush and be invoked with no
separate .NET runtime install.

## Layout

| File | Role |
|---|---|
| `Program.cs` | Entry point, CLI flag dispatch |
| `PsRunner.cs` | Shared PowerShell-invocation layer with persistent runspace |
| `BridgeMode.cs` | `--bridge` plugin-protocol loop (Phase 2) |
| `McpMode.cs` | `--mcp` JSON-RPC loop (Phase 3) |
| `rush-ps-bridge.csproj` | Project file |
| `global.json` | .NET SDK pin (10.0.100) |

The legacy `dotnet/` tree (Rush .NET interpreter) is still present
for reference and will be retired in Phase 6 once this binary is
proven in daily use.

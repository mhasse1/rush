# rush-ps-bridge

Standalone PowerShell bridge. Ship artifact: `rush-ps`. Lets any
JSON-speaking client drive a persistent PowerShell runspace, so
Rush (and anything else that wants to) can script Windows /
Exchange / AD / Azure / PowerShell modules without embedding
PowerShell itself.

Tracking issue: [#267](https://github.com/mhasse1/rush/issues/267).

## Modes

```
rush-ps --bridge     plugin JSON-lines protocol (Rush's plugin.ps wire)
rush-ps --mcp        MCP JSON-RPC 2.0 server (any MCP client)
rush-ps --version
rush-ps --help
```

Same binary, picked at startup. Both share a persistent PowerShell
runspace (see `PsRunner.cs`), so variables and function definitions
survive across calls within a single invocation.

The ship binary is named `rush-ps` (not `rush-ps-bridge`) so Rush's
plugin discovery — which looks for `rush-<name>` on PATH — picks it
up for `plugin.ps ... end` blocks automatically.

## Why .NET

Hosting PowerShell is what `System.Management.Automation` is for.
Writing a PS host in Rust would mean re-implementing the protocol
(large surface, moving target) or pinvoke'ing into .NET anyway.
Keeping this component in .NET and talking to everything else over
JSON is the right split.

## Build

```
dotnet build                              # requires .NET 10 SDK
dotnet publish -c Release -r <rid> -p:PublishSingleFile=true --self-contained
```

Or the cross-platform helper:

```
pwsh ./build.ps1                          # detect RID, build
pwsh ./build.ps1 -Install                 # also copy to ~/bin (Win) or /usr/local/bin (Unix)
```

The self-contained publish produces a ~122 MB single-file binary
named `rush-ps` (or `rush-ps.exe` on Windows).

From the repo root, `install.sh` also auto-publishes + installs the
bridge when .NET 10 is on PATH:

```
./install.sh                              # also installs rush-ps alongside rush
PS_BRIDGE=0 ./install.sh                  # skip bridge even if SDK is present
PS_BRIDGE=1 ./install.sh                  # force bridge build (hard error if SDK missing)
```

## Test

```
dotnet test Tests/
```

Three test files in `Tests/`:

- **`PsRunnerTests.cs`** — unit tests. Exercise `PsRunner` directly
  (no subprocess) — session persistence, stream capture, exception
  trapping, empty-script handling. Fast.
- **`BridgeModeTests.cs`** — integration tests that spawn the built
  binary in `--bridge` mode and drive the plugin JSON-lines protocol
  end-to-end. Requires `dotnet build` has run first.
- **`McpModeTests.cs`** — integration tests that spawn the binary
  in `--mcp` mode and drive the JSON-RPC 2.0 handshake + `tools/call`.

The legacy `dotnet/Rush.Tests/` tested the .NET Rush interpreter
(Lexer, Parser, ScriptEngine, Transpiler, …) — none of that applies
to the bridge, so we start fresh rather than salvage.

## Using from Rush

### As a plugin (`plugin.ps ... end` blocks)

Install the binary on PATH as `rush-ps`. Rush's plugin discovery
picks it up automatically. Blocks like:

```rush
server = "host1"
plugin.ps
  Get-Service -ComputerName #{server} | Where-Object { $_.Status -eq "Running" }
end
```

work with no additional configuration.

### As an MCP server (`mcp(...)` builtin)

Add to `~/.config/rush/mcp-servers.json`:

```jsonc
{
  "mcpServers": {
    "ps": {
      "type": "stdio",
      "command": "/usr/local/bin/rush-ps",
      "args": ["--mcp"],
      "env": {}
    }
  }
}
```

Then from Rush:

```rush
result = mcp("ps", "invoke", script: "Get-Service | Where-Object { $_.Status -eq 'Running' }")
puts result
```

Other MCP clients (Claude Desktop, Claude Code) can use the same
binary with their usual registration flows.

## Layout

| File | Role |
|---|---|
| `Program.cs` | Entry point, CLI flag dispatch |
| `PsRunner.cs` | Shared PowerShell-invocation layer with persistent runspace |
| `BridgeMode.cs` | `--bridge` plugin-protocol loop |
| `McpMode.cs` | `--mcp` JSON-RPC loop |
| `rush-ps-bridge.csproj` | Project file (ships `rush-ps` binary via AssemblyName) |
| `global.json` | .NET SDK pin (10.0.100) |
| `build.ps1` | Cross-platform pwsh build + install helper |
| `Tests/` | xunit test project |

The legacy `dotnet/` tree (Rush .NET interpreter) is still present
for reference and will be retired in Phase 6 of #267 once the bridge
is proven in daily use.

# Rush: .NET → Rust Migration Guide for Claude Code

You've been using Rush built on C#/.NET with PowerShell as the execution engine. Rush has been rewritten in Rust as a native interpreter. This document covers what changed.

## The big change

**PowerShell is no longer the execution engine.** Rush no longer transpiles to PowerShell. It has its own evaluator that executes Rush syntax directly, and shell commands go through `fork/exec` (Unix) or `cmd.exe` (Windows).

PowerShell is now available as an optional **plugin** — a separate .NET binary (`rush-ps`) that Rush talks to over JSON when you use `plugin.ps...end` blocks.

## What's the same

- All Rush syntax: variables (`x = 42`), string interpolation (`"#{x}"`), arrays, hashes, control flow, functions, classes, enums, error handling
- Pipeline operators: `where`, `select`, `sort`, `objectify`, `as json`, `from csv`, etc.
- File/Dir/Time stdlib with the same method names
- Config location: `~/.config/rush/` (config.json, init.rush, secrets.rush)
- `rush --llm` JSON wire protocol (same contract)
- MCP servers (`rush --mcp`, `rush --mcp-ssh`)

## What's different

| .NET Rush | Rust Rush |
|-----------|-----------|
| Transpiles Rush → PowerShell | Native tree-walking interpreter |
| Shell commands via PowerShell pipeline | `fork/exec` (Unix), `cmd.exe` fallback (Windows) |
| `ps { Get-Service }` | `plugin.ps; Get-Service; end` |
| `ps5 { }` blocks | `plugin.ps; ...; end` (same) |
| `.NET` methods on objects | Not available — use Rush stdlib |
| PowerShell cmdlets always available | Only inside `plugin.ps...end` blocks |
| Requires .NET runtime | ~5MB static binary, no runtime |
| ~200ms startup | ~10ms startup |
| Windows-first, Unix via PowerShell | Unix-native, Windows supported |

## Syntax changes

### PowerShell blocks
```rush
# OLD (.NET)
ps { Get-Process | Sort-Object CPU -Descending | Select-Object -First 5 }

# NEW (Rust) — requires rush-ps companion binary on PATH
plugin.ps
  Get-Process | Sort-Object CPU -Descending | Select-Object -First 5
end
```

### Shell commands in functions
```rush
# This now works — Rush detects shell commands in function bodies
def deploy(env)
  rsync -avz ./dist/ "#{env}.example.com:/var/www/"
  echo "deployed to #{env}"
end
```

The evaluator triages each line: Rush syntax is evaluated directly, shell commands are fork/exec'd. Variable expansion (`#{env}`) works in both.

### Cross-platform paths
```rush
# Backslashes from Windows env vars are auto-normalized to /
puts $HOME        # C:/Users/mark (not C:\Users\mark)

# If you need native Windows paths:
path = $HOME.native_path   # C:\Users\mark
```

## Plugin system

Generic mechanism, not PowerShell-specific:

```rush
plugin.ps          # Finds rush-ps on PATH or ~/.config/rush/plugins/
  Get-Service
end

plugin.python      # Would find rush-python
  import sys
  print(sys.version)
end
```

Discovery: `rush-NAME` binary on PATH → `~/.config/rush/plugins/rush-NAME`. Companion speaks JSON lines on stdio (same protocol as `rush --llm`).

## AI integration

Same providers, slightly different invocation:
```rush
ai "how do I find large files?"
cat error.log | ai "what went wrong?"
git diff | ai "review this"
```

Config: `set ai_provider anthropic` / `openai` / `gemini` / `ollama`

## MCP setup

```bash
rush install mcp --claude    # registers rush-local and rush-ssh servers
```

The MCP servers expose `rush_execute`, `rush_read_file`, `rush_context` tools and a `rush://lang-spec` resource.

## Build and install

```bash
cargo build --release
sudo cp target/release/rush-cli /usr/local/bin/rush
# or
./install.sh
```

Requires Rust toolchain. No .NET required for the base shell. `rush-ps` plugin requires .NET 10 if you want PowerShell blocks.

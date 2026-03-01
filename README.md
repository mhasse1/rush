# Rush

Unix-style commands on the PowerShell 7 engine.

Rush gives you the Unix CLI you know — `ls`, `grep`, `cat`, `ps`, pipes, `&&`/`||` — powered by PowerShell 7's structured object pipeline. One shell that speaks both languages.

## Quick Start

```bash
# Build
dotnet build

# Run
dotnet run

# Publish self-contained binary
dotnet publish -c Release -r osx-arm64
dotnet publish -c Release -r linux-x64
dotnet publish -c Release -r win-x64
```

## What It Does

| You type | Rush runs |
|----------|-----------|
| `ls -la` | `Get-ChildItem -Force` |
| `ps \| grep dotnet` | `Get-Process \| Where-Object { $_ -cmatch 'dotnet' }` |
| `cat log.txt \| head -20` | `Get-Content log.txt \| Select-Object -First 20` |
| `ps \| where CPU > 10` | `Get-Process \| Where-Object { $_.CPU -gt 10 }` |
| `ps \| .ProcessName` | `Get-Process \| ForEach-Object { $_.ProcessName }` |
| `ps \| as json` | `Get-Process \| ConvertTo-Json -Depth 5` |
| `json config.json \| .settings` | `Get-Content config.json \| ConvertFrom-Json \| ForEach-Object { $_.settings }` |

Full PowerShell syntax works too — Rush translates what it recognizes and passes everything else through.

## Features

**Commands** — `ls`, `cat`, `cp`, `mv`, `rm`, `mkdir`, `touch`, `ps`, `kill`, `grep`, `echo`, `find`, `head`, `tail`, `sort`, `wc`, `uniq`, `env`, `which`, `whoami`, `hostname`, `df`, `curl`, `wget`

**Pipes** — Unix-style pipelines that translate to PowerShell's object pipeline:
- `grep` filters with regex (`-cmatch` / `-match`)
- `head`/`tail` → `Select-Object -First`/`-Last`
- `sort` → `Sort-Object` (with `-r` for descending)
- `wc -l` → `Measure-Object -Line`
- `uniq` → `Select-Object -Unique`

**Data Pipeline** — filter, select, and format structured data:
- `where CPU > 10` — property filtering with Unix operators
- `select Id, Name` — property selection
- `as json` / `as csv` / `as table` — format output
- `from json` / `from csv` — parse input
- `json file.json` — read and parse JSON files
- `.property` — dot-notation for property access

**Shell Features:**
- Vi and Emacs line editing modes
- Tab completion (paths + commands)
- Ctrl+R reverse history search
- Fish-style autosuggestions (ghost text)
- Persistent history across sessions
- Syntax highlighting as you type
- `cd -` to toggle previous directory
- `!!` and `!$` bang expansion
- `&&` / `||` command chaining
- `>` / `>>` output redirection
- `$HOME` environment variable expansion
- Git-aware prompt with branch display
- Command timing for slow commands (>500ms)
- "Did you mean?" suggestions on typos
- Configurable via `~/.config/rush/config.json`
- Startup script at `~/.config/rush/init.rush`

## Configuration

```json
// ~/.config/rush/config.json
{
  "editMode": "vi",
  "aliases": {
    "ll": "ls -la",
    "g": "git"
  }
}
```

## CLI

```
rush                 Start interactive shell
rush -c 'command'    Execute command and exit
rush --version       Show version
rush --help          Show help
```

## Requirements

- .NET 8 SDK (build) or self-contained publish (no runtime needed)
- Works on macOS, Linux, and Windows

## Architecture

Rush embeds the PowerShell 7 engine (MIT-licensed `Microsoft.PowerShell.SDK`) as a .NET library. It provides a custom `PSHost` implementation for the REPL, translates Unix commands to PowerShell cmdlets, and renders structured `PSObject` output in clean terminal formats.

Key components:
- `CommandTranslator` — maps Unix commands + flags to PowerShell cmdlets
- `LineEditor` — vi/emacs line editing with history, tab completion, autosuggestions
- `OutputRenderer` — type-aware output formatting (ls-style, tables, plain text)
- `SyntaxHighlighter` — real-time ANSI colorization
- `Prompt` — git-aware prompt rendering
- `TabCompleter` — path + command completion via PowerShell's `CommandCompletion` API

## License

Proprietary. All rights reserved.

# Rush

Unix-style commands on the PowerShell 7 engine.

Rush gives you the Unix CLI you know — `ls`, `grep`, `cat`, `ps`, pipes, `&&`/`||` — powered by PowerShell 7's structured object pipeline. One shell that speaks both languages.

## Quick Start

```bash
# Build
dotnet build

# Run
dotnet run

# Publish self-contained binary (no .NET runtime needed)
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
| `json config.json \| .settings` | `Get-Content config.json \| ConvertFrom-Json \| ...` |
| `ls \| count` | `Get-ChildItem \| Measure-Object \| ForEach-Object { $_.Count }` |
| `ps \| sum WorkingSet64` | `Get-Process \| Measure-Object -Property WorkingSet64 -Sum \| ...` |

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
- `where CPU > 10` — property filtering with Unix operators (`>`, `<`, `=`, `!=`, `~`)
- `select Id, Name` — property selection
- `count` — count items in pipeline
- `first 5` / `last 3` / `skip 2` — slice results
- `distinct` — unique values (works on unsorted data)
- `sum` / `avg` / `min` / `max` — math aggregations on properties
- `tee output.txt` — save to file while passing through
- `as json` / `as csv` / `as table` / `as list` — format output
- `from json` / `from csv` — parse input
- `json file.json` — read and parse JSON files
- `.property` — dot-notation for property access

**Shell Features:**
- Vi and Emacs line editing modes
- Tab completion (paths + commands)
- Ctrl+R reverse history search
- Persistent history across sessions
- Real-time syntax highlighting as you type
- Color-coded `ls` output by file type
- Human-readable process memory display
- PATH management (`path add`, `path rm`, `path check`, `path dedupe`, `path edit`, `path add...end` blocks)
- Terminal background color (`set bg "#hex"`) with contrast-aware palette generation
- `cd -` to toggle previous directory
- `~` tilde expansion to home directory
- `!!` and `!$` bang expansion
- `!N` run Nth command from history
- `&&` / `||` / `;` command chaining
- `>` / `>>` output redirection
- `$HOME` environment variable expansion
- `export FOO=bar` / `unset FOO` for env vars
- `alias ll='ls -la'` for interactive alias definition
- `source file.rush` for running rush scripts
- Git-aware prompt with branch display
- Command timing for slow commands (>500ms)
- "Did you mean?" suggestions on typos
- Configurable via `~/.config/rush/config.json`
- Startup script at `~/.config/rush/init.rush`
- LLM agent mode (`rush --llm`) — JSON wire protocol for AI-driven automation

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
rush script.rush     Execute a Rush script
rush --llm           LLM agent mode (JSON wire protocol, runs init.rush)
rush --llm --inherit /path/state.json   LLM mode with inherited session state
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
- `LineEditor` — vi/emacs line editing with history, tab completion
- `OutputRenderer` — type-aware output formatting (colorized ls, process tables, generic tables)
- `SyntaxHighlighter` — real-time ANSI colorization of commands, flags, strings, operators
- `Prompt` — git-aware prompt rendering with exit code indication
- `TabCompleter` — path + command completion via PowerShell's `CommandCompletion` API
- `LlmMode` — JSON wire protocol for machine-to-machine operation (`rush --llm`)

## License

Business Source License 1.1 (BSL). Free for non-commercial use. Commercial use requires a paid license. Each version converts to Apache 2.0 after four years. See [LICENSE](LICENSE) for details.

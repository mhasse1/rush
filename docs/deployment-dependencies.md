# Rush Deployment Dependencies

What rush handles internally vs what must be in PATH.

---

## Rush Builtins (built into rush binary)

These are .NET implementations — no external dependency needed.

| Command | Purpose | File |
|---------|---------|------|
| `ai` | LLM assistant (Anthropic/OpenAI/Gemini/Ollama) | AiCommand.cs |
| `alias`/`unalias` | Shell aliases | Program.cs |
| `cat` | File concatenation (`-n`, stdin, concat) | CatCommand.cs |
| `cd` | Change directory (with `cd -`) | Program.cs |
| `clear` | Clear screen | Program.cs |
| `dirs`/`pushd`/`popd` | Directory stack | Program.cs |
| `exec` | Replace process | Program.cs |
| `exit`/`quit` | Exit shell | Program.cs |
| `export`/`unset` | Environment variables | Program.cs |
| `help` | Builtin help | Program.cs |
| `history` | Command history (`-c` to clear) | Program.cs |
| `init` | Edit init.rush in $EDITOR | Program.cs |
| `jobs`/`fg`/`bg`/`wait` | Job control | Program.cs |
| `path` | PATH management (add/remove/list/edit) | Program.cs |
| `printf` | Formatted output | Program.cs |
| `read` | Read stdin into variable | Program.cs |
| `reload` | Hot-reload config (`--hard` for full) | Program.cs |
| `set` | Settings (edit mode, theme, AI, etc.) | Program.cs |
| `source`/`.` | Run rush scripts | Program.cs |
| `sql` | SQLite/PostgreSQL/ODBC queries | SqlCommand.cs |
| `sync` | Config sync (git/ssh/path) | Program.cs |
| `trap` | Signal handlers | Program.cs |
| `which`/`type` | Command lookup (checks builtins first) | Program.cs |

## Pipeline Operators (translated to PowerShell)

These are rush-specific — always available via CommandTranslator.

| Operator | Translation |
|----------|-------------|
| `where` | `Where-Object { $_.Prop -op Value }` |
| `select` | `Select-Object -Property ...` |
| `count` | `Measure-Object \| ForEach-Object { $_.Count }` |
| `first [N]` | `Select-Object -First N` |
| `last [N]` | `Select-Object -Last N` |
| `skip N` | `Select-Object -Skip N` |
| `sort [Prop]` | `Sort-Object [-Property Prop]` (after pipe) |
| `distinct` | `Sort-Object -Unique` |
| `sum`/`avg`/`min`/`max` | `Measure-Object -Property ... -Sum/Average/etc.` |
| `as json\|csv\|table\|list` | `ConvertTo-Json/Csv`, `Format-Table/List` |
| `from json\|csv` | `ConvertFrom-Json/Csv` |
| `tee file` | `Tee-Object -FilePath file` |
| `grep` (after pipe) | `Where-Object { $_ -cmatch 'pattern' }` |
| `head [N]` (after pipe) | `Select-Object -First N` |
| `tail [N]` (after pipe) | `Select-Object -Last N` |
| `wc -l` (after pipe) | `Measure-Object -Line` |
| `uniq` (after pipe) | `Select-Object -Unique` |
| `.Property` | `ForEach-Object { $_.Property }` |
| `objectify` | Text → structured objects |
| `json file` | `Get-Content file \| ConvertFrom-Json` |

## Native Commands (must be in PATH)

Rush does **not** bundle these. They run via the OS.

### Required on all platforms

| Command | Notes |
|---------|-------|
| `ls` | Directory listing (was a rush builtin, now native) |
| `grep` | Pattern search (was translated, now native) |
| `find` | File search (was translated, now native) |
| `cp` | Copy files |
| `mv` | Move/rename files |
| `rm` | Remove files |
| `touch` | Create/update timestamps |
| `mkdir` | Create directories (rush also has `Dir.mkdir`) |
| `head` | First N lines of file |
| `tail` | Last N lines of file |
| `sort` | Sort lines (standalone; after-pipe uses PowerShell) |
| `ps` | Process list |
| `kill` | Kill process by PID |
| `pwd` | Print working directory |
| `env` | Environment listing |
| `whoami` | Current username |
| `hostname` | System hostname |
| `df` | Disk usage |
| `uptime` | System uptime |
| `curl` | HTTP client |
| `wget` | HTTP download |

### macOS / Linux

All commands above ship with the OS. No bundling needed.

### Windows

PowerShell 7 provides built-in aliases for common commands:
`ls`→`Get-ChildItem`, `cp`→`Copy-Item`, `mv`→`Move-Item`, `rm`→`Remove-Item`,
`mkdir`→`New-Item`, `cat`→`Get-Content`, `pwd`→`Get-Location`, `kill`→`Stop-Process`,
`ps`→`Get-Process`, `sort`→`Sort-Object`, `clear`→`Clear-Host`

**Not covered by PS7 aliases** (need bundling or PATH):
`grep`, `find`, `head`, `tail`, `touch`, `env`, `whoami`, `hostname`,
`df`, `uptime`, `curl` (PS7 has `Invoke-WebRequest` alias but different behavior),
`wget` (same issue)

Options for Windows:
1. Bundle [BusyBox-w64](https://frippery.org/busybox/) (~500KB, covers most)
2. Require Git for Windows (includes grep, find, head, tail, sort, etc.)
3. Rely on PS7 aliases for basics, document gaps

## Native Command Colors (auto-configured)

Rush detects the terminal background (dark/light) at startup and sets
color environment variables so native commands produce readable output:

| Variable | Purpose | Dark theme | Light theme |
|----------|---------|------------|-------------|
| `LS_COLORS` | GNU `ls` colors | Bold (bright on dark bg) | Non-bold (darker shades) |
| `LSCOLORS` | BSD `ls` colors (macOS) | Uppercase = bold | Lowercase = plain |
| `GREP_COLORS` | `grep` match colors | Bold red matches | Plain red matches |
| `CLICOLOR` | Enable BSD `ls` color | `1` (macOS only) | `1` (macOS only) |

- **Respects user values** — if already set in `.bashrc`/`.zshrc`, rush won't overwrite
- **Respects `NO_COLOR`** — if set, no color vars are injected
- **Live updates** — `set theme light`/`dark` or `reload` updates these immediately
- **Implementation** — `Theme.SetNativeColorEnvVars()` in Theme.cs

---

## Dead Code

| File | Status |
|------|--------|
| `FileListCommand.cs` | No longer intercepted — can be deleted |

---

## Runtime Dependencies

| Dependency | Required | Notes |
|------------|----------|-------|
| .NET 8 Runtime | Bundled | Self-contained publish includes it |
| PowerShell 7 SDK | Bundled | Microsoft.PowerShell.SDK NuGet |
| SQLite | Optional | System.Data.SQLite — bundled in binary |
| PostgreSQL | Optional | Npgsql — bundled in binary |
| ODBC | Optional | System.Data.Odbc — requires system ODBC driver |

# Changelog

All notable changes to the Rush shell project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [0.9.0-beta] - 2026-03-20

### Added

- **Classes and OOP**: Full class support with inheritance, static methods, enums, typed attributes, default attribute values, named args at `.new()`, regex literals, and type-aware dot-completion
- **Ternary operator**: `condition ? then : else` inline expressions (#23)
- **Exclusive range operator**: `1...4` produces `[1, 2, 3]` (#24)
- **PowerShell interop**: Static member access via `[Math]::PI` syntax (#26)
- **AI integration**: `ai` builtin with `--exec` mode, `--agent` autonomous mode (Anthropic and Gemini), `--verbose`/`--debug` flags, and custom AI provider support
- **LLM wire protocol**: Machine-to-machine JSON protocol (`rush --llm`) for tool-use agents
- **MCP server mode**: `rush --mcp` for persistent sessions via Claude Code; `rush install mcp --claude` for registration
- **MCP-SSH gateway**: Persistent `rush --llm` sessions per remote host with raw-shell fallback and SSH connection pooling via ControlMaster
- **Platform blocks**: `macos`, `win64`, `win32`, `linux` conditional blocks with property conditions (e.g., `macos.version >= "15"`)
- **Path management**: `path add...end` / `path rm...end` declarative block syntax, `path check` with duplicate flagging, `path dedupe`, `path --name=VARNAME`, `export --save`
- **SQL command**: Native `sql` builtin with SQLite, Postgres, and ODBC drivers
- **Auto-theming**: Contrast-aware color system that validates colors against terminal background; `setbg` command with `--selector` in-terminal color picker (#18); `.rushbg` directory-based auto-theming; root shell background tinting
- **Fish-style autosuggestions**: Ghost text from history with Alt+Right partial word acceptance (#2)
- **Editor features**: Edit-in-editor (`v` in vi mode, `Ctrl+X Ctrl+E` in emacs), auto-indent for multi-line blocks, auto-outdent for `end` keyword, floating hints
- **Vi mode improvements**: Full readline compliance, yank/undo, Backspace behavior fixes
- **Hot reload**: `reload --hard` with full state serialization and restore
- **Backtick command substitution**: `` `command` `` syntax in scripts (#28)
- **Config sync**: Multi-transport sync via SSH, filesystem path, and git with conflict detection
- **Self-documenting config**: `secrets.rush` file support, `init` builtin
- **Objectify**: Pipe transform for structured data, auto-objectify for known commands
- **Cat builtin**: Bypasses PowerShell `Get-Content` for correct behavior
- **`isssh...end` block**: Detect and branch on SSH sessions
- **Compound assignment operators**: `*=` and `/=`
- **Cross-platform builds**: Windows ARM64 cross-build, Linux single-file publish with self-extraction
- **Stale binary warning**: Prompt indicator after `install.sh` when running an older binary

### Fixed

- **Theme and contrast**: Fixed illegible text on light backgrounds including Solarized Light, light gray, and pure white (#14, #15, #16); fixed LS_COLORS/GREP_COLORS contrast validation against actual terminal background
- **Autosuggestion ghost text**: Fixed ghost text lingering on screen after pressing Enter (#22)
- **History search**: Fixed to search newest-first and handle empty queries (#38)
- **Alias execution**: Fixed aliases running through PowerShell instead of natively (#36, #37)
- **String escape sequences**: `\n`, `\t`, `\e`, `\a`, `\r`, `\0`, `\\` now work correctly in strings (#31)
- **`find` and glob arguments**: Fixed quoted glob args not reaching native commands like `find` (#17)
- **Class method output**: Fixed `puts` producing no output inside class methods in `rush -c` (#27)
- **Export in scripts**: Fixed `export` not supporting variable values (#29)
- **String `.split()`**: Fixed lowercase `.split()` returning char array instead of string array (#32)
- **Variable name conflicts**: Fixed variable names matching shell builtins being rejected as assignments (#34)
- **`env.VAR` assignment**: Fixed assignment failing for underscored variable names (#33)
- **Multi-line arrays**: Fixed nested array access and multi-line array literals in script files (#30)
- **Cursor positioning**: Fixed cursor landing mid-screen after terminal resize (#5); drained stale input before prompt
- **Shell redirections**: Fixed `2>/dev/null` for native commands; preserved redirection operators in CommandTranslator
- **Ctrl+C handling**: Fixed for all builtins, not just PowerShell pipelines; fixed not cancelling multiline block input
- **Exit hang**: Fixed intermittent hang on exit by explicitly closing PowerShell runspace
- **OSC 11 detection**: Multiple fixes for iTerm2 compatibility; removed unreliable detection in favor of `RUSH_BG` persistence
- **Table display**: Fixed truncated columns and last-column overflow
- **Tilde expansion**: Fixed `=~` and `!~` operators
- **Child process CWD**: Synced `Environment.CurrentDirectory` at startup and after commands
- **`reload --hard`**: Fixed variable types (unwrap JsonElement to native types) and terminal inheritance
- **Tab completion**: Fixed completing from history instead of filesystem; fixed quoting
- **`rush -c` mode**: Fixed command substitution with Rush stdlib; fixed chain operators; injected Rush env vars for platform blocks
- **Printf**: Added `\e`, `\a`, `\r` escape sequences

### Changed

- **Native command execution**: Unix commands (ls, grep, find, etc.) now run natively on macOS/Linux instead of being translated to PowerShell equivalents
- **Removed ls builtin**: Replaced with native `ls`; removed Windows command translations
- **Background color management**: Moved from runtime OSC detection to `set bg` config setting for reliable color-from-first-output behavior; `bg` defaults to "off"
- **Dir.list**: Returns basenames instead of relative paths (#35); unified from separate `Dir.files`/`Dir.dirs`; added symbol flags and script-friendly output (#4)
- **Shared command dispatch**: Extracted `ProcessCommand`/`ShellState`/`ExecuteTranspiledBlock` so `init.rush` supports all builtins
- **Color selector**: Improved palettes with better visual distinction and gaps between swatches
- **Theme detection warning**: Only shown when all detection methods fail

### Documentation

- Updated user manual with platform blocks, sql command, auto-theming, UNC paths, native commands, AI providers, and config sync
- Added bash-to-rush cheat sheet
- Documented class inheritance, static methods, enums, and MCP server setup
- Added rush language specification (`rush-lang-spec.yaml`) optimized for LLM consumption
- Updated docs to remove Ruby references (#7) and reflect current Dir.list API (#6, #10)
- Documented fish-style autosuggestions (#11), multi-line `rush -c` behavior (#13), and `*=`/`/=` operators (#12)
- Added SSH requirements documentation and deployment dependencies guide
- Added 800+ tests including user-manual doc tests, remote Linux test suites, and auto-theme visual test suite (#8, #9)

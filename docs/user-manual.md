# Rush User Manual

> **Version 0.9.x (beta)** — A modern shell with clean, readable syntax on PowerShell 7

---

## Table of Contents

1. [Overview](#overview)
2. [Installation](#installation)
3. [Configuration](#configuration)
4. [The REPL](#the-repl)
5. [Shell Features](#shell-features)
6. [Objectify & Auto-Objectify](#objectify--auto-objectify)
7. [Scripting Language](#scripting-language)
8. [Platform Blocks](#platform-blocks)
9. [Standard Library](#standard-library)
10. [Built-in Commands](#built-in-commands)
11. [The `ls` Builtin](#the-ls-builtin)
12. [The `cat` Builtin](#the-cat-builtin)
13. [The `sql` Command](#the-sql-command)
14. [Command Translations](#command-translations)
15. [Built-in Variables](#built-in-variables)
16. [LLM Mode](#llm-mode)
17. [MCP Server Mode](#mcp-server-mode)
18. [Tips & Tricks](#tips--tricks)

---

## Overview

Rush is a Unix shell that combines Bash's pipeline and process model with clean, readable syntax. Under the hood, Rush code transpiles to PowerShell 7, giving you access to the full .NET runtime while writing natural, expressive commands.

**Design principles:**

- Shell commands are first-class — `ls -la /tmp` just works
- No sigils for variables — `name = "mark"`, not `$name`
- String interpolation uses `#{expr}`
- Block keywords use `end`, not braces — `if`/`end`, `def`/`end`
- Braces `{ }` are for lambdas and blocks only
- Pipelines work for both shell commands and objects
- AI integration is optional — Rush is a full shell with or without API keys

---

## Installation

### Prerequisites

- .NET 8 SDK
- macOS (arm64) or Linux

### Build from Source

```bash
export PATH="/opt/homebrew/opt/dotnet@8/bin:$PATH"
export DOTNET_ROOT="/opt/homebrew/opt/dotnet@8/libexec"
dotnet publish -c Release -r osx-arm64
```

### Install

```bash
./install.sh
```

This copies the self-contained binary to `/usr/local/lib/rush/` and creates a symlink at `/usr/local/bin/rush`. No runtime dependencies needed.

### Set as Login Shell (Optional)

```bash
chsh -s /usr/local/bin/rush
```

### Command-Line Usage

```
rush                     Start interactive shell
rush script.rush         Execute a Rush script
rush -c 'command'        Execute command and exit
rush --llm               LLM agent mode (JSON wire protocol)
rush --mcp               MCP server mode (local stdio)
rush --mcp-ssh           MCP server mode (SSH gateway)
rush install mcp --claude  Register Rush MCP server in Claude Code
rush --version (-v)      Show version
rush --help (-h)         Show help
```

#### Multi-line `rush -c`

`rush -c` supports multi-line scripts. All lines are checked for Rush syntax (not just the first line), and mixed Rush/shell lines work correctly — Rush lines are transpiled while shell lines pass through:

```bash
# Mixed Rush and shell in one invocation
rush -c $'export FOO=bar\nputs env.FOO'

# Multi-line Rush script
rush -c $'name = "world"\nputs "hello #{name}"'
```

Use `$'...'` in your calling shell to interpret `\n` as newlines.

**Escape sequences in strings:** Rush double-quoted strings support backslash escapes — `\n` (newline), `\t` (tab), `\e` (escape/ANSI), and `\a` (bell) are expanded at runtime.

---

## Configuration

### Config Directory

All configuration lives in `~/.config/rush/`:

| File | Purpose |
|------|---------|
| `config.json` | Settings and saved aliases (managed by `set`/`alias --save`) |
| `init.rush` | Startup script — PATH, exports, functions, prompt |
| `secrets.rush` | API keys and tokens (not synced) |
| `history` | Persistent command history |
| `sync.json` | Config sync settings |

### config.json

Created automatically on first run. All settings with their defaults:

```json
{
  "editMode": "vi",
  "historySize": 500,
  "bg": "off",
  "theme": "auto",
  "promptFormat": "default",
  "showTiming": true,
  "showTips": true,
  "showHints": true,
  "stopOnError": false,
  "pipefailMode": false,
  "traceCommands": false,
  "strictGlobs": false,
  "completionIgnoreCase": true,
  "contrast": "standard",
  "rootBackground": "auto",
  "aiProvider": "anthropic",
  "aiModel": "auto",
  "aliases": {}
}
```

| Setting | Values | Description |
|---------|--------|-------------|
| `editMode` | `"vi"`, `"emacs"` | Line editing mode |
| `historySize` | `10`–`∞` (default `500`) | Max history entries |
| `bg` | `"#hex"`, `"off"` | Terminal background color for palette |
| `theme` | `"auto"`, `"dark"`, `"light"` | Color theme (`auto` detects terminal) |
| `promptFormat` | `"default"` | Prompt style |
| `showTiming` | `true`, `false` | Show elapsed time for commands > 500ms |
| `showTips` | `true`, `false` | Show a rotating tip on startup |
| `showHints` | `true`, `false` | Show contextual hints during editing — surfaces lesser-known features so you can use them today |
| `aiProvider` | `"anthropic"`, `"openai"`, etc. | AI provider for the `ai` command |
| `aiModel` | `"auto"`, model name | AI model (`auto` = provider default) |

### Startup Script

**`init.rush`** runs on every shell launch, fully transpiled through the Rush engine. Everything goes here — exports, aliases, functions, and prompt customization:

```rush
# ~/.config/rush/init.rush
export PATH="/opt/homebrew/bin:$PATH"
export EDITOR=vim

alias ll='ls -la'
alias g='git'

def rush_prompt()
  time = Time.now.ToString("HH:mm")
  dir = pwd
  "#{time} #{dir} > "
end
```

A commented default is created on first run. Custom prompts have access to context variables: `$exit_code`, `$exit_failed`, `$is_ssh`, `$is_root`, and `$needs_reload` (true when the binary has been updated since startup).

### Colors & Theming

**Important:** Contrast-aware theming requires `set --save bg "#hex"` to be set to your terminal's background color. Without it, Rush cannot validate color contrast and text may be hard to read. Set it once and Rush handles the rest.

All Rush UI colors (prompt, syntax highlighting, file types, `ls`, `grep`) are validated against a WCAG 3:1 minimum contrast ratio when `bg` is set.

Rush also configures native command colors automatically:

| Variable | Purpose |
|----------|---------|
| `LS_COLORS` | GNU `ls` colors (Linux, Homebrew coreutils) |
| `LSCOLORS` | BSD `ls` colors (macOS) |
| `GREP_COLORS` | `grep` match highlighting |
| `CLICOLOR` | Enable BSD `ls` color (macOS only) |

**Dark terminals** get bold/bright colors. **Light terminals** get non-bold/darker shades.

```rush
set theme dark              # Force dark palette
set theme light             # Force light palette
set theme auto              # Auto-detect (default)
set --save theme light      # Persist to config.json
```

**Exact background color:** Use `set bg` to tell Rush your terminal's exact background color. This enables precise 256-color palette generation for maximum readability. Without it, Rush falls back to basic dark/light detection with 16-color palettes.

```rush
set bg "#222733"           # Tell Rush your exact background color
set --save bg "#222733"    # Persist across sessions
setbg "#222733"            # Shorthand (same as set bg)
setbg --save "#222733"     # Shorthand with persist
set bg off                 # Turn off (let terminal control background)
```

If you've customized `LS_COLORS` or `GREP_COLORS` in your shell profile, Rush respects your values.
Set `NO_COLOR=1` to disable all color output.

### Reloading

Run `reload` to re-read all config files without restarting Rush.

The `init` command opens init.rush in `$EDITOR` and reloads automatically after you save:

```rush
init       # opens ~/.config/rush/init.rush in $EDITOR, then reloads
reload     # re-read config files (settings, theme, aliases)
```

---

## The REPL

### Prompt

The default prompt shows:

```
✓ 14:32  mark@macbook  rush/src  main*
  █
```

Components (left to right):
- **Exit status**: ✓ (success) or ✗ + exit code (failure)
- **Time**: 24-hour HH:mm
- **User@host**: highlights differently when connected via SSH
- **Path**: shortened to last 2 directory levels (`~` for home)
- **Git branch**: with `*` suffix if there are uncommitted changes
- **Stale indicator**: `[stale]` in yellow when the binary has been updated — run `reload --hard` to restart

### Vi Mode (Default)

Rush defaults to vi-style line editing.

**Insert mode** (type normally):

| Key | Action |
|-----|--------|
| `Esc` | Switch to normal mode |
| `Tab` | Complete paths and commands |
| `Ctrl+C` | Cancel current line |
| `Ctrl+R` | Reverse history search |

**Normal mode** (after pressing Esc):

| Key | Action |
|-----|--------|
| `h` / `l` | Move left / right |
| `w` / `b` / `e` | Word forward / back / end |
| `0` / `$` | Beginning / end of line |
| `i` / `a` | Insert before / after cursor |
| `I` / `A` | Insert at beginning / end of line |
| `x` | Delete character |
| `d` + motion | Delete (e.g., `dw` delete word, `d$` delete to end) |
| `c` + motion | Change (delete + enter insert mode) |
| `D` | Delete to end of line |
| `C` | Change to end of line |
| `y` + motion | Yank (copy) |
| `p` | Paste |
| `u` | Undo |
| `f`/`F` + char | Find character forward / backward |
| `j` / `k` | Next / previous history entry |
| `v` | Open current input in `$EDITOR` (multi-line editing) |
| `3w` | Count prefix — move 3 words forward |

### Emacs Mode

Switch with `set emacs`.

| Key | Action |
|-----|--------|
| `Ctrl+A` | Beginning of line |
| `Ctrl+E` | End of line |
| `Ctrl+K` | Kill to end of line |
| `Ctrl+U` | Kill to beginning of line |
| `Ctrl+W` | Kill previous word |
| `Ctrl+Y` | Yank (paste) killed text |
| `Alt+B` | Back one word |
| `Alt+F` | Forward one word |
| `Alt+D` | Delete word forward |

### Edit in $EDITOR

During multi-line input (after 3+ continuation lines), Rush shows a hint:

```
# (esc v → $EDITOR)
```

Press **Esc v** (vi normal mode) to open the entire block in your `$EDITOR`. The code is properly indented in the editor for readability. After saving and quitting, Rush executes the result. Quit without saving (`:q!` in vim) to cancel.

**Hints** are not training wheels — they surface lesser-known features so you can take advantage of them today. Disable them with:

```rush
set --save showHints false
```

### Tab Completion

Tab completes based on context:

- **First token**: commands (builtins, translated commands, PATH binaries)
- **After `cd` / `pushd`**: directories only
- **After `-`**: flags (context-aware per command)
- **After `$`**: environment variables
- **Otherwise**: files and directories (with `/` suffix for dirs)

Press Tab multiple times to cycle through matches.

### History

Commands are saved to `~/.config/rush/history` (persistent across sessions).

| Command | Action |
|---------|--------|
| `history` | Show last 50 entries (numbered) |
| `history -c` | Clear all history |
| `Ctrl+R` | Interactive reverse search |
| `!N` | Run command number N |
| `!!` | Repeat last command |
| `!$` | Last argument of previous command |
| `j` / `k` (vi normal) | Browse history |

### Multi-Line Editing

Block-opening keywords automatically continue to the next line:

```rush
if count > 10
  puts "many"       # ← continuation line
end                  # ← block closes
```

This works for `if`, `unless`, `for`, `while`, `until`, `loop`, `def`, `begin`, `try`, `case`, `match`, `class`, `enum`.

### Syntax Highlighting

Rush highlights your input as you type: keywords, strings, numbers, operators, commands, and comments are each colored distinctly. The theme (`"auto"`, `"dark"`, `"light"` in config.json) controls the color palette.

### Autosuggestions

Rush provides fish-style autosuggestions based on your command history. As you type, a ghost-text suggestion appears in dim gray at the end of the current line. The suggestion auto-updates on every keystroke, showing the most recent matching history entry.

Ghost text only appears when:
- The cursor is at the end of the line
- The input buffer is non-empty

**Accepting suggestions:**

| Key | Action |
|-----|--------|
| `Right Arrow` | Accept full suggestion |
| `End` | Accept full suggestion |
| `Ctrl+E` | Accept full suggestion |
| `l` (vi normal, at EOL) | Accept full suggestion |
| `$` (vi normal, at EOL) | Accept full suggestion |
| `Alt+Right` | Accept next WORD (partial acceptance) |

Partial acceptance with `Alt+Right` lets you incrementally accept a suggestion word by word, which is useful when the suggested command is close but not exactly what you need.

### Special Keys

| Key | Action |
|-----|--------|
| `Ctrl+C` | Cancel current line, interrupt running commands |
| `Ctrl+D` | Exit Rush (on empty line), EOF for stdin reading |
| `Ctrl+R` | Reverse incremental search |
| `\` (trailing) | Line continuation |

`Ctrl+C` works for all command types: builtins (`cat`, `read`), native commands (`sleep`, `curl`), and PowerShell pipelines. Running processes are killed and Rush returns to the prompt.

---

## Shell Features

### Pipelines

Chain commands with `|`. Objects flow through the pipeline:

```rush
ls /var/log | grep ".log" | head 5
ps | where CPU > 10 | select ProcessName, CPU | as table
```

### Redirections

```rush
ls > files.txt          # Overwrite
echo "line" >> log.txt  # Append
sort < input.txt        # Read from file
cmd 2> errors.txt       # Redirect stderr
cmd 2>&1                # Merge stderr into stdout
```

### Brace Expansion

```rush
echo {a,b,c}            # → a b c
touch file_{1,2,3}.txt  # Creates file_1.txt file_2.txt file_3.txt
echo {1..5}             # → 1 2 3 4 5
```

### Tilde Expansion

```rush
cd ~                    # Home directory
ls ~/Documents          # Home + path
cd ~otheruser           # Another user's home
```

### Globbing

```rush
ls *.txt                # All .txt files
ls file?.log            # Single character wildcard
ls [abc]*.rs            # Character class
```

### Command Substitution

```rush
branch = $(git rev-parse --abbrev-ref HEAD)
echo "on branch: $(git branch --show-current)"
```

### Process Substitution

```rush
diff <(ls dir1) <(ls dir2)
```

### Arithmetic Expansion

```rush
echo $(( 2 + 3 ))       # → 5
echo $(( 10 * 4 / 2 ))  # → 20
```

### Heredocs

```rush
cat <<EOF
Hello #{name},
Welcome to Rush.
EOF
```

### Background Jobs

```rush
sleep 60 &              # Run in background
jobs                    # List background jobs
fg %1                   # Bring job 1 to foreground
kill %1                 # Kill job 1
wait                    # Wait for all background jobs
wait %1                 # Wait for specific job
```

### Chain Operators

```rush
make && make install    # Run second only if first succeeds
cmd || echo "failed"    # Run second only if first fails
cmd1 ; cmd2 ; cmd3      # Run all regardless of exit status
```

### Dot Notation in Pipelines

Access object properties directly in pipeline:

```rush
ps | .ProcessName       # Extract property
json config.json | .settings.theme
```

### UNC SSH Paths

Access remote files directly using `//ssh:` paths — no `scp` or `rsync` needed:

```rush
ls //ssh:server/var/log             # List remote directory
cat //ssh:server/etc/hosts          # Read remote file
cp //ssh:server/data/file.csv .     # Download file
cp local.txt //ssh:server/tmp/      # Upload file
mv //ssh:server/a.txt //ssh:server/b.txt  # Rename remote file
rm //ssh:server/tmp/old.log         # Delete remote file
mkdir //ssh:server/tmp/newdir       # Create remote directory
```

Supports user@ syntax: `//ssh:mark@server/path`. Uses your SSH config (`~/.ssh/config`) for host aliases, keys, and ports.

**Requires Rush on the remote host** — UNC operations run `rush -c` over SSH. Copy operations (`cp`) use `scp`.

### Windows SMB/UNC Paths

On Windows, Rush supports native SMB network paths using forward slashes:

```rush
cd //fileserver/shared/docs        # Navigate to network share
ls //nas/backups/2026/              # List network directory
cp //server/share/file.txt ./      # Copy from network share
```

Rush translates `//server/share` to native Windows UNC (`\\server\share`) transparently. You never type backslashes — Rush handles it.

**How it works:**
- `//server/share/path` → `\\server\share\path` (automatic translation)
- Tab completion works on share subfolders
- Authentication uses your Windows session/domain credentials
- Works with any SMB share accessible from the machine

**Windows only** — on macOS/Linux, SMB shares must be mounted first (see `man mount_smbfs` or `man mount.cifs`). Full cross-platform SMB proxy support is planned.

**Path convention:**

| Syntax | Purpose | Platform |
|--------|---------|----------|
| `//ssh:host/path` | SSH remote file operations | All |
| `//server/share` | Windows SMB network share | Windows |

Both use forward slashes consistently. Rush always displays `/`, never `\`.

---

## Objectify & Auto-Objectify

Native commands like `netstat`, `docker ps`, and `lsof` produce text tables. The `objectify` pipe converts text output into PowerShell objects so they can participate in the object pipeline (`where`, `select`, `sort`, `as json`, etc.).

### Explicit Objectify

Use `objectify` as a pipe to convert text table output into objects:

```rush
# Whitespace-delimited with header row (default)
my-tool | objectify | as json

# Fixed-width columns (auto-detect from header positions)
my-tool | objectify --fixed | where Status == "Active"

# Custom delimiter
my-tool | objectify --delim "," | select name,value

# Skip header lines
free | objectify --skip 1 | as json

# Manual column names
my-tool | objectify --cols "pid,name,cpu" | where cpu > 10
```

**Flags:**

| Flag | Description |
|------|-------------|
| `--delim REGEX` | Field delimiter regex (default: `\s+`) |
| `--fixed` | Fixed-width parsing using header character positions |
| `--fixed 6,13,20` | Fixed-width with explicit column boundary positions |
| `--no-header` | First line is data; auto-generate col1, col2, ... |
| `--cols name,pid` | Manual column names (implies `--no-header`) |
| `--skip N` | Skip first N lines before header |
| `--save` | Persist flags to `~/.config/rush/objectify.rush` |

Numbers are automatically parsed as integers when the value matches `^\d+$`.

Property names in `where`, `select`, and `sort` are case-insensitive: `where PID > 100` and `where pid > 100` are equivalent.

### Auto-Objectify

Known commands automatically inject `objectify` when piped to pipeline operators (`where`, `select`, `sort`, `count`, `first`, `last`, `as json`, etc.). You don't need to type `objectify` for commands Rush already recognizes:

```rush
# These just work — objectify is injected transparently
ps aux | where CPU > 5 | sort CPU
df -h | where Use% > "50%" | select Filesystem,Use%
netstat | where State == "ESTABLISHED" | as json
docker ps | where Status ~ "Up" | select Names
w | select USER,IDLE
who | count
last | where USER == "mark"
kubectl get pods | where STATUS != "Running"

# Standalone commands show raw text (no objectify injected)
netstat
docker ps
```

Known commands include: `ps`, `df`, `w`, `who`, `last`, `netstat`, `ss`, `lsof`, `free`, `mount`, `docker ps`, `docker images`, `kubectl get`, and others.

For commands Rush doesn't know about, use explicit `| objectify |` in the pipeline:

```rush
my-tool | objectify | where Status == "Active" | as json
my-tool | objectify --fixed | select name,value
```

### Config Hierarchy

Auto-objectify hints come from three layers (later overrides earlier):

1. **Built-in defaults** (ships with Rush): `ps`, `df`, `w`, `who`, `last`, `netstat`, `ss`, `lsof`, `free`, `docker ps`, `docker images`, `kubectl get`, `mount`, etc.
2. **System config**: `/etc/rush/objectify.rush`
3. **User config**: `~/.config/rush/objectify.rush`

You can customize auto-objectify behavior for any command by editing `~/.config/rush/objectify.rush`. Config file format (command and flags separated by 2+ spaces or tab):

```
# ~/.config/rush/objectify.rush
netstat       --fixed
docker ps     --delim "\s{2,}"
my-tool       --fixed 0,10,20
```

Use `--save` to persist a custom hint from the command line:

```rush
my-tool | objectify --fixed --save
# Future runs: my-tool | where ... just works
```

### Columns Pipe

Index-based column selection (1-based, like awk). Complements `select` which uses property names:

```rush
my-tool | objectify | columns 1,3,5
netstat | columns 1 4 6          # space-separated indices also work
```

---

## Scripting Language

### Variables & Assignment

```rush
name = "world"
count = 42
files = ls /tmp         # Capture command output
today = Time.now
```

Compound assignment:

```rush
count += 1
total -= 5
score *= 2
ratio /= 100
```

Multiple assignment:

```rush
a, b, c = 1, 2, 3
first, last = "Alice", "Smith"
```

### Strings

**Double-quoted** — with `#{expr}` interpolation:

```rush
greeting = "hello #{name}"
puts "there are #{items.count} items"
puts "path: #{env.HOME}/documents"
```

**Single-quoted** — literal (no interpolation):

```rush
pattern = 'no #{interpolation} here'
```

**Escape sequences** (in double-quoted strings):

| Escape | Character |
|--------|-----------|
| `\n` | Newline |
| `\t` | Tab |
| `\\` | Backslash |
| `\"` | Double quote |

**Heredocs** for multi-line strings:

```rush
message = <<END
Dear #{name},
This is a multi-line
message with interpolation.
END
```

### String Methods

```rush
"hello".upcase              # → "HELLO"
"HELLO".downcase            # → "hello"
"  hi  ".strip              # → "hi"
"  hi  ".lstrip             # → "hi  "
"  hi  ".rstrip             # → "  hi"

"a,b,c".split(",")         # → ["a", "b", "c"]
"hello world".split_whitespace  # → ["hello", "world"]
"line1\nline2".lines        # → ["line1", "line2"]

"hi".ljust(10)              # → "hi        "
"hi".rjust(10)              # → "        hi"
"100%".trim_end("%")        # → "100"

"hello".start_with?("he")  # → true
"hello".end_with?("lo")    # → true
"hello".include?("ell")    # → true
"".empty?                   # → true

"hello".replace("l", "r")  # → "herro"
"hello".to_i                # → 0
"42".to_i                   # → 42
"3.14".to_f                 # → 3.14
42.to_s                     # → "42"
nil.nil?                    # → true
"hello".nil?                # → false
```

**Color methods** — for terminal output:

```rush
"success".green
"error".red
"warning".yellow
"info".cyan
# Colors: .red, .green, .blue, .cyan, .yellow, .magenta, .white, .gray
```

### Regex

**Literals and matching:**

```rush
name =~ /^test/             # Match (returns true/false)
name !~ /admin/             # Not match

line.sub(/^#\s*/, "")       # Replace first match
text.gsub(/\t/, "  ")       # Replace all matches
path.scan(/[^\/]+/)         # All matches as array
line.match(/^(\w+)=(.*)$/)  # Match with captures
```

### Numbers

```rush
(-5).abs                    # → 5
3.14159.round(2)            # → 3.14
```

**Formatting methods:**

```rush
14.25.to_currency           # → "$14.25"
14.25.to_currency(pad: 10)  # → "    $14.25"
1048576.to_filesize         # → "1.0 MB"
0.8734.to_percent           # → "87.3%"
0.8734.to_percent(decimals: 2)  # → "87.34%"
```

**Duration literals:**

```rush
2.hours                     # TimeSpan
30.minutes
45.seconds
7.days
```

### Control Flow

**if / elsif / else / end:**

```rush
if count > 10
  puts "many"
elsif count > 0
  puts "some"
else
  puts "none"
end
```

**unless / end:**

```rush
unless quiet
  puts "verbose output"
end
```

**Postfix form** (one-liners):

```rush
puts "hello" if verbose
puts "skipping" unless debug
```

**Comparison operators:**

| Rush | Meaning |
|------|---------|
| `==` | Equal |
| `!=` | Not equal |
| `>` | Greater than |
| `<` | Less than |
| `>=` | Greater or equal |
| `<=` | Less or equal |

**Logical operators:**

| Rush | Also |
|------|------|
| `&&` | `and` |
| `\|\|` | `or` |
| `!` | `not` |

**Safe navigation:**

```rush
user&.name                  # Returns nil if user is nil, otherwise user.name
```

**Pattern matching:**

```rush
case status
when "ok"
  puts "all good"
when "error"
  puts "something broke"
else
  puts "unknown: #{status}"
end
```

`match` / `when` / `end` also works as an alias for `case`.

### Loops

**for / in / end:**

```rush
for file in Dir.list(".")
  puts file.Name
end

for i in 1..5
  puts i                    # Prints 1 through 5
end

for i in 1...5
  puts i                    # Prints 1 through 4 (exclusive end)
end
```

**while / end:**

```rush
while retries > 0
  retries -= 1
  # try something
end
```

**until / end:**

```rush
until done
  # keep working
end
```

**loop / end** (infinite):

```rush
loop
  line = ask "command> "
  break if line == "quit"
  # process line
end
```

**Loop control:**

```rush
next if line.empty?         # Skip this iteration
break if count > 100        # Exit the loop
```

`continue` also works as an alias for `next`.

**Iteration with `.times`:**

```rush
5.times { |i|
  puts "iteration #{i}"    # 0, 1, 2, 3, 4
}
```

### Functions

```rush
def greet(name, greeting = "hello")
  puts "#{greeting}, #{name}!"
end

greet("world")              # → "hello, world!"
greet("world", "hey")       # → "hey, world!"
```

**Named arguments** (colon syntax):

```rush
def deploy(env, dry_run: false, verbose: true)
  puts "deploying to #{env}" unless dry_run
end

deploy("staging", dry_run: true)
deploy("production", verbose: false)
```

**Return values:**

```rush
def double(n)
  n * 2                     # Implicit return (last expression)
end

def validate(input)
  return "invalid" if input.empty?
  input.strip
end

result = double(21)         # → 42
```

### Classes

Define classes with `class`/`end`, attributes with `attr`, and constructors with `def initialize`:

```rush
class Greeter
  attr name

  def initialize(name)
    self.name = name
  end

  def greet
    return "Hello, " + self.name
  end
end

g = Greeter.new("World")
puts g.greet()               # → "Hello, World"
```

**Key concepts:**

- **`attr`** declares instance attributes (comma-separated for multiple)
- **`attr name: String`** — optional type annotation (`String`, `Int`, `Bool`, `Float`, or custom class)
- **`self.prop`** accesses instance properties (required — no bare name shortcuts)
- **`def initialize(...)`** defines the constructor
- **`ClassName.new(args)`** creates a new instance
- **`ClassName.new(name: value)`** — named args are reordered to match constructor params
- Methods without `return` are void; methods with `return` produce values

**Constructor defaults:**

```rush
class Counter
  attr value

  def initialize(start: 0)
    self.value = start
  end

  def increment
    self.value = self.value + 1
  end

  def get_value
    return self.value
  end
end

c = Counter.new()             # Uses default start: 0
c.increment()
c.increment()
puts c.get_value()            # → 2

d = Counter.new(10)           # Explicit start value
d.increment()
puts d.get_value()            # → 11
```

**Typed attributes and named args:**

```rush
class Person
  attr name: String, age: Int

  def initialize(name, age)
    self.name = name
    self.age = age
  end
end

p = Person.new(age: 30, name: "Alice")  # named args, any order
puts p.name                              # → "Alice"
```

**Inheritance:**

Use `<` to inherit from a parent class. Call `super(args)` in the constructor to pass arguments to the parent, and `super.method()` to call a parent method from an override:

```rush
class Animal
  attr name

  def initialize(name)
    self.name = name
  end

  def speak
    puts "..."
  end
end

class Dog < Animal
  attr breed

  def initialize(name, breed)
    super(name)
    self.breed = breed
  end

  def speak
    super.speak()
    puts "Woof!"
  end
end

d = Dog.new("Rex", "Lab")
puts d.name                   # → "Rex"
d.speak()                     # → "..." then "Woof!"
```

Inheritance rules:
- The parent class must be defined before the child class in the same script
- `super(args)` in a constructor passes arguments to the parent constructor
- `super.method()` calls the parent's version of a method
- Child classes inherit all attributes and methods from the parent

**Static methods:**

Define class-level methods with `def self.method_name`. Call them with `ClassName.method()` — no instance needed:

```rush
class MathHelper
  def self.add(a, b)
    return a + b
  end

  def self.pi
    return 3.14159
  end
end

puts MathHelper.add(2, 3)     # → 5
puts MathHelper.pi()          # → 3.14159
```

A class can have both instance methods and static methods:

```rush
class Counter
  attr value

  def initialize(start: 0)
    self.value = start
  end

  def increment
    self.value = self.value + 1
  end

  def self.create_pair
    return [Counter.new(0), Counter.new(100)]
  end
end

pair = Counter.create_pair()
```

### Enums

Define enumerations with `enum`/`end`. Members are listed one per line, optionally with explicit integer values:

```rush
enum Color
  red
  green
  blue
end

enum Priority
  low = 1
  medium = 5
  high = 10
end
```

Access members with `EnumName.member`:

```rush
favorite = Color.blue
puts favorite                 # → "Blue"

if favorite == Color.blue
  puts "good choice"
end
```

Enum rules:
- Member names are lowercased in Rush but PascalCased in output
- Members without explicit values are auto-numbered (starting from 0)
- Enums support assignment and comparison (`==`, `!=`)

### Blocks & Iteration

**Block syntax** — `{ |args| }` or `do |args| ... end`:

```rush
files.each { |f| puts f.Name }

names = users.map { |u| u.name }

big_files = files.select { |f| f.Length > 1mb }

small_files = files.reject { |f| f.Length > 1mb }
```

**Available iteration methods:**

| Method | Description |
|--------|-------------|
| `.each { \|x\| }` | Iterate over each element |
| `.map { \|x\| }` | Transform each element |
| `.select { \|x\| }` | Keep elements where block is true |
| `.reject { \|x\| }` | Remove elements where block is true |
| `.sort_by { \|x\| }` | Sort by block result |
| `.group_by { \|x\| }` | Group into hash by block result |
| `.flat_map { \|x\| }` | Map and flatten one level |
| `.any? { \|x\| }` | True if any element matches |
| `.all? { \|x\| }` | True if all elements match |
| `.count` | Number of elements |
| `.first(n)` | First n elements |
| `.last(n)` | Last n elements |
| `.skip(n)` | Skip first n elements |
| `.uniq` | Remove duplicates |
| `.reverse` | Reverse order |

**Method chaining:**

```rush
Dir.list("/var/log", type: "file", recursive: true)
  .select { |f| f.Name =~ /\.log$/ }
  .sort_by { |f| f.Length }
  .reverse
  .first(10)
  .each { |f| puts "#{f.Name.ljust(30)} #{f.Length.to_filesize}" }
```

**Chained output:**

```rush
Dir.list(".", recursive: true).print
[1, 2, 3].map { |x| x * 2 }.puts
```

### Error Handling

```rush
begin
  data = File.read_json("config.json")
rescue => e
  warn "failed to read config: #{e}"
  data = {}
end
```

**With ensure (finally):**

```rush
try
  file = File.read("important.txt")
rescue IOError => e
  warn "IO error: #{e}"
rescue => e
  warn "unexpected error: #{e}"
ensure
  puts "cleanup done"
end
```

**Exit status checking:**

```rush
git pull origin main
if $?.failed?
  warn "git pull failed with code #{$?.code}"
  exit 1
end

curl -s https://api.example.com/health
puts "API is up" if $?.ok?
```

### Data Structures

**Arrays:**

```rush
items = [1, 2, 3, "four", 5.0]
items[0]                    # → 1
items[-1]                   # → 5.0
items.count                 # → 5
```

**Hashes:**

```rush
config = { name: "rush", version: "0.2.0", debug: false }
config[:name]               # → "rush"
config.name                 # → "rush" (dot access)
```

**Symbols:**

```rush
status = :ok
mode = :verbose
```

**Ranges:**

```rush
1..10                       # Inclusive: 1 through 10
1...10                      # Exclusive: 1 through 9
```

---

## Platform Blocks

Platform-specific blocks execute code only on the matching operating system. They act as implicit `if $os` conditionals with clean syntax.

### Basic Platform Blocks

```rush
macos
  puts "Running on macOS"
end

linux
  puts "Running on Linux"
end

win64
  puts "Running on Windows (64-bit PS 7)"
end
```

Non-matching blocks are silently skipped — no error, no output.

### Property Conditions

Add `.arch` or `.version` conditions for fine-grained platform targeting:

```rush
macos.arch == "arm64"
  puts "Apple Silicon"
end

linux.arch == "x64"
  puts "64-bit Linux"
end

macos.version >= "25.0"
  puts "Darwin 25 or later"
end

win64.version >= "10.0.22000"
  puts "Windows 11 or later"
end
```

**Available properties:**

| Property   | Values                         | Source                              |
|------------|--------------------------------|-------------------------------------|
| `.arch`    | `x64`, `arm64`, `x86`         | CPU architecture                    |
| `.version` | `25.3.0`, `6.8.0`, `10.0.22631` | OS version (Darwin/kernel/Windows) |

Version comparisons use numeric ordering, so `"25.3.0" >= "6.8.0"` works correctly.

### win64 — Windows with PowerShell 7

`win64` blocks run normal Rush code on 64-bit Windows. Since Rush runs on PowerShell 7 (which is 64-bit only), `win64` is the standard Windows block. Use it for Windows-specific paths, commands, and 64-bit driver access:

```rush
win64
  # Windows-specific paths and commands
  export TEMP="C:\\Temp"
  sql @sqlserver "SELECT * FROM orders WHERE status = 'pending'"
  puts "Processed on Windows"
end
```

`win64` also supports property conditions:

```rush
win64.arch == "arm64"
  puts "Windows on ARM (Surface Pro X, Snapdragon)"
end

win64.version >= "10.0.22000"
  puts "Windows 11+"
end
```

### win32 — 32-bit PowerShell Escape Hatch

The `win32` block is special: its body is **raw PowerShell 5.1** (not Rush syntax) and executes in the 32-bit PowerShell process at `C:\Windows\SysWOW64\WindowsPowerShell\v1.0\powershell.exe`. This is needed for 32-bit ODBC/OLEDB drivers like ACE and BC that cannot load in 64-bit processes.

```rush
win32
  # Raw PowerShell 5.1 — runs in 32-bit powershell.exe
  $conn = New-Object System.Data.OleDb.OleDbConnection("Provider=Microsoft.ACE.OLEDB.12.0;Data Source=C:\Data\customers.accdb")
  $conn.Open()
  $cmd = $conn.CreateCommand()
  $cmd.CommandText = "SELECT * FROM Customers"
  $reader = $cmd.ExecuteReader()
  while ($reader.Read()) {
    Write-Output "$($reader[0]) | $($reader[1])"
  }
  $conn.Close()
end
```

**Key differences from other platform blocks:**

- Body is **raw PowerShell 5.1**, not Rush syntax
- Executes in a **32-bit process** (SysWOW64 path)
- Rush variables are automatically injected as `$var = 'value'` preamble
- On macOS/Linux, win32 blocks are silently skipped

### ps — Raw PowerShell Passthrough

The `ps` block passes content directly to the embedded PowerShell 7 engine with **NO Rush variable expansion**. `$_`, `$job`, script blocks, and all PowerShell syntax survive untouched. This is the recommended way to use PowerShell cmdlets that rely on `$_`, `Where-Object` script blocks, or PS-native variable scoping.

```rush
ps
  Get-Service | Where-Object { $_.Status -eq "Running" }
  $fw = Get-NetFirewallProfile
  $fw | Format-Table Name, Enabled
end
```

**Key features:**
- `$` variables are NOT expanded by Rush — they reach PowerShell as-is
- Works on **all platforms** (Rush bundles the PS 7 SDK)
- Supports version gating: `ps.version >= "7.4" ... end`
- Bare `ps` starts a block; `ps -ef` and `ps aux` still run the Unix `ps` command

**When to use `ps` vs regular Rush:**

| Scenario | Use |
|----------|-----|
| Simple commands, file ops, pipelines | Regular Rush |
| PowerShell cmdlets with `$_` or `{}` script blocks | `ps ... end` |
| Windows admin (Get-Service, Get-NetFirewall, etc.) | `ps ... end` |
| Legacy PS 5.1 modules (AD, OLEDB) | `ps5 ... end` or `win32 ... end` |

### ps5 — PowerShell 5.1 (Windows Only)

The `ps5` block runs raw PowerShell 5.1 via `powershell.exe` on Windows. Use it for modules that require Windows PowerShell 5.1 (not PS 7), such as some Active Directory cmdlets or older management tools.

```rush
ps5
  Import-Module ActiveDirectory
  Get-ADUser -Filter * | Select-Object Name, Enabled
end
```

On macOS/Linux, `ps5` blocks are silently skipped (PS 5.1 doesn't exist there).

### Cross-Platform Scripts

Combine platform blocks for scripts that run everywhere:

```rush
# Common setup
db_name = "production"

macos
  export ODBC_DRIVER="/usr/local/lib/libmyodbc8a.so"
end

linux
  export ODBC_DRIVER="/usr/lib/x86_64-linux-gnu/odbc/libmyodbc8a.so"
end

win64
  export ODBC_DRIVER="{MySQL ODBC 8.0 Unicode Driver}"
end

win32
  # 32-bit Access database via ACE driver
  $conn = New-Object System.Data.OleDb.OleDbConnection("Provider=Microsoft.ACE.OLEDB.12.0;Data Source=C:\legacy\data.accdb")
  $conn.Open()
  # ... query legacy data ...
  $conn.Close()
end

# Common code runs on all platforms
puts "Database: #{db_name}"
```

---

## Standard Library

Receivers are case-insensitive: `Dir.list()` and `dir.list()` both work.

### File

```rush
File.read("path")           # Read entire file as string
File.read_lines("path")     # Read as array of lines
File.read_json("path")      # Parse JSON file into object
File.read_csv("path")       # Parse CSV file into array of objects

File.write("path", content) # Write string (overwrite)
File.append("path", text)   # Append string to file

File.exist?("path")         # Check if file exists → true/false
File.delete("path")         # Delete file
File.size("path")           # File size in bytes
```

**Examples:**

```rush
# Read and process JSON config
config = File.read_json("package.json")
puts "Project: #{config.name} v#{config.version}"

# Process CSV data
rows = File.read_csv("sales.csv")
total = rows.map { |r| r.amount.to_f }.sum
puts "Total sales: #{total.to_currency}"

# Check before reading
if File.exist?("config.json")
  settings = File.read_json("config.json")
else
  warn "no config found, using defaults"
end

# Write results
File.write("output.txt", results.join("\n"))
File.append("log.txt", "#{Time.now}: task complete\n")
```

### Dir

`Dir.list` returns relative path strings by default (one per line). Use symbol flags to control output:

```rush
Dir.list(".")                    # relative paths, one per line
Dir.list(".", :ls)               # verbose ls-style output (objects)
Dir.list(".", :recurse, :files)  # recursive, files only
Dir.list(".", :dirs)             # directories only
Dir.list(".", :hidden)           # include dotfiles
Dir.list(".", :recurse, :hidden, :files)  # combine flags
```

**Symbol flags:**

| Flag | Description |
|------|-------------|
| `:recurse` | Recurse into subdirectories |
| `:files` | Return only files |
| `:dirs` | Return only directories |
| `:hidden` | Include dotfiles/hidden entries |
| `:ls` | Return objects with verbose ls-style display |

The older named-argument syntax still works:

```rush
Dir.list(".", type: "file")            # same as :files
Dir.list(".", type: "dir")             # same as :dirs
Dir.list("src", recursive: true)       # same as :recurse
Dir.list(".", type: "file", hidden: true) # same as :files, :hidden
```

Other Dir methods:

```rush
Dir.exist?("path")                     # Check if directory exists
Dir.mkdir("path")                      # Create directory (+ parents)
```

**Examples:**

```rush
# Find all log files recursively
logs = Dir.list("/var/log", :recurse, :files)
  .select { |f| f =~ /\.log$/ }
puts "Found #{logs.count} log files"

# List project structure
Dir.list(".", :dirs).each { |d| puts d }

# Verbose listing with details
Dir.list("src", :ls, :files)

# Create output directory
Dir.mkdir("output/reports") unless Dir.exist?("output/reports")
```

### Time

```rush
Time.now                    # Current local DateTime
Time.utc_now                # Current UTC DateTime
Time.today                  # Today at midnight

# Duration arithmetic
yesterday = Time.now - 24.hours
next_week = Time.now + 7.days
timeout = 30.minutes + 45.seconds
```

---

## Built-in Commands

### Output

| Command | Description |
|---------|-------------|
| `puts "text"` | Print with newline |
| `print "text"` | Print without newline |
| `warn "text"` | Print to stderr |
| `die "error"` | Print error and exit |
| `printf "%s: %d\n" name count` | C-style formatted output |

`printf` supports: `%s` (string), `%d` (integer), `%f` (float), `%x` (hex), `%%` (literal %).

### Navigation

| Command | Description |
|---------|-------------|
| `cd path` | Change directory |
| `cd -` | Toggle to previous directory |
| `cd` | Go to home directory |
| `..` | Go up one directory (`cd ..`) |
| `...` | Go up two directories (`cd ../..`) |
| `....` | Go up three directories (`cd ../../..`) |
| `pwd` | Print working directory |
| `pushd dir` | Push current dir, cd to dir |
| `popd` | Pop directory from stack and cd |
| `dirs` | Show directory stack |
| `dirs -v` | Show stack with indices |

The dot shortcuts extend to any number of dots: each `.` beyond the first means one more level up.

`cd` also searches `CDPATH` — if a relative path isn't found in the current directory, it checks directories listed in `$CDPATH`.

### Environment

| Command | Description |
|---------|-------------|
| `export VAR=value` | Set environment variable |
| `export --save VAR=value` | Set and persist to init.rush |
| `unset VAR` | Remove environment variable |

`export --save` writes the export to the `# ── Environment` section in init.rush. If the variable already exists there, the line is updated in place (idempotent).

### PATH Management

| Command | Description |
|---------|-------------|
| `path` | List PATH entries (one per line) |
| `path add ~/bin` | Append directory to PATH |
| `path add --front ~/bin` | Prepend directory to PATH |
| `path add --save ~/bin` | Add to PATH and persist to init.rush |
| `path rm ~/bin` | Remove directory from PATH |
| `path rm --save ~/bin` | Remove from PATH and init.rush |
| `path edit` | Edit PATH in `$EDITOR` |
| `path check` | List PATH entries with existence check and duplicate detection |
| `path dedupe` | Remove duplicate entries (first occurrence wins) |
| `path dedupe --save` | Remove duplicates and persist to init.rush |

Use `--name=VARNAME` to manage any colon-separated variable:

```rush
path --name=MANPATH add ~/man          # add to MANPATH
path --name=PYTHONPATH                  # list PYTHONPATH entries
path --name=MANPATH edit                # edit MANPATH in $EDITOR
path --name=MANPATH add --save ~/man   # add and persist
```

Multi-line blocks provide declarative PATH management, ideal for init.rush:

```rush
# Multi-line blocks — declarative PATH in init.rush
path add
  /opt/homebrew/bin
  /usr/local/bin
  ~/.local/bin
end

path rm
  /opt/old/bin
  /usr/old/bin
end

# Diagnostics
path check               # show all entries with ✓/✗ and duplicate flags
path dedupe              # remove duplicate entries
path dedupe --save       # remove and persist
```

### Aliases

| Command | Description |
|---------|-------------|
| `alias` | List all aliases |
| `alias name='cmd'` | Define alias (session only) |
| `alias --save name='cmd'` | Define and persist to config.json |
| `unalias name` | Remove alias |
| `unalias --save name` | Remove from config.json |

**Windows paths with spaces:** wrap the exe path in inner double quotes:
```
alias --save vi='"C:/Program Files/Neovim/bin/nvim.exe"'
```

### History

| Command | Description |
|---------|-------------|
| `history` | Show last 50 entries |
| `history -c` | Clear all history |

### Input

| Command | Description |
|---------|-------------|
| `ask "prompt"` | Read line from user, return string |
| `ask "prompt", char: true` | Read single character |
| `read var` | Read line into variable |
| `read -p "prompt" var` | Read with prompt |

### Execution

| Command | Description |
|---------|-------------|
| `source file.rush` | Execute script in current session |
| `. file.rush` | Same as `source` |
| `exec cmd` | Replace shell with command |
| `sleep N` | Sleep N seconds |
| `exit [code]` | Exit Rush with optional code |
| `which cmd` | Find command location |

### Settings

| Command | Description |
|---------|-------------|
| `set vi` | Switch to vi edit mode |
| `set emacs` | Switch to emacs edit mode |
| `set -e` | Exit on error |
| `set +e` | Clear exit-on-error |
| `set -x` | Trace commands (print before executing) |
| `set +x` | Clear trace |
| `set -o pipefail` | Fail pipeline if any segment fails |
| `set +o pipefail` | Clear pipefail |
| `set --save key val` | Change setting and persist to config.json |
| `set --secret KEY val` | Save API key/token to secrets.rush |
| `set bg "#hex"` | Set terminal background color for precise theming |
| `set bg off` | Disable Rush background control |

### AI Assistant

The `ai` builtin lets you query an LLM directly from the shell. It streams responses in real-time and is fully context-aware (Rush language, OS, cwd, recent commands).

| Command | Description |
|---------|-------------|
| `ai "prompt"` | Ask a question |
| `ai "prompt" < file` | Ask with file context via redirection |
| `cat file \| ai "prompt"` | Pipe content as context |
| `failing-cmd \| ai "what happened?"` | Pipe errors — stderr is included automatically |
| `ai "prompt" > out.txt` | Capture response to file |
| `ai --provider ollama "prompt"` | Use a specific provider |
| `ai --model gpt-4o "prompt"` | Use a specific model |
| `ai --exec` | Execute the last AI response as a command |
| `ai --agent "task"` | Autonomous agent mode — executes commands to complete a task |

**Fence stripping:** Markdown code fences (` ``` `) are automatically stripped from AI output. When the AI wraps commands in fences, only the clean content is displayed. `ai --exec` prefers code block content — so explanatory text is skipped and only the actual commands run.

**Setup:**

```rush
set --secret ANTHROPIC_API_KEY sk-ant-...    # Default provider
set --secret OPENAI_API_KEY sk-...           # For OpenAI
set --secret GEMINI_API_KEY AIza...          # For Google Gemini
# Ollama requires no key (runs locally)
```

**Config:**

```rush
set --save aiProvider anthropic    # anthropic, openai, gemini, ollama
set --save aiModel auto            # "auto" = provider default
```

**Supported providers:**

| Provider | API Key Env Var | Default Model | Notes |
|----------|----------------|---------------|-------|
| Anthropic | `ANTHROPIC_API_KEY` | Claude Sonnet | Default provider |
| OpenAI | `OPENAI_API_KEY` | GPT-4o | |
| Google Gemini | `GEMINI_API_KEY` | Gemini Pro | |
| Ollama | *(none — local)* | llama3 | Runs locally, no API key needed |

**Custom providers:** Drop a JSON file in `~/.config/rush/ai-providers/` for any OpenAI-compatible API:

```json
{
  "name": "together",
  "endpoint": "https://api.together.xyz/v1/chat/completions",
  "format": "openai",
  "apiKeyEnvVar": "TOGETHER_API_KEY",
  "defaultModel": "meta-llama/Llama-3-70b-chat-hf"
}
```

The `format` field selects the wire protocol parser: `openai`, `anthropic`, `gemini`, or `ollama`.

#### Agent Mode

`ai --agent` runs the AI as an autonomous agent that executes commands and iterates until the task is complete. Instead of just answering questions, the agent takes action.

```rush
ai --agent "list the files in docs/ and tell me which are largest"
ai --agent "check git status and show recent commits"
ai --agent "find all TODO comments in the source code"
ai --agent --model claude-sonnet-4-20250514 "deploy the app"
```

The agent:
- Receives your task as a goal
- Executes commands via Rush's LLM mode engine (same process, shared runspace)
- Observes structured JSON results (including object-mode output)
- Iterates up to 25 turns until the task is done
- Streams thinking text and command results to the terminal

**Terminal output:**
- Thinking text appears in dim gray
- Commands appear in cyan with `▸` prefix
- Results show `✓` (green) or `✗` (red) with one-line summaries
- A summary line at the end shows total commands, turns, and elapsed time

**Supported providers:** Anthropic (default) and Gemini. Use `--provider gemini` for Gemini's rates. OpenAI support coming soon.

**Current limitations:**
- No approval mode yet — the agent executes commands directly.

### Hot Reload

| Command | Description |
|---------|-------------|
| `init` | Edit init.rush in `$EDITOR`, then reload |
| `reload` | Reload config files (settings, theme, aliases) |
| `reload --hard` | Full binary restart preserving session state |

`init` opens `~/.config/rush/init.rush` in your editor. After saving, it re-runs the startup script to pick up changes immediately.

`reload --hard` serializes your session (variables, env, cwd, aliases, flags) to a temp file, restarts the Rush binary, and restores everything. Use this after updating the Rush binary with `install.sh`.

When the binary is updated while Rush is running, a yellow `[stale]` indicator appears in the prompt. A one-time hint message explains what to do:

```
  binary updated — run 'reload --hard' to restart
```

### Signals

| Command | Description |
|---------|-------------|
| `trap 'cmd' SIGNAL` | Register signal handler |
| `trap -l` | List registered traps |

```rush
trap 'echo interrupted' INT
trap 'cleanup' EXIT         # Run on shell exit
```

### Config Sync

| Command | Description |
|---------|-------------|
| `sync init github` | Initialize GitHub sync |
| `sync init ssh host:path` | Initialize SSH sync |
| `sync init path /dir` | Initialize path sync |
| `sync push` | Push config to remote |
| `sync pull` | Pull config from remote |
| `sync status` | Show sync status |

### Other

| Command | Description |
|---------|-------------|
| `clear` | Clear the screen |
| `help` | Show built-in help |
| `help <topic>` | Detailed reference for a topic (file, dir, time, strings, arrays, hashes, classes, enums, functions, loops, control-flow, pipelines, regex, errors, platforms, sql, pipeline-ops, llm-mode, objectify) |

---

## Native Commands

Standard Unix commands (`ls`, `grep`, `find`, `cp`, `mv`, `rm`, etc.) run natively on macOS and Linux — Rush does not translate or intercept them. All flags and behaviors work exactly as you'd expect.

Rush configures theme-aware colors for native commands at startup (see [Colors & Theming](#colors--theming)), so `ls` and `grep` produce readable colored output on both dark and light terminals automatically.

---

## The `cat` Builtin

Rush includes a native `cat` implementation built on .NET (not PowerShell's `Get-Content`). It provides Unix-compatible behavior for file reading, concatenation, and stdin.

### Basic Usage

```rush
cat file.txt                # Print file contents
cat file1.txt file2.txt     # Concatenate multiple files
cat -n file.txt             # Print with line numbers
cat --number file.txt       # Same as -n
```

### Stdin Reading

```rush
cat                         # Read from stdin (type lines, Ctrl+D to end)
cat > output.txt            # Read stdin, write to file
cat >> output.txt           # Read stdin, append to file
```

Press **Ctrl+D** on an empty line to signal EOF (end of input). Press **Ctrl+C** to cancel.

### Redirection

```rush
cat file.txt > copy.txt     # Copy file contents
cat file.txt >> log.txt     # Append to file
cat < input.txt             # Read via stdin redirection
```

### Pipeline Fallthrough

When `cat` is used in a pipeline (`cat file | grep pattern`) or with process substitution (`cat <(cmd)`), Rush falls through to the native `/bin/cat` so pipeline semantics work naturally.

---

## The `sql` Command

Rush includes a native `sql` command for querying databases directly from the shell. Results are formatted as aligned tables for human consumption, or as structured JSON for AI agents.

### Querying

```rush
# Inline URI (ad-hoc)
sql sqlite:///path/to/data.db "SELECT * FROM users"
sql postgres://user:pass@host/dbname "SELECT count(*) FROM orders"
sql odbc://DSN=MyAccess "SELECT * FROM Customers"

# Named connection (configured in databases.json)
sql @mydb "SELECT * FROM users WHERE active = true"
```

### Output Modes

```rush
sql @db "SELECT ..." --json       # JSON array of objects
sql @db "SELECT ..." --csv        # RFC 4180 CSV
sql @db "SELECT ..." --limit 50   # Limit rows (default: 1000)
sql @db "SELECT ..." --no-limit   # No row limit
sql @db "SELECT ..." --timeout 30 # Query timeout in seconds
```

Default output is an aligned table with colored headers:

```
name     email                created_at
───────  ───────────────────  ───────────────────
Alice    alice@example.com    2024-01-15 09:30:00
Bob      bob@example.com      2024-02-20 14:15:00

2 row(s) (12ms)
```

In LLM mode (`rush --llm`), output is automatically JSON in the `LlmResult` envelope with `stdout_type: "json/rows"`.

### Connection Management

```rush
sql add @mydb --driver sqlite --path ~/data/app.db
sql add @prod --driver postgres --host db.example.com --database myapp --user admin
sql add @legacy --driver odbc --dsn "MyAccessDB"
sql list                         # Show all named connections
sql test @mydb                   # Test connectivity
sql remove @mydb                 # Remove a connection
```

Connections are stored in `~/.config/rush/databases.json`. Passwords are never stored directly — use `--password-env VAR` to reference an environment variable set in `secrets.rush`.

### Supported Drivers

| Driver | Scheme | Notes |
|--------|--------|-------|
| SQLite | `sqlite://` | Built-in, no server required |
| PostgreSQL | `postgres://` | Built-in, requires server |
| ODBC | `odbc://` | Built-in, uses system ODBC drivers |

---

## Command Execution

On macOS and Linux, Unix commands (`ls`, `grep`, `find`, `cp`, `mv`, `rm`, `head`, `tail`, `sort`, `ps`, `kill`, etc.) run **natively** — Rush does not translate or intercept them. All flags work exactly as the OS provides.

`echo` is the only standalone command Rush translates (to PowerShell's `Write-Output`) for PowerShell variable expansion.

### Pipeline Operations (After `|`)

These change behavior when used in a pipeline:

**Filtering:**

```rush
ps | where CPU > 10             # Filter by property
ps | where Name == "rush"
ps | where Name =~ /^dotnet/    # Regex match
ls | grep "\.txt$"              # Pattern filter
```

Where operators: `>`, `<`, `>=`, `<=`, `==`, `!=`, `=~` (match), `!~` (not match), `contains`.

**Selection:**

```rush
ps | select ProcessName, CPU    # Pick properties
ps | .ProcessName               # Dot-access shorthand
```

**Sorting:**

```rush
ps | sort CPU                   # Sort ascending
ps | sort -r CPU                # Sort descending
```

**Slicing:**

```rush
ls | first 5                    # First 5 items
ls | last 3                     # Last 3 items
ls | skip 10                    # Skip first 10
ls | head 5                     # Alias for first (default 10)
ls | tail 5                     # Alias for last (default 10)
```

**Aggregation:**

```rush
ls | count                      # Count items
ps | sum WorkingSet64           # Sum a property
ps | avg CPU                    # Average
ps | min Id                     # Minimum
ps | max WorkingSet64           # Maximum
```

**Uniqueness:**

```rush
ls | distinct                   # Unique values
ls | uniq                       # Alias for distinct
ls | distinct Name              # Unique by property
```

**Format conversion:**

```rush
ps | as json                    # → JSON
ps | as csv                     # → CSV
ps | as table                   # → Formatted table
ps | as list                    # → Formatted list
cat data.json | from json       # Parse JSON
cat data.csv | from csv         # Parse CSV
json config.json                # Read + parse JSON file
```

**I/O:**

```rush
ls | tee files.txt | count      # Save and pass through
ls | tee -a log.txt             # Append mode
```

---

## Built-in Variables

| Variable | Description |
|----------|-------------|
| `$?` | Last command exit status (object) |
| `$?.ok?` | True if last command succeeded |
| `$?.failed?` | True if last command failed |
| `$?.code` | Numeric exit code |
| `ARGV` | Script arguments (array) |
| `__FILE__` | Current script file path |
| `__DIR__` | Current script directory |
| `env.HOME` | Environment variable (dot access) |
| `env["PATH"]` | Environment variable (bracket access) |
| `os` | Operating system name |
| `hostname` | Machine name |
| `rush_version` | Rush version string |
| `is_login_shell` | True if launched as login shell |
| `needs_reload` | True if binary updated since startup (prompt context) |
| `__rush_arch` | CPU architecture: `x64`, `arm64`, `x86` |
| `__rush_os_version` | OS version string (Darwin/kernel/Windows) |

---

## LLM Mode

`rush --llm` replaces the human shell interface with a structured JSON wire protocol for LLM agents. Every interaction is machine-readable — no ANSI colors, no decorations, no human prompts. Environment variables (`CI=true`, `GIT_TERMINAL_PROMPT=0`, `DEBIAN_FRONTEND=noninteractive`) are forced to prevent hidden interactive prompts from hanging the session.

### Session Initialization

`rush --llm` automatically runs `init.rush` and `secrets.rush` before entering the wire protocol loop — the LLM gets the user's PATH, exports, and environment. Rush built-in variables (`$os`, `$hostname`, `$rush_version`, `$is_login_shell`) and config aliases are also loaded.

### State Inheritance

`rush --llm --inherit /path/to/state.json` carries over live runtime state from a parent interactive session:

- Environment variables (user-defined)
- PowerShell variables (user-defined)
- Aliases (from running session)
- CWD + previous directory (for `cd -`)
- Shell flags (`set -e`, `set -x`, `set -o pipefail`)

The state file is consumed on use (deleted after loading) — one-shot transfer. The state JSON uses the same format as `reload --hard`.

### Wire Protocol

Every turn follows the same pattern:

1. **Rush emits a JSON context line** (host, user, cwd, git state, last exit code)
2. **LLM sends a command** (plain text or JSON-quoted string for multi-line)
3. **Rush emits a JSON result envelope** (status, stdout, stderr, duration)

```
→ {"ready":true,"host":"server","user":"mark","cwd":"/home/mark","git_branch":"main","git_dirty":false,"last_exit_code":0,"shell":"rush","version":"1.2.54"}
← ls -la
→ {"status":"success","exit_code":0,"cwd":"/home/mark","stdout":"total 42\n...","duration_ms":12}
```

### Object-Mode Output

When a command produces structured .NET objects (e.g., `ls` returns `FileInfo`/`DirectoryInfo`, `ps` returns `Process`), Rush auto-detects this and serializes them as a JSON array instead of formatted text. The `stdout_type` field is set to `"objects"` and `stdout` becomes an array of curated property projections:

```
← ls docs
→ {"status":"success","exit_code":0,"cwd":"/home/mark","stdout_type":"objects","stdout":[
    {"name":"readme.md","size":1234,"modified":"2026-03-06T13:00:00Z","type":"file","path":"/home/mark/docs/readme.md"},
    {"name":"src","modified":"2026-03-06T14:00:00Z","type":"directory","path":"/home/mark/docs/src"}
  ],"duration_ms":27}
```

Text commands (`echo`, `git status`, native executables, arithmetic) remain unchanged — `stdout` is a string and `stdout_type` is omitted. Simple values (string, int, bool, DateTime) are always text mode.

### Multi-Line Input

Send Rush blocks as JSON-quoted strings with `\n` for newlines:

```
← "if File.exist?(\"config.json\")\n  puts \"found\"\nend"
```

### LLM-Only Builtins

| Command | Description |
|---------|-------------|
| `lcat <path>` | Read file with metadata — text as UTF-8, binary as base64, with mime type, size, and encoding |
| `spool <offset>:<count>` | Sliding window into spooled output (see output limits below) |
| `spool --head=N` | First N lines of spooled output |
| `spool --tail=N` | Last N lines of spooled output |
| `spool --grep=<pattern>` | Search spooled output with line numbers |
| `spool --all` | Return all spooled output |
| `timeout <N> <command>` | Run command with N-second timeout — kills on timeout, returns exit code 124 with `error_type: "timeout"` |

### Output Limits

When command output exceeds 4KB, Rush spools it internally and returns a 512-byte preview with line/byte counts. Use `spool` to navigate the captured output:

```json
{"status":"output_limit","preview":"first 512 bytes...","preview_bytes":512,"stdout_lines":2987,"stdout_bytes":148201,"hint":"Use spool to retrieve: spool 0:50, spool --tail=20, spool --grep=pattern"}
```

### TTY Commands

Interactive commands (vim, nano, less, top, etc.) return structured errors with alternatives:

```json
{"status":"error","error_type":"tty_required","command":"vim","hint":"Use lcat to read files, File.write() to write"}
```

### SSH

For structured remote access, run Rush on both ends:

```bash
ssh server "rush --llm"
```

**Requires Rush on the remote host.** The JSON protocol flows over SSH transparently. The context prompt includes `host` so the LLM always knows which machine it's on. The remote Rush loads its own `init.rush` and `secrets.rush`, so environment, PATH, and API keys are configured per-host.

### SSH Keepalive

In LLM mode, Rush auto-injects `-o ServerAliveInterval=15 -o ServerAliveCountMax=3` on any `ssh` command the agent executes. This detects dead connections in ~45 seconds instead of hanging indefinitely — critical for autonomous agents that can't manually interrupt a stuck session.

### Usage

```bash
# Pipe commands (init.rush runs first, then your command)
echo "ls" | rush --llm

# Multi-command session
printf 'ls\npwd\n' | rush --llm

# With inherited state from parent shell
rush --llm --inherit /tmp/rush-state.json

# From an LLM agent framework
ssh server "rush --llm"   # JSON in, JSON out
```

For full protocol details, see `docs/llm-mode-design.md`.

---

## MCP Server Mode

Rush provides two MCP (Model Context Protocol) servers for integration with Claude Code and Claude Desktop. Both use JSON-RPC 2.0 over stdio.

### Installation

```rush
rush install mcp --claude
```

This registers both servers in:
- **Claude Code** (`~/.claude/mcp.json`) — server entries
- **Claude Desktop** (`~/Library/Application Support/Claude/claude_desktop_config.json`) — server entries
- **Claude Code permissions** (`~/.claude/settings.json`) — auto-allows all 6 tools

Restart Claude Code / Claude Desktop after installing.

### rush --mcp (Local Persistent Session)

Server name: `rush-local`

A stateful MCP server with a persistent PowerShell runspace. Variables, working directory, and environment changes survive across tool calls.

**Tools:**

| Tool | Description |
|------|-------------|
| `rush_execute` | Execute Rush syntax, Unix commands, or PowerShell. State persists. |
| `rush_read_file` | Read a file with MIME detection. Text as UTF-8, binary as base64. |
| `rush_context` | Get current context: hostname, cwd, git branch/dirty, last exit code. |

The session loads `init.rush` and `secrets.rush` at startup, so Claude gets the user's PATH, aliases, and environment.

### rush --mcp-ssh (SSH Gateway)

Server name: `rush-ssh`

A stateless SSH gateway for remote execution. Each tool call runs an independent SSH session. All tools require a `host` parameter.

**Does not require Rush on the remote host** — commands execute in the remote system's default shell. Only needs SSH key-based auth (BatchMode is enforced — no password prompts).

**Tools:**

| Tool | Description |
|------|-------------|
| `rush_execute` | Execute a command on `host` via SSH. |
| `rush_read_file` | Read a file from `host` via SSH. |
| `rush_context` | Get hostname, cwd, git status from `host` via SSH. |

Designed for parallel execution across multiple hosts — Claude can target different servers simultaneously. Connections are multiplexed via OpenSSH ControlMaster for performance (~0ms reconnect vs ~200-500ms handshake).

### Local vs SSH

| Aspect | rush-local | rush-ssh |
|--------|-----------|----------|
| State | Persistent (vars, cwd, env survive) | Stateless per call |
| Hosts | Local machine | Any SSH-accessible host |
| Use case | Multi-step scripts, interactive workflows | Quick commands, multi-host admin |

### Resources

Both servers provide a `rush://lang-spec` resource containing the Rush language specification (YAML), so Claude understands Rush syntax.

---

## SSH Requirements

Rush uses SSH in several ways, with different requirements for each:

| Mode | What it does | Rush on remote? | Auth |
|------|-------------|----------------|------|
| `//ssh:host/path` | UNC file operations | **Yes** (runs `rush -c`) | Key-based |
| `ssh host "rush --llm"` | Structured remote shell | **Yes** | Key-based |
| `rush --mcp-ssh` | MCP gateway for Claude | **Recommended** — persistent session with structured JSON, variable/cwd persistence. Falls back to raw shell if Rush not installed. | Key-based (BatchMode) |
| `sync init ssh host:path` | Config sync | **No** (uses scp) | Key-based |

### Prerequisites

- **SSH key-based auth** — all modes use `BatchMode=yes` or equivalent (no password prompts)
- **`ssh` in PATH** on the local machine
- **Rush on remote** — required for UNC paths and LLM mode, recommended for MCP-SSH (enables persistent sessions with structured output)

### Deploying Rush to Remote Hosts

```bash
# Build for the target platform
dotnet publish -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true

# Copy the binary
scp bin/Release/net8.0/linux-x64/publish/rush server:/usr/local/bin/

# Verify
ssh server "rush --version"
```

### Connection Pooling

Rush automatically uses OpenSSH ControlMaster multiplexing for UNC paths and MCP-SSH. The first connection to a host creates a master socket; subsequent connections reuse it (~0ms vs ~200-500ms handshake). Sockets persist for 60 seconds after the last use.

---

## Tips & Tricks

### PowerShell Passthrough

Any PowerShell cmdlet works directly in Rush:

```rush
Get-Process | Where-Object CPU -gt 50
[Math]::PI                      # → 3.14159265358979
[DateTime]::Now.DayOfWeek       # → Monday
[System.IO.Path]::GetExtension("file.txt")  # → .txt
```

### Debugging

```rush
set -x                          # Trace — prints each command before running
set -e                          # Stop on first error
set -o pipefail                 # Pipeline fails if any segment fails
```

### Quick Navigation

```rush
cd -                            # Toggle between last two directories
..                              # Up one level (cd ..)
...                             # Up two levels (cd ../..)
....                            # Up three levels (cd ../../..)
pushd /tmp                      # Save current dir, go to /tmp
popd                            # Return to saved dir
```

### Mixing Rush and Shell

Rush scripting and shell commands coexist naturally:

```rush
# Rush variable + shell command
pattern = "error"
grep #{pattern} /var/log/syslog | tail 20

# Capture shell output into Rush variable
branch = $(git branch --show-current).strip
count = $(ls | wc -l).strip.to_i

# Use Rush control flow around shell commands
if File.exist?("Makefile")
  make clean && make
else
  warn "no Makefile found"
end
```

### Size Literals

PowerShell size constants work in Rush:

```rush
1kb                             # 1,024
5mb                             # 5,242,880
2gb                             # 2,147,483,648
```

### History Tricks

```rush
!!                              # Repeat last command
!$                              # Last argument of previous command
!42                             # Run command #42 from history
!git                            # Run most recent command starting with "git"
```

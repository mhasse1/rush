# Rush — Feature Reference

**Version:** 0.1.0
**Engine:** Native Rust interpreter (Lexer → Parser → AST → Evaluator)
**Tests:** 678
**Binary:** ~8MB (single static binary, no runtime dependencies)
**Startup:** ~10ms

---

## Shell Execution

### Process Management
- Native fork/exec with inherited TTY (colors, interactive commands work)
- Native pipe chains (pipe/fork/dup2/exec per segment)
- Process groups (setpgid) for proper Ctrl+C handling
- Signal disposition: foreground gets SIG_DFL, background gets SIG_IGN for SIGINT
- Terminal control (tcsetpgrp) for foreground jobs
- Platform abstraction layer (Unix backend, Windows stub)

### Redirections
- `> file`, `>> file`, `< file`
- `2> file`, `2>> file`, `2>&1`, `2>/dev/null`
- `>| file` (clobber override with set -C)
- `<> file` (read+write)
- `<<EOF` heredocs, `<<-EOF` (tab strip)
- `N<&M` fd duplication, `N<&-` / `N>&-` fd close
- Multiple redirections per command

### Expansions (POSIX order)
- Tilde: `~`, `~/path`, `~+`, `~-`
- Parameter: `$VAR`, `${VAR}`, `${var:-default}`, `${var:=default}`, `${var:+word}`, `${var:?error}`, `${#var}`, `${var%pat}`, `${var%%pat}`, `${var#pat}`, `${var##pat}`
- Command substitution: `$(cmd)`, `` `cmd` ``
- Arithmetic: `$((2 + 3 * 4))` with full operator support
- IFS field splitting (whitespace collapses, non-whitespace delimits)
- Pathname/glob: `*`, `?`, `[abc]` (via glob crate)
- Brace: `{a,b,c}`, `{1..5}`, `{a..z}`, nested
- `$'...'` ANSI-C quoting (`\n`, `\t`, `\xHH`, `\ddd`)
- `set -f` disables glob expansion

### Special Parameters
- `$?` exit status, `$$` PID, `$!` last bg PID, `$0` script name
- `$#` arg count, `$@` all args, `$*` all args, `$-` current flags
- `$PPID`, `$RANDOM` (0-32767), `$SECONDS`, `$LINENO`

### Chain Operators
- `cmd1 && cmd2` (AND), `cmd1 || cmd2` (OR), `cmd1 ; cmd2` (sequential)
- `cmd &` (background), `! cmd` (negation)
- Inline env vars: `VAR=val cmd`

### Job Control
- `cmd &` — background execution
- `jobs` — list background jobs
- `fg [%N]` — bring to foreground
- `bg [%N]` — resume in background
- `wait [%N]` — wait for completion
- `kill [-signal] %N` — kill by job ID
- SIGHUP to all jobs on shell exit

### Shell Flags
- `set -e` (errexit), `set -f` (noglob), `set -x` (xtrace)
- `set -C` (noclobber), `set -v` (verbose)
- `set -a` (allexport), `set -b` (notify), `set -m` (monitor)
- `set -n` (noexec), `set -u` (nounset)

---

## Scripting Language

### Variables & Assignment
- `name = value` (no sigils)
- `a, b, c = 1, 2, 3` (multiple)
- `x += 1` (compound: `+=`, `-=`, `*=`, `/=`)
- `readonly VAR` (immutable)

### Strings
- `"hello #{name}"` — interpolation with `#{}`
- `'literal'` — no interpolation
- Escape sequences: `\n`, `\t`, `\r`, `\\`, `\"`, `\e`, `\0`
- Methods: `.upcase`, `.downcase`, `.strip`, `.split`, `.replace`, `.gsub`, `.length`, `.include?`, `.start_with?`, `.end_with?`, `.empty?`, `.reverse`, `.chars`, `.lines`, `.to_i`, `.to_f`

### Arrays
- `[1, 2, 3]` literal, `arr[0]` / `arr[-1]` indexing
- `.each { |x| }`, `.map { |x| }`, `.select { |x| }`, `.reject { |x| }`
- `.sort`, `.reverse`, `.uniq`, `.flatten`, `.join(sep)`
- `.push`, `.first`, `.last`, `.length`, `.sum`, `.min`, `.max`
- `.include?`, `.empty?`, `.reduce { |acc, x| }`
- Block forms: `{ |args| body }` for one-liners, `do |args| ... end` for multi-line — both accepted everywhere a block is expected

### Hashes
- `{a: 1, b: 2}` literal, `h.keys`, `h.values`
- `.length`, `.empty?`, dot-access `h.key`

### Control Flow
- `if/elsif/else/end`, `unless/end`
- `while/end`, `until/end`, `loop/end`
- `for x in collection...end` (also `for x...end` iterates ARGV)
- `parallel x in collection...end` — concurrent iteration
- `parallel(N) x in items...end` — worker pool limit
- `parallel(N, timeout) x in items...end` — with timeout
- `parallel! x in items...end` — fail-fast on error
- `orchestrate...task...end` — dependency graph with concurrent waves
- `case/when/else/end` with `;&` fallthrough, `;;&` continue
- Postfix: `puts "x" if condition`, `break if done`
- `break`, `next`/`continue`, `return`
- Ternary: `x > 0 ? "pos" : "neg"`

### Functions
- `def name(arg, arg2 = default)...end`
- Named params: `def greet(name:)...end`
- Implicit return (last expression) or `return value`
- Recursive calls

### Classes
- `class Name < Parent...end`
- `attr field1, field2`
- `def initialize(args)...end` (constructor)
- `self.prop` access, `ClassName.new(args)`
- Static methods: `def self.method()...end`

### Enums
- `enum Name...end` with members, optional values

### Error Handling
- `try/rescue => e/ensure/end`

### Ranges
- `1..10` (inclusive), `1...10` (exclusive)
- Used in for loops and array indexing

---

## Stdlib

### File
- `File.read(path)`, `File.write(path, content)`, `File.append(path, content)`
- `File.exist?(path)`, `File.delete(path)`, `File.size(path)`
- `File.copy(src, dst)`, `File.move(src, dst)`
- `File.basename(path)`, `File.dirname(path)`, `File.ext(path)`
- `File.read_lines(path)`, `File.read_json(path)`

### Dir
- `Dir.list(path)`, `Dir.list(path, :files)`, `Dir.list(path, :dirs)`
- `Dir.exist?(path)`, `Dir.mkdir(path)`, `Dir.rmdir(path)`
- `Dir.pwd`, `Dir.home`, `Dir.glob(pattern)`

### Time
- `Time.now`, `Time.utc_now`, `Time.today`, `Time.epoch`
- Duration literals: `2.hours`, `30.minutes`, `1.day`

### Ssh
- `Ssh.run("host", "command")` — execute on remote, returns hash `{status, exit_code, stdout, stderr, host}`
- `Ssh.test("host")` — connectivity test, returns bool

### Path
- `Path.join`, `Path.normalize`, `Path.expand`, `Path.exist?`
- `Path.basename`, `Path.dirname`, `Path.ext`, `Path.absolute?`, `Path.sep`

---

## Builtins

### Navigation
- `cd [dir]`, `cd -`, `cd ~`, `..`, `...`, `....`
- `pushd dir`, `popd`, `dirs`
- `pwd`

### Environment
- `export VAR=val`, `unset VAR`
- `alias name='cmd'` (--save), `unalias name`
- `path add/rm/check/dedupe`
- `set [option value]` — shell settings + POSIX flags

### I/O
- `puts`, `print`, `warn`, `die`
- `printf "%s" arg` — formatted output
- `read [-p prompt] var` — read line from stdin
- `echo` — via native command

### Shell Control
- `source file`, `eval args`, `exec cmd`
- `exit [code]`, `trap 'cmd' signal`
- `history [-c]`, `!!`, `!$`, `!N`, `!prefix`
- `reload`, `reload --hard`

### Information
- `help [topic]` — 19 help topics
- `which/type cmd`
- `command [-v] cmd`

### Job Control
- `jobs`, `fg [%N]`, `bg [%N]`, `wait [%N]`, `kill [-sig] %N`

### Utilities
- `o [path]` — cross-platform open
- `init` — edit init.rush in $EDITOR
- `mark "label"`, `---` — visual separator
- `clear`
- `setbg [hex]` — terminal background color
- `:` — null command
- `true`, `false`

---

## Pipeline Operators

After `|`:
- `where field op value`, `where /pattern/`
- `select field1, field2`
- `sort [field] [--desc]`
- `count`, `first [n]`, `last [n]`, `skip n`
- `sum`, `avg`, `min`, `max`
- `distinct`/`uniq`, `reverse`
- `grep pattern`
- `as json`, `as csv`, `from json`, `from csv`
- `objectify` — tabular text → objects
- `tee file` — write + pass through
- `columns 1,3,5`

---

## LLM Mode (`rush --llm`)

JSON wire protocol for AI agent integration:
- Structured results: `{status, exit_code, stdout, stderr, cwd, duration_ms}`
- `lcat path` — file reader with MIME detection + encoding
- `spool offset count` — paginated output for large results
- TTY blocklist (vim, nano, less → helpful hints)
- Context message with host, user, cwd, git, version

## MCP Server (`rush --mcp`)

JSON-RPC 2.0 over stdio:
- `rush_execute` — run commands with structured results
- `rush_read_file` — read files with metadata
- `rush_context` — shell state (cwd, git, user)
- `rush://lang-spec` resource — embedded language spec

## AI Integration

- `ai "question"` — streaming responses
- `ai --agent "task"` — autonomous agent mode
- Providers: Anthropic, OpenAI, Gemini, Ollama
- `--provider`/`-p`, `--model`/`-m` overrides

## Agent Mode (`rush --agent`)

- Local LLM agent (Ollama) with Rush execution layer
- Spawns `rush --llm`, talks to Ollama `/api/chat`
- Auto-injects Rush language spec on first connection
- Configurable: `OLLAMA_HOST`, `RUSH_AGENT_MODEL`, `RUSH_BIN`

---

## REPL

- Line editor: [`rushline`](https://github.com/mhasse1/rushline) — fork of nushell/reedline
- Vi mode (insert/normal) with mode marker at start of input line (» / :)
- Emacs mode (`set emacs`)
- Fish-style autosuggestions from history (suppressed while completion menu is open)
- Tab completion (IdeMenu): commands, paths, Rush methods, env vars, pipe operators
  - Menu renders **below** the prompt — status line stays visible
  - Case-insensitive matching for paths and commands
  - Shift+Tab cycles backward
- Syntax highlighting: Rush keywords + shell commands
- Multi-line editing (if/def/for blocks)
- Persistent history (~/.config/rush/history, 10K entries)
- Reverse search (Ctrl+R) — uses fzf when installed
- Command timing (>500ms shown)
- Training hints after failed commands
- First-run welcome
- [stale] marker when binary updated

## Prompt

```

✓ 16:54  mark@rocinante  src/rush  main*  [12345]
» 
```
Two-line prompt: a status line in `prompt_left` followed by a short mode marker + input. The mode marker (`»` insert, `:` vi-normal) is swapped to `| ` automatically when a completion menu opens.

- Exit status: ✓ (green) / ✗ code (red)
- Time: HH:MM
- User@Host (SSH detection)
- CWD (last 2 levels, ~ for home)
- Git branch + dirty *
- [stale] when binary updated
- [pid] muted, for attaching debuggers / profilers

## Theme

- Dark/light auto-detection (RUSH_BG, COLORFGBG, macOS appearance)
- 256-color LS_COLORS/GREP_COLORS with hue-aware selection
- WCAG contrast floor + CIEDE2000 collision avoidance (ΔE2000 ≥ 5)
- 8 semantic hue families × 3 intensities (Neutral, Info, Emphasis, Success, Warning, Error, Data, Accent)
- `setbg #hex` with OSC 11 + re-theme
- `setbg --flavor pastel|muted|vibrant|mono` — chroma profile
- `setbg --accent #hex` — override Accent family hue
- Per-project `.rushbg` files, autoloaded on cd (REPL)
- Env overrides: `RUSH_BG`, `RUSH_FLAVOR`, `RUSH_ACCENT`
- NO_COLOR support

## Configuration

- `~/.config/rush/config.json` — settings (JSONC with // comments)
- `~/.config/rush/init.rush` — startup script
- `~/.config/rush/secrets.rush` — API keys
- `~/.config/rush/history` — command history

# Rush Feature Reference

Complete feature list for Rush v0.9.x-beta.

---

## Scripting Language

### Variables
- `name = value` — no `$` prefix, no `let`/`var`
- `a, b, c = 1, 2, 3` — multiple assignment
- `name = $(whoami)` — command capture
- `+=`, `-=`, `*=`, `/=` — compound assignment

### Strings
- `"hello #{expr}"` — interpolation (double-quoted only)
- `'literal'` — no interpolation
- `<<END ... END` — heredocs
- Escapes: `\n`, `\t`, `\e` (ESC), `\\`
- Methods: `.upcase`, `.downcase`, `.strip`, `.reverse`, `.chomp`, `.length`, `.empty?`, `.include?(s)`, `.start_with?(s)`, `.end_with?(s)`, `.replace(old, new)`, `.split(sep)`, `.split_whitespace`, `.lines`, `.ljust(n)`, `.rjust(n)`, `.trim_end(chars)`, `.sub(pat, rep)`, `.gsub(pat, rep)`, `.scan(pat)`, `.match(pat)`, `.to_i`, `.to_f`, `.to_s`
- Color methods: `.red`, `.green`, `.blue`, `.cyan`, `.yellow`, `.magenta`, `.white`, `.gray`
- Output methods: `.print` (no newline), `.puts` (with newline)

### Numbers
- `.abs`, `.round(n)`, `.to_i`, `.to_f`, `.to_s`, `.to_currency`, `.to_filesize`, `.to_percent`
- `.times { |i| }` — iteration
- Size literals: `1kb`, `5mb`, `2gb`

### Arrays
- `[1, 2, 3]` — literal
- `.count`, `.length`, `.first`, `.first(n)`, `.last`, `.last(n)`, `.reverse`, `.sort`, `.uniq`, `.flatten`, `.include?(x)`, `.push(x)`, `.skip(n)`, `.join(sep)`
- Iteration: `.each { |x| }`, `.map { |x| }`, `.select { |x| }`, `.reject { |x| }`, `.sort_by { |x| }`, `.find { |x| }`, `.any? { |x| }`, `.all? { |x| }`

### Hashes
- `{ name: "DNS", port: 53 }` — literal
- `h["key"]` or `h.key` — access
- `h.each { |k, v| }` — iteration

### Ranges
- `1..5` — inclusive (1,2,3,4,5)
- `1...5` — exclusive (1,2,3,4)

### Control Flow
- `if`/`elsif`/`else`/`end`
- `unless cond ... end` (no elsif/else)
- `case expr; when val ... else ... end` (also `match`)
- Inline: `puts x if verbose`, `next unless valid`
- Ternary: `condition ? then_expr : else_expr`
- Safe navigation: `obj&.method`

### Loops
- `for item in collection ... end`
- `while cond ... end`
- `until cond ... end`
- `loop ... break if cond ... end`
- `5.times { |i| }`
- `next` (continue), `break`

### Parallel & Orchestration
- `parallel item in collection ... end` — concurrent iteration
- `parallel(N) item in collection ... end` — limit to N threads
- `parallel(N, timeout) item in collection ... end` — with timeout
- `parallel! item in collection ... end` — fail-fast on error
- `orchestrate ... task "name" do ... end ... end` — task dependency graph
- `task "name", after: ["dep1", "dep2"] do ... end` — declare dependencies

### Functions
- `def name(param, default = value) ... end`
- Named arguments: `greet("World", greeting: "Hi")`
- Implicit return (last expression) or explicit `return`

### Classes
- `class Dog < Animal ... end` — inheritance with `<`
- `attr name, breed` — properties
- `attr age: Int = 0` — typed with defaults
- `def initialize(params) ... end` — constructor
- `self.prop` — instance property access (required)
- `def self.method() ... end` — static methods
- `.new()` — instantiation
- `super(args)` / `super.method()`

### Enums
- `enum Status; pending = 0; active = 1; end`
- `Status.active` — access

### Error Handling
- `begin ... rescue ExType => e ... ensure ... end`
- `try ... rescue e ... end` — shorthand
- `die "message"` — raise exception

### Regex
- `/pattern/flags` — literal (flags: `i`, `m`, `x`)
- `=~` (match), `!~` (no match)
- `.sub(pat, rep)`, `.gsub(pat, rep)`, `.scan(pat)`, `.match(pat)`

### Operators
- Comparison: `==`, `!=`, `>`, `<`, `>=`, `<=`
- Logical: `&&`, `||`, `!`, `not`
- Regex: `=~`, `!~`
- Ternary: `condition ? then_expr : else_expr`
- Safe navigation: `obj&.method`

### Platform Blocks
- `macos ... end`, `linux ... end`, `win64 ... end` — OS-conditional
- `win32 ... end` — 32-bit Windows conditional
- `.arch` (x64, arm64), `.version` (OS version comparison)

---

## Standard Library

### File
- `File.read(path)` — entire file as string
- `File.read_lines(path)` — array of lines
- `File.read_json(path)` — parse JSON to object
- `File.read_csv(path)` — parse CSV to array of objects
- `File.write(path, content)` — write/overwrite
- `File.append(path, content)` — append
- `File.exist?(path)` — boolean
- `File.delete(path)` — remove file
- `File.size(path)` — bytes

### Dir
- `Dir.list(path)` — all entries
- `Dir.list(path, :files)` — files only
- `Dir.list(path, :dirs)` — directories only
- `Dir.list(path, :recurse)` — recursive
- `Dir.list(path, :hidden)` — include dotfiles
- `Dir.list(path, :ls)` — files + dirs (exclude `.` and `..`)
- `Dir.exist?(path)` — boolean
- `Dir.mkdir(path)` — create with parents

### Time
- `Time.now` — current datetime
- `Time.utc_now` — UTC datetime
- `Time.today` — midnight today
- `24.hours`, `30.minutes`, `5.seconds`, `7.days` — durations
- `Time.now - 24.hours` — datetime arithmetic

### Ssh
- `Ssh.run("host", "command")` — execute on remote host, returns hash `{status, exit_code, stdout, stderr, host}`
- `Ssh.test("host")` — test connectivity, returns bool

### Path
- `Path.join("a", "b")` — cross-platform join
- `Path.normalize(p)` — convert `\` to `/`
- `Path.expand("~/src")` — expand tilde
- `Path.exist?(p)`, `Path.absolute?(p)`, `Path.basename(p)`, `Path.dirname(p)`, `Path.ext(p)`

---

## Pipeline Operators

| Operator | Description |
|----------|-------------|
| `\| where prop op value` | Filter (`==`, `!=`, `>`, `<`, `>=`, `<=`, `=~`, `!~`) |
| `\| where /regex/` | Match regex against full line |
| `\| where prop /regex/` | Match regex against property |
| `\| select prop1, prop2` | Pick columns |
| `\| sort [--desc] [prop]` | Sort ascending/descending |
| `\| first N` | Take first N items |
| `\| last N` | Take last N items |
| `\| skip N` | Skip first N items |
| `\| count` | Count items |
| `\| distinct` | Unique values |
| `\| sum [prop]` | Sum |
| `\| avg [prop]` | Average |
| `\| min [prop]` | Minimum |
| `\| max [prop]` | Maximum |
| `\| as json \| as csv` | Format output |
| `\| from json \| from csv` | Parse input |
| `\| tee file` | Write to file and pass through |
| `\| columns 1,3,5` | Select columns by index (1-based) |
| `\| objectify [flags]` | Convert text table to objects |

### Objectify
- `--delim REGEX` — field delimiter (default: `\s+`)
- `--no-header` — first line is data
- `--cols name,pid` — manual column names
- `--fixed [positions]` — fixed-width parsing
- `--skip N` — skip first N lines
- Auto-objectify for known commands: `netstat`, `docker ps`, `kubectl get`, `lsof`, `ss`, `free`

---

## Shell Features

### Line Editing
- Vi mode and Emacs mode (`set vi` / `set emacs`)
- Tab completion (paths and commands)
- Ctrl+R reverse history search (Ctrl+R to cycle matches)
- Vi search: `/query` + Enter, `n`/`N` to cycle
- Persistent history across sessions
- Real-time syntax highlighting
- Autosuggestions from history (right arrow to accept)

### Prompt
- Git-aware: branch name, dirty state indicator
- Exit code indicator (✓ green / ✗ red)
- Command timing for slow commands (>500ms)
- Current directory with `~` abbreviation

### Navigation
- `cd -` — toggle previous directory
- `pushd`/`popd`/`dirs` — directory stack
- `~` tilde expansion

### History
- `!!` — repeat last command
- `!$` — last argument of previous command
- `!N` — run Nth command from history
- `history` — show history

### Environment
- `export FOO=bar` — set env var (`--save` to persist)
- `unset FOO` — remove env var
- `env.NAME` or `env["NAME"]` — access env vars
- `env[/REGEX/]` — filter env vars by pattern

### PATH Management
- `path add <dir>` — append to PATH (`--front` for prepend, `--save` to persist)
- `path rm <dir>` — remove from PATH
- `path check` — list entries with existence (✓/✗) and duplicate (↑) flags
- `path dedupe` — remove duplicates (first wins)
- `path add ... end` / `path rm ... end` — multi-line blocks

### Command Chaining
- `&&` — run next if previous succeeded
- `||` — run next if previous failed
- `;` — run next regardless
- `>` / `>>` — redirect output (overwrite / append)
- `cmd &` — run in background
- `jobs` / `fg N` / `kill %N` — job control

### Other
- `alias ll='ls -la'` — define aliases
- `source file.rush` — run scripts
- `$(cmd)` — command substitution
- `<<EOF ... EOF` — heredocs
- `\` — line continuation
- `set` — show/change settings (`--save` to persist)
- `set --secret KEY val` — save API keys to secrets.rush
- `set -e` / `set -x` — stop on error / trace commands
- `sync` — config sync via git/ssh/path
- `init` — edit init.rush in $EDITOR
- `reload` — reload config
- `clear` — clear screen

---

## Theming & Colors

- Auto-detects dark/light terminal background
- Theme-aware colors for `ls`, `grep`, and all output
- `set bg "#hex"` / `setbg "#hex"` — set terminal background
- `setbg --selector` — in-terminal color picker with curated palette
- `--save` — persist globally, `--local` — per-project via `.rushbg`
- Respects `NO_COLOR` environment variable
- Native color env vars auto-set: `LS_COLORS`, `LSCOLORS`, `GREP_COLORS`, `CLICOLOR`

---

## Database (sql)

- `sql add @name --driver sqlite --path ~/db.sqlite` — add connection
- `sql @name "SELECT * FROM table"` — query
- `sql @name "query" --json` / `--csv` — format output
- `sql list` — show connections
- `sql test @name` — test connection
- Inline: `sql sqlite:///path "query"`, `sql postgres://user:pass@host/db "query"`
- Drivers: SQLite, PostgreSQL, ODBC

---

## AI Integration

- `ai "prompt"` — ask AI a question
- `cat log | ai "what went wrong?"` — pipe context to AI
- Providers: Anthropic, OpenAI, Gemini, Ollama
- Custom providers: `~/.config/rush/ai-providers/*.json`
- API keys: `set --secret ANTHROPIC_API_KEY sk-...`

---

## LLM Agent Mode

`rush --llm` provides a JSON wire protocol for AI agent automation.

### Wire Protocol
- Context prompt on startup with host, user, cwd, git state
- Structured result envelope: status, exit_code, stdout, stderr, duration_ms
- Statuses: `success`, `error`, `syntax_error`, `output_limit`

### LLM Builtins
- `lcat <file>` — read files with metadata (mime, size, encoding, binary detection)
- `spool [offset] [length]` — page through large output
- `timeout N command` — run with time limit (exit 124 on timeout)
- `help [topic]` — on-demand reference (19 topics)
- `sql` — database queries with JSON output

### Safety
- 4KB output limit with spooling for overflow
- TTY blocklist: vim, nano, less, more, top, htop (with alternatives)
- Output spooling with `spool search` for targeted retrieval

---

## Help System

`help` — show interactive shell overview
`help <topic>` — detailed reference for a specific topic

### Available Topics

| Category | Topics |
|----------|--------|
| Stdlib | `file`, `dir`, `time`, `ssh`, `path` |
| Types | `strings`, `arrays`, `hashes`, `classes`, `enums` |
| Flow | `functions`, `loops`, `parallel`, `orchestrate`, `control-flow`, `errors` |
| Data | `pipelines`, `pipeline-ops`, `regex`, `objectify`, `sql` |
| Other | `platforms`, `llm-mode`, `mcp` |

Topics are embedded in the binary — no external files needed. Designed for both human and LLM consumption.

---

## Built-in Variables

| Variable | Description |
|----------|-------------|
| `$?` | Last exit status (`.ok?`, `.failed?`, `.code`) |
| `ARGV` | Script arguments array |
| `env.NAME` | Environment variable |
| `env[/REGEX/]` | Filter env vars by pattern |
| `__FILE__` | Current script path |
| `__DIR__` | Current script directory |

---

## Built-in Functions

| Function | Description |
|----------|-------------|
| `puts expr` | Print with newline |
| `print expr` | Print without newline |
| `warn expr` | Print to stderr |
| `die msg` | Raise fatal error |
| `exit N` | Exit with code |
| `sleep N` | Sleep N seconds |
| `ask prompt` | Interactive input |

---

## CLI Modes

```
rush                  Interactive shell (REPL)
rush -c 'command'     Execute command and exit
rush script.rush      Run a Rush script
rush --llm            LLM agent mode (JSON wire protocol)
rush --agent 'task'   Local LLM agent (Ollama + Rush)
rush --mcp            MCP server mode (JSON-RPC over stdio)
rush --mcp-ssh        MCP SSH gateway (multi-host)
rush --version        Show version
rush --help           Show help
```

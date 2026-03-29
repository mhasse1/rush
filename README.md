# Rush

A cross-platform (macOS, Linux, Windows), modern shell with clean syntax, structured data pipelines, and built-in LLM agent protocol and MCP support.

Rush gives you clean scripting syntax, Unix shell commands, and PowerShell 7's structured object pipeline — in one shell, on every platform.

```rush
# Shell commands just work
ls -la /var/log | grep ".log"

# Structured pipelines — filter, select, format
ps aux | where CPU > 50 | select Name, CPU | sort --desc

# Clean scripting — no $ prefix, blocks use end
files = Dir.list(".", :files)
large = files.select { |f| File.size(f) > 1mb }
large.each { |f| puts "#{f}: #{File.size(f).to_filesize}" }

# LLM agent mode — JSON wire protocol
rush --llm   # structured I/O for AI agents
```

## Why Rush?

**For humans:** Clean syntax. No `$()` soup, no `[[ ]]` vs `[ ]` confusion, no semicolons. Tab completion, vi/emacs modes, syntax highlighting, git-aware prompt — a shell that doesn't fight you.

**For LLM agents:** `rush --llm` provides structured JSON input/output, typed errors, output spooling, a TTY blocklist, and a help system — everything an agent needs to operate a machine without parsing bash text.

**For scripts:** A real scripting language with classes, error handling, named arguments, and a File/Dir/Time stdlib — without leaving the shell.

**For Windows admins:** `ps...end` blocks pass raw PowerShell through without Rush expansion — `$_`, script blocks, and cmdlets work untouched. Platform blocks (`win64`, `ps5`, `win32`) target specific Windows layers in one script.

## Install

```bash
# macOS (Homebrew)
brew install mhasse1/tap/rush

# Manual (all platforms) — download from GitHub Releases
# https://github.com/mhasse1/rush/releases

# Build from source
dotnet publish -c Release -r osx-arm64    # or linux-x64, win-x64
./install.sh
```

Requires .NET 10 SDK to build. Published binaries are self-contained (no runtime needed).

## Feature Highlights

### Structured Data Pipelines

```rush
# Filter, select, aggregate — like SQL for your terminal
ps aux | where CPU > 10 | select Name, CPU, Memory | sort --desc
docker ps | where Status =~ /Up/ | select Names, Status
netstat | where State == "ESTABLISHED" | count

# Format output
ps | as json                    # JSON output
cat data.csv | from csv | where age > 30 | as json

# Auto-objectify — known commands become structured automatically
netstat | where State == "LISTEN" | select Local, PID
```

### Clean Scripting Language

```rush
# Variables — no sigils
name = "world"
count = 42
items = [1, 2, 3]
config = { host: "localhost", port: 8080 }

# String interpolation
puts "hello #{name}, you have #{items.count} items"

# Functions with defaults and named args
def deploy(env, dry_run = false)
  if dry_run
    puts "! would deploy to #{env}"
  else
    rsync -avz ./dist/ "#{env}.example.com:/var/www/"
    puts "> deployed to #{env}"
  end
end
deploy("staging", dry_run: true)

# Blocks and iteration
files = Dir.list("src", :recurse)
files.select { |f| f =~ /\.rs$/ }
     .sort_by { |f| File.size(f) }
     .reverse
     .first(10)
     .each { |f| puts f }
```

### Classes, Enums, Error Handling

```rush
class Server
  attr host: String, port: Int = 8080

  def initialize(host)
    self.host = host
  end

  def url
    return "http://#{self.host}:#{self.port}"
  end
end

s = Server.new("localhost")
puts s.url

# Error handling
begin
  data = File.read_json("config.json")
rescue => e
  die "config error: #{e.message}"
end
```

### Standard Library

```rush
# File I/O
content = File.read("data.txt")
lines = File.read_lines("log.txt")
config = File.read_json("config.json")
File.write("/tmp/out.txt", "hello")
File.exist?("config.json")

# Directory operations
Dir.list(".", :files)           # files only
Dir.list("src", :recurse)      # recursive
Dir.mkdir("/tmp/myapp/logs")    # with parents

# Time and durations
t = Time.now
recent = Time.now - 24.hours
```

### Platform Blocks

```rush
# Code runs only on the matching OS
macos
  export HOMEBREW_NO_AUTO_UPDATE=1
end

linux
  export LD_LIBRARY_PATH="/usr/local/lib"
end

win64
  export TEMP="C:\\Temp"
end

# Property conditions
macos.arch == "arm64"
  puts "Apple Silicon"
end

linux.version >= "6.0"
  puts "Modern kernel"
end
```

### Windows: One Script, Every Layer

No other shell wraps all Windows execution environments in one syntax. Rush gives you `ps`, `ps5`, and `win32` blocks — each targeting a different PowerShell layer, all in the same script:

```rush
# PowerShell 7 — raw passthrough, no Rush expansion
# $_, script blocks, and cmdlets work untouched
ps
  Get-Service | Where-Object { $_.Status -eq "Running" }
  $fw = Get-NetFirewallProfile
  $fw | Format-Table Name, Enabled
end

# PowerShell 5.1 — legacy modules (AD, Exchange, older management tools)
ps5
  Import-Module ActiveDirectory
  Get-ADUser -Filter * | Select-Object Name, Enabled
end

# 32-bit PowerShell 5.1 — OLEDB/ODBC drivers (Access, Excel, Business Central)
win32
  $conn = New-Object System.Data.OleDb.OleDbConnection("Provider=Microsoft.ACE.OLEDB.12.0;...")
  $conn.Open()
end
```

| Block | Engine | Use case |
|-------|--------|----------|
| `ps` | PowerShell 7 (cross-platform) | Cmdlets with `$_`, script blocks, Where-Object |
| `ps5` | PowerShell 5.1 (Windows) | Legacy modules: AD, Exchange, older management tools |
| `win32` | 32-bit PS 5.1 (Windows) | 32-bit OLEDB/ODBC: Access, Excel, Business Central |
| `win64` | Rush syntax (Windows) | Windows-specific Rush code |

Version gating: `ps.version >= "7.4"` targets specific PowerShell versions.

### Database Queries

```rush
# SQLite, PostgreSQL, ODBC — built in
sql add @mydb --driver sqlite --path ~/data.db
sql @mydb "SELECT name, email FROM users WHERE active = 1"
sql @mydb "SELECT * FROM orders" --json
```

### Windows Network Paths

```rush
# SMB shares — forward slashes always, Rush translates for Windows
cd //fileserver/shared/docs
ls //nas/backups/2026/
cp //server/share/report.xlsx ./

# SSH remote files — works on all platforms
cat //ssh:server/etc/hosts
cp //ssh:server/data/file.csv .
```

### Built-in AI Assistant

```rush
# Ask a question
ai "how do I find large files on macOS?"

# Pipe context to AI — the killer feature
cat error.log | ai "what went wrong?"
git diff | ai "review this change"
ps aux | ai "anything unusual?"
sql @prod "SELECT * FROM orders WHERE status='failed'" | ai "summarize these failures"

# Use any provider
set --save aiProvider anthropic    # or openai, gemini, ollama
set --save aiModel claude-sonnet-4-20250514

# Custom providers
# ~/.config/rush/ai-providers/my-llm.json
```

Supports Anthropic, OpenAI, Gemini, and Ollama out of the box. Pipe anything to `ai` — logs, diffs, query results, command output — and get an answer in context.

### LLM Agent Mode

`rush --llm` is a JSON wire protocol designed for AI agents. The agent reads structured JSON — no terminal parsing needed:

```
$ rush --llm

← Rush emits context (ready prompt):
{"ready":true,"host":"web-prod","user":"deploy","cwd":"/var/www","git_branch":"main","shell":"rush"}

→ Agent sends a command:
ls src | where /\.cs$/ | count

← Rush returns structured result:
{"status":"success","exit_code":0,"cwd":"/var/www","stdout":"47","duration_ms":12}

→ Agent sends a failing command:
cat /nonexistent

← Rush returns structured error:
{"status":"error","exit_code":1,"cwd":"/var/www","stderr":"cat: /nonexistent: No such file or directory","duration_ms":3}
```

**Built-in LLM commands:**
- `lcat file` — read files with metadata (mime type, size, encoding, binary → base64)
- `spool` — retrieve output that exceeded the 4KB capture limit
- `help <topic>` — on-demand reference (22 topics) to reduce context burn
- `timeout N command` — prevent runaway commands

**Safety features:**
- Output capped at 4KB — excess goes to a spool buffer the agent can page through
- TTY blocklist — `vim`, `top`, `less` etc. are blocked with suggested alternatives
- Structured errors — exit codes, stderr, and error types in every response

### MCP Server Integration

Rush provides two MCP servers for Claude Code and Claude Desktop:

```bash
rush install mcp --claude    # registers both servers
```

- **rush-local** — persistent session on local machine (variables, cwd survive between calls)
- **rush-ssh** — SSH gateway for remote hosts (parallel execution, auto-detects Rush on remote)

Both servers expose `rush_execute`, `rush_read_file`, and `rush_context` tools, plus a `rush://lang-spec` resource so Claude understands Rush syntax.

### Shell Features

- Vi and Emacs line editing modes
- Tab completion (paths, commands, pipeline operators, flags)
- Ctrl+R reverse history search, vi `/` search with `n`/`N` cycling
- Real-time syntax highlighting
- Git-aware prompt with branch and dirty state
- Theme-aware colors — auto-adapts to dark and light terminals
- `setbg --selector` — in-terminal color picker for background
- Per-directory themes via `.rushbg` files
- PATH management (`path add`, `path rm`, `path check`, `path dedupe`)
- `help <topic>` — 22 built-in reference topics, `keyword --help` routing
- Bash-to-Rush training hints after commands
- `ai "prompt"` — built-in AI assistant (Anthropic, OpenAI, Gemini, Ollama)
- Background jobs, command chaining, output redirection, heredocs

## CLI

```
rush                  Interactive shell
rush -c 'command'     Execute and exit
rush script.rush      Run a script
rush --llm            LLM agent mode (JSON wire protocol)
rush --mcp            MCP server (local persistent session)
rush --mcp-ssh        MCP server (SSH gateway)
rush --version        Show version
rush --help           Show help
```

## Configuration

```
~/.config/rush/
  config.json       Settings and saved aliases (managed by set/alias --save)
  init.rush         Startup script — PATH, exports, functions, prompt
  secrets.rush      API keys and tokens (never synced)
```

## Architecture

Rush transpiles its syntax to PowerShell 7, giving you access to the full .NET runtime. Shell commands run natively on Unix — Rush only translates what it recognizes and passes everything else through.

```
Rush source → Lexer → Parser → AST → Transpiler → PowerShell 7 engine
                                                  ↘ native commands (ls, git, etc.)
```

Built on the MIT-licensed `Microsoft.PowerShell.SDK` (.NET 10 LTS). Single self-contained binary — no runtime dependencies.

## Documentation

- [User Manual](docs/user-manual.md) — comprehensive guide
- [Feature Reference](docs/rush-features.md) — complete feature list
- [LLM Mode Design](docs/llm-mode-design.md) — wire protocol specification
- [Language Spec](docs/rush-lang-spec.yaml) — compact syntax reference
- [Contributing](CONTRIBUTING.md) — development setup, project structure, how to add features

## License

Business Source License 1.1 (BSL). Source-available — read, build, modify, and use freely. Each version converts to Apache 2.0 after four years. See [LICENSE](LICENSE) for details.

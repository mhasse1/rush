# Rush

A modern shell with clean syntax, structured data pipelines, and a built-in LLM agent protocol.

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

## Install

```bash
# macOS (Homebrew)
brew install mhasse1/tap/rush

# Build from source (any platform)
dotnet publish -c Release -r osx-arm64    # or linux-x64, win-x64
./install.sh
```

Requires .NET 8 SDK to build. Published binaries are self-contained (no runtime needed).

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

### Database Queries

```rush
# SQLite, PostgreSQL, ODBC — built in
sql add @mydb --driver sqlite --path ~/data.db
sql @mydb "SELECT name, email FROM users WHERE active = 1"
sql @mydb "SELECT * FROM orders" --json
```

### Platform Blocks

```rush
macos
  export HOMEBREW_NO_AUTO_UPDATE=1
end

linux
  export LD_LIBRARY_PATH="/usr/local/lib"
end

win64
  puts "Windows detected"
end
```

### LLM Agent Mode

`rush --llm` is a JSON wire protocol designed for AI agents:

```json
← {"ready":true,"host":"server","user":"mark","cwd":"/home/mark","shell":"rush"}
→ ps aux | where CPU > 50 | as json
← {"status":"success","exit_code":0,"stdout":"[...]","duration_ms":45}
```

- Structured errors with exit codes, stderr separation, and error types
- Output spooling — large output is capped at 4KB with a spool buffer for paging
- TTY blocklist — interactive commands (vim, top, less) are blocked with alternatives
- `lcat` — read files with metadata (mime type, size, encoding, binary detection)
- `help <topic>` — on-demand reference the LLM can query to reduce context burn
- `timeout N command` — prevent runaway commands

### Shell Features

- Vi and Emacs line editing modes
- Tab completion (paths + commands)
- Ctrl+R reverse history search, vi `/` search with `n`/`N` cycling
- Real-time syntax highlighting
- Git-aware prompt with branch and dirty state
- Theme-aware colors — auto-adapts to dark and light terminals
- `setbg --selector` — in-terminal color picker for background
- PATH management (`path add`, `path rm`, `path check`, `path dedupe`)
- `help <topic>` — 19 built-in reference topics
- `ai "prompt"` — built-in AI assistant (Anthropic, OpenAI, Gemini, Ollama)
- Background jobs, command chaining, output redirection, heredocs

## CLI

```
rush                  Interactive shell
rush -c 'command'     Execute and exit
rush script.rush      Run a script
rush --llm            LLM agent mode (JSON wire protocol)
rush --version        Show version
rush --help           Show help
```

## Configuration

```bash
~/.config/rush/config.json    # Settings (edit mode, theme, aliases, AI provider)
~/.config/rush/init.rush      # Startup script (runs on shell launch)
~/.config/rush/secrets.rush   # API keys (set --secret KEY value)
```

## Architecture

Rush transpiles its syntax to PowerShell 7, giving you access to the full .NET runtime. Shell commands run natively on Unix — Rush only translates what it recognizes and passes everything else through.

```
Rush source → Lexer → Parser → AST → Transpiler → PowerShell 7 engine
                                                  ↘ native commands (ls, git, etc.)
```

Built on the MIT-licensed `Microsoft.PowerShell.SDK`. Single self-contained binary — no runtime dependencies.

## Documentation

- [User Manual](docs/user-manual.md) — comprehensive guide
- [Feature Reference](docs/rush-features.md) — complete feature list
- [LLM Mode Design](docs/llm-mode-design.md) — wire protocol specification
- [Language Spec](docs/rush-lang-spec.yaml) — compact syntax reference

## License

Business Source License 1.1 (BSL). Source-available — read, build, modify, and use freely. Each version converts to Apache 2.0 after four years. See [LICENSE](LICENSE) for details.

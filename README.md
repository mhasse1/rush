# Rush

A Unix-style shell built in Rust, designed for both humans and LLM agents.

Rush gives you clean scripting syntax, structured data pipelines, native concurrency, and a JSON wire protocol for AI agents -- in one shell, on every platform.

```rush
# Shell commands just work
ls -la /var/log | grep ".log"

# Structured pipelines
ps aux | where CPU > 50 | select Name, CPU | sort --desc

# Clean scripting -- no $ prefix, #{} interpolation, end blocks
files = Dir.list("src", :recurse)
large = files.select { |f| File.size(f) > 1mb }
large.each { |f| puts "#{f}: #{File.size(f).to_filesize}" }

# LLM agent mode
rush --llm   # structured JSON I/O for AI agents
```

## Rush vs Bash

```bash
# Bash                                    # Rush
name="world"                              name = "world"
echo "hello ${name}"                      puts "hello #{name}"
if [ -f "$file" ]; then                   if File.exist?(file)
  cat "$file" | wc -l                       File.read_lines(file).length
fi                                        end
files=$(find . -name "*.log")             files = Dir.glob("*.log")
for f in $files; do                       for f in files
  size=$(stat -f%z "$f")                    puts "#{f}: #{File.size(f)}"
done                                      end
```

## Why Rush?

**For humans.** No `$()` soup, no `[[ ]]` vs `[ ]` confusion. Variables without sigils, blocks with `end`, `#{}` interpolation. Vi/emacs modes, autosuggestions, syntax highlighting, git-aware prompt.

**For LLM agents.** `rush --llm` provides structured JSON I/O, typed errors, output spooling, and a TTY blocklist. `rush --mcp` exposes tools for Claude Code. No terminal escape parsing needed.

**For scripts.** Functions, classes, enums, error handling, native concurrency (`parallel`, `orchestrate`), and a File/Dir/Time stdlib -- without leaving the shell.

## Install

```bash
# Build from source (requires Rust toolchain)
git clone https://github.com/mhasse1/rush
cd rush
cargo build --release
sudo cp target/release/rush-cli /usr/local/bin/rush

# Or use the install script
./install.sh
```

## Features

### Structured Data Pipelines

```rush
ps aux | where CPU > 10 | select Name, CPU, Memory | sort --desc
docker ps | where Status =~ /Up/ | select Names, Status
netstat | where State == "ESTABLISHED" | count

cat data.csv | from csv | where age > 30 | as json
```

Pipeline operators: `where`, `select`, `sort`, `count`, `first`, `last`, `skip`, `sum`, `avg`, `min`, `max`, `distinct`, `reverse`, `objectify`, `as json`, `as csv`, `from json`, `from csv`, `tee`, `columns`.

### Scripting Language

```rush
# Variables, arrays, hashes
name = "world"
items = [1, 2, 3]
config = { host: "localhost", port: 8080 }

# String interpolation
puts "hello #{name}, #{items.length} items"

# Functions with defaults and named args
def deploy(env, dry_run = false)
  if dry_run
    puts "would deploy to #{env}"
  else
    rsync -avz ./dist/ "#{env}.example.com:/var/www/"
  end
end
deploy("staging", dry_run: true)

# Classes
class Server
  attr host, port
  def url
    return "http://#{self.host}:#{self.port}"
  end
end

# Error handling
try
  data = File.read_json("config.json")
rescue => e
  die "config error: #{e.message}"
end
```

Control flow: `if/elsif/else/end`, `unless`, `while`, `until`, `loop`, `for..in`, `case/when`, postfix conditionals, ternary operator. Full POSIX expansions: tilde, parameter, command substitution, arithmetic, glob, brace.

### Standard Library

```rush
# File I/O -- cross-platform, no external deps
File.read("data.txt")
File.write("/tmp/out.txt", "hello")
File.read_json("config.json")
File.exist?("config.json")
File.size("archive.tar.gz")

# Directories
Dir.list("src", :recurse)
Dir.glob("**/*.rs")
Dir.mkdir("/tmp/myapp/logs")

# Time and durations
t = Time.now
recent = Time.now - 24.hours
```

### REPL

- Vi mode (default) and Emacs mode with mode indicator
- Fish-style autosuggestions from history
- Tab completion: commands, paths, methods, env vars, pipeline operators
- Syntax highlighting for Rush keywords and shell commands
- Ctrl+R reverse search, vi `/` search with `n`/`N`
- Multi-line editing for blocks
- Persistent history (10K entries)
- Git-aware prompt with branch and dirty state
- Dark/light terminal auto-detection

### Shell Features

- 50+ builtins: `cd`, `pushd/popd`, `export`, `alias`, `source`, `eval`, `exec`, `trap`, `history`, `jobs`, `fg`, `bg`, `wait`, `kill`, `help`, `path`, `set`, and more
- Background jobs with proper signal handling and terminal control
- Heredocs, redirections (including fd duplication), process substitution
- POSIX shell flags: `set -e`, `set -x`, `set -u`, `set -C`, `set -f`, and others
- PATH management: `path add`, `path rm`, `path check`, `path dedupe`
- Platform blocks: `macos`, `linux`, `win64` with property conditions

### Concurrency and Orchestration

```rush
# Concurrent iteration with worker pool
parallel(4) host in ["web1", "web2", "web3", "web4"]
  result = Ssh.run(host, "cd /app && git pull && cargo build --release")
  puts "#{host}: #{result["status"]}"
end

# Dependency-ordered task graph
orchestrate
  task "build-linux" do
    Ssh.run("linux-host", "cargo build --release")
  end
  task "build-mac" do
    Ssh.run("mac-host", "cargo build --release")
  end
  task "deploy", after: ["build-linux", "build-mac"] do
    Ssh.run("prod", "sudo systemctl restart app")
  end
end
```

`parallel` runs iterations concurrently with optional worker limits, timeouts, and fail-fast mode. `orchestrate` runs a dependency graph where independent tasks execute in parallel and dependent tasks wait. Both return structured results. `Ssh.run` gives agents and scripts structured access to remote hosts without parsing text.



### Built-in AI Assistant

```rush
ai "how do I find large files on macOS?"
cat error.log | ai "what went wrong?"
git diff | ai "review this change"
```

Supports Anthropic, OpenAI, Gemini, and Ollama. Pipe anything to `ai` and get an answer in context.

## LLM Agent Mode

`rush --llm` is a JSON wire protocol for AI agents:

```
$ rush --llm
<- {"ready":true,"host":"web-prod","user":"deploy","cwd":"/var/www","git_branch":"main","shell":"rush"}
-> ls src | count
<- {"status":"success","exit_code":0,"cwd":"/var/www","stdout":"47","duration_ms":12}
```

Built-in commands: `lcat` (file reader with MIME detection), `spool` (paginated output), `help` (on-demand reference), `timeout` (runaway prevention). Output capped at 4KB with spool for overflow. TTY blocklist prevents interactive commands with suggested alternatives.

LLM/MCP mode is a command executor, not a REPL — bare expressions (`y.sum`, `x + 1`) evaluate silently. Use `puts` or `print` when you want the value to appear in stdout.

**Local LLM agent.** `rush --agent "task"` runs a Claude Code-style agent loop against a local Ollama instance. The agent receives the Rush language spec automatically, generates Rush commands, gets structured JSON results, and iterates.

## MCP Server

Rush provides MCP servers for Claude Code and Claude Desktop:

```bash
rush install mcp --claude    # registers both servers
```

- **rush-local** -- persistent local session (variables, cwd survive between calls)
- **rush-ssh** -- SSH gateway for remote hosts (auto-detects Rush on remote)

Tools: `rush_execute`, `rush_read_file`, `rush_write_file`, `rush_context`. Includes `rush://lang-spec` resource so the LLM understands Rush syntax.

## Plugin System

Extend Rush with plugins in any language via JSON wire protocol:

```rush
# PowerShell plugin
plugin.ps
  Get-Service | Where-Object { $_.Status -eq "Running" }
end

# Python plugin
plugin.python
  import json
  data = json.loads(input())
  print(json.dumps({"result": len(data)}))
end
```

## CLI

```
rush                  Interactive shell
rush -c 'command'     Execute and exit
rush script.rush      Run a script
rush --llm            LLM agent mode (JSON wire protocol)
rush --agent "task"   Local LLM agent (Ollama)
rush --mcp            MCP server (local persistent session)
rush --mcp-ssh        MCP server (SSH gateway)
rush --version        Show version
```

## Configuration

```
~/.config/rush/
  config.json       Settings and saved aliases
  init.rush         Startup script (PATH, exports, functions, prompt)
  secrets.rush      API keys and tokens (never synced)
  history           Command history
```

Config sync across machines via git, ssh, or shared path.

## Architecture

Rush is built in Rust as a workspace of four crates:

```
Rush source -> Lexer -> Parser -> AST -> Evaluator (native Rust)
                                       -> fork/exec for shell commands
```

- **rush-core** -- lexer, parser, AST, evaluator, stdlib, pipeline operators
- **rush-cli** -- REPL, line editor, prompt, themes, AI, MCP, LLM mode
- **rush-line** -- ground-up line editor (cursor-relative painter, signal-aware input)
- **rush-agent** -- local LLM agent loop (Ollama integration)
- **rush-ps-bridge** -- PowerShell companion binary, invoked via `plugin.ps` / `plugin.ps5`

678 tests. ~5MB binary. ~10ms startup. CI on macOS, Linux, and Windows.

## Documentation

- [Feature Reference](docs/rust-rush-features.md) -- complete feature list
- [Language Spec](docs/rush-lang-spec.yaml) -- compact syntax reference
- [LLM Mode Design](docs/llm-mode-design.md) -- wire protocol specification

## License

Business Source License 1.1. Source-available -- read, build, modify, and use freely. Each version converts to Apache 2.0 after four years. See [LICENSE](LICENSE) for details.

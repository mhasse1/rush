# Rush: A Shell for Humans and Machines

*A Unix shell with clean scripting syntax and structured data pipelines, designed equally for humans typing at a terminal and LLM agents executing programmatically.*

## The opportunity

Bash has thrived for 35 years for good reason: it's everywhere, it's stable, and it gets the job done. But the world it was designed for was text-in, text-out, one human at a terminal. Two things have changed:

1. **Structured data is the norm.** We spend increasing time wrangling JSON, CSV, and tabular command output with `jq`, `awk`, and `sed`. These tools work, but they weren't designed to compose naturally with a shell pipeline.

2. **LLM agents are a new class of shell user.** They're the fastest-growing consumers of shell commands, and they're parsing terminal escape codes, guessing at column boundaries in `ps` output, and wrapping every call in error-handling boilerplate. Every agent framework reinvents "run a shell command and parse the output."

Rush builds on the same Unix / POSIX foundation as Bash and ZSH (fork/exec, pipes, signals, PATH) while adding the structured data layer and machine interface that modern workflows need.

## What Rush is

A Unix shell that runs your existing commands (`ls`, `git`, `docker`, `kubectl`) while giving you a real programming language and structured data pipelines:

```rush
# Your commands still work
ls -la /var/log | grep ".log"
git status
docker ps

# But now you can also do this
ps aux | where CPU > 50 | select Name, CPU | sort --desc
files = Dir.glob("**/*.log")
large = files.select { |f| File.size(f) > 1mb }
cat data.csv | from csv | where age > 30 | as json
```

No new coreutils to learn. Your `ls`, `grep`, `find`, `git` all work. But when you pipe their output through Rush's structured operators, text becomes queryable data.

## What makes it different

**Clean, instinctive syntax.** Spaces around `=` just work. `if File.exist?(f)...end` instead of `if [ -f "$f" ]; then...fi`. No sigils on variables. Blocks close with `end`. Functions, classes, and error handling. A real scripting language without leaving the shell, at a fraction of the ceremony most scripting languages require.

**Structured pipelines.** `objectify` turns text output from any command into arrays of hashes. Then `where`, `select`, `sort`, `as json`, `from csv` let you query it. Configurable per-command parsing hints mean `docker ps`, `netstat`, and `lsblk` all objectify correctly out of the box. Text in, structured data out, no `awk` required.

**Files with spaces just work.** No quoting gymnastics, no `IFS` hacks. Rush handles filenames with spaces, special characters, and Unicode natively: in variables, loops, globs, and pipelines. The thing that breaks half of all bash scripts isn't an issue here.

**Machine-native.** Rush was designed from day one for both humans at a terminal and LLM agents executing programmatically. Not bolted on after the fact. The same structured pipelines that make humans productive give agents typed, parseable output.

**A REPL you want to live in.** Vi mode (default) and Emacs mode with a mode indicator. Fish-style autosuggestions from history. Tab completion for commands, paths, methods, and pipeline operators. Syntax highlighting. Git-aware prompt with branch and dirty state. fzf-powered history search when available. Propagate your config across machines via git, ssh, or a shared path. This is your daily-driver shell, not a scripting-only tool.

---

## For LLM agents: `rush --llm`

`rush --llm` is a JSON wire protocol purpose-built for AI agents:

```
$ rush --llm
← {"ready":true,"host":"web-prod","user":"deploy","cwd":"/var/www","git_branch":"main"}
→ ps aux | where CPU > 10 | as json
← {"status":"success","exit_code":0,"stdout":"[{\"USER\":\"root\",...}]","duration_ms":45}
→ cat /etc/hosts
← {"status":"success","exit_code":0,"stdout":"127.0.0.1 localhost\n...","duration_ms":2}
```

Every response is structured JSON with status, exit code, timing, and cwd. No escape codes, no terminal noise, no guessing. Additional features:

- **Output spooling**: Large outputs are capped at 4KB with `spool` for paginated retrieval and `spool search` for finding relevant lines without reading page by page. Agents don't blow their context windows.
- **TTY blocklist**: Interactive commands (`vim`, `top`, `less`) are blocked with suggested alternatives, so agents can't get stuck in a TUI.
- **`lcat`**: File reader with MIME detection. Agents get file contents without worrying about binary files or encoding.
- **Structured errors**: Parse errors, command failures, and timeouts all have typed JSON responses.
- **Self-teaching**: The full Rush language specification is embedded in the binary and delivered on first connection. Any LLM that connects learns Rush syntax automatically. No internet searches, no Stack Overflow mining.

**For agent builders**: If your framework already shells out to bash, replacing `bash -c` with `rush --llm` gets you structured output, typed errors, and no escape-code parsing, in one line of integration code.

## For IDEs and AI assistants: MCP servers

Rush ships two MCP servers that work with any MCP-compatible client (Claude Code, Claude Desktop, and others):

```bash
rush install mcp --claude    # one command, registers both servers
```

- **rush-local**: Persistent local session. Variables, cwd, and function definitions survive between tool calls. The LLM builds up state across a conversation instead of starting from scratch each time.
- **rush-ssh**: SSH gateway. Claude Code can execute on remote hosts (in parallel) through Rush, with automatic detection of Rush on the remote side to take advantage of llm-mode at both ends.

Tools exposed: `rush_execute`, `rush_read_file`, `rush_write_file` (with base64 encoding for binary files), `rush_context`. The same embedded language spec is available here as a `rush://lang-spec` resource.

**Why this matters**: Most MCP shell integrations are stateless; every tool call is a fresh shell. Rush's MCP server is a persistent session. An agent can `cd` into a directory, set variables, define functions, and they're all still there on the next call. When the LLM does run into an issue, it can run one command at a time to debug.

## Built-in AI assistant

Rush has a native `ai` command that pipes context to LLMs:

```rush
cat error.log | ai "what went wrong?"
git diff | ai "review this change"
ps aux | where CPU > 80 | ai "should I be worried?"
```

Supports Anthropic, OpenAI, Gemini, and Ollama. Pipe anything to `ai` and get an answer in context.

---

## Plugin system and the PowerShell bridge

Rush is extensible through a plugin system that works with any language. Plugins are companion binaries that speak the same JSON wire protocol as `rush --llm`:

```rush
plugin.ps
  Get-Service | Where-Object { $_.Status -eq "Running" }
  Get-Process | Sort-Object CPU -Descending | Select-Object -First 10
end
```

Rush discovers companion binaries named `rush-NAME` on PATH or in `~/.config/rush/plugins/`. The companion just needs to speak JSON lines on stdio. This is the same protocol agents use, which means any tool that can talk to `rush --llm` can also be a plugin.

**rush-powershell** is the first companion, a sister project that wraps the PowerShell SDK (.NET 10) behind the plugin protocol. It gives Rush access to the full PowerShell ecosystem (Active Directory, Exchange, Azure, DSC, WMI) without making Rush depend on .NET. The base shell stays a ~5MB Rust binary with ~10ms startup. PowerShell is there when you need it, absent when you don't.

The same mechanism extends to any runtime. `rush-python`, `rush-ruby`, `rush-node` would each be a small binary that accepts commands on stdin and returns JSON results. The plugin protocol is the integration surface, and any language that can read and write JSON can participate.

## How Rush fits in

- **It's another shell on your system, like bash or zsh.** Your existing commands, scripts, and muscle memory still work. Rush adds structured pipelines and a scripting language on top of the same Unix primitives.
- **A real POSIX-compliant shell.** `cd`, `export`, pipe, redirect, background jobs, signal handling, PATH management. Rush is a login shell, not a language with a shell bolted on.
- **Lighter than PowerShell.** No .NET dependency. Unix-native `fork/exec`. ~5MB binary. ~10ms startup. Cross-platform without a runtime. PowerShell is available as a plugin when you need its ecosystem.
- **Different from nushell.** Rush runs bash commands natively (no `^` prefix). Familiar syntax for anyone who's used a modern scripting language. Designed to feel like a natural evolution of the shells you already know.

## Technical details

- **Written in Rust.** Three crates. rush-core (language engine), rush-cli (REPL and builtins), rush-ps-bridge (PowerShell interop).
- **591 tests (and growing).** CI on macOS, Linux, and Windows.
- **REPL.** Vi and Emacs modes, fish-style autosuggestions, tab completion, syntax highlighting, git-aware prompt, fzf integration for history search, and auto-theming to keep ls legible on any background. You can even set backgrounds per project to visually identify your terminals.
- **Cross-platform paths.** Windows backslashes normalized to `/` internally. Scripts work on all platforms without modification. `.native_path` for Windows-specific handoff.
- **BSL 1.1.** Source-available. Read, build, modify, use freely. Each version converts to Apache 2.0 after four years.
- **Giving back to the community.** The PS bridge is MIT licensed and we will be contributing back to the Reedline project as we continue to improve its vi mode.

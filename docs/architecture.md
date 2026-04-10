# Rush Architecture: Rust Native Engine

## Pipeline: Source → Execution

```
Input → Lexer → Parser → AST → Evaluator (native Rust)
                                  ├── Rush expressions: evaluated directly
                                  └── Shell commands: fork/exec (Unix) or cmd.exe (Windows)
```

No transpilation. `x = 42` creates a variable binding directly. `ls -la` fork/execs `ls`. The triage module decides which path each line takes by examining tokens for Rush keywords, operators, method calls, etc.

## Crate Layout

```
rush/
├── crates/
│   ├── rush-core/          # Language engine (29 modules)
│   │   ├── lexer.rs        # Tokenizer
│   │   ├── parser.rs       # Recursive-descent parser → AST
│   │   ├── ast.rs          # Node types (expressions, statements, blocks)
│   │   ├── eval.rs         # Tree-walking evaluator
│   │   ├── value.rs        # Runtime value types (String, Int, Float, Array, Hash, Nil, Bool)
│   │   ├── env.rs          # Scope chain, function/class registry
│   │   ├── process.rs      # fork/exec, pipes, redirections, env var expansion
│   │   ├── pipeline.rs     # Structured operators: where, select, sort, objectify, etc.
│   │   ├── stdlib.rs       # File, Dir, Time, Path, string/array/hash methods
│   │   ├── plugin.rs       # Plugin discovery + JSON wire protocol sessions
│   │   ├── triage.rs       # "Is this Rush or a shell command?"
│   │   ├── llm.rs          # --llm JSON wire protocol
│   │   ├── mcp.rs          # --mcp server (local persistent session)
│   │   ├── mcp_ssh.rs      # --mcp-ssh server (SSH gateway)
│   │   ├── ai.rs           # Built-in AI assistant (Anthropic/OpenAI/Gemini/Ollama)
│   │   ├── config.rs       # ~/.config/rush/config.json (JSONC)
│   │   ├── theme.rs        # Terminal colors, dark/light detection
│   │   ├── objectify_config.rs  # YAML config for text→structured parsing
│   │   ├── hints.rs        # Training hints ("try this in Rush instead")
│   │   └── ...             # dispatch, flags, jobs, sync, trap, token
│   │
│   ├── rush-cli/           # Binary and REPL (8 modules)
│   │   ├── main.rs         # Entry point, flag parsing, mode dispatch
│   │   ├── repl.rs         # Reedline-based REPL, fzf integration
│   │   ├── builtins.rs     # 50+ shell builtins (cd, export, alias, path, set, etc.)
│   │   ├── prompt.rs       # Git-aware prompt with timing
│   │   ├── completer.rs    # Tab completion (commands, paths, methods, pipeline ops)
│   │   ├── highlighter.rs  # Syntax highlighting
│   │   ├── validator.rs    # Multi-line block detection
│   │   └── signals.rs      # SIGINT/SIGCHLD handling
│   │
│   └── rush-ps-bridge/     # Placeholder for native PS interop
│
├── rush-powershell/         # PowerShell plugin companion binary (.NET 10)
├── docs/                    # Lang spec, help YAML, user manual
├── tests/                   # Integration test scripts
└── 591 tests, CI on macOS/Linux/Windows
```

## Shell Command Execution

- **Unix**: `fork()` + `execvp()` — same as bash. Pipes are real Unix pipes (`pipe()` + `dup2()`).
- **Windows**: `Command::new()` with `cmd.exe /C` fallback for builtins like `dir`.
- **Environment variables**: Expanded inline with `$VAR` / `${VAR}`. On Windows, backslashes in expanded paths are normalized to `/` so scripts work cross-platform.

## Plugin System

Plugins are companion binaries that speak the JSON wire protocol over stdio:

```
Rush (Rust)                    rush-ps (.NET 10)
    │                              │
    │  ── JSON stdin ──>           │  PowerShell SDK
    │  "Get-Service"               │  executes command
    │                              │
    │  <── JSON stdout ──          │
    │  {status, stdout, ...}       │
```

Syntax:
```rush
plugin.ps
  Get-Service | Where-Object { $_.Status -eq "Running" }
end
```

Discovery: Rush looks for `rush-NAME` on PATH, then in `~/.config/rush/plugins/`. Any language works — the companion just speaks JSON lines on stdio.

## LLM Integration

Three modes:
- **`rush --llm`**: JSON wire protocol for AI agents. Structured output, error typing, output spooling, TTY blocklist.
- **`rush --mcp`** / **`rush --mcp-ssh`**: MCP servers for Claude Code / Claude Desktop. Persistent session with stateful cwd, variables, etc.
- **`ai` builtin**: Inline assistant. `cat log | ai "what happened?"`. Supports Anthropic, OpenAI, Gemini, Ollama.

## Key Design Decisions

- **Native interpreter, not transpiler.** The .NET version transpiled Rush to PowerShell. Rust Rush evaluates directly. This removes the .NET dependency, gives ~10ms startup, and makes behavior predictable (no PowerShell semantics leaking through).
- **Cross-platform paths.** Backslashes from Windows env vars are normalized to `/` during expansion. The parser treats `\` as escape consistently on all platforms. `.native_path` converts back for Windows-specific handoff.
- **Objectify.** Text output from any command can be parsed into structured data (array of hashes) using configurable per-command hints (YAML, three-layer: built-in → system → user).
- **Reedline fork.** Vi `/` search required a fork of the reedline line editor. fzf integration (auto-detected) provides Ctrl+R and Esc+/ history search.

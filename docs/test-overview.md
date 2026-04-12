# Rush Test Suite Overview

## Architecture

Rush tests are Rust-native, run via `cargo test`, with CI on GitHub Actions across 3 platforms.

**CI Platforms:**
- macOS/arm64 (GitHub Actions)
- Linux/x64 (GitHub Actions)
- Windows/x64 (GitHub Actions)

**Development Hosts:**
- rocinante — macOS/arm64 (local dev)
- trinity — Linux/x64 (SSH target)
- buster — Windows/x64 (SSH target)
- spark — Linux/arm64 (DGX Spark)

---

## Test Suites

### 1. Core Unit Tests (`cargo test -p rush-core`)

**678 tests** covering the full language engine:

| Category | What's Tested |
|----------|---------------|
| **Lexer** | Token types, keywords, operators, string literals, regex, size literals |
| **Parser** | AST generation for all language constructs |
| **Evaluator** | Variables, strings, arrays, hashes, control flow, loops, functions, classes, enums, error handling |
| **Stdlib** | File, Dir, Time, Path, Ssh, Env methods |
| **Triage** | Rush syntax vs shell command detection |
| **Pipeline** | where, select, sort, first/last, count, objectify, as json/csv |
| **Process** | Command execution, background jobs, redirections, chain operators |
| **LLM mode** | Wire protocol, JSON I/O, spool, lcat, TTY blocklist |
| **MCP** | JSON-RPC protocol, tool dispatch, resource serving |
| **Parallel** | Concurrent iteration, worker pools, fail-fast, timeouts |
| **Orchestrate** | Task dependencies, wave execution, progress output |
| **POSIX** | Signal handling, exit codes, flags, parameter expansion |
| **Theme** | Contrast detection, OKLCH palette, background-aware colors |

### 2. CLI Tests (`cargo test -p rush-cli`)

**9 tests** covering:
- Command-line argument parsing
- `-c` command execution
- Script file execution
- `--llm` / `--mcp` mode dispatch

### 3. Integration Test Scripts

Located in the repo root, run against built binaries:

| Script | Assertions | Covers |
|--------|-----------|--------|
| `portability-test.rush` | 76 | Variables, strings, arrays, control flow, loops, functions, stdlib, platform detection |
| `pipeline-ops-test.rush` | 32 | Pipeline operators: where, select, sort, count, distinct, sum/avg/min/max, as json |
| `builtins-test.rush` | 29 | puts, print, def, case/when, loops, platform blocks, regex, heredoc |
| `rush-c-test.sh` | 34 | `rush -c` mode: help, printf, variables, arrays, File stdlib, loops, platform detection |
| `llm-mode-test.sh` | 41 | Wire protocol: context, execution, errors, envelope, file transfer, TTY blocklist |
| `llm-advanced-test.sh` | 41 | Spool, timeout, metacharacters, binary files, session resilience, complex Rush blocks |
| `mcp-mode-test.sh` | 43 | MCP initialize, tools/list, execute, read_file, context, resources, state persistence |
| `mcp-ssh-test.sh` | 99 | SSH gateway: context, Rush syntax, file transfer, variable persistence, multi-host |

---

## Running Tests

```bash
# All Rust tests
cargo test

# Core engine tests only
cargo test -p rush-core

# Specific test category
cargo test parallel
cargo test orchestrate
cargo test ssh

# Integration test scripts (require built binary)
./target/release/rush-cli portability-test.rush
bash llm-mode-test.sh
bash mcp-mode-test.sh
bash mcp-ssh-test.sh trinity buster
```

---

## What's NOT Tested (Known Gaps)

### Interactive-only (can't automate):
- Tab completion behavior and cycling
- Vi/emacs mode switching and keybindings
- Prompt rendering and theming
- Autosuggestions from history
- Ctrl+C / Ctrl+D signal handling
- Reverse search (Ctrl+R)
- Terminal resize handling

### Need specific infrastructure:
- Config sync round-trip between machines
- UNC paths with real Windows network shares
- AI command with pipe-to-ai
- Orchestrate with real multi-host SSH (tested with mocks)

### Tested but shallow:
- Classes: constructor and basic methods, but inheritance/super need more coverage
- Error handling: begin/rescue works, but edge cases in nested contexts
- Hashes: creation + access + iteration, not complex nesting

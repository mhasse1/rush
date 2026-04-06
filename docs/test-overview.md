# Rush Test Suite Overview

## Architecture

Rush tests run across 5 platforms via CI (GitHub Actions) and a custom pipeline (`pipeline.sh`) that tests on real hardware.

**Platforms:**
- macOS/arm64 (rocinante — local dev machine)
- Linux/x64 (trinity — Ubuntu server)
- Linux/arm64 (oci — Oracle Cloud Ubuntu)
- Windows/x64 (buster — Windows 11)
- Windows/arm64 (faust — Windows 11, COI environment)

**Test Hosts (MCP-SSH targets):**
- trinity — Linux with Rush (persistent session)
- buster — Windows with Rush (persistent session)
- oci — Linux ARM64 with Rush (persistent session)
- rrr — Linux without Rush (raw-shell fallback mode)

---

## Test Suites

### 1. xUnit (C# — 1122 tests)
**Run:** `dotnet test Rush.Tests` (CI runs on macOS, Linux, Windows)
**Covers:** Parser, Lexer, Transpiler, ScriptEngine, CommandTranslator, integration tests for all language features via `rush -c`.

### 2. Portability Test (`portability-test.rush` — 76 assertions)
**Run:** `rush portability-test.rush` on any platform
**Covers:**
- Variables: int, string, bool, nil, multiple assignment, compound operators (+=, -=, *=)
- Strings: upcase, downcase, length, include?, replace, strip, interpolation, start_with?, end_with?, split, regex =~/!~
- Arrays: count, index, include?, sort, join, empty?, first, last, reverse
- Control flow: if/else, unless, case/when, comparison operators
- Loops: for..in, while, break
- Functions: def/return, default args
- File/Dir stdlib: mkdir, exist?, write, read, append, read_lines, size, delete, list
- Paths with spaces: Dir.mkdir and File.read in spaced directories
- Platform detection: $os, $__rush_arch, $rush_version, platform blocks (macos/linux/win64), non-matching platform skipped
- ps blocks: env var bridge, arithmetic, $_ in pipelines, PSVersion
- Command substitution: $(hostname)
- Semantic puts: heading, subheading, success, warning, error, info
- Environment variables: set + access via ps block
- PATH variable: $PATH set, colon-separated, forward slashes, no path-separator backslashes (Windows)
- Type conversions: to_i, to_f, to_s
- String methods: start_with?, end_with?, empty?, length

### 3. Pipeline Operators Test (`pipeline-ops-test.rush` — 32 assertions)
**Run:** `rush pipeline-ops-test.rush`
**Covers:**
- where: numeric filter, regex filter
- select: first from array
- first / last
- sort
- count
- join
- include? / empty?
- reverse
- String methods: downcase, upcase, strip, replace, split
- Numeric: abs
- distinct (via ps block)
- sum / avg / min / max (via ps block)
- each (via ps block)
- times (via ps block)
- as json (via ps block)
- tee (file round-trip)
- to_i / to_f / to_s

### 4. Builtins Test (`builtins-test.rush` — 29 assertions)
**Run:** `rush builtins-test.rush`
**Covers:**
- puts / print
- String interpolation (basic + expression)
- def / return, default args
- case/when
- Loops: for..in, while, break
- unless
- Platform detection + platform blocks
- ps block env bridge
- Command substitution
- File/Dir stdlib + spaced paths
- Regex: =~, !~, replace
- elsif chain
- printf (via ps proxy)
- heredoc (via PS here-string)

### 5. Rush -c Test (`rush-c-test.sh` — 34 assertions)
**Run:** `bash rush-c-test.sh [path-to-rush]`
**Covers:**
- help: topic + list
- printf: %s, %d formats
- puts: basic + interpolation
- Variables: arithmetic, method calls
- Arrays: sort + join, count
- File stdlib: write + read + exist? + delete
- Loops: for in -c mode
- Control flow: if/elsif
- Platform detection
- Command substitution
- Regex: =~
- export: standalone + chain
- mark: with label, bare, --- shorthand
- path: list entries
- alias: create
- cd: change dir, cd ~
- unset
- set: show settings
- sync: status doesn't crash

### 6. LLM Basic Test (`llm-mode-test.sh` / `.ps1` — 37-41 assertions)
**Run:** `bash llm-mode-test.sh` or `pwsh -File llm-mode-test.ps1`
**Covers:**
- Startup context: ready, shell, host, cwd, version
- Command execution: echo, status, exit_code, stdout, duration_ms
- Error handling: bad command returns error + non-zero exit
- Rush syntax: puts, variable interpolation
- Multi-line: JSON-quoted for loop
- JSON envelope: cmd, cwd, env vars, missing cmd error
- File transfer: put, get, content match, encoding, missing file error
- Transfer exec: script execution, stdout, args
- Builtins: lcat (status, mime, content), help (output)
- TTY blocklist: vim blocked
- Exit code tracking: non-zero after failure, context tracks it
- Backward compatibility: plain text, JSON-quoted, JSON envelope all work
- Windows-specific: ps block with Get-Process, platform detection
- ps5 variable bridging (Windows): string + numeric bridge
- UNC path string handling

### 7. LLM Advanced Test (`llm-advanced-test.sh` — 41 assertions)
**Run:** `bash llm-advanced-test.sh`
**Covers:**
- **Spool:** Large output triggers output_limit, stdout_lines, preview, hint, retrieve first page, retrieve tail
- **Timeout:** Exit code 124 on timeout
- **Metacharacter survival:** $_ in pipelines, semicolons in compound statements, single quotes inside doubles, backtick-n (PS newline), braces + $_ via ps block, $() subexpression
- **Object-mode output:** stdout_type=objects, structured JSON with properties
- **Binary file handling:** PNG → base64 encoding, mime=image/png
- **Session resilience:** Error doesn't kill session, next command works, last_exit_code tracks failure
- **Iterative development:** Write buggy script v1, fix to v2, verify output
- **File transfer round-trip:** Multi-line YAML put + get, content match, YAML mime detection
- **Complex Rush blocks:** def + call + interpolation, case/when, array sort + join
- **Help system:** File topic has read+write, unknown topic shows available
- **Hashes:** Literal creation + key access, keys list
- **Enums:** Definition + value access
- **Environment variables:** Set + read via envelope, special chars in value
- **PATH normalization:** $PATH set, colon-separated, forward slashes

### 8. MCP Local Test (`mcp-mode-test.sh` / `.ps1` — 43 assertions)
**Run:** `bash mcp-mode-test.sh` or `pwsh -File mcp-mode-test.ps1`
**Covers:**
- Initialize: protocolVersion, server name=rush-local, version, capabilities (tools + resources), instructions
- tools/list: 3 tools, rush_execute/rush_read_file/rush_context present, schema validation
- rush_execute: success, stdout, isError=false, Rush syntax, error status, isError=true
- rush_read_file: success, content, mime=text/plain, encoding=utf8, missing file error
- rush_context: ready, host, cwd, shell=rush
- Resources: resources/list (rush://lang-spec), resources/read (content, mimeType=text/yaml)
- JSON-RPC errors: -32601 (unknown method), -32602 (unknown tool, missing param)
- State persistence: variables survive across tool calls
- File workflow: write JSON config + read back
- Rush via MCP: for loop, array sort + join
- CWD persistence across calls
- Error details: exit_code, isError, stderr message
- Platform detection via MCP

### 9. MCP-SSH Test (`mcp-ssh-test.sh` — 99 assertions across 4 hosts)
**Run:** `bash mcp-ssh-test.sh [host1] [host2] ...`
**Covers per host (rush-to-rush mode):**
- Context: shell=rush, hostname, cwd
- Simple command execution
- Rush syntax: puts, array.count
- Envelope: cwd=/tmp, env var injection
- Variable persistence: interpolation survives across calls
- CWD persistence: cd + pwd across calls
- File transfer: write JSON config + read back + mime detection
- File append: write + append + verify both lines
- Missing file: returns error
- exec_script: Rush sysinfo script
- exec_script: Native script with args (bash on Linux, PowerShell on Windows)
- exec_script: System info (OS-appropriate)
- Error handling: bad command returns error, exit_code != 0
- PowerShell via ps block: Get-Process count
- Multi-step workflow: mkdir → write → read → verify

**Covers (raw-shell fallback mode — host without Rush):**
- Context: shell=raw, hostname, cwd
- Simple command execution
- File write + read + append
- Missing file error
- Native script execution with args
- Error handling

### 10. MCP-SSH Metacharacter Survival Test (`mcp-ssh-metachar-test.sh` — 11 assertions per host)
**Run:** `bash mcp-ssh-metachar-test.sh [host]`
**Covers (B1/B2 bug scenarios):**
- $_ in ForEach-Object pipeline (B1 scenario — was stripped by SSH transport)
- $_ in Where-Object filter
- Semicolons in compound statements (B2 scenario — was fragmented by SSH)
- Single quotes inside double quotes
- Forward slashes in paths
- PowerShell @{} hashtable syntax
- PowerShell @() array syntax
- Rush script with array.select over SSH
- OS-aware native scripts: bash $@/$1/$#/$? on Linux, PS $args on Windows
- JSON content write + read round-trip (quotes inside quotes)

### 11. Windows Verification Test (`windows-verify-test.sh` — 14 assertions)
**Run:** `bash windows-verify-test.sh [windows-host]`
**Covers (Windows-specific bug fixes):**
- #111 PATH normalization: $PATH forward slashes, colon separators, no path-separator backslashes, $env:PATH still native, $PATH readable with correct format
- #112 Backslash-space: File.exist? and File.read in spaced directory
- #74 COLUMNS env var in LLM mode
- #79 SSH Rush detection: shell=rush, version present
- ps5: PowerShell version accessible
- Filesystem: Get-ChildItem works

### 12. ps5 Bridge Test (`ps5-bridge-test.sh` — 3 assertions)
**Run:** `bash ps5-bridge-test.sh [windows-host]`
**Covers:**
- String variable bridges from Rush into ps5 block
- Numeric variable bridges from Rush into ps5 block
- PS 5.1-only feature: Get-WmiObject (not available in PS 7)

### 13. Advanced Features Test (`advanced-features-test.sh` — 6 assertions)
**Run:** `bash advanced-features-test.sh`
**Covers:**
- SQL: SQLite database create, named connection add + query, inline URI query
- Dir.list: count files, filter by type
- Note: AI and UNC:SSH are REPL-only features, not tested in LLM mode

---

## What's NOT Tested (Known Gaps)

### Interactive-only (can't automate):
- Tab completion behavior, cycling, special character escaping
- Vi/emacs mode switching and keybindings
- Prompt rendering and theming
- Autosuggestions from history
- Ctrl+C / Ctrl+D signal handling
- Reverse search (Ctrl+R)
- Terminal resize handling
- mark command visual output (REPL builtin)

### Need specific infrastructure:
- Config sync round-trip between machines (#114)
- UNC paths with real Windows network shares
- AI command with pipe-to-ai and large file handling
- Background jobs (bg, fg, jobs)

### Tested but shallow:
- Classes: method invocation + auto-constructor fixed (#134), but inheritance, super, static methods need more coverage
- Error handling: begin/rescue works in LLM mode but behavior varies in script mode
- Hashes: creation + access tested, not iteration or merge
- Objectify / columns pipe operators: untested
- Heredoc: tested via PS here-string proxy, not native Rush heredoc syntax

---

## Running the Full Pipeline

```bash
# All phases (build + test + CI + deploy)
./pipeline.sh

# Build + test only (phases 1-4)
./pipeline.sh 1-4

# Just integration tests (phase 3)
./pipeline.sh 3

# Deploy after CI (phases 5-6)
./pipeline.sh 5-6
```

Pipeline phases:
1. Source sync (git pull on all platforms)
2. Build + xUnit (1122 tests × 3 platforms, parallel)
3. Integration tests (all suites × 4 platforms, parallel) + Windows verification + ps5 bridge
4. Cross-platform MCP-SSH (4 targets) + metacharacter survival (3 targets)
5. CI artifact download (5 platform binaries)
6. Deploy to all hosts + staging

**Total assertions: ~6,000+ across 5 platforms**

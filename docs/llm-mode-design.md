# LLM Mode Design

## Problem

When an LLM operates a shell, it's parsing human-formatted output — ANSI escape codes, pretty tables, colored prompts, progress bars. This is hallucination fuel. The LLM wastes tokens guessing whether a command succeeded, what directory it's in, and what the output actually says underneath the formatting.

Every LLM-integrated terminal today (Warp, Cursor, Claude Code) is screen-scraping. Rush can do better because it controls the entire pipeline from parse to output.

## Solution

`rush --llm` — a machine-to-machine wire protocol mode where the shell shifts from "aesthetic/helpful" to "atomic/verifiable."

All input/output follows a structured JSON-lines format. The LLM never guesses — it reads structured data.

## Design Priorities

1. **Correct context on every turn** — hostname, cwd, git state, exit code. Restate it; redundancy is a feature.
2. **Structured output** — JSON envelopes, not decorated text.
3. **Zero noise** — no color, no hints, no tips, no progress bars, no "did you mean."
4. **Inherit the human's session** — the LLM steps into the user's shoes, not a clean room.

## Wire Protocol

### Context Prompt (before each command)

Instead of the human prompt (`✓ 13:15  mark@gnome  src/rush  main`), emit:

```json
{"ready":true,"host":"gnome","user":"mark","cwd":"/Users/mark/src/rush","git_branch":"main","git_dirty":true,"last_exit_code":0,"shell":"rush","version":"0.3.1"}
```

Every field is something LLMs commonly hallucinate or lose track of mid-conversation. Restating `host` and `cwd` on every turn is cheap insurance.

### Command Result Envelope

After each command executes, emit:

```json
{"status":"success","exit_code":0,"cwd":"/Users/mark/src/rush","stdout":"Program.cs\nLexer.cs\nParser.cs\n","stderr":"","duration_ms":12}
```

On failure:

```json
{"status":"error","exit_code":1,"cwd":"/Users/mark/src/rush","stdout":"","stderr":"ls: no such file or directory: nope","duration_ms":5}
```

Note: `cwd` appears in both context prompt AND result — intentional redundancy. LLMs work better when context is restated.

### Rush Syntax Errors (AST Pre-Validation)

Because Rush has a parser, syntax errors can be caught before execution:

```json
{"status":"syntax_error","errors":["Expected 'end' to close 'if' block (line 4)"],"cwd":"/Users/mark/src/rush"}
```

This is unique to Rush — most shells parse-and-exec in one step.

### Full Conversation Flow

```
→ {"ready":true,"host":"gnome","user":"mark","cwd":"/Users/mark/src/rush","git_branch":"main","git_dirty":false,"last_exit_code":0,...}
← ls src/
→ {"status":"success","exit_code":0,"cwd":"/Users/mark/src/rush","stdout":"Program.cs\nLexer.cs\n...","stderr":"","duration_ms":12}
→ {"ready":true,"host":"gnome","user":"mark","cwd":"/Users/mark/src/rush",...}
← cd /tmp
→ {"status":"success","exit_code":0,"cwd":"/tmp","stdout":"","stderr":"","duration_ms":1}
→ {"ready":true,"host":"gnome","user":"mark","cwd":"/tmp",...}
```

One JSON object per line. No decoration. No ambiguity.

## Behavior Changes in `--llm` Mode

| Aspect | Human Mode (`rush`) | LLM Mode (`rush --llm`) |
|--------|---------------------|-------------------------|
| **Prompt** | Colored, decorated, git icons | JSON context line |
| **Output** | Formatted, colored, paged | JSON envelope with raw stdout/stderr |
| **Errors** | Friendly suggestions, "did you mean" | Raw error text + exit codes |
| **Timing** | "took 2.3s" (if > 500ms) | `duration_ms` field in every response |
| **Colors** | Theme-based ANSI | None — `NO_COLOR=1` forced |
| **Interactive prompts** | Normal (git credential, npm choice) | Suppressed — `CI=true`, `GIT_TERMINAL_PROMPT=0`, `DEBIAN_FRONTEND=noninteractive` |
| **Hints/Tips** | Random tips, esc-v hint | Disabled |
| **Syntax errors** | Console error message | Structured JSON before execution |
| **Exit codes** | Status icon in next prompt | Explicit field in result + context |
| **CWD tracking** | Visual in prompt | Explicit field in result + context |

## Phases

### Phase 1: Wire Protocol (MVP)

`rush --llm` enters a REPL with:
- JSON context prompt instead of human prompt
- JSON result envelope wrapping every command
- `NO_COLOR=1`, `CI=true`, `GIT_TERMINAL_PROMPT=0`, `DEBIAN_FRONTEND=noninteractive`
- `ShowHints=false`, `ShowTips=false`
- Disable line editor (no vi mode, no tab completion, no history navigation) — read raw lines from stdin
- AST pre-validation for Rush syntax: catch parse errors before execution

Data for the context prompt is already computed in `Prompt.cs`: hostname, username, cwd, git branch, git dirty, exit code. Just serialize differently.

**Scope**: ~150-200 lines. New `RunLlmMode()` method in Program.cs parallel to the main REPL loop.

**Files to modify**:

| File | Changes |
|------|---------|
| `Program.cs` | Add `--llm` flag parsing, `RunLlmMode()` method |
| `Prompt.cs` | Extract data-gathering into reusable method (currently mixed with rendering) |

### Phase 2: State Inheritance ✓

`rush --llm` now runs `init.rush` and `secrets.rush` on every session — the LLM gets the user's PATH, exports, and aliases automatically. Rush environment variables (`$os`, `$hostname`, `$rush_version`, `$is_login_shell`) are injected. Config aliases are applied to the command translator.

`rush --llm --inherit /path/to/state.json` additionally carries over live runtime state from a parent interactive session:
- Environment variables (user-defined, diffed against baseline)
- PowerShell variables (user-defined)
- Aliases (from running session)
- CWD + previous directory (for `cd -`)
- Shell flags (`set -e`, `set -x`, `set -o pipefail`)

The state file is consumed on use (deleted after loading) — one-shot transfer, same as `reload --hard`.

Native commands in LLM mode run via `Process.Start` (not PowerShell `AddScript`) so they use the .NET process PATH which includes init.rush `path add` directories. Translated commands and piped commands still go through PowerShell.

**Files modified**: `Program.cs` (flag parsing, init.rush execution, env var injection), `LlmMode.cs` (native command execution path), `Config.cs` (nullable LineEditor for Apply()), `ReloadState.cs` (Load overload for file path), `ScriptEngine.cs` (path add variable expansion fix).

### Phase 2.5: Timeout & SSH Resilience ✓

Reliability fix shipped before Phase 3. If a command hangs (SSH network partition, unresponsive server, long-running process), the LLM agent can now control execution time.

**`timeout N command`** — wraps any command with a timeout in seconds. On timeout, kills the child process and returns exit code 124 (Unix convention) with `error_type: "timeout"` in the result envelope. Works on both native commands (Process.Start path) and PowerShell-transpiled Rush syntax (async BeginInvoke path).

**SSH keepalive auto-injection** — when a native command starts with `ssh`, Rush injects `-o ServerAliveInterval=15 -o ServerAliveCountMax=3` so dead connections are detected in ~45s instead of hanging forever.

The `timeout` builtin is LLM-mode only — interactive REPL users have Ctrl+C. The `timeoutMs` parameter threads through `ExecuteRushSyntax` → `ExecutePowerShell` and `ExecuteShellCommand` → `ExecuteNativeCommand`/`ExecutePowerShell`, keeping the existing no-timeout behavior as the default.

**Files modified**: `LlmMode.cs` (HandleTimeout, timeoutMs parameter threading, proc.WaitForExit(ms) + Kill, ps.BeginInvoke + WaitOne + Stop, SSH keepalive injection).

### Phase 3: Object-Mode Output ✓

When a command produces .NET/PowerShell objects (not raw text), auto-serialize to JSON instead of formatted text. Text commands (`echo`, `git status`, `cat`, native executables) remain unchanged — `stdout` is a string and `stdout_type` is omitted.

```
← ls docs
→ {"status":"success","exit_code":0,"cwd":"/tmp","stdout_type":"objects","stdout":[
    {"name":"readme.md","size":1234,"modified":"2026-03-06T13:00:00Z","type":"file","path":"/tmp/readme.md"},
    {"name":"src","modified":"2026-03-06T14:00:00Z","type":"directory","path":"/tmp/src"}
  ],"duration_ms":27}

← echo hello
→ {"status":"success","exit_code":0,"cwd":"/tmp","stdout":"hello","duration_ms":14}
```

This leverages PowerShell's object pipeline — Rush's unfair advantage. `ls` in bash gives you a string to parse. `ls` in Rush gives you `DirectoryInfo`/`FileInfo` objects serialized to JSON with curated property projections.

**Detection**: `IsObjectOutput()` checks if `ps.Invoke()` results are structured .NET types (not simple values like string/int/bool/DateTime/Guid, not PathInfo). When detected, `SerializeObjects()` routes each object through type-specific projections:

| Type | Fields |
|------|--------|
| `FileInfo` | name, size, modified (ISO 8601), type="file", path |
| `DirectoryInfo` | name, modified (ISO 8601), type="directory", path |
| `Process` | pid, name, memory, cpu |
| `PSDriveInfo` | name, used, free, provider |
| Fallback | Up to 10 non-PS properties |

**Spool for object mode**: Each object serialized as one JSONL line so `spool 0:5` returns 5 objects, `spool --grep=pattern` searches objects by content.

**Files modified**: `LlmMode.cs` (LlmResult.Stdout → object?, StdoutType field, IsObjectOutput, SerializeObjects, type-specific ProjectX methods, ExecutePowerShell branching).

### Phase 3.5: Agent Mode (`ai --agent`) ✓

`ai --agent "task"` — autonomous multi-turn agent that drives LlmMode directly:

- Supports Anthropic (tool_use) and Gemini (function calling) with streaming SSE
- Single tool: `run_command` — executes commands via `LlmMode.ExecuteCommand()`
- Agent loop: call LLM → stream thinking + tool_use blocks → execute commands → feed results back → repeat
- Same-process execution: shares runspace with interactive REPL (variables, cwd persist)
- Max 25 turns, clean terminal output (dim thinking, cyan commands, green/red results)
- Ctrl+C cancels cleanly

**Files:** `AiAgent.cs` (new, ~300 lines), `LlmMode.cs` (Execute/GetContext made public), `AiCommand.cs` (--agent flag), `Program.cs` (LlmMode pass-through)

### Phase 4: Sandbox Mode (shelved)

`rush --llm --sandbox` — read-only execution:
- File write operations (`File.write`, `Dir.mkdir`, `rm`, `mv`, `cp`) blocked at the AST level
- Environment mutations (`export`, `set`) blocked
- Commands are executed but filesystem writes are intercepted

This is the hardest piece. Options:
1. **AST blocklist**: Reject transpiled code that contains write operations. Coarse but effective.
2. **PowerShell Constrained Language Mode**: Limits what cmdlets can run. May be too restrictive.
3. **Filesystem overlay**: Run in a temp overlay. Complex, OS-specific.

Recommendation: Start with AST blocklist (Phase 4a), consider overlay later.

## Implementation Details

### `--llm` Flag Parsing

```csharp
// In arg processing (Program.cs, near line 19)
bool llmMode = false;
if (args.Contains("--llm"))
{
    llmMode = true;
    args = args.Where(a => a != "--llm").ToArray();
}
```

### RunLlmMode() — Core Loop

```csharp
static void RunLlmMode(Runspace runspace, Config config)
{
    // Force machine-friendly settings
    Environment.SetEnvironmentVariable("NO_COLOR", "1");
    config.ShowHints = false;
    config.ShowTips = false;

    var prompt = new Prompt(config);
    var scriptEngine = new ScriptEngine(config);

    while (true)
    {
        // Emit context prompt
        var context = prompt.GetContextData(runspace);
        Console.WriteLine(JsonSerializer.Serialize(context));

        // Read command — JSON string or plain text
        var raw = Console.ReadLine();
        if (raw == null) break;  // EOF

        // JSON-quoted input: unwrap to get newlines. Plain text: use as-is.
        string input;
        if (raw.StartsWith('"') && raw.EndsWith('"'))
            input = JsonSerializer.Deserialize<string>(raw);
        else
            input = raw;

        // Execute and emit result envelope
        var result = ExecuteWithEnvelope(input, runspace, scriptEngine, config);
        Console.WriteLine(JsonSerializer.Serialize(result));
    }
}
```

### Context Data Object

```csharp
class LlmContext
{
    public bool Ready { get; set; } = true;
    public string Host { get; set; }
    public string User { get; set; }
    public string Cwd { get; set; }
    public string GitBranch { get; set; }
    public bool GitDirty { get; set; }
    public int LastExitCode { get; set; }
    public string Shell { get; set; } = "rush";
    public string Version { get; set; }
}
```

### Result Envelope Object

```csharp
class LlmResult
{
    public string Status { get; set; }     // "success", "error", "syntax_error"
    public int ExitCode { get; set; }
    public string Cwd { get; set; }
    public string Stdout { get; set; }
    public string Stderr { get; set; }
    public long DurationMs { get; set; }
    public List<string> Errors { get; set; } // syntax errors
}
```

### Capturing stdout/stderr

The key difference from human mode: ALL commands go through captured execution (no inherited TTY path). We need the output as strings, not terminal display.

```csharp
// In LLM mode, always capture — never inherit TTY
var ps = PowerShell.Create();
ps.Runspace = runspace;
ps.AddScript(translatedCommand);

var outputBuffer = new PSDataCollection<PSObject>();
var errorBuffer = new PSDataCollection<ErrorRecord>();
ps.Streams.Error = errorBuffer;

var results = ps.Invoke();
var stdout = string.Join("\n", results.Select(r => r.ToString()));
var stderr = string.Join("\n", errorBuffer.Select(e => e.ToString()));
```

For native commands that normally get inherited TTY, redirect to capture:

```csharp
var psi = new ProcessStartInfo(cmd) {
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    UseShellExecute = false
};
```

### AST Pre-Validation

For Rush syntax, parse first and report errors without executing:

```csharp
if (scriptEngine.IsRushSyntax(input))
{
    try
    {
        var ast = parser.Parse(input);
        var transpiled = transpiler.Transpile(ast);
        // Execute transpiled code...
    }
    catch (ParseException ex)
    {
        return new LlmResult {
            Status = "syntax_error",
            Errors = new List<string> { ex.Message },
            Cwd = GetCwd(runspace)
        };
    }
}
```

## What This Enables

1. **Any LLM tool can drive Rush** — Claude Code, aider, custom agents. Pipe stdin/stdout, parse JSON lines.
2. **Rush's own `ai` command improves** — instead of streaming markdown, the AI agent can send structured commands and get structured results.
3. **Testing and automation** — JSON output is trivially testable compared to parsing terminal output.
4. **Composability** — other programs can embed Rush as a structured subprocess.

## What Rush Does NOT Build

- **Database drivers** — users install their own SQL clients. Rush formats the output.
- **Remote execution** — SSH is the transport. Rush is the local interface.
- **LLM hosting** — Rush talks to LLMs via API (existing `ai` command). LLM mode is about being *driven by* an LLM, not driving one.

## Decisions

### Multi-line Input: JSON String (Decided)

LLMs generate complete responses in one shot — they don't type line-by-line. Input is read as a single line where `\n` represents newlines:

```
← "if x > 10\n  puts \"big\"\nelse\n  puts \"small\"\nend"
```

This matches how the output side already works (JSON lines). Input is JSON-parsed, so escaping is standard JSON string escaping. No delimiter protocol needed, no artificial single-line limitation.

In `RunLlmMode()`, input handling becomes:

```csharp
var raw = Console.ReadLine();
if (raw == null) break;

// If input is JSON-quoted, unwrap it; otherwise treat as raw command
string input;
if (raw.StartsWith('"') && raw.EndsWith('"'))
    input = JsonSerializer.Deserialize<string>(raw);
else
    input = raw;  // plain text fallback (simple commands)
```

This gives LLMs the clean JSON path while still allowing plain `ls` without wrapping.

### File Reading: `lcat` Builtin (Decided)

LLMs driving Rush remotely can't use their own file-reading tools — those run locally, not on the target host. Rush needs to serve file contents through the protocol.

`lcat` — a purpose-built file reader that attaches metadata and encodes for transport:

**Text file:**
```json
← lcat src/main.rs
→ {"status":"success","file":"/Users/mark/src/rush/src/main.rs","mime":"text/plain","size_bytes":1420,"encoding":"utf8","content":"fn main() {\n    println!(\"hello\");\n}\n","lines":42}
```

**Binary file:**
```json
← lcat report.pdf
→ {"status":"success","file":"/Users/mark/src/rush/report.pdf","mime":"application/pdf","size_bytes":84210,"encoding":"base64","content":"JVBERi0xLj..."}
```

- Text files: UTF-8 content (readable, token-efficient)
- Binary files: base64-encoded (universal decode on any platform)
- `encoding` field tells the LLM exactly what it got — no guessing
- `mime` from extension map (.NET built-in or simple lookup table) — no external dependency
- `cat` stays Unix-standard with no behavior change; `lcat` is the structured path
- Works identically on macOS, Linux, Windows
- In human mode, `lcat` falls back to normal `cat` behavior (just dump the file)

### Long Output: Preview + Spool (Decided)

Don't truncate silently. Don't dump 100K lines. Give the LLM a preview and let it decide.

**Default limit: 4KB.** Under 4KB, output flows through `stdout` normally. Over 4KB, Rush spools the output internally and returns a preview:

```json
← find /
→ {"status":"output_limit","exit_code":0,"cwd":"/tmp","stdout_lines":84210,"stdout_bytes":2841503,"preview":"bin\nboot\ndev\netc\nhome\nlib\nmedia\nmnt\nopt\nproc\n","preview_bytes":512,"hint":"Output spooled (2.8MB, 84210 lines). Use spool to retrieve: spool --head=100, spool --bytes=32k, spool --all, spool --grep=<pattern>"}
```

The LLM sees:
1. How big the output is (`stdout_lines`, `stdout_bytes`)
2. A 512-byte taste of the content (`preview`)
3. Actionable instructions to get what it needs (`hint`)

**`spool` builtin** — a sliding window into the captured buffer. No re-execution.

Core primitive: `spool offset:count` — read `count` lines starting at `offset`:

```json
← spool 0:10
→ {"status":"success","stdout":"...lines 0-9...","spool_position":10,"spool_total":3400}

← spool 200:20
→ {"status":"success","stdout":"...lines 200-219...","spool_position":220,"spool_total":3400}
```

Response includes `spool_position` (cursor after read) and `spool_total` so the LLM always knows where it is.

Convenience shortcuts built on the same primitive:

| Command | Equivalent | Behavior |
|---------|-----------|----------|
| `spool 0:10` | — | Lines 0-9 |
| `spool 500:5` | — | Lines 500-504 |
| `spool --head=100` | `spool 0:100` | First 100 lines |
| `spool --tail=50` | `spool -50:50` | Last 50 lines (negative = from end) |
| `spool --grep=ERROR` | — | Matching lines with line numbers |
| `spool --all` | — | Everything (LLM explicitly opts in) |

**Typical LLM workflow:**
1. Command returns `output_limit` with 512-byte preview
2. `spool --grep=ERROR` → finds errors at lines 1482, 2901, 4388
3. `spool 1475:15` → context around first error
4. `spool 2895:15` → context around second error

`spool` output goes through the same envelope — if the result itself exceeds 4KB, it spools again. The LLM can always narrow down.

If the limit and `spool` are documented in the yaml spec, LLMs learn the pattern and explore data efficiently without burning context.

### Interactive/TTY Commands: Two Layers (Decided)

**Layer 1 — yaml spec (prevention):** Document in `rush-lang-spec.yaml` that TTY-requiring commands don't work in LLM mode, with alternatives. The LLM reads this as context and learns not to try:

```yaml
llm_mode:
  tty_commands_unavailable:
    - { cmd: "vim, vi, nano, emacs", use_instead: "lcat to read, File.write() to write" }
    - { cmd: "less, more", use_instead: "lcat or cat (output captured automatically)" }
    - { cmd: "top, htop", use_instead: "ps aux" }
    - { cmd: "ssh (interactive)", use_instead: "ssh host command (non-interactive)" }
```

**Layer 2 — structured error (catch):** If the LLM tries anyway, return an error with the exact alternative:

```json
← vim config.yaml
→ {"status":"error","error_type":"tty_required","command":"vim","hint":"Use lcat config.yaml to read, File.write(\"config.yaml\", content) to write."}
```

```json
← less /var/log/syslog
→ {"status":"error","error_type":"tty_required","command":"less","hint":"Use lcat /var/log/syslog to read. Output is captured automatically in LLM mode."}
```

```json
← top
→ {"status":"error","error_type":"tty_required","command":"top","hint":"Use ps aux for process listing."}
```

Detection: small blocklist of known TTY commands checked before execution. The hint is command-specific, not generic.

`sudo` is a separate concern — privilege escalation, not TTY. Deferred to Phase 4 (sandbox).

### SSH: Rush on Both Ends (Decided)

Remote host operation requires Rush installed on the target. The LLM connects via:

```
ssh server "rush --llm"
```

SSH is just a pipe — the JSON protocol flows over it transparently. The context prompt on every turn includes `host`, so the LLM always knows which machine it's operating on. No hallucinating, no wrong-server mistakes.

**Why require Rush on both ends:**
- The entire protocol (JSON envelopes, `lcat`, `spool`, TTY detection, AST pre-validation) works identically on the remote host
- No proxy layer to build, no output-scraping from a dumb remote shell
- Rush is a single self-contained binary — deploying it is copying one file
- The alternative (proxying a remote bash session) reintroduces every problem we're solving

**Production incident flow:**
```
← ssh prod-web-03 "rush --llm"
→ {"ready":true,"host":"prod-web-03","user":"deploy","cwd":"/var/app/current",...}
← cat /var/log/app/error.log | tail -50
→ {"status":"success","stdout":"[ERROR] Connection pool exhausted at 14:32:01..."}
← lcat config/database.yml
→ {"status":"success","file":"/var/app/current/config/database.yml","mime":"text/plain","content":"pool_size: 5\n..."}
← ps aux | grep java
→ {"status":"success","stdout":"deploy 12841 98.2 4.1 ..."}
```

Structured data, host context on every turn, `spool` for large logs, `lcat` for config files. Production debugging becomes a conversation.

**Multi-host sessions:** The LLM can open multiple SSH connections to different hosts and correlate data across them — all with explicit host context preventing confusion.

## Open Questions

(None remaining — ready for Phase 1 implementation.)

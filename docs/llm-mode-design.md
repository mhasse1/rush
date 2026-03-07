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
| **Hints/Tips** | Random tips, esc-v hint | Disabled |
| **Syntax errors** | Console error message | Structured JSON before execution |
| **Exit codes** | Status icon in next prompt | Explicit field in result + context |
| **CWD tracking** | Visual in prompt | Explicit field in result + context |

## Phases

### Phase 1: Wire Protocol (MVP)

`rush --llm` enters a REPL with:
- JSON context prompt instead of human prompt
- JSON result envelope wrapping every command
- `NO_COLOR=1` forced, `ShowHints=false`, `ShowTips=false`
- Disable line editor (no vi mode, no tab completion, no history navigation) — read raw lines from stdin
- AST pre-validation for Rush syntax: catch parse errors before execution

Data for the context prompt is already computed in `Prompt.cs`: hostname, username, cwd, git branch, git dirty, exit code. Just serialize differently.

**Scope**: ~150-200 lines. New `RunLlmMode()` method in Program.cs parallel to the main REPL loop.

**Files to modify**:

| File | Changes |
|------|---------|
| `Program.cs` | Add `--llm` flag parsing, `RunLlmMode()` method |
| `Prompt.cs` | Extract data-gathering into reusable method (currently mixed with rendering) |

### Phase 2: State Inheritance

`rush --llm --inherit` carries over:
- All environment variables from parent shell
- All `def`-defined functions
- Optionally: last N commands from history (opt-in, since history may contain secrets)

This is mostly wiring — `--inherit` already has precedent in subshell design.

**Security**: History inheritance must be opt-in. `export API_KEY=...` in history is a risk.

### Phase 3: Object-Mode Output

When a command produces .NET/PowerShell objects (not raw text), auto-serialize to JSON instead of formatted text.

```
← ls
→ {"status":"success","exit_code":0,"cwd":"/tmp","stdout_type":"objects","stdout":[{"name":"foo.txt","size":1234,"modified":"2026-03-06T13:00:00Z","type":"file"},...],...}
```

This leverages PowerShell's object pipeline — Rush's unfair advantage. `ls` in bash gives you a string to parse. `ls` in Rush gives you `DirectoryInfo` objects that can be serialized to JSON directly.

Detection: If `ps.Invoke()` returns `PSObject` instances (not raw strings), serialize them via `ConvertTo-Json` equivalent.

### Phase 4: Sandbox Mode

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

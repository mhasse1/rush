using System.Diagnostics;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Reflection;
using System.Runtime.InteropServices;
using Rush;

// ── Windows Console Setup ────────────────────────────────────────────
// Enable UTF-8 output (for ✓/✗ and other Unicode) and ANSI virtual
// terminal processing (for escape codes like \x1b[J, colors, cursor movement).
if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    Console.InputEncoding = System.Text.Encoding.UTF8;
    EnableWindowsAnsi();
}

// Version is derived from git at build time (see Rush.csproj GitVersion target).
// InformationalVersion = "1.2.348-a3b4c5d" (commit count + short SHA)
string Version = RushVersion.Full;

// ── Login Shell Detection ───────────────────────────────────────────
// macOS sets argv[0] to "-rush" when launching a login shell.
// Also accept --login / -l flags explicitly.
bool isLoginShell = false;
var argv0 = Environment.GetCommandLineArgs()[0];
if (Path.GetFileName(argv0).StartsWith('-'))
    isLoginShell = true;
if (args.Contains("--login") || args.Contains("-l"))
    isLoginShell = true;
// Strip login flags so they don't interfere with other argument parsing
args = args.Where(a => a is not "--login" and not "-l").ToArray();

// ── Hot-Reload Resume Detection ──────────────────────────────────────
bool isResuming = args.Contains("--resume");
args = args.Where(a => a != "--resume").ToArray();

// ── LLM / MCP Mode Detection ──────────────────────────────────────
bool llmMode = args.Contains("--llm");
bool mcpMode = args.Contains("--mcp");
bool mcpSshMode = args.Contains("--mcp-ssh");
string? inheritPath = null;
var inheritIdx = Array.IndexOf(args, "--inherit");
if (inheritIdx >= 0 && inheritIdx + 1 < args.Length)
    inheritPath = args[inheritIdx + 1];
args = args.Where(a => a != "--llm" && a != "--mcp" && a != "--mcp-ssh" && a != "--inherit" && a != inheritPath).ToArray();

// ── CLI Arguments ────────────────────────────────────────────────────
if (args.Length > 0)
{
    if (args[0] is "--version" or "-v")
    {
        Console.WriteLine($"rush {Version}");
        return;
    }
    if (args[0] is "--help" or "-h")
    {
        Console.WriteLine($"rush {Version} — Unix-style shell on the PowerShell 7 engine");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  rush                 Start interactive shell");
        Console.WriteLine("  rush script.rush     Execute Rush script file");
        Console.WriteLine("  rush -c 'command'    Execute command and exit");
        Console.WriteLine("  rush --llm           LLM wire protocol mode (JSON I/O)");
        Console.WriteLine("  rush --llm --inherit <state.json>  LLM mode with parent session state");
        Console.WriteLine("  rush --mcp           MCP server mode (JSON-RPC over stdio)");
        Console.WriteLine("  rush --mcp-ssh       MCP SSH gateway (dynamic multi-host)");
        Console.WriteLine("  rush install mcp --claude  Install MCP servers into Claude");
        Console.WriteLine("  rush --login         Start as login shell");
        Console.WriteLine("  rush --version       Show version");
        Console.WriteLine("  rush --help          Show this help");
        return;
    }
    // ── install subcommand ───────────────────────────────────────────
    if (args[0] == "install")
    {
        if (args.Length >= 2 && args[1] == "mcp" && args.Contains("--claude"))
        {
            Rush.McpInstaller.InstallClaude(Version);
            return;
        }
        Console.WriteLine("Usage: rush install mcp --claude");
        Console.WriteLine();
        Console.WriteLine("  Registers rush as an MCP server in Claude Code and Claude Desktop.");
        Console.WriteLine("  Adds rush MCP tools to the Claude Code permissions allow list.");
        return;
    }
    if (args[0] == "-c" && args.Length > 1)
    {
        // Non-interactive: execute command and exit
        RunNonInteractive(string.Join(' ', args[1..]));
        return;
    }
    // Script file execution: rush script.rush [args...]
    if (args[0].EndsWith(".rush", StringComparison.OrdinalIgnoreCase) && File.Exists(args[0]))
    {
        RunScriptFile(args[0], args[1..]);
        return;
    }
}

// ── MCP Server Mode ──────────────────────────────────────────────────
// Model Context Protocol server — JSON-RPC over stdio for Claude Code.
// Exposes rush_execute, rush_read_file, rush_context as MCP tools.
// Persistent session: variables, cwd, env survive across tool calls.
if (mcpMode)
{
    var mcpConfig = RushConfig.Load();
    mcpConfig.ShowHints = false;
    mcpConfig.ShowTips = false;
    var ui = new RushHostUI();
    var h = new RushHost(ui);
    var ss = InitialSessionState.CreateDefault();
    using var rs = RunspaceFactory.CreateRunspace(h, ss);
    rs.Open();
    var mcpObjConfig = ObjectifyConfig.Load();
    var tr = new CommandTranslator(mcpObjConfig);
    var se = new ScriptEngine(tr);
    mcpConfig.Apply(null, tr);
    InjectRushEnvVars(rs, Version, isLoginShell);
    RunStartupScripts(rs, se);
    ReloadState.CaptureBaseline(rs);

    var mcp = new Rush.McpMode(rs, se, tr, Version);
    mcp.Run();
    return;
}

// ── MCP SSH Gateway Mode ────────────────────────────────────────────
// Dynamic multi-host SSH gateway — no PowerShell runspace needed.
// Tools take a `host` parameter; each call runs ssh <host> <command>.
if (mcpSshMode)
{
    var sshMcp = new Rush.McpSshMode(Version);
    sshMcp.Run();
    return;
}

// ── LLM Mode ────────────────────────────────────────────────────────
// Machine-to-machine wire protocol: JSON context prompts, JSON result
// envelopes, structured file reading, output spooling. No ANSI, no
// line editor, no human prompts. See docs/llm-mode-design.md
if (llmMode)
{
    var llmConfig = RushConfig.Load();
    llmConfig.ShowHints = false;
    llmConfig.ShowTips = false;
    var ui = new RushHostUI();
    var h = new RushHost(ui);
    var ss = InitialSessionState.CreateDefault();
    using var rs = RunspaceFactory.CreateRunspace(h, ss);
    rs.Open();
    var llmObjConfig = ObjectifyConfig.Load();
    var tr = new CommandTranslator(llmObjConfig);
    var se = new ScriptEngine(tr);

    // Apply config aliases to translator (null editor — no LineEditor in LLM mode)
    llmConfig.Apply(null, tr);

    // Set Rush built-in variables ($os, $hostname, $rush_version, $is_login_shell)
    InjectRushEnvVars(rs, Version, isLoginShell);

    // Windows: shim uutils if installed
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
        ShimCoreutilsIfNeeded(rs, quiet: true);
        ShimDiffutilsIfNeeded(rs, quiet: true);
    }

    // Run startup scripts (init.rush, secrets.rush) — user's PATH, exports, aliases
    RunStartupScripts(rs, se);

    // Capture baseline so ReloadState diffing works if needed
    ReloadState.CaptureBaseline(rs);

    // Restore inherited session state if --inherit provided
    if (inheritPath != null)
    {
        try
        {
            var saved = ReloadState.Load(inheritPath);
            if (saved != null)
            {
                string? prevDir = null;
                bool dummyE = false, dummyX = false, dummyPf = false;
                ReloadState.Restore(saved, rs, ref prevDir, ref dummyE, ref dummyX, ref dummyPf,
                    llmConfig, tr);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{{\"status\":\"error\",\"stderr\":\"inherit: {ex.Message.Replace("\"", "\\\"")}\"}}");
        }
    }

    var llm = new Rush.LlmMode(rs, se, tr, Version);
    llm.Run();
    return;
}

// ── Load Config ──────────────────────────────────────────────────────
var config = RushConfig.Load();

// ── Theme (single path: .rushbg > config.Bg > auto-detect) ──────────
ApplyTheme(config, Environment.CurrentDirectory);

// ── Banner ───────────────────────────────────────────────────────────
Console.ForegroundColor = Theme.Current.Banner;
Console.WriteLine($"rush v{Version} — a modern-day warrior");
Console.ForegroundColor = Theme.Current.Muted;
Console.WriteLine($"PowerShell 7 engine | {config.EditMode} mode | Tab | Ctrl+R");
Console.ResetColor();

if (config.IsFirstRun)
{
    Console.WriteLine();
    Console.ForegroundColor = Theme.Current.Banner;
    Console.WriteLine("Welcome to Rush! A few things work differently here:");
    Console.ResetColor();
    Console.WriteLine();
    Console.ForegroundColor = Theme.Current.Muted;
    Console.WriteLine("  alias ll='ls -la'     session-only (--save to persist)");
    Console.WriteLine("  path add ~/bin         session-only (--save to persist)");
    Console.WriteLine("  set editMode emacs     session-only (--save to persist)");
    Console.WriteLine();
    Console.WriteLine("  Builtins support --help:  alias --help, path --help, cd --help");
    Console.WriteLine("  help                   list all help topics");
    Console.WriteLine("  help xref              bash → Rush cross-reference");
    Console.ResetColor();
}
else if (config.ShowTips)
{
    // Warn if contrast-aware theming is disabled
    bool bgOff = string.IsNullOrEmpty(config.Bg) || string.Equals(config.Bg, "off", StringComparison.OrdinalIgnoreCase);
    if (bgOff && Theme.ActiveRushBgFile == null)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write("  Note: ");
        Console.ResetColor();
        Console.WriteLine("Contrast-aware theming disabled. Enable: set --save bg \"#282828\"");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("  See help config or set --help for details.");
        Console.ResetColor();
    }
    else
    {
        var tip = GetStartupTip(config);
        Console.WriteLine();
        Console.ForegroundColor = Theme.Current.Muted;
        Console.Write("Tip: ");
        Console.ResetColor();
        Console.WriteLine(tip);
    }
}

// ── Initialize PowerShell Engine ─────────────────────────────────────
var hostUI = new RushHostUI();
var host = new RushHost(hostUI);
var iss = InitialSessionState.CreateDefault();
var runspace = RunspaceFactory.CreateRunspace(host, iss);
runspace.Open();

// ── Initialize Components ────────────────────────────────────────────
var jobManager = new Rush.JobManager(iss, host);
var objectifyConfig = ObjectifyConfig.Load();
var translator = new CommandTranslator(objectifyConfig);
var scriptEngine = new ScriptEngine(translator);
var lineEditor = new LineEditor();
var binaryWatcher = new BinaryWatcher();
var prompt = new Prompt(binaryWatcher);
var tabCompleter = new TabCompleter(runspace, translator, config);
var highlighter = new SyntaxHighlighter(translator);

// Apply config (sets edit mode, custom aliases, behavioral flags)
var (cfgStopOnError, cfgTrace, cfgPipefail) = config.Apply(lineEditor, translator);

// Load persistent history
lineEditor.LoadHistory();

// Wire syntax highlighting
lineEditor.Highlighter = highlighter;

// Wire tab completion into line editor
lineEditor.CompleteHandler = (input, cursor) => tabCompleter.Complete(input, cursor);
lineEditor.ShowCompletionsHandler = () =>
{
    if (tabCompleter.CompletionCount > 1)
    {
        tabCompleter.ShowCompletions();
        // Redraw prompt after showing completions
        prompt.Render(runspace);
    }
};

// ── Rush Environment Variables ───────────────────────────────────────
InjectRushEnvVars(runspace, Version, isLoginShell);

// ── State ────────────────────────────────────────────────────────────
bool signalExit = false; // Set by SIGHUP/SIGTERM to trigger graceful exit

// ── Create ShellState ────────────────────────────────────────────────
var state = new ShellState
{
    Runspace = runspace,
    Translator = translator,
    ScriptEngine = scriptEngine,
    Config = config,
    LineEditor = lineEditor,
    JobManager = jobManager,
    Prompt = prompt,
    Host = host,
    TabCompleter = tabCompleter,
    SetE = cfgStopOnError,
    SetX = cfgTrace,
    SetPipefail = cfgPipefail,
};

// ── Windows: shim uutils coreutils if needed ────────────────────────
if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
{
    ShimCoreutilsIfNeeded(runspace);
    ShimDiffutilsIfNeeded(runspace);
    DetectWindowsCoreutils(runspace, config);
}

// ── Run Startup Scripts (with full builtin support) ─────────────────
RunStartupScripts(runspace, scriptEngine, state);

// ── Sync .NET Working Directory ──────────────────────────────────────
// PowerShell's runspace may have a different working directory than
// Environment.CurrentDirectory (which child processes inherit).
// Ensure they match so that launching zsh, claude, etc. starts in the
// correct directory — not stuck at ~ or wherever the process launched from.
try
{
    using var locPs = PowerShell.Create();
    locPs.Runspace = runspace;
    locPs.AddCommand("Get-Location");
    var loc = locPs.Invoke();
    if (loc.Count > 0)
    {
        var psDir = loc[0].ToString();
        if (psDir != null)
            Environment.CurrentDirectory = psDir;
    }
}
catch { /* best-effort — don't crash startup */ }

// ── Capture State Baseline (for reload --hard) ──────────────────────
ReloadState.CaptureBaseline(runspace);

// ── Hot-Reload State Restoration ─────────────────────────────────────
if (isResuming)
{
    try
    {
        var saved = ReloadState.Load();
        if (saved != null)
        {
            string? prevDir = state.PreviousDirectory;
            bool tmpE = state.SetE, tmpX = state.SetX, tmpPf = state.SetPipefail;
            ReloadState.Restore(saved, runspace, ref prevDir, ref tmpE, ref tmpX, ref tmpPf,
                state.Config, state.Translator);
            state.PreviousDirectory = prevDir;
            state.SetE = tmpE;
            state.SetX = tmpX;
            state.SetPipefail = tmpPf;
            Console.ForegroundColor = Theme.Current.Muted;
            Console.WriteLine("  session restored");
            Console.ResetColor();
        }
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = Theme.Current.Error;
        Console.Error.WriteLine($"resume: {ex.Message}");
        Console.ResetColor();
    }
}

// ── Signal Handling ──────────────────────────────────────────────────
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true; // Don't kill the process
    if (state.RunningPs != null)
    {
        try { state.RunningPs.Stop(); } // Interrupt running PowerShell pipeline
        catch { }
    }
    try { state.BuiltinCts.Cancel(); } catch { } // Signal .NET builtins (cat, read, native procs)
    // If at the prompt (RunningPs == null), LineEditor already handles Ctrl+C
};

// Ctrl+Z (SIGTSTP) — ignore it. .NET's process model makes proper Unix
// job control (setpgid/tcsetpgrp/waitpid) unreliable: Process.Start()
// doesn't expose the fork-to-exec window needed for setpgid, and the
// dotnet host process doesn't handle SIGTSTP. Rather than half-working
// suspend/resume that corrupts terminal state, we simply swallow it.
// Modern workflows (tmux, multiple tabs, SSH multiplexing) make Ctrl+Z
// suspension largely unnecessary.
PosixSignalRegistration? sigtstpReg = null;
PosixSignalRegistration? sighupReg = null;
PosixSignalRegistration? sigtermReg = null;
PosixSignalRegistration? sigwinchReg = null;
if (!OperatingSystem.IsWindows())
{
    sigtstpReg = PosixSignalRegistration.Create(PosixSignal.SIGTSTP, ctx =>
    {
        ctx.Cancel = true; // Swallow — do nothing
    });

    // SIGHUP — terminal closed (e.g. window close, SSH disconnect)
    // Exit gracefully: save history, fire EXIT traps, clean up
    sighupReg = PosixSignalRegistration.Create(PosixSignal.SIGHUP, ctx =>
    {
        ctx.Cancel = true; // Prevent default termination
        signalExit = true;
        try { state.RunningPs?.Stop(); } catch { }
    });

    // SIGTERM — system shutdown or kill request
    sigtermReg = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
    {
        ctx.Cancel = true;
        signalExit = true;
        Theme.RestoreBackground();
        try { state.RunningPs?.Stop(); } catch { }
    });

    // SIGWINCH — terminal resized. Flag the LineEditor so it recaptures
    // cursor position on the next keypress (prevents stale _startTop).
    sigwinchReg = PosixSignalRegistration.Create(PosixSignal.SIGWINCH, ctx =>
    {
        ctx.Cancel = true;
        lineEditor.NotifyResize();
        // Update COLUMNS/LINES for native commands
        try
        {
            Environment.SetEnvironmentVariable("COLUMNS", Console.WindowWidth.ToString());
            Environment.SetEnvironmentVariable("LINES", Console.WindowHeight.ToString());
        }
        catch { }
    });
}

// ── REPL ─────────────────────────────────────────────────────────────
while (true)
{
    // Report completed background jobs before the prompt
    var completedJobs = jobManager.GetCompletedUnreported();
    foreach (var job in completedJobs)
    {
        Console.ForegroundColor = Theme.Current.Muted;
        Console.WriteLine($"  [{job.JobId}] done: {job.Command}");
        Console.ResetColor();
    }
    jobManager.RemoveCompletedJobs();

    prompt.Render(runspace);
    tabCompleter.Reset();

    var input = lineEditor.ReadLine();
    if (input == null || signalExit) break; // EOF (Ctrl+D) or SIGHUP/SIGTERM

    // ── Edit in $EDITOR (v in vi normal, Ctrl+X Ctrl+E in emacs) ──
    if (input == "\x16")
    {
        input = OpenInEditor(lineEditor.CurrentBuffer);
        if (input == null) continue;
    }

    // ── Continuation Lines (trailing \, unclosed quotes/brackets) ──
    input = ReadContinuationLines(input, lineEditor);

    input = input.Trim();
    if (string.IsNullOrEmpty(input)) continue;

    // ── Inline env vars: VAR=value command (must be before triage) ────
    // POSIX: VAR=value command sets VAR for the duration of command only.
    // Extract and set them, run the command, then restore.
    var inlineEnvSaved = ApplyInlineEnvVars(ref input);

    // ── --help flag (must be before Rush syntax triage — "for --help" etc.) ──
    if (input.EndsWith(" --help", StringComparison.OrdinalIgnoreCase)
        || input.Equals("--help", StringComparison.OrdinalIgnoreCase))
    {
        var keyword = input.Equals("--help", StringComparison.OrdinalIgnoreCase)
            ? null
            : input[..input.LastIndexOf(" --help", StringComparison.OrdinalIgnoreCase)].Trim();
        var helpTopic = MapKeywordToHelpTopic(keyword);
        // Pass original keyword if mapping returned null, so renderer shows "unknown topic"
        HelpRenderer.Render(helpTopic ?? keyword);
        continue;
    }

    // ── Rush Scripting Language Triage ─────────────────────────────
    // Check if input is Rush syntax (if/for/def/assignment/method chains).
    // If so, accumulate multi-line blocks, parse, transpile to PS, and execute.
    if (scriptEngine.IsRushSyntax(input))
    {
        // Accumulate multi-line blocks (if/end, def/end, etc.) with auto-indent
        int continuationCount = 0;
        while (scriptEngine.IsIncomplete(input))
        {
            continuationCount++;

            var depth = scriptEngine.GetBlockDepth(input);
            if (depth < 0) depth = 1; // Lexer failed — assume 1 level
            var indent = new string(' ', Prompt.InputPrefix.Length + depth * 2);
            Console.ForegroundColor = Theme.Current.Muted;
            Console.Write(indent);
            Console.ResetColor();

            var continuation = lineEditor.ReadLine();

            if (continuation == null) break;           // Ctrl+D
            if (continuation == "") { input = ""; break; }  // Ctrl+C — cancel block

            // Edit in $EDITOR — hand off entire accumulated block
            if (continuation == "\x16")
            {
                var editorContent = input + "\n" + lineEditor.CurrentBuffer;
                var edited = OpenInEditor(IndentRushBlock(editorContent, scriptEngine));
                if (edited == null) { input = ""; break; }
                input = StripLeadingWhitespace(edited);
                break;
            }

            input += "\n" + continuation;

            // Auto-outdent: if the line was `end`, rewrite at the correct depth
            var trimmed = continuation.Trim();
            if (trimmed.Equals("end", StringComparison.OrdinalIgnoreCase))
            {
                var newDepth = scriptEngine.GetBlockDepth(input);
                if (newDepth < 0) newDepth = 0;
                var correctIndent = new string(' ', Prompt.InputPrefix.Length + newDepth * 2);
                // Move cursor up, clear line, rewrite with correct indent
                Console.Write("\x1b[A\x1b[2K");
                Console.ForegroundColor = Theme.Current.Muted;
                Console.Write(correctIndent);
                Console.ResetColor();
                Console.WriteLine("end");
            }
        }

        // Handle puts/print/warn with shell redirects (>> or >)
        // These are Rush syntax but users expect redirects to work like echo.
        var rushRedirect = ExtractRushOutputRedirect(ref input);

        var psCode = scriptEngine.TranspileLine(input);
        if (psCode != null)
        {
            if (rushRedirect != null)
                psCode += rushRedirect;
            var (rushFailed, rushShouldExit) = ExecuteTranspiledBlock(psCode, state);
            if (!rushFailed)
            {
                // Track variable assignments for type-aware dot-completion (V2)
                TrackVariableAssignment(input, tabCompleter);
            }
            prompt.SetLastCommandFailed(rushFailed);
            if (rushShouldExit) break;
        }

        lineEditor.SaveHistory();
        RestoreInlineEnvVars(inlineEnvSaved);
        continue;
    }

    // ── Path block accumulation (path add...end / path rm...end) ────
    if (IsPathBlock(input))
    {
        while (true)
        {
            Console.ForegroundColor = Theme.Current.Muted;
            Console.Write(new string(' ', Prompt.InputPrefix.Length + 2));
            Console.ResetColor();
            var continuation = lineEditor.ReadLine();
            if (continuation == null) break;           // Ctrl+D
            if (continuation == "") { input = ""; break; }  // Ctrl+C
            input += "\n" + continuation;
            if (continuation.Trim().Equals("end", StringComparison.OrdinalIgnoreCase))
                break;
        }
        if (string.IsNullOrEmpty(input)) continue; // cancelled
    }

    // ── Shell command processing (non-Rush syntax) ──────────────────
    var (cmdFailed, cmdExitCode, cmdShouldExit) = ProcessCommand(input, state);
    if (cmdShouldExit || signalExit) break;

    // Re-emit background after external commands that may have changed terminal bg
    // (e.g., sudo -s, ssh). Cheap and idempotent — just resends the OSC escape.
    Theme.ReemitBackground();

    prompt.SetLastCommandFailed(cmdFailed, cmdExitCode);
    TrainingHints.TryShowHint(input, cmdFailed, config);
    lineEditor.SaveHistory();
    RestoreInlineEnvVars(inlineEnvSaved);
}

// ── Graceful Exit ────────────────────────────────────────────────────

// Fire EXIT trap if registered
if (state.Traps.TryGetValue("EXIT", out var exitTrap))
{
    var exitTranslated = translator.Translate(exitTrap) ?? exitTrap;
    using var exitPs = PowerShell.Create();
    exitPs.Runspace = runspace;
    exitPs.AddScript(exitTranslated);
    try { exitPs.Invoke(); } catch { }
}

jobManager.Dispose();
lineEditor.SaveHistory();
runspace.Close();
runspace.Dispose();
Console.Write("\x1b[0 q"); // Reset cursor shape
Theme.RestoreBackground(); // Restore original bg if root shell changed it
if (host.ShouldExit)
    Environment.ExitCode = host.ExitCode;
if (!signalExit) // Don't write to a dead terminal (SIGHUP)
    Console.WriteLine("bye.");
SshPool.Cleanup();

// Dispose signal registrations
sigtstpReg?.Dispose();
sighupReg?.Dispose();
sigtermReg?.Dispose();
sigwinchReg?.Dispose();

// ═══════════════════════════════════════════════════════════════════
// Shared State & Dispatch
// ═══════════════════════════════════════════════════════════════════

/// <summary>
/// Execute transpiled PowerShell code, handling errors, output, and timing.
/// Used by both the REPL (for Rush syntax blocks) and startup scripts.
/// </summary>
static (bool failed, bool shouldExit) ExecuteTranspiledBlock(string psCode, ShellState state)
{
    bool failed = false;
    bool shouldExit = false;
    var sw = System.Diagnostics.Stopwatch.StartNew();
    try
    {
        using var ps = System.Management.Automation.PowerShell.Create();
        ps.Runspace = state.Runspace;
        ps.AddScript(psCode);

        state.RunningPs = ps;
        List<System.Management.Automation.PSObject> results;
        try
        {
            results = ps.Invoke().ToList();
        }
        catch (System.Management.Automation.PipelineStoppedException)
        {
            Console.WriteLine();
            sw.Stop();
            state.RunningPs = null;
            return (true, false);
        }
        finally
        {
            state.RunningPs = null;
        }

        sw.Stop();

        if (ps.HadErrors)
        {
            OutputRenderer.RenderErrors(ps.Streams);
            failed = true;
        }

        if (results.Count > 0)
            OutputRenderer.Render(results.ToArray());

        if (state.Host?.ShouldExit == true)
            shouldExit = true;
    }
    catch (System.Management.Automation.PipelineStoppedException)
    {
        Console.WriteLine();
        failed = true;
    }
    catch (Exception ex)
    {
        failed = true;
        var msg = ex.InnerException?.Message ?? ex.Message;
        Console.ForegroundColor = Theme.Current.Error;
        Console.Error.WriteLine($"error: {msg}");
        Console.ResetColor();
    }

    if (state.IsInteractive && sw.ElapsedMilliseconds > 500)
    {
        Console.ForegroundColor = Theme.Current.Muted;
        var elapsed = sw.Elapsed;
        if (elapsed.TotalMinutes >= 1)
            Console.WriteLine($"  took {elapsed.Minutes}m {elapsed.Seconds}s");
        else
            Console.WriteLine($"  took {elapsed.TotalSeconds:F1}s");
        Console.ResetColor();
    }

    return (failed, shouldExit);
}

/// <summary>
/// Process a shell command line through the full dispatch pipeline:
/// heredoc, bang expansion, brace/tilde/env/arithmetic/process/command substitution,
/// chain splitting, and for each segment: builtin dispatch + translate + execute.
/// This is the shared code path for both REPL and script execution.
/// </summary>
static (bool failed, int exitCode, bool shouldExit) ProcessCommand(string input, ShellState state)
{
    // ── Heredoc Detection ───────────────────────────────────────────
    string? heredocContent = null;
    input = DetectAndReadHeredoc(input, out heredocContent);
    if (heredocContent != null)
    {
        // Pipe heredoc content into the command as a PowerShell here-string
        input = $"@'\n{heredocContent}\n'@ | {input}";
    }

    // ── Bang Expansion (interactive only) ──────────────────────────
    if (state.IsInteractive && input.Contains('!'))
    {
        bool expanded = false;
        var lineEditor = state.LineEditor!;

        // !N — run Nth command from history
        if (input.StartsWith('!') && input.Length > 1 && char.IsDigit(input[1]))
        {
            var numStr = new string(input.Skip(1).TakeWhile(char.IsDigit).ToArray());
            if (int.TryParse(numStr, out var histNum) && histNum > 0 && histNum <= lineEditor.History.Count)
            {
                input = lineEditor.History[histNum - 1];
                expanded = true;
            }
        }

        // !string — repeat last command starting with string
        if (!expanded && input.StartsWith('!') && input.Length > 1
            && char.IsLetter(input[1]))
        {
            var prefix = input[1..];
            for (int i = lineEditor.History.Count - 1; i >= 0; i--)
            {
                if (lineEditor.History[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    && lineEditor.History[i] != input)
                {
                    input = lineEditor.History[i];
                    expanded = true;
                    break;
                }
            }
        }

        // !! and !$
        if (input.Contains("!!") || input.Contains("!$"))
        {
            string? prevCmd = null;
            for (int i = lineEditor.History.Count - 1; i >= 0; i--)
            {
                if (lineEditor.History[i] != input)
                {
                    prevCmd = lineEditor.History[i];
                    break;
                }
            }

            if (prevCmd != null)
            {
                if (input.Contains("!!"))
                    input = input.Replace("!!", prevCmd);
                if (input.Contains("!$"))
                {
                    var lastArg = prevCmd.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
                    input = input.Replace("!$", lastArg);
                }
                expanded = true;
            }
        }

        if (expanded)
        {
            lineEditor.ReplaceLastHistory(input);
            Console.ForegroundColor = Theme.Current.Muted;
            Console.WriteLine($"  → {input}");
            Console.ResetColor();
        }
    }

    // ── Expansion Pipeline (brace, tilde, env, arithmetic, process, command sub) ──
    List<string>? procSubTempFiles;
    (input, procSubTempFiles) = RunExpansionPipeline(input, state.Translator, state.Runspace);

    // ── Split on Chain Operators (&&, ||, ;) ──────────────────────────
    var (chainSegments, chainOps) = SplitChainOperators(input);

    bool lastSegmentFailed = false;
    int lastExitCode = 0;
    bool shouldExit = false;

    for (int ci = 0; ci < chainSegments.Count; ci++)
    {
        var segment = chainSegments[ci].Trim();
        if (string.IsNullOrEmpty(segment)) continue;

        // Reset Ctrl+C token for this segment (previous cancellation shouldn't bleed)
        if (state.BuiltinCts.IsCancellationRequested)
        {
            state.BuiltinCts.Dispose();
            state.BuiltinCts = new CancellationTokenSource();
        }

        // Chain logic: && skips on failure, || skips on success, ; always runs
        if (ci > 0)
        {
            var prevOp = chainOps[ci - 1];
            if (prevOp == "&&" && lastSegmentFailed) continue;
            if (prevOp == "||" && !lastSegmentFailed) continue;
            // ";" always falls through
        }

        // ── Background Job Detection ────────────────────────────────
        bool runInBackground = false;
        if (segment.EndsWith(" &"))
        {
            segment = segment[..^2].TrimEnd();
            runInBackground = true;
        }

        // ── UNC path handling ──────────────────────────────────────
        if (segment.Contains("//ssh:") && UncHandler.TryHandle(segment, out bool uncFailed))
        {
            lastSegmentFailed = uncFailed;
            lastExitCode = uncFailed ? 1 : 0;
            continue;
        }

        // ── Try Built-in Commands ───────────────────────────────────
        // Job control builtins (interactive only)
        if (segment.Equals("jobs", StringComparison.OrdinalIgnoreCase))
        {
            if (state.JobManager != null)
            {
                var allJobs = state.JobManager.GetJobs();
                if (allJobs.Count == 0)
                {
                    Console.ForegroundColor = Theme.Current.Muted;
                    Console.WriteLine("  no jobs");
                    Console.ResetColor();
                }
                else
                {
                    foreach (var job in allJobs)
                    {
                        var status = job.IsCompleted ? "done" : "running";
                        var elapsed = DateTime.Now - job.StartTime;
                        Console.ForegroundColor = Theme.Current.Muted;
                        Console.Write($"  [{job.JobId}] ");
                        Console.ForegroundColor = job.IsCompleted ? Theme.Current.Accent
                                                : Theme.Current.Warning;
                        Console.Write($"{status,-10}");
                        Console.ResetColor();
                        Console.WriteLine($" {job.Command}  ({elapsed.TotalSeconds:F0}s)");
                    }
                }
            }
            lastSegmentFailed = false;
            continue;
        }

        if (segment.StartsWith("fg ", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("fg", StringComparison.OrdinalIgnoreCase))
        {
            if (state.JobManager == null) { lastSegmentFailed = true; continue; }
            var idStr = segment.Length > 3 ? segment[3..].Trim().TrimStart('%') : "";
            int fgId;
            if (!int.TryParse(idStr, out fgId))
            {
                var recent = state.JobManager.GetJobs()
                    .Where(j => !j.IsCompleted)
                    .OrderByDescending(j => j.JobId)
                    .FirstOrDefault();
                if (recent != null) fgId = recent.JobId;
                else
                {
                    Console.ForegroundColor = Theme.Current.Error;
                    Console.Error.WriteLine("fg: no current job");
                    Console.ResetColor();
                    lastSegmentFailed = true;
                    continue;
                }
            }

            var job = state.JobManager.GetJob(fgId);
            if (job == null)
            {
                Console.ForegroundColor = Theme.Current.Error;
                Console.Error.WriteLine($"fg: no such job: {fgId}");
                Console.ResetColor();
                lastSegmentFailed = true;
                continue;
            }

            // Bring background job to foreground — wait for it
            var fgResults = state.JobManager.WaitForJob(fgId);
            if (fgResults != null && fgResults.Count > 0)
                OutputRenderer.Render(fgResults);
            continue;
        }

        // ── bg (no-op: suspension is disabled) ──────────────────────
        if (segment.StartsWith("bg ", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("bg", StringComparison.OrdinalIgnoreCase))
        {
            Console.ForegroundColor = Theme.Current.Error;
            Console.Error.WriteLine("bg: job suspension not supported");
            Console.ResetColor();
            lastSegmentFailed = true;
            continue;
        }

        if (segment.StartsWith("kill %", StringComparison.OrdinalIgnoreCase))
        {
            if (state.JobManager == null) { lastSegmentFailed = true; continue; }
            var idStr = segment[6..].Trim();
            if (int.TryParse(idStr, out var killId))
            {
                if (state.JobManager.KillJob(killId))
                {
                    Console.ForegroundColor = Theme.Current.Muted;
                    Console.WriteLine($"  [{killId}] killed");
                    Console.ResetColor();
                }
                else
                {
                    Console.ForegroundColor = Theme.Current.Error;
                    Console.Error.WriteLine($"kill: no such job: {killId}");
                    Console.ResetColor();
                    lastSegmentFailed = true;
                }
            }
            continue;
        }

        if (segment.Equals("wait", StringComparison.OrdinalIgnoreCase) ||
            segment.StartsWith("wait ", StringComparison.OrdinalIgnoreCase))
        {
            if (state.JobManager == null) { lastSegmentFailed = false; continue; }
            if (segment.Equals("wait", StringComparison.OrdinalIgnoreCase))
            {
                // Wait for ALL running jobs
                var running = state.JobManager.GetJobs().Where(j => !j.IsCompleted).ToList();
                if (running.Count == 0)
                {
                    Console.ForegroundColor = Theme.Current.Muted;
                    Console.WriteLine("  no running jobs");
                    Console.ResetColor();
                }
                else
                {
                    foreach (var job in running)
                    {
                        var results = state.JobManager.WaitForJob(job.JobId);
                        Console.ForegroundColor = Theme.Current.Muted;
                        Console.WriteLine($"  [{job.JobId}] done: {job.Command}");
                        Console.ResetColor();
                        if (results != null && results.Count > 0)
                            OutputRenderer.Render(results);
                    }
                }
            }
            else
            {
                var idStr = segment[5..].Trim().TrimStart('%');
                if (int.TryParse(idStr, out var waitId))
                {
                    var results = state.JobManager.WaitForJob(waitId);
                    if (results == null)
                    {
                        Console.ForegroundColor = Theme.Current.Error;
                        Console.Error.WriteLine($"wait: no such job: {waitId}");
                        Console.ResetColor();
                        lastSegmentFailed = true;
                        continue;
                    }
                    Console.ForegroundColor = Theme.Current.Muted;
                    Console.WriteLine($"  [{waitId}] done");
                    Console.ResetColor();
                    if (results.Count > 0)
                        OutputRenderer.Render(results);
                }
            }
            lastSegmentFailed = false;
            continue;
        }

        if (segment.Equals("exit", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("quit", StringComparison.OrdinalIgnoreCase))
        {
            shouldExit = true;
            break;
        }

        // ── --help flag: "file --help" → "help file", "for --help" → "help loops"
        if (segment.EndsWith(" --help", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("--help", StringComparison.OrdinalIgnoreCase))
        {
            var keyword = segment.Equals("--help", StringComparison.OrdinalIgnoreCase)
                ? null
                : segment[..segment.LastIndexOf(" --help", StringComparison.OrdinalIgnoreCase)].Trim();
            var topic = MapKeywordToHelpTopic(keyword);
            HelpRenderer.Render(topic ?? keyword);
            lastSegmentFailed = false;
            continue;
        }

        if (segment.Equals("help", StringComparison.OrdinalIgnoreCase) ||
            segment.StartsWith("help ", StringComparison.OrdinalIgnoreCase))
        {
            var helpArg = segment.Length > 5 ? segment[5..].Trim() : null;
            if (string.IsNullOrEmpty(helpArg))
            {
                // No topic — show interactive shell help
                if (state.LineEditor != null)
                    ShowHelp(state.LineEditor, state.Translator);
            }
            else
            {
                // Topic-based help from embedded rush-help.yaml
                HelpRenderer.Render(helpArg);
            }
            lastSegmentFailed = false;
            continue;
        }

        // ── man — intercept for Rush builtins, pass through for everything else ──
        if (segment.StartsWith("man ", StringComparison.OrdinalIgnoreCase))
        {
            var manTopic = segment[4..].Trim();
            var helpTopic = MapKeywordToHelpTopic(manTopic);
            if (helpTopic != null || HelpCommand.GetTopic(manTopic) != null)
            {
                HelpRenderer.Render(helpTopic ?? manTopic);
                lastSegmentFailed = false;
                continue;
            }
            // Not a Rush topic — fall through to system man
        }

        // ── which — check builtins before falling through to Get-Command ──
        if (segment.StartsWith("which ", StringComparison.OrdinalIgnoreCase) ||
            segment.StartsWith("type ", StringComparison.OrdinalIgnoreCase))
        {
            var whichCmd = segment.IndexOf(' ') is var sp && sp > 0 ? segment[(sp + 1)..].Trim() : "";
            if (!string.IsNullOrEmpty(whichCmd))
            {
                var rushBuiltins = RushConstants.Builtins;

                if (rushBuiltins.Contains(whichCmd))
                {
                    Console.WriteLine($"{whichCmd}: rush builtin");
                    lastSegmentFailed = false;
                }
                else if (state.Config.Aliases.ContainsKey(whichCmd))
                {
                    Console.WriteLine($"{whichCmd}: aliased to '{state.Config.Aliases[whichCmd]}'");
                    lastSegmentFailed = false;
                }
                else
                {
                    // Fall through to Get-Command for external commands
                    using var ps = PowerShell.Create();
                    ps.Runspace = state.Runspace;
                    ps.AddScript($"Get-Command '{whichCmd.Replace("'", "''")}' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source");
                    var results = ps.Invoke();
                    if (results.Count > 0 && results[0] != null)
                    {
                        Console.WriteLine(results[0].ToString());
                        lastSegmentFailed = false;
                    }
                    else
                    {
                        Console.ForegroundColor = Theme.Current.Error;
                        Console.Error.WriteLine($"{whichCmd} not found");
                        Console.ResetColor();
                        lastSegmentFailed = true;
                    }
                }
                continue;
            }
        }

        // ── set (settings viewer/editor) ────────────────────────────
        if (segment.Equals("set", StringComparison.OrdinalIgnoreCase) ||
            segment.StartsWith("set ", StringComparison.OrdinalIgnoreCase))
        {
            var setArg = segment.Length > 3 ? segment[4..].Trim() : "";

            // Backward-compatible shortcuts
            if (setArg.Equals("vi", StringComparison.OrdinalIgnoreCase))
            {
                if (state.LineEditor != null)
                    state.LineEditor.Mode = EditMode.Vi;
                state.Config.EditMode = "vi";
                Console.ForegroundColor = Theme.Current.Muted;
                Console.WriteLine("  editMode = vi");
                Console.ResetColor();
            }
            else if (setArg.Equals("emacs", StringComparison.OrdinalIgnoreCase))
            {
                if (state.LineEditor != null)
                    state.LineEditor.Mode = EditMode.Emacs;
                state.Config.EditMode = "emacs";
                Console.ForegroundColor = Theme.Current.Muted;
                Console.WriteLine("  editMode = emacs");
                Console.ResetColor();
            }
            else if (setArg == "-e") { state.SetE = true; state.Config.StopOnError = true; Console.ForegroundColor = Theme.Current.Muted; Console.WriteLine("  stopOnError = true"); Console.ResetColor(); }
            else if (setArg == "+e") { state.SetE = false; state.Config.StopOnError = false; Console.ForegroundColor = Theme.Current.Muted; Console.WriteLine("  stopOnError = false"); Console.ResetColor(); }
            else if (setArg == "-x") { state.SetX = true; state.Config.TraceCommands = true; Console.ForegroundColor = Theme.Current.Muted; Console.WriteLine("  traceCommands = true"); Console.ResetColor(); }
            else if (setArg == "+x") { state.SetX = false; state.Config.TraceCommands = false; Console.ForegroundColor = Theme.Current.Muted; Console.WriteLine("  traceCommands = false"); Console.ResetColor(); }
            else if (setArg == "-o pipefail") { state.SetPipefail = true; state.Config.PipefailMode = true; Console.ForegroundColor = Theme.Current.Muted; Console.WriteLine("  pipefailMode = true"); Console.ResetColor(); }
            else if (setArg == "+o pipefail") { state.SetPipefail = false; state.Config.PipefailMode = false; Console.ForegroundColor = Theme.Current.Muted; Console.WriteLine("  pipefailMode = false"); Console.ResetColor(); }
            else if (string.IsNullOrEmpty(setArg))
            {
                // set (no args) — show all settings grouped by category
                ShowAllSettings(state);
            }
            else if (setArg.StartsWith("--save ", StringComparison.OrdinalIgnoreCase))
            {
                // set --save key value — change and persist
                var rest = setArg[7..].Trim();
                var parts = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2)
                {
                    if (state.Config.SetValue(parts[0], parts[1]))
                    {
                        ApplySettingToRuntime(parts[0], state);
                        state.Config.Save();
                        Console.ForegroundColor = Theme.Current.Muted;
                        Console.WriteLine($"  {parts[0]} = {parts[1]} (saved)");
                        Console.ResetColor();
                    }
                    else
                    {
                        var info = RushConfig.FindSetting(parts[0]);
                        Console.ForegroundColor = Theme.Current.Error;
                        if (info == null)
                            Console.Error.WriteLine($"  unknown setting: {parts[0]}");
                        else
                            Console.Error.WriteLine($"  invalid value for {parts[0]}: {parts[1]} (valid: {info.ValidValues})");
                        Console.ResetColor();
                        lastSegmentFailed = true;
                    }
                }
                else
                {
                    Console.ForegroundColor = Theme.Current.Error;
                    Console.Error.WriteLine("  usage: set --save <key> <value>");
                    Console.ResetColor();
                    lastSegmentFailed = true;
                }
            }
            else if (setArg.StartsWith("--secret ", StringComparison.OrdinalIgnoreCase))
            {
                // set --secret KEY value — persist env var to secrets.rush
                var rest = setArg[9..].Trim();
                var parts = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2)
                {
                    var secretKey = parts[0];
                    var secretVal = parts[1].Trim('"', '\'');
                    // Set in current session immediately
                    Environment.SetEnvironmentVariable(secretKey, secretVal);
                    // Persist to secrets.rush
                    try
                    {
                        SecretsFile.SetExport(secretKey, secretVal);
                        Console.ForegroundColor = Theme.Current.Muted;
                        Console.WriteLine($"  {secretKey} = *** (saved to secrets.rush)");
                        Console.ResetColor();
                    }
                    catch (Exception ex)
                    {
                        Console.ForegroundColor = Theme.Current.Error;
                        Console.Error.WriteLine($"  error saving secret: {ex.Message}");
                        Console.ResetColor();
                        lastSegmentFailed = true;
                    }
                }
                else
                {
                    Console.ForegroundColor = Theme.Current.Error;
                    Console.Error.WriteLine("  usage: set --secret <KEY> <value>");
                    Console.ResetColor();
                    lastSegmentFailed = true;
                }
            }
            else
            {
                // set key value — change for session
                // set key — show one setting
                var parts = setArg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 1)
                {
                    // Show one setting
                    var info = RushConfig.FindSetting(parts[0]);
                    if (info != null)
                    {
                        var val = state.Config.GetValue(info.Key);
                        var isDefault = val == info.DefaultValue;
                        Console.ForegroundColor = Theme.Current.Banner;
                        Console.Write($"  {info.Key}");
                        Console.ResetColor();
                        Console.Write(" = ");
                        Console.ForegroundColor = isDefault ? Theme.Current.Muted : ConsoleColor.White;
                        Console.Write(val);
                        Console.ResetColor();
                        if (isDefault)
                        {
                            Console.ForegroundColor = Theme.Current.Muted;
                            Console.Write(" (default)");
                            Console.ResetColor();
                        }
                        Console.WriteLine();
                        Console.ForegroundColor = Theme.Current.Muted;
                        Console.WriteLine($"  {info.Description}");
                        Console.WriteLine($"  Valid: {info.ValidValues}");
                        Console.ResetColor();
                    }
                    else
                    {
                        Console.ForegroundColor = Theme.Current.Error;
                        Console.Error.WriteLine($"  unknown setting: {parts[0]}");
                        Console.Error.WriteLine("  run 'set' to see all settings");
                        Console.ResetColor();
                        lastSegmentFailed = true;
                    }
                }
                else
                {
                    // Handle: set key --save value (e.g., set bg --save "#hex")
                    bool saveFromValue = false;
                    if (parts[1].StartsWith("--save ", StringComparison.OrdinalIgnoreCase))
                    {
                        saveFromValue = true;
                        parts[1] = parts[1][7..].TrimStart();
                    }

                    // Set value for session (and persist if --save)
                    if (state.Config.SetValue(parts[0], parts[1]))
                    {
                        ApplySettingToRuntime(parts[0], state);
                        if (saveFromValue)
                        {
                            state.Config.Save();
                            Console.ForegroundColor = Theme.Current.Muted;
                            Console.WriteLine($"  {parts[0]} = {parts[1]} (saved)");
                            Console.ResetColor();
                        }
                        else
                        {
                            Console.ForegroundColor = Theme.Current.Muted;
                            Console.WriteLine($"  {parts[0]} = {parts[1]}");
                            Console.ResetColor();
                        }
                    }
                    else
                    {
                        var info = RushConfig.FindSetting(parts[0]);
                        Console.ForegroundColor = Theme.Current.Error;
                        if (info == null)
                            Console.Error.WriteLine($"  unknown setting: {parts[0]}");
                        else
                            Console.Error.WriteLine($"  invalid value for {parts[0]}: {parts[1]} (valid: {info.ValidValues})");
                        Console.ResetColor();
                        lastSegmentFailed = true;
                    }
                }
            }
            if (!lastSegmentFailed) lastSegmentFailed = false;
            continue;
        }

        if (segment.Equals("history -c", StringComparison.OrdinalIgnoreCase))
        {
            if (state.LineEditor != null)
                state.LineEditor.ClearHistory();
            Console.ForegroundColor = Theme.Current.Muted;
            Console.WriteLine("  history cleared");
            Console.ResetColor();
            lastSegmentFailed = false;
            continue;
        }

        if (segment.Equals("history", StringComparison.OrdinalIgnoreCase))
        {
            if (state.LineEditor != null)
                ShowHistory(state.LineEditor);
            lastSegmentFailed = false;
            continue;
        }

        // history | ... → pipe history entries through the pipeline
        if ((segment.StartsWith("history |", StringComparison.OrdinalIgnoreCase)
            || segment.StartsWith("history|", StringComparison.OrdinalIgnoreCase))
            && state.LineEditor != null)
        {
            var history = state.LineEditor.History;
            var entries = new System.Text.StringBuilder();
            entries.Append("@(");
            for (int i = 0; i < history.Count; i++)
            {
                if (i > 0) entries.Append(',');
                entries.Append($"'{history[i].Replace("'", "''")}'");
            }
            entries.Append(')');

            // Replace "history" with the array, translate the rest of the pipeline
            var pipeIdx = segment.IndexOf('|');
            var rest = segment[pipeIdx..]; // "| distinct" etc.
            var histTranslated = state.Translator.Translate($"data {rest}");
            var tPipeIdx = histTranslated?.IndexOf('|') ?? -1;
            var psPipe = tPipeIdx >= 0 ? histTranslated![tPipeIdx..] : rest;

            var psCommand = $"{entries} {psPipe}";
            try
            {
                using var ps = PowerShell.Create();
                ps.Runspace = state.Runspace;
                ps.AddScript(psCommand);
                var results = ps.Invoke();
                foreach (var r in results)
                    Console.WriteLine(r);
                lastSegmentFailed = ps.HadErrors;
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = Theme.Current.Error;
                Console.Error.WriteLine($"history: {ex.Message}");
                Console.ResetColor();
                lastSegmentFailed = true;
            }
            continue;
        }

        if (segment.Equals("alias", StringComparison.OrdinalIgnoreCase))
        {
            ShowAliases(state.Translator);
            lastSegmentFailed = false;
            continue;
        }

        if (segment.Equals("reload", StringComparison.OrdinalIgnoreCase) ||
            segment.StartsWith("reload ", StringComparison.OrdinalIgnoreCase))
        {
            var reloadArg = segment.Length > 6 ? segment[6..].Trim() : "";
            if (reloadArg.Equals("--hard", StringComparison.OrdinalIgnoreCase))
            {
                // Full binary restart with state preservation
                try
                {
                    var rlState = ReloadState.Capture(state.Runspace, state.Config, state.PreviousDirectory, state.SetE, state.SetX, state.SetPipefail);
                    ReloadState.Save(rlState);
                    state.LineEditor?.SaveHistory();

                    // Use "rush" to re-resolve via PATH/symlink, not Environment.ProcessPath
                    // which points at the resolved (possibly stale) binary.
                    var currentBinary = "rush";
                    Console.ForegroundColor = Theme.Current.Muted;
                    Console.WriteLine("  restarting rush...");
                    Console.ResetColor();

                    var psi = new ProcessStartInfo
                    {
                        FileName = currentBinary,
                        ArgumentList = { "--resume" },
                        UseShellExecute = false,
                        RedirectStandardInput = false,
                        RedirectStandardOutput = false,
                        RedirectStandardError = false
                    };
                    var child = Process.Start(psi);
                    if (child != null)
                    {
                        // Child inherits our terminal (stdin/stdout/stderr).
                        // Parent waits then exits with child's exit code.
                        child.WaitForExit();
                        Environment.Exit(child.ExitCode);
                    }
                    else
                    {
                        Console.ForegroundColor = Theme.Current.Error;
                        Console.Error.WriteLine("reload: failed to start new process");
                        Console.ResetColor();
                        lastSegmentFailed = true;
                    }
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = Theme.Current.Error;
                    Console.Error.WriteLine($"reload: {ex.Message}");
                    Console.ResetColor();
                    lastSegmentFailed = true;
                }
            }
            else
            {
                // Soft reload — config only (existing behavior)
                state.Config = RushConfig.Load();
                if (state.IsInteractive)
                {
                    var (reloadE, reloadX, reloadPf) = state.Config.Apply(state.LineEditor, state.Translator);
                    state.SetE = reloadE; state.SetX = reloadX; state.SetPipefail = reloadPf;
                }
                else
                {
                    var (reloadE, reloadX, reloadPf) = state.Config.Apply(null, state.Translator);
                    state.SetE = reloadE; state.SetX = reloadX; state.SetPipefail = reloadPf;
                }
                // Retheme — clear tracking so .rushbg is re-read
                Theme.ActiveRushBgFile = null;
                ApplyTheme(state.Config, Environment.CurrentDirectory);
                // Re-check uutils shimming (user may have installed since startup)
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    ShimCoreutilsIfNeeded(state.Runspace, quiet: true);
                    ShimDiffutilsIfNeeded(state.Runspace, quiet: true);
                }
                Console.ForegroundColor = Theme.Current.Muted;
                Console.WriteLine("  config reloaded");
                Console.ResetColor();
            }
            lastSegmentFailed = false;
            continue;
        }

        // ── init — edit init.rush in $EDITOR, then reload ─────────
        if (segment.Equals("init", StringComparison.OrdinalIgnoreCase))
        {
            var initPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config", "rush", "init.rush");

            // Create file if it doesn't exist
            if (!File.Exists(initPath))
            {
                var dir = Path.GetDirectoryName(initPath)!;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(initPath, "# ~/.config/rush/init.rush\n# Startup script — runs on every shell launch.\n");
            }

            var editor = Environment.GetEnvironmentVariable("EDITOR") ?? "vi";
            try
            {
                var psi = new ProcessStartInfo(editor, initPath) { UseShellExecute = false };
                var proc = Process.Start(psi);
                if (proc != null)
                {
                    proc.WaitForExit();
                    proc.Dispose();

                    // Re-run init.rush to pick up changes
                    RunStartupRushFile(state.Runspace, state.ScriptEngine, "init.rush", state);
                    Console.ForegroundColor = Theme.Current.Muted;
                    Console.WriteLine("  init.rush reloaded");
                    Console.ResetColor();
                }
                else
                {
                    Console.ForegroundColor = Theme.Current.Error;
                    Console.Error.WriteLine($"init: could not start {editor}");
                    Console.ResetColor();
                    lastSegmentFailed = true;
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = Theme.Current.Error;
                Console.Error.WriteLine($"init: {ex.Message}");
                Console.ResetColor();
                lastSegmentFailed = true;
            }
            lastSegmentFailed = false;
            continue;
        }

        if (segment.Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            Console.Clear();
            lastSegmentFailed = false;
            continue;
        }

        // ── mark: output demarcation line ───────────────────────────
        // mark, mark "label", mark 'label', or --- (all dashes)
        // Does NOT match mark | ... (pipe) — only bare mark or mark with quoted label
        if (segment.Equals("mark", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("---", StringComparison.Ordinal) ||
            (segment.StartsWith("---") && segment.All(c => c == '-')))
        {
            EmitMark(null);
            lastSegmentFailed = false;
            continue;
        }
        if (segment.StartsWith("mark ", StringComparison.OrdinalIgnoreCase))
        {
            var labelArg = segment[5..].Trim();
            // Only treat as mark label if quoted — otherwise fall through to normal execution
            // This allows mark | times 2 to work as a pipe
            if ((labelArg.StartsWith('"') && labelArg.EndsWith('"')) ||
                (labelArg.StartsWith('\'') && labelArg.EndsWith('\'')))
            {
                EmitMark(labelArg[1..^1]);
                lastSegmentFailed = false;
                continue;
            }
            else
            {
                // Unquoted label (may contain pipes — ignore them for mark)
                // mark | times 2 → just emit a plain mark (pipes don't apply to builtins)
                var pipeIdx = labelArg.IndexOf('|');
                var cleanLabel = pipeIdx >= 0 ? labelArg[..pipeIdx].Trim() : labelArg;
                EmitMark(string.IsNullOrEmpty(cleanLabel) ? null : cleanLabel);
                lastSegmentFailed = false;
                continue;
            }
        }

        // ── o: cross-platform open (file, URL, directory) ───────────
        if (segment.Equals("o", StringComparison.OrdinalIgnoreCase) ||
            segment.StartsWith("o ", StringComparison.OrdinalIgnoreCase))
        {
            var target = segment.Length > 2 ? segment[2..].Trim() : ".";
            // Strip quotes
            if (target.Length >= 2 &&
                ((target[0] == '"' && target[^1] == '"') || (target[0] == '\'' && target[^1] == '\'')))
                target = target[1..^1];
            // Handle backslash-space
            target = target.Replace("\\ ", " ");

            lastSegmentFailed = OpenWithSystem(target);
            continue;
        }

        // ── Interactive alias definition ────────────────────────────
        // alias ll='ls -la'        (session-only)
        // alias --save ll='ls -la' (persisted to config.json)
        if (segment.StartsWith("alias ", StringComparison.OrdinalIgnoreCase))
        {
            var aliasBody = segment[6..].Trim();
            bool save = false;
            if (aliasBody.StartsWith("--save ", StringComparison.OrdinalIgnoreCase))
            {
                save = true;
                aliasBody = aliasBody[7..].Trim();
            }
            var eqPos = aliasBody.IndexOf('=');
            if (eqPos > 0)
            {
                var aliasName = aliasBody[..eqPos].Trim();
                var aliasValue = aliasBody[(eqPos + 1)..].Trim();
                // Strip surrounding quotes
                if ((aliasValue.StartsWith('\'') && aliasValue.EndsWith('\'')) ||
                    (aliasValue.StartsWith('"') && aliasValue.EndsWith('"')))
                    aliasValue = aliasValue[1..^1];

                state.Translator.RegisterAlias(aliasName, aliasValue);
                if (save)
                {
                    state.Config.Aliases[aliasName] = aliasValue;
                    state.Config.Save();
                }
                Console.ForegroundColor = Theme.Current.Muted;
                Console.WriteLine(save
                    ? $"  alias {aliasName} → {aliasValue} (saved)"
                    : $"  alias {aliasName} → {aliasValue}");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = Theme.Current.Error;
                Console.Error.WriteLine("usage: alias name='command'  (or alias --save name='command')");
                Console.ResetColor();
                lastSegmentFailed = true;
            }
            continue;
        }

        if (segment.StartsWith("unalias ", StringComparison.OrdinalIgnoreCase))
        {
            var name = segment[8..].Trim();
            bool save = false;
            if (name.StartsWith("--save ", StringComparison.OrdinalIgnoreCase))
            {
                save = true;
                name = name[7..].Trim();
            }
            if (state.Translator.UnregisterAlias(name))
            {
                if (save)
                {
                    state.Config.Aliases.Remove(name);
                    state.Config.Save();
                }
                Console.ForegroundColor = Theme.Current.Muted;
                Console.WriteLine(save
                    ? $"  unalias {name} (saved)"
                    : $"  unalias {name}");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = Theme.Current.Error;
                Console.Error.WriteLine($"unalias: {name}: not found");
                Console.ResetColor();
                lastSegmentFailed = true;
            }
            continue;
        }

        // ── export: set environment variable ────────────────────────
        // export FOO=bar  or  export FOO="bar baz"  or  export --save FOO=bar
        if (segment.StartsWith("export ", StringComparison.OrdinalIgnoreCase) &&
            (segment.Contains('=') || segment.Contains("--save", StringComparison.OrdinalIgnoreCase)))
        {
            var exportBody = segment[7..].Trim();

            // Detect and strip --save flag
            bool save = false;
            if (exportBody.StartsWith("--save ", StringComparison.OrdinalIgnoreCase) ||
                exportBody.StartsWith("--save\t", StringComparison.OrdinalIgnoreCase))
            {
                save = true;
                exportBody = exportBody[6..].TrimStart();
            }

            var eqPos = exportBody.IndexOf('=');
            if (eqPos > 0)
            {
                var varName = exportBody[..eqPos].Trim();
                var varValue = exportBody[(eqPos + 1)..].Trim();
                // Strip surrounding quotes
                if ((varValue.StartsWith('\'') && varValue.EndsWith('\'')) ||
                    (varValue.StartsWith('"') && varValue.EndsWith('"')))
                    varValue = varValue[1..^1];

                Environment.SetEnvironmentVariable(varName, varValue);
                // Also set in PowerShell runspace
                using var ps = PowerShell.Create();
                ps.Runspace = state.Runspace;
                ps.AddScript($"$env:{varName} = '{varValue.Replace("'", "''")}'");
                ps.Invoke();

                if (save)
                    SaveExportToInit(varName, varValue);
            }
            else if (save)
            {
                Console.ForegroundColor = Theme.Current.Error;
                Console.Error.WriteLine("export --save: requires KEY=value");
                Console.ResetColor();
                lastSegmentFailed = true;
                continue;
            }
            lastSegmentFailed = false;
            continue;
        }

        // ── unset: remove environment variable ──────────────────────
        if (segment.StartsWith("unset ", StringComparison.OrdinalIgnoreCase))
        {
            var varName = segment[6..].Trim();
            Environment.SetEnvironmentVariable(varName, null);
            using var ps = PowerShell.Create();
            ps.Runspace = state.Runspace;
            ps.AddScript($"Remove-Item Env:{varName} -ErrorAction SilentlyContinue");
            ps.Invoke();
            lastSegmentFailed = false;
            continue;
        }

        // ── printf: formatted output ─────────────────────────────────
        if (segment.StartsWith("printf ", StringComparison.OrdinalIgnoreCase))
        {
            var printfArgs = CommandTranslator.SplitCommandLine(segment[7..]);
            if (printfArgs.Length >= 1)
            {
                var fmt = StripQuotes(printfArgs[0]);
                var fmtArgs = printfArgs.Skip(1).Select(StripQuotes).ToArray();
                Console.Write(PrintfFormat(fmt, fmtArgs));
            }
            lastSegmentFailed = false;
            continue;
        }

        // ── read: read line from stdin into variable ─────────────────
        if (segment.Equals("read", StringComparison.OrdinalIgnoreCase) ||
            segment.StartsWith("read ", StringComparison.OrdinalIgnoreCase))
        {
            string? readPrompt = null;
            string varName = "REPLY";
            var readArgs = segment.Length > 5
                ? CommandTranslator.SplitCommandLine(segment[5..])
                : Array.Empty<string>();

            int argIdx = 0;
            while (argIdx < readArgs.Length)
            {
                if (readArgs[argIdx] == "-p" && argIdx + 1 < readArgs.Length)
                {
                    readPrompt = StripQuotes(readArgs[argIdx + 1]);
                    argIdx += 2;
                }
                else
                {
                    varName = readArgs[argIdx];
                    argIdx++;
                }
            }

            if (readPrompt != null) Console.Write(readPrompt);
            var value = CatCommand.ReadLineInterruptible(state.BuiltinCts.Token) ?? "";
            if (state.BuiltinCts.IsCancellationRequested)
            {
                lastSegmentFailed = true;
                lastExitCode = 130;
                continue;
            }

            using var readPs = PowerShell.Create();
            readPs.Runspace = state.Runspace;
            readPs.AddScript($"${varName} = '{value.Replace("'", "''")}'");
            readPs.Invoke();
            Environment.SetEnvironmentVariable(varName, value);

            lastSegmentFailed = false;
            continue;
        }

        // ── exec: replace process ────────────────────────────────────
        if (segment.StartsWith("exec ", StringComparison.OrdinalIgnoreCase))
        {
            var execCmd = segment[5..].Trim();
            var execParts = CommandTranslator.SplitCommandLine(execCmd);
            if (execParts.Length > 0)
            {
                state.LineEditor?.SaveHistory();
                var psi = new ProcessStartInfo
                {
                    FileName = StripQuotes(execParts[0]),
                    UseShellExecute = false
                };
                foreach (var arg in execParts.Skip(1))
                    psi.ArgumentList.Add(StripQuotes(arg));
                try
                {
                    var proc = Process.Start(psi);
                    proc?.WaitForExit();
                    Environment.Exit(proc?.ExitCode ?? 1);
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = Theme.Current.Error;
                    Console.Error.WriteLine($"exec: {ex.Message}");
                    Console.ResetColor();
                    lastSegmentFailed = true;
                }
            }
            continue;
        }

        // ── trap: register signal handler ────────────────────────────
        if (segment.StartsWith("trap ", StringComparison.OrdinalIgnoreCase))
        {
            var trapArgs = CommandTranslator.SplitCommandLine(segment[5..]);
            if (trapArgs.Length >= 2)
            {
                var trapCmd = StripQuotes(trapArgs[0]);
                var signal = trapArgs[1].ToUpperInvariant();
                state.Traps[signal] = trapCmd;
                Console.ForegroundColor = Theme.Current.Muted;
                Console.WriteLine($"  trap {signal} → {trapCmd}");
                Console.ResetColor();
            }
            else if (trapArgs.Length == 1 && trapArgs[0] == "-l")
            {
                foreach (var (sig, cmd) in state.Traps)
                {
                    Console.ForegroundColor = Theme.Current.Muted;
                    Console.WriteLine($"  {sig} → {cmd}");
                    Console.ResetColor();
                }
            }
            lastSegmentFailed = false;
            continue;
        }

        // ── source: run a rush script ───────────────────────────────
        if (segment.StartsWith("source ", StringComparison.OrdinalIgnoreCase) ||
            segment.StartsWith(". ", StringComparison.OrdinalIgnoreCase))
        {
            var scriptPath = segment.StartsWith("source ") ? segment[7..].Trim() : segment[2..].Trim();
            if (scriptPath.StartsWith("~/"))
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                scriptPath = Path.Combine(home, scriptPath[2..]);
            }
            if (File.Exists(scriptPath))
            {
                try
                {
                    var scriptSource = File.ReadAllText(scriptPath);

                    // Use ScriptEngine for .rush files, line-by-line for others
                    if (scriptPath.EndsWith(".rush", StringComparison.OrdinalIgnoreCase))
                    {
                        var psCode = state.ScriptEngine.TranspileFile(scriptSource);
                        if (psCode != null)
                        {
                            using var ps = PowerShell.Create();
                            ps.Runspace = state.Runspace;
                            ps.AddScript(psCode);
                            var scriptResults = ps.Invoke().ToList();
                            if (scriptResults.Count > 0) OutputRenderer.Render(scriptResults);
                            if (ps.HadErrors) OutputRenderer.RenderErrors(ps.Streams);

                            // Reset ErrorActionPreference — TranspileFile sets it to 'Stop'
                            using var reset = PowerShell.Create();
                            reset.Runspace = state.Runspace;
                            reset.AddScript("$ErrorActionPreference = 'Continue'");
                            reset.Invoke();
                        }
                    }
                    else
                    {
                        // Legacy: line-by-line execution for non-.rush scripts
                        var scriptLines = scriptSource.Split('\n');
                        foreach (var rawLine in scriptLines)
                        {
                            var scriptLine = rawLine.Trim();
                            if (string.IsNullOrEmpty(scriptLine) || scriptLine.StartsWith('#')) continue;
                            var scriptTranslated = state.Translator.Translate(scriptLine) ?? scriptLine;
                            using var ps = PowerShell.Create();
                            ps.Runspace = state.Runspace;
                            ps.AddScript(scriptTranslated);
                            var scriptResults = ps.Invoke();
                            if (scriptResults.Count > 0) OutputRenderer.Render(scriptResults);
                            if (ps.HadErrors) OutputRenderer.RenderErrors(ps.Streams);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = Theme.Current.Error;
                    Console.Error.WriteLine($"source: {ex.Message}");
                    Console.ResetColor();
                    lastSegmentFailed = true;
                    continue;
                }
            }
            else
            {
                Console.ForegroundColor = Theme.Current.Error;
                Console.Error.WriteLine($"source: {scriptPath}: no such file");
                Console.ResetColor();
                lastSegmentFailed = true;
                continue;
            }
            lastSegmentFailed = false;
            continue;
        }

        // ── pushd / popd / dirs ─────────────────────────────────────
        if (segment.StartsWith("pushd ", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("pushd", StringComparison.OrdinalIgnoreCase))
        {
            var currentDir = GetRunspaceDir(state.Runspace);
            if (segment.Equals("pushd", StringComparison.OrdinalIgnoreCase))
            {
                // No arg: swap current with top of stack
                if (state.DirStack.Count == 0)
                {
                    Console.ForegroundColor = Theme.Current.Error;
                    Console.Error.WriteLine("pushd: no other directory");
                    Console.ResetColor();
                    lastSegmentFailed = true;
                }
                else
                {
                    var top = state.DirStack.Pop();
                    if (currentDir != null) state.DirStack.Push(currentDir);
                    var (f, _) = HandleCd(state.Runspace, $"cd {top}", null);
                    lastSegmentFailed = f;
                    if (!f) PrintDirStack(state.Runspace, state.DirStack);
                }
            }
            else
            {
                // pushd <dir>: push current, cd to target
                var target = segment[6..].Trim();
                if (currentDir != null) state.DirStack.Push(currentDir);
                var (f, _) = HandleCd(state.Runspace, $"cd {target}", null);
                if (f) { if (currentDir != null) state.DirStack.Pop(); } // undo push on failure
                else PrintDirStack(state.Runspace, state.DirStack);
                lastSegmentFailed = f;
            }
            continue;
        }

        if (segment.Equals("popd", StringComparison.OrdinalIgnoreCase))
        {
            if (state.DirStack.Count == 0)
            {
                Console.ForegroundColor = Theme.Current.Error;
                Console.Error.WriteLine("popd: directory stack empty");
                Console.ResetColor();
                lastSegmentFailed = true;
            }
            else
            {
                var target = state.DirStack.Pop();
                var (f, _) = HandleCd(state.Runspace, $"cd {target}", null);
                lastSegmentFailed = f;
                if (!f) PrintDirStack(state.Runspace, state.DirStack);
            }
            continue;
        }

        if (segment.Equals("dirs", StringComparison.OrdinalIgnoreCase) ||
            segment.StartsWith("dirs ", StringComparison.OrdinalIgnoreCase))
        {
            bool verbose = segment.Contains("-v", StringComparison.OrdinalIgnoreCase);
            var currentDir = GetRunspaceDir(state.Runspace) ?? ".";
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            if (verbose)
            {
                string Shorten(string p) => p.StartsWith(home) ? "~" + p[home.Length..] : p;
                Console.WriteLine($"  0  {Shorten(currentDir)}");
                int idx = 1;
                foreach (var d in state.DirStack)
                    Console.WriteLine($"  {idx++}  {Shorten(d)}");
            }
            else
            {
                PrintDirStack(state.Runspace, state.DirStack);
            }
            lastSegmentFailed = false;
            continue;
        }

        // ── Bare dot shortcuts (.., ..., ...., etc.) ───────────────
        // .. → cd ..   ... → cd ../..   .... → cd ../../..  etc.
        if (segment.Length >= 2 && segment.All(c => c == '.'))
        {
            var levels = string.Join("/", Enumerable.Repeat("..", segment.Length - 1));
            segment = $"cd {levels}";
            // Fall through to cd handler below
        }

        // ── ai --exec: run last AI response ──────────────────────
        if (segment.Equals("ai --exec", StringComparison.OrdinalIgnoreCase) ||
            segment.TrimEnd().Equals("ai --exec", StringComparison.OrdinalIgnoreCase))
        {
            var lastResponse = AiCommand.GetLastResponse();
            if (string.IsNullOrWhiteSpace(lastResponse))
            {
                Console.ForegroundColor = Theme.Current.Error;
                Console.Error.WriteLine("ai: no previous response to execute");
                Console.ResetColor();
                lastSegmentFailed = true;
                continue;
            }

            // Extract first non-empty line as the command
            var command = lastResponse
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .FirstOrDefault(l => l.Length > 0);

            if (string.IsNullOrEmpty(command))
            {
                Console.ForegroundColor = Theme.Current.Error;
                Console.Error.WriteLine("ai: last response contained no executable content");
                Console.ResetColor();
                lastSegmentFailed = true;
                continue;
            }

            // Show what we're executing
            Console.ForegroundColor = Theme.Current.Muted;
            Console.WriteLine($"  \u2192 {command}");
            Console.ResetColor();

            // Replace segment and fall through to normal dispatch
            segment = command;
        }

        // ── ai builtin ────────────────────────────────────────────
        if (segment.Equals("ai", StringComparison.OrdinalIgnoreCase) ||
            segment.StartsWith("ai ", StringComparison.OrdinalIgnoreCase))
        {
            if (state.IsInteractive)
            {
                var (aiCmd, _, aiStdin, _) = RedirectionParser.Parse(segment);
                var aiArgs = aiCmd.Length > 2 ? aiCmd[2..].TrimStart() : "";
                string? pipedInput = null;
                if (aiStdin != null)
                {
                    try { pipedInput = File.ReadAllText(aiStdin.FilePath); }
                    catch (Exception ex)
                    {
                        Console.ForegroundColor = Theme.Current.Error;
                        Console.Error.WriteLine($"ai: {ex.Message}");
                        Console.ResetColor();
                        lastSegmentFailed = true;
                        continue;
                    }
                }
                using var aiCts = new CancellationTokenSource();
                ConsoleCancelEventHandler aiCancel = (_, e) => { e.Cancel = true; aiCts.Cancel(); };
                Console.CancelKeyPress += aiCancel;
                try
                {
                    // Check for --agent flag: autonomous multi-turn agent mode
                    if (aiArgs.Contains("--agent", StringComparison.OrdinalIgnoreCase))
                    {
                        var llm = new LlmMode(state.Runspace, state.ScriptEngine, state.Translator, RushVersion.Full);
                        var (aiOk, _) = AiCommand.ExecuteAgentAsync(aiArgs, llm, state.Config,
                            state.LineEditor!.History, aiCts.Token).GetAwaiter().GetResult();
                        lastSegmentFailed = !aiOk;
                    }
                    else
                    {
                        var (aiOk, _) = AiCommand.ExecuteAsync(aiArgs, pipedInput, state.Config,
                            state.LineEditor!.History, aiCts.Token).GetAwaiter().GetResult();
                        lastSegmentFailed = !aiOk;
                    }
                }
                finally
                {
                    Console.CancelKeyPress -= aiCancel;
                }
            }
            continue;
        }

        // ── path: PATH management ─────────────────────────────────────
        if (segment.Equals("path", StringComparison.OrdinalIgnoreCase) ||
            segment.StartsWith("path ", StringComparison.OrdinalIgnoreCase))
        {
            var pathArgs = segment.Length > 4 ? segment[4..].Trim() : "";
            lastSegmentFailed = HandlePathCommand(pathArgs, state.Runspace, quiet: state.IsStartupScript);
            continue;
        }

        // ── cd (with - support) ─────────────────────────────────────
        if (segment.StartsWith("cd ", StringComparison.OrdinalIgnoreCase) || segment == "cd")
        {
            var (cdFailed, newPrev) = HandleCd(state.Runspace, segment, state.PreviousDirectory);
            if (!cdFailed && newPrev != null) state.PreviousDirectory = newPrev;
            lastSegmentFailed = cdFailed;
            continue;
        }

        // ── sync: GitHub config sync ────────────────────────────────
        if (segment.Equals("sync", StringComparison.OrdinalIgnoreCase) ||
            segment.StartsWith("sync ", StringComparison.OrdinalIgnoreCase))
        {
            var syncArgs = segment.Length > 4 ? segment[4..].Trim() : "";
            ConfigSync.HandleSync(syncArgs);
            lastSegmentFailed = false;
            continue;
        }

        // ── setbg: shorthand for `set bg` ────────────────────────
        // Supports: setbg "#hex", setbg --save "#hex", setbg reset
        //           setbg --selector [--save|--local]
        if (segment.Equals("setbg", StringComparison.OrdinalIgnoreCase) ||
            segment.StartsWith("setbg ", StringComparison.OrdinalIgnoreCase))
        {
            var bgArgs = segment.Length > 5 ? segment[5..].Trim() : "";
            bool saveBg = false;
            bool localBg = false;
            bool selectorMode = false;

            // Parse flags in any order
            var flagArgs = bgArgs;
            while (true)
            {
                if (flagArgs.StartsWith("--save", StringComparison.OrdinalIgnoreCase) &&
                    (flagArgs.Length == 6 || flagArgs[6] == ' ' || flagArgs[6] == '\t'))
                {
                    saveBg = true;
                    flagArgs = flagArgs.Length > 6 ? flagArgs[6..].TrimStart() : "";
                }
                else if (flagArgs.StartsWith("--local", StringComparison.OrdinalIgnoreCase) &&
                    (flagArgs.Length == 7 || flagArgs[7] == ' ' || flagArgs[7] == '\t'))
                {
                    localBg = true;
                    flagArgs = flagArgs.Length > 7 ? flagArgs[7..].TrimStart() : "";
                }
                else if (flagArgs.StartsWith("--selector", StringComparison.OrdinalIgnoreCase) &&
                    (flagArgs.Length == 10 || flagArgs[10] == ' ' || flagArgs[10] == '\t'))
                {
                    selectorMode = true;
                    flagArgs = flagArgs.Length > 10 ? flagArgs[10..].TrimStart() : "";
                }
                else break;
            }

            if (selectorMode)
            {
                var currentBg = Environment.GetEnvironmentVariable("RUSH_BG");
                var selected = ColorPicker.Run(currentBg);
                if (selected != null)
                {
                    if (localBg)
                    {
                        // Write to .rushbg in current directory
                        var rushBgPath = Path.Combine(Environment.CurrentDirectory, ".rushbg");
                        File.WriteAllText(rushBgPath, selected);
                        Theme.ActiveRushBgFile = rushBgPath;
                        Theme.SetBackground(selected);
                        Console.ForegroundColor = Theme.Current.Muted;
                        Console.WriteLine($"  bg = {selected} (saved to .rushbg)");
                        Console.ResetColor();
                    }
                    else
                    {
                        state.Config.SetValue("bg", selected);
                        ApplySettingToRuntime("bg", state);
                        Theme.ActiveRushBgFile = null;
                        if (saveBg)
                        {
                            state.Config.Save();
                            Console.ForegroundColor = Theme.Current.Muted;
                            Console.WriteLine($"  bg = {selected} (saved)");
                            Console.ResetColor();
                        }
                    }
                    lastSegmentFailed = false;
                }
                else
                {
                    // Cancelled — bg already restored by ColorPicker
                    lastSegmentFailed = false;
                }
                lastExitCode = 0;
                continue;
            }

            var bgValue = string.IsNullOrEmpty(flagArgs) ? "reset" : flagArgs.Trim('"', '\'');
            if (state.Config.SetValue("bg", bgValue))
            {
                ApplySettingToRuntime("bg", state);
                Theme.ActiveRushBgFile = null; // manual setbg overrides .rushbg tracking
                if (saveBg)
                {
                    state.Config.Save();
                    Console.ForegroundColor = Theme.Current.Muted;
                    Console.WriteLine($"  bg = {bgValue} (saved)");
                    Console.ResetColor();
                }
                lastSegmentFailed = false;
            }
            else
            {
                Console.ForegroundColor = Theme.Current.Error;
                Console.Error.WriteLine($"setbg: invalid color '{bgValue}' — use #RGB or #RRGGBB format");
                Console.ResetColor();
                lastSegmentFailed = true;
            }
            lastExitCode = lastSegmentFailed ? 1 : 0;
            continue;
        }

        // ── sql builtin (when not piped) ────────────────────────
        // Native database queries with table/JSON/CSV output.
        // When piped (sql @db "query" | grep), falls through to stdout.
        if ((segment.Equals("sql", StringComparison.OrdinalIgnoreCase) ||
             segment.StartsWith("sql ", StringComparison.OrdinalIgnoreCase) ||
             segment.StartsWith("sql\t", StringComparison.OrdinalIgnoreCase)) &&
            !segment.Contains('|'))
        {
            var sqlArgs = segment.Length > 3 ? segment[3..].TrimStart() : "";
            lastSegmentFailed = !SqlCommand.Execute(sqlArgs);
            lastExitCode = lastSegmentFailed ? 1 : 0;
            continue;
        }

        // ── cat builtin (when not piped) ────────────────────────
        // Direct .NET file I/O — supports stdin, concatenation, -n.
        // When piped (cat file | grep), falls through to native cat
        // (Unix: /bin/cat, Windows: PowerShell's cat alias for Get-Content).
        if ((segment.Equals("cat", StringComparison.OrdinalIgnoreCase) ||
             segment.StartsWith("cat ", StringComparison.OrdinalIgnoreCase) ||
             segment.StartsWith("cat\t", StringComparison.OrdinalIgnoreCase)) &&
            !segment.Contains('|') &&
            !segment.Contains("<("))
        {
            var (catCmd, catRedirect, catStdin, _) = RedirectionParser.Parse(segment);
            string? catStdinContent = null;
            if (catStdin != null)
            {
                try { catStdinContent = File.ReadAllText(catStdin.FilePath); }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"cat: {ex.Message}");
                    lastSegmentFailed = true;
                    lastExitCode = 1;
                    continue;
                }
            }
            var catArgs = catCmd.Length > 3 ? catCmd[3..].TrimStart() : "";
            lastSegmentFailed = !CatCommand.Execute(catArgs, catRedirect, catStdinContent, state.BuiltinCts.Token);
            lastExitCode = lastSegmentFailed ? 1 : 0;
            continue;
        }

        // ── set -x trace ─────────────────────────────────────────
        if (state.SetX)
        {
            Console.ForegroundColor = Theme.Current.Muted;
            Console.Error.WriteLine($"+ {segment}");
            Console.ResetColor();
        }

        // ── Parse Redirection ─────────────────────────────────────
        var (cmdPart, redirect, stdinRedirect, stderrRedirect) = RedirectionParser.Parse(segment);

        // ── Translate & Execute (with timing) ────────────────────────
        var translated = state.Translator.Translate(cmdPart);
        var commandToRun = translated ?? cmdPart;
        var cmdFirstWord = cmdPart.Split(' ', 2)[0];
        bool isUserAlias = state.Translator.IsUserAlias(cmdFirstWord);

        // Glob expansion: for passthrough (native) commands and user aliases
        if (translated == null || isUserAlias)
            commandToRun = ExpandGlobs(commandToRun);

        // ── Background Job ─────────────────────────────────────────
        if (runInBackground)
        {
            if (state.JobManager != null)
            {
                var jobId = state.JobManager.StartBackground(segment, commandToRun);
                Console.ForegroundColor = Theme.Current.Muted;
                Console.WriteLine($"  [{jobId}] started: {segment}");
                Console.ResetColor();
            }
            lastSegmentFailed = false;
            continue;
        }

        // ── Native Command Execution ─────────────────────────────
        // Native commands (not translated to PowerShell cmdlets) run directly
        // with inherited stdio, giving them real TTY access. This handles
        // shells, TUI apps, REPLs, and any program — no allowlist needed.
        // PowerShell is only used when we need its pipeline/capture features.
        //
        // User aliases (e.g., gp → git push) run natively so they get real
        // TTY access (needed for editors like vim) and stderr is not captured
        // as PowerShell error records (fixes git push "error:" prefix).
        bool needsPowerShell = (translated != null && !isUserAlias)
            || redirect != null
            || stdinRedirect != null
            || CommandTranslator.HasUnquotedPipe(cmdPart)
            || (isUserAlias && CommandTranslator.HasUnquotedPipe(commandToRun));

        // ── Pipe-to-AI interception ── cmd | ai "prompt" ──────────
        if (state.IsInteractive && CommandTranslator.HasUnquotedPipe(cmdPart))
        {
            var pipeSegs = CommandTranslator.SplitOnPipe(cmdPart);
            var lastPipeSeg = pipeSegs[^1].Trim();
            if (lastPipeSeg.Equals("ai", StringComparison.OrdinalIgnoreCase) ||
                lastPipeSeg.StartsWith("ai ", StringComparison.OrdinalIgnoreCase))
            {
                // Execute everything before | ai through PowerShell, capture output
                var prePipeline = string.Join(" | ", pipeSegs[..^1]);
                var aiPipeArgs = lastPipeSeg.Length > 2 ? lastPipeSeg[2..].TrimStart() : "";
                string pipedContent;
                try
                {
                    using var ps = PowerShell.Create();
                    ps.Runspace = state.Runspace;
                    // Translate the prefix pipeline
                    var preTrans = state.Translator.Translate(prePipeline) ?? prePipeline;
                    ps.AddScript(preTrans);
                    var results = ps.Invoke();
                    pipedContent = string.Join("\n", results.Select(r => r?.ToString() ?? ""));
                    // Include error stream so AI sees stderr too
                    if (ps.Streams.Error.Count > 0)
                    {
                        var errors = string.Join("\n", ps.Streams.Error.Select(e => e.ToString()));
                        pipedContent = string.IsNullOrEmpty(pipedContent)
                            ? errors : pipedContent + "\n" + errors;
                    }
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = Theme.Current.Error;
                    Console.Error.WriteLine($"ai: pipe error: {ex.Message}");
                    Console.ResetColor();
                    lastSegmentFailed = true;
                    continue;
                }
                using var aiPipeCts = new CancellationTokenSource();
                ConsoleCancelEventHandler aiPipeCancel = (_, e) => { e.Cancel = true; aiPipeCts.Cancel(); };
                Console.CancelKeyPress += aiPipeCancel;
                try
                {
                    var (aiPipeOk, _) = AiCommand.ExecuteAsync(aiPipeArgs, pipedContent, state.Config,
                        state.LineEditor!.History, aiPipeCts.Token).GetAwaiter().GetResult();
                    lastSegmentFailed = !aiPipeOk;
                }
                finally
                {
                    Console.CancelKeyPress -= aiPipeCancel;
                }
                continue;
            }
        }

        if (!needsPowerShell)
        {
            var sw2 = Stopwatch.StartNew();
            var nativeExitCode = RunInteractive(commandToRun, state.Translator, state.BuiltinCts.Token, stderrRedirect);
            lastSegmentFailed = nativeExitCode != 0;
            lastExitCode = nativeExitCode;
            sw2.Stop();
            if (state.Config.ShowTiming && sw2.Elapsed.TotalSeconds >= 0.5)
            {
                Console.ForegroundColor = Theme.Current.Muted;
                Console.WriteLine($"  took {FormatDuration(sw2.Elapsed)}");
                Console.ResetColor();
            }
            continue;
        }

        var sw = Stopwatch.StartNew();

        // ── Stdin Redirection ── pipe file content into command
        if (stdinRedirect != null)
        {
            try
            {
                var stdinContent = File.ReadAllText(stdinRedirect.FilePath);
                commandToRun = $"@'\n{stdinContent}\n'@ | {commandToRun}";
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = Theme.Current.Error;
                Console.Error.WriteLine($"redirect: {ex.Message}");
                Console.ResetColor();
                lastSegmentFailed = true;
                lastExitCode = 1;
                sw.Stop();
                continue;
            }
        }

        // ── Quoted executable path ── prepend & for PowerShell invocation
        // "C:\Program Files\app.exe" -args → & "C:\Program Files\app.exe" -args
        if (commandToRun.StartsWith('"') || commandToRun.StartsWith('\''))
        {
            var cmdTrimmed = commandToRun.TrimStart();
            char q = cmdTrimmed[0];
            var closeIdx = cmdTrimmed.IndexOf(q, 1);
            if (closeIdx > 0)
            {
                var path = cmdTrimmed[1..closeIdx];
                // Only prepend & if it looks like a file path (contains / or \)
                if (path.Contains('/') || path.Contains('\\'))
                    commandToRun = "& " + commandToRun;
            }
        }

        // ── Stderr Merge ── inject PS redirect so errors flow into output
        if (redirect?.MergeStderr == true)
            commandToRun += " 2>&1";

        try
        {
            using var ps = PowerShell.Create();
            ps.Runspace = state.Runspace;
            ps.AddScript(commandToRun);

            // Track for Ctrl+C interruption
            state.RunningPs = ps;
            List<PSObject> results;
            try
            {
                results = ps.Invoke().ToList();
            }
            catch (PipelineStoppedException)
            {
                // Ctrl+C — cancel the running pipeline
                Console.WriteLine();
                sw.Stop();
                state.RunningPs = null;
                lastSegmentFailed = true;
                lastExitCode = 130;
                continue;
            }
            finally
            {
                state.RunningPs = null;
            }

            sw.Stop();

            if (ps.HadErrors)
            {
                OutputRenderer.RenderErrors(ps.Streams);
                lastSegmentFailed = true;
                // Try to get the actual exit code from $LASTEXITCODE (set by native commands)
                try
                {
                    var lec = state.Runspace.SessionStateProxy.GetVariable("LASTEXITCODE");
                    lastExitCode = lec is int code ? code : 1;
                }
                catch { lastExitCode = 1; }

                // Smart error correction: suggest similar commands
                foreach (var error in ps.Streams.Error)
                {
                    var exType = error.Exception?.GetType().Name ?? "";
                    var errId = error.FullyQualifiedErrorId ?? "";
                    if (exType.Contains("CommandNotFoundException") || errId.Contains("CommandNotFoundException"))
                    {
                        var target = error.TargetObject?.ToString();
                        if (target != null) ShowSuggestions(target, state.Translator);
                    }
                }
            }
            else
            {
                lastSegmentFailed = false;
                lastExitCode = 0;

                // set -o pipefail: treat native command pipeline failures as errors
                // even when PowerShell doesn't report HadErrors
                if (state.SetPipefail)
                {
                    try
                    {
                        var lec = state.Runspace.SessionStateProxy.GetVariable("LASTEXITCODE");
                        if (lec is int pipeCode && pipeCode != 0)
                        {
                            lastSegmentFailed = true;
                            lastExitCode = pipeCode;
                        }
                    }
                    catch { /* ignore */ }
                }
            }

            if (results.Count > 0)
            {
                if (redirect != null)
                    WriteRedirectedOutput(results, redirect);
                else
                    OutputRenderer.Render(results.ToArray());
            }

            // Check if PowerShell called exit
            if (state.Host?.ShouldExit == true)
            {
                shouldExit = true;
                break;
            }
        }
        catch (PipelineStoppedException)
        {
            // Ctrl+C during pipeline (redundant catch for safety)
            sw.Stop();
            Console.WriteLine();
            lastSegmentFailed = true;
            lastExitCode = 130;
        }
        catch (Exception ex)
        {
            sw.Stop();
            lastSegmentFailed = true;
            lastExitCode = 1;
            var msg = ex.InnerException?.Message ?? ex.Message;
            Console.ForegroundColor = Theme.Current.Error;
            Console.Error.WriteLine($"error: {msg}");
            Console.ResetColor();
        }

        // Show timing for slow commands (>500ms)
        if (state.Config.ShowTiming && sw.ElapsedMilliseconds > 500)
        {
            Console.ForegroundColor = Theme.Current.Muted;
            var elapsed = sw.Elapsed;
            if (elapsed.TotalMinutes >= 1)
                Console.WriteLine($"  took {elapsed.Minutes}m {elapsed.Seconds}s");
            else
                Console.WriteLine($"  took {elapsed.TotalSeconds:F1}s");
            Console.ResetColor();
        }
    }

    // Clean up process substitution temp files
    if (procSubTempFiles != null)
        foreach (var f in procSubTempFiles)
            try { File.Delete(f); } catch { }

    // Normalize exit code: ensure consistency with failure flag
    if (lastSegmentFailed && lastExitCode == 0) lastExitCode = 1;
    if (!lastSegmentFailed) lastExitCode = 0;

    // set -e: exit on error
    if (state.SetE && lastSegmentFailed)
    {
        Console.ForegroundColor = Theme.Current.Error;
        Console.Error.WriteLine($"  exit (set -e): command failed with status {lastExitCode}");
        Console.ResetColor();
        shouldExit = true;
    }

    // Sync .NET CWD from PowerShell after every command.
    try
    {
        var psDir = GetRunspaceDir(state.Runspace);
        if (psDir != null && psDir != Environment.CurrentDirectory)
            Environment.CurrentDirectory = psDir;
    }
    catch { /* best-effort */ }

    return (lastSegmentFailed, lastExitCode, shouldExit);
}

// ═══════════════════════════════════════════════════════════════════
// Helper Methods
// ═══════════════════════════════════════════════════════════════════

// ── Startup Scripts ─────────────────────────────────────────────────

/// <summary>
/// Execute a .rush startup script through the scripting engine.
/// When a ShellState is provided, builtins (export, alias, set, cd, setbg, etc.)
/// work by routing non-Rush-syntax lines through ProcessCommand.
/// Without ShellState, falls back to transpile-the-whole-file mode.
/// </summary>
static void RunStartupRushFile(Runspace runspace, ScriptEngine engine, string filename, ShellState? state = null)
{
    var path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "rush", filename);

    if (!File.Exists(path)) return;

    try
    {
        if (state != null)
        {
            // New path: line-by-line processing with full builtin support
            // Mark as startup script so path add silently skips non-existent dirs
            var wasStartup = state.IsStartupScript;
            state.IsStartupScript = true;
            var lines = File.ReadAllLines(path);
            var accumulated = new System.Text.StringBuilder();
            System.Text.StringBuilder? pathBlock = null; // accumulates path add...end / path rm...end

            // Platform block state: when inside a win64/macos/linux/isssh block in init.rush,
            // we process the body lines through ProcessCommand (not the transpiler) so that
            // builtins like path, export, alias, cd work correctly.
            bool inPlatformBlock = false;
            bool platformActive = false; // true if the platform matches this OS
            int platformDepth = 0;

            foreach (var rawLine in lines)
            {
                var trimmed = rawLine.TrimStart();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
                {
                    // Comments inside path blocks are kept (filtered later by HandlePathCommand)
                    if (pathBlock != null && trimmed.StartsWith('#'))
                        pathBlock.AppendLine().Append(trimmed);
                    continue;
                }

                // ── Platform block handling (win64/macos/linux/isssh...end) ──
                if (inPlatformBlock)
                {
                    if (trimmed.Equals("end", StringComparison.OrdinalIgnoreCase))
                    {
                        platformDepth--;
                        if (platformDepth <= 0)
                        {
                            inPlatformBlock = false;
                            platformActive = false;
                            continue;
                        }
                    }
                    // Track nested blocks inside platform blocks
                    if (engine.IsRushSyntax(trimmed) && engine.IsIncomplete(trimmed))
                        platformDepth++;

                    if (platformActive)
                    {
                        // Execute the line through ProcessCommand so builtins work
                        if (IsPathBlock(trimmed))
                        {
                            pathBlock = new System.Text.StringBuilder(trimmed);
                        }
                        else if (pathBlock != null)
                        {
                            pathBlock.AppendLine().Append(trimmed);
                            if (trimmed.Equals("end", StringComparison.OrdinalIgnoreCase))
                            {
                                var fullCmd = pathBlock.ToString();
                                pathBlock = null;
                                var pathArgs = fullCmd.Length > 5 ? fullCmd[5..] : "";
                                HandlePathCommand(pathArgs, state.Runspace, quiet: true);
                            }
                        }
                        else
                        {
                            ProcessCommand(trimmed, state);
                        }
                    }
                    continue;
                }

                // Detect platform block start
                var platformKeyword = trimmed.ToLowerInvariant();
                if (platformKeyword is "win64" or "macos" or "linux" or "isssh" or "win32")
                {
                    inPlatformBlock = true;
                    platformDepth = 1;
                    var currentOs = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "macos" :
                        RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "linux" : "windows";
                    platformActive = platformKeyword switch
                    {
                        "win64" => currentOs == "windows",
                        "win32" => currentOs == "windows",
                        "macos" => currentOs == "macos",
                        "linux" => currentOs == "linux",
                        "isssh" => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SSH_CLIENT"))
                            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SSH_TTY")),
                        _ => false
                    };
                    continue;
                }

                // ── Path block accumulation ──────────────────────────
                if (pathBlock != null)
                {
                    pathBlock.AppendLine().Append(trimmed);
                    if (trimmed.Equals("end", StringComparison.OrdinalIgnoreCase))
                    {
                        var fullCmd = pathBlock.ToString();
                        pathBlock = null;
                        var pathArgs = fullCmd.Length > 5 ? fullCmd[5..] : "";
                        HandlePathCommand(pathArgs, state.Runspace, quiet: true);
                    }
                    continue;
                }

                // Accumulate for multi-line Rush blocks
                if (accumulated.Length > 0)
                    accumulated.AppendLine().Append(rawLine);
                else
                    accumulated.Append(rawLine);

                var current = accumulated.ToString();

                // Rush syntax? Check if complete
                if (engine.IsRushSyntax(current))
                {
                    if (engine.IsIncomplete(current)) continue;
                    // Complete Rush block — transpile and execute
                    var psCode = engine.TranspileLine(current);
                    if (psCode != null)
                        ExecuteTranspiledBlock(psCode, state);
                    accumulated.Clear();
                    continue;
                }

                // Not Rush syntax — check for path block start
                accumulated.Clear();

                if (IsPathBlock(trimmed))
                {
                    pathBlock = new System.Text.StringBuilder(trimmed);
                    continue;
                }

                var (failed, _, shouldExit) = ProcessCommand(trimmed, state);
                if (shouldExit) break;
            }

            // Handle any remaining accumulated Rush code
            if (accumulated.Length > 0)
            {
                var psCode = engine.TranspileLine(accumulated.ToString());
                if (psCode != null)
                    ExecuteTranspiledBlock(psCode, state);
            }

            state.IsStartupScript = wasStartup;
        }
        else
        {
            // Legacy path: transpile entire file at once (no builtin support)
            var source = File.ReadAllText(path);
            var psCode = engine.TranspileFile(source);
            if (psCode != null)
            {
                using var ps = PowerShell.Create();
                ps.Runspace = runspace;
                ps.AddScript(psCode);
                ps.Invoke();
                if (ps.HadErrors && ps.Streams.Error.Count > 0)
                {
                    Console.ForegroundColor = Theme.Current.Error;
                    foreach (var err in ps.Streams.Error)
                        Console.Error.WriteLine($"rush: {filename}: {err}");
                    Console.ResetColor();
                }

                // Reset ErrorActionPreference — TranspileFile sets it to 'Stop'
                using var reset = PowerShell.Create();
                reset.Runspace = runspace;
                reset.AddScript("$ErrorActionPreference = 'Continue'");
                reset.Invoke();
            }
        }
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = Theme.Current.Error;
        Console.Error.WriteLine($"rush: {filename}: {ex.Message}");
        Console.ResetColor();
    }
}

/// <summary>
/// Run startup scripts: init.rush then secrets.rush (if it exists).
/// Both are fully transpiled through the Rush engine.
/// secrets.rush is never synced — safe for API keys and tokens.
/// </summary>
static void RunStartupScripts(Runspace runspace, ScriptEngine engine, ShellState? state = null)
{
    RunStartupRushFile(runspace, engine, "init.rush", state);
    RunStartupRushFile(runspace, engine, "secrets.rush", state);
}

/// <summary>
/// Inject Rush built-in variables ($os, $hostname, $rush_version, $is_login_shell)
/// into a PowerShell runspace. Called from both interactive and LLM mode paths.
/// </summary>
static void InjectRushEnvVars(Runspace runspace, string version, bool isLoginShell)
{
    using var ps = PowerShell.Create();
    ps.Runspace = runspace;
    var osName = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "macos" :
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "linux" : "windows";
    var loginVal = isLoginShell ? "$true" : "$false";
    var archName = RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        Architecture.X86 => "x86",
        _ => RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()
    };
    var osVersion = Environment.OSVersion.Version.ToString();
    ps.AddScript($"$os = '{osName}'; $hostname = '{Environment.MachineName.ToLowerInvariant()}'; $rush_version = '{version}'; $is_login_shell = {loginVal}; $__rush_arch = '{archName}'; $__rush_os_version = '{osVersion}'");
    ps.Invoke();

    // Create Rush-normalized $PATH variable (Unix-style: forward slashes, escaped spaces, colon-separated).
    // This is separate from $env:PATH which stays native for child processes.
    // On Unix, $PATH == $env:PATH (already in the right format).
    var rushPath = PathUtils.ImportPath(Environment.GetEnvironmentVariable("PATH") ?? "");
    using var pathPs = PowerShell.Create();
    pathPs.Runspace = runspace;
    pathPs.AddScript($"$PATH = '{rushPath.Replace("'", "''")}'");
    pathPs.Invoke();

    // Set COLUMNS/LINES env vars so native commands (ls, etc.) know the terminal size.
    // Process.Start children don't inherit console dimensions on Windows.
    try
    {
        var cols = Console.WindowWidth.ToString();
        var lines = Console.WindowHeight.ToString();
        Environment.SetEnvironmentVariable("COLUMNS", cols);
        Environment.SetEnvironmentVariable("LINES", lines);
    }
    catch { /* non-interactive — skip */ }

    // Windows-specific runspace setup
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
        using var winPs = PowerShell.Create();
        winPs.Runspace = runspace;
        // Set execution policy to RemoteSigned for this process only.
        // Rush's embedded PS inherits the system policy which is often Restricted
        // on Windows Server, blocking all scripts including ps...end blocks.
        // Process scope doesn't change system/user settings — resets when Rush exits.
        winPs.AddScript("Set-ExecutionPolicy RemoteSigned -Scope Process -Force");
        // Import CimCmdlets — required for Test-NetConnection, Get-CimInstance,
        // Get-NetAdapter, Get-NetFirewallProfile and other Windows admin cmdlets.
        winPs.AddScript("Import-Module CimCmdlets -ErrorAction SilentlyContinue");
        winPs.Invoke();
    }

    // Inject __rush_win32 and __rush_ps5 helper functions
    // Both use JSON variable bridging to pass Rush variables into the child PS process.
    // __rush_win32: targets 32-bit PS 5.1 (SysWOW64) for win32 platform blocks
    // __rush_ps5:   targets 64-bit PS 5.1 (System32) for ps5 blocks
    using var ps2 = PowerShell.Create();
    ps2.Runspace = runspace;
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
        ps2.AddScript(@"
function __rush_bridge_vars {
    # Serialize current Rush variables to a JSON temp file for child PS processes.
    # Returns the temp file path. Handles strings, numbers, bools, arrays, hashtables.
    $exclude = @('PSCommandPath','PSScriptRoot','MyInvocation','_','args','input',
                 'null','true','false','PSBoundParameters','PSDefaultParameterValues',
                 'ErrorActionPreference','WarningPreference','InformationPreference',
                 'DebugPreference','VerbosePreference','ConfirmPreference','WhatIfPreference',
                 'EncodedBody','body','preamble','Host','HOME','PID','PWD','ShellId',
                 'ExecutionContext','Error','NestedPromptLevel','LASTEXITCODE','PROFILE',
                 'PSCulture','PSUICulture','PSVersionTable','StackTrace','switch','foreach',
                 'Matches','ConsoleFileName','MaximumHistoryCount','OutputEncoding',
                 'ProgressPreference','PSSessionApplicationName','PSSessionConfigurationName',
                 'PSSessionOption','PSEmailServer','PSModuleAutoLoadingPreference')

    $vars = @{}
    Get-Variable -Scope 1 -ErrorAction SilentlyContinue | Where-Object {
        $_.Name -notin $exclude -and -not $_.Name.StartsWith('__rush_')
    } | ForEach-Object {
        $val = $_.Value
        if ($null -eq $val) { return }
        $t = $val.GetType()
        if ($t -eq [string] -or $t -eq [int] -or $t -eq [long] -or
            $t -eq [double] -or $t -eq [bool] -or $t -eq [decimal]) {
            $vars[$_.Name] = $val
        }
        elseif ($val -is [array]) {
            # Only serialize arrays of simple types
            $simple = $true
            foreach ($item in $val) {
                if ($null -ne $item -and $item.GetType() -notin @([string],[int],[long],[double],[bool])) {
                    $simple = $false; break
                }
            }
            if ($simple) { $vars[$_.Name] = $val }
        }
        elseif ($val -is [hashtable]) {
            $vars[$_.Name] = $val
        }
    }

    if ($vars.Count -eq 0) { return $null }

    $jsonPath = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), ""rush_vars_$(New-Guid).json"")
    $vars | ConvertTo-Json -Depth 4 -Compress | Set-Content -Path $jsonPath -Encoding UTF8
    return $jsonPath
}

function __rush_var_preamble {
    param([string]$JsonPath)
    # Generate a PS preamble that reads the JSON vars file and creates local variables.
    # Compatible with both PS 5.1 and PS 7.
    if (-not $JsonPath) { return '' }
    # The preamble is PS code that runs in the child process
    return @""
`$__rv = Get-Content '$JsonPath' -Raw -ErrorAction SilentlyContinue | ConvertFrom-Json -ErrorAction SilentlyContinue
if (`$__rv) { `$__rv.PSObject.Properties | ForEach-Object { Set-Variable -Name `$_.Name -Value `$_.Value } }
Remove-Item '$JsonPath' -Force -ErrorAction SilentlyContinue
Remove-Variable __rv -ErrorAction SilentlyContinue

""@
}

function __rush_win32 {
    param([string]$EncodedBody)
    $body = [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($EncodedBody))

    $jsonPath = __rush_bridge_vars
    $preamble = __rush_var_preamble $jsonPath
    $fullScript = $preamble + $body

    $ps32 = 'C:\Windows\SysWOW64\WindowsPowerShell\v1.0\powershell.exe'
    if (-not (Test-Path $ps32)) {
        Write-Error 'win32: 32-bit PowerShell not found at SysWOW64 path'
        if ($jsonPath) { Remove-Item $jsonPath -Force -ErrorAction SilentlyContinue }
        return
    }

    & $ps32 -NoProfile -NonInteractive -Command $fullScript 2>&1
}

function __rush_ps5 {
    param([string]$EncodedBody)
    $body = [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($EncodedBody))

    $jsonPath = __rush_bridge_vars
    $preamble = __rush_var_preamble $jsonPath
    $fullScript = $preamble + $body

    $ps51 = 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe'
    if (-not (Test-Path $ps51)) {
        Write-Error 'ps5: PowerShell 5.1 not found'
        if ($jsonPath) { Remove-Item $jsonPath -Force -ErrorAction SilentlyContinue }
        return
    }

    & $ps51 -NoProfile -NonInteractive -Command $fullScript 2>&1
}
");
    }
    else
    {
        // Non-Windows: no-op (win32/ps5 blocks are gated by $os check, but define
        // the functions anyway so TranspileFile output doesn't error)
        ps2.AddScript(@"
function __rush_win32 {
    param([string]$EncodedBody)
    # No-op on non-Windows platforms
}
function __rush_ps5 {
    param([string]$EncodedBody)
    # No-op on non-Windows platforms
}
");
    }
    ps2.Invoke();

    // Inject __rush_puts — semantic output formatting for puts
    // Detects prefix characters (# heading, ! warn, !! error, > success, ~ info)
    // and applies theme-aware colors. Strips prefix when piped or NO_COLOR is set.
    using var ps3 = PowerShell.Create();
    ps3.Runspace = runspace;
    ps3.AddScript(@"
function __rush_puts {
    param([string]$Text)
    if ([string]::IsNullOrEmpty($Text)) { Write-Output ''; return }

    # Check for escaped prefixes — emit literal without prefix
    if ($Text.StartsWith('\# ') -or $Text.StartsWith('\## ') -or
        $Text.StartsWith('\> ') -or $Text.StartsWith('\! ') -or
        $Text.StartsWith('\!! ') -or $Text.StartsWith('\~ ')) {
        Write-Output $Text.Substring(1)
        return
    }

    # Detect semantic prefix
    $level = $null; $body = $Text
    if ($Text.StartsWith('## '))     { $level = 'h2';      $body = $Text.Substring(3) }
    elseif ($Text.StartsWith('# '))  { $level = 'h1';      $body = $Text.Substring(2) }
    elseif ($Text.StartsWith('!! ')) { $level = 'error';   $body = $Text.Substring(3) }
    elseif ($Text.StartsWith('! '))  { $level = 'warn';    $body = $Text.Substring(2) }
    elseif ($Text.StartsWith('> '))  { $level = 'success'; $body = $Text.Substring(2) }
    elseif ($Text.StartsWith('~ '))  { $level = 'info';    $body = $Text.Substring(2) }

    if ($null -eq $level) { Write-Output $Text; return }

    # If piped/redirected or NO_COLOR set, strip prefix and emit plain text
    if ($env:NO_COLOR -or -not [Console]::IsOutputRedirected -eq $false) {
        # IsOutputRedirected is true when piped — emit plain
    }
    if ([Console]::IsOutputRedirected -or $env:NO_COLOR) {
        Write-Output $body
        return
    }

    # Apply colors
    switch ($level) {
        'h1'      { Write-Host $body -ForegroundColor White -BackgroundColor DarkBlue }
        'h2'      { Write-Host $body -ForegroundColor Cyan }
        'error'   { Write-Host $body -ForegroundColor Red }
        'warn'    { Write-Host $body -ForegroundColor Yellow }
        'success' { Write-Host $body -ForegroundColor Green }
        'info'    { Write-Host $body -ForegroundColor DarkGray }
    }
}
");
    ps3.Invoke();
}

// ── Windows: uutils coreutils shimming ──────────────────────────────

/// <summary>
/// On Windows, detect uutils coreutils.exe and shim its commands.
/// Runs "coreutils --list" to discover available commands, then creates
/// PowerShell functions that route transparently: ls → coreutils.exe ls $args
/// </summary>
static void ShimCoreutilsIfNeeded(Runspace runspace, bool quiet = false)
{
    try
    {
        // Try to get the command list from coreutils
        var psi = new ProcessStartInfo("coreutils.exe", "--list")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi);
        if (proc == null) return;
        var output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(3000);
        if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(output)) return;

        var commands = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(c => c.Trim())
            .Where(c => c.Length > 0 && !c.Contains(' '))
            .ToList();
        if (commands.Count == 0) return;

        using var ps = PowerShell.Create();
        ps.Runspace = runspace;

        var sb = new System.Text.StringBuilder();
        foreach (var cmd in commands)
        {
            sb.AppendLine($"if (Test-Path \"Alias:{cmd}\") {{ Remove-Item \"Alias:{cmd}\" -Force }}");
            sb.AppendLine($"Set-Item -Path \"Function:\\{cmd}\" -Value ([scriptblock]::Create(\"coreutils.exe {cmd} `$args\")).GetNewClosure()");
        }

        ps.AddScript(sb.ToString());
        ps.Invoke();

        if (!quiet)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"  Using uutils coreutils ({commands.Count} commands shimmed).");
            Console.ResetColor();
        }
    }
    catch
    {
        // coreutils.exe not found or failed — nothing to shim
    }
}

/// <summary>
/// On Windows, detect uutils diffutils (diff, cmp) and shim as PS functions.
/// Same pattern as ShimCoreutilsIfNeeded but for diffutils.exe multi-call binary.
/// </summary>
static void ShimDiffutilsIfNeeded(Runspace runspace, bool quiet = false)
{
    try
    {
        var psi = new ProcessStartInfo("diffutils.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi);
        if (proc == null) return;

        // diffutils prints usage to stderr when called with no args
        var stderr = proc.StandardError.ReadToEnd();
        var stdout = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(3000);

        // Parse the command list from the help output
        // Format: "Currently defined functions:\n\n    cmp, diff\n"
        var output = stdout + stderr;
        var commands = new List<string>();
        var funcLine = output.Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Contains(',') && !l.Contains("Usage") && !l.Contains("Expected"));

        if (funcLine != null)
        {
            commands = funcLine.Split(',')
                .Select(c => c.Trim())
                .Where(c => c.Length > 0 && !c.Contains(' '))
                .ToList();
        }

        if (commands.Count == 0) return;

        using var ps = PowerShell.Create();
        ps.Runspace = runspace;

        var sb = new System.Text.StringBuilder();
        foreach (var cmd in commands)
        {
            sb.AppendLine($"if (Test-Path \"Alias:{cmd}\") {{ Remove-Item \"Alias:{cmd}\" -Force }}");
            sb.AppendLine($"Set-Item -Path \"Function:\\{cmd}\" -Value ([scriptblock]::Create(\"diffutils.exe {cmd} `$args\")).GetNewClosure()");
        }

        ps.AddScript(sb.ToString());
        ps.Invoke();

        if (!quiet)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"  Using uutils diffutils ({string.Join(", ", commands)}).");
            Console.ResetColor();
        }
    }
    catch
    {
        // diffutils.exe not found — nothing to shim
    }
}

/// <summary>
/// On Windows, detect available coreutils and either add Git for Windows
/// to PATH or show a one-time tip. Called after ShimCoreutilsIfNeeded.
/// </summary>
static void DetectWindowsCoreutils(Runspace runspace, RushConfig config)
{
    try
    {
        // Check if coreutils shim already set up a working 'ls' function
        using var ps = PowerShell.Create();
        ps.Runspace = runspace;
        ps.AddScript("Get-Command ls -CommandType Function -ErrorAction SilentlyContinue");
        var result = ps.Invoke();
        if (result.Count > 0)
            return; // coreutils.exe shimmed — all good

        // Check if individual uutils binaries are in PATH (zip install)
        ps.Commands.Clear();
        ps.AddScript("Get-Command ls.exe -ErrorAction SilentlyContinue");
        var lsExe = ps.Invoke();
        if (lsExe.Count > 0)
            return; // Individual binaries installed — no shim or tip needed

        // Check for Git for Windows
        var gitUsrBin = @"C:\Program Files\Git\usr\bin";
        if (Directory.Exists(gitUsrBin) && File.Exists(Path.Combine(gitUsrBin, "ls.exe")))
        {
            using var ps2 = PowerShell.Create();
            ps2.Runspace = runspace;
            ps2.AddScript($"$env:PATH = \"{gitUsrBin};$env:PATH\"");
            ps2.Invoke();
            // Also update .NET's PATH so Process.Start finds the tools
            var currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            Environment.SetEnvironmentVariable("PATH", $"{gitUsrBin};{currentPath}");

            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("  Using Git for Windows coreutils.");
            Console.ResetColor();
            return;
        }

        // Nothing found — show one-time tip
        if (!config.CoreutilsTipShown)
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write("  Tip: ");
            Console.ResetColor();
            Console.WriteLine("Rush works best with Unix-compatible tools.");
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("        winget install uutils.coreutils   # ls, grep, find, etc.");
            Console.WriteLine("        winget install uutils.diffutils   # diff, cmp");
            Console.WriteLine("        winget install Neovim.Neovim      # vi/vim editor");
            Console.ResetColor();
            config.CoreutilsTipShown = true;
            config.Save();
        }
    }
    catch
    {
        // Best-effort — don't fail startup over detection
    }
}

// ── Script File Execution ──────────────────────────────────────────

/// <summary>
/// Execute a .rush script file non-interactively.
/// Used for: rush script.rush
/// </summary>
static void RunScriptFile(string path, string[] scriptArgs)
{
    try
    {
        // Initialize theme for output rendering
        var scriptCfg = RushConfig.Load();
        ApplyTheme(scriptCfg, Environment.CurrentDirectory, emitOsc: false);

        var source = File.ReadAllText(path);
        var iss = InitialSessionState.CreateDefault();
        var hostUI = new RushHostUI();
        var host = new RushHost(hostUI);
        var runspace = RunspaceFactory.CreateRunspace(host, iss);
        runspace.Open();

        // Inject Rush environment variables and helper functions
        InjectRushEnvVars(runspace, RushVersion.Full, false);

        // Inject script-specific variables: ARGV, __FILE__, __DIR__
        {
            using var scriptPs = PowerShell.Create();
            scriptPs.Runspace = runspace;
            var fullPath = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(fullPath) ?? ".";
            var argvItems = string.Join(", ", scriptArgs.Select(a => $"'{a.Replace("'", "''")}'"));
            scriptPs.AddScript($"$ARGV = @({argvItems}); $__FILE__ = '{fullPath.Replace("'", "''")}'; $__DIR__ = '{directory.Replace("'", "''")}'");
            scriptPs.Invoke();
        }

        var objConfig = ObjectifyConfig.Load();
        var translator = new CommandTranslator(objConfig);
        var engine = new ScriptEngine(translator);
        var psCode = engine.TranspileFile(source);

        if (psCode != null)
        {
            using var ps = PowerShell.Create();
            ps.Runspace = runspace;
            ps.AddScript(psCode);
            var results = ps.Invoke().Where(r => r != null).ToList();
            foreach (var result in results) Console.WriteLine(result);
            if (ps.HadErrors)
            {
                OutputRenderer.RenderErrors(ps.Streams);
                Environment.ExitCode = 1;
            }
        }

        runspace.Close();
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine($"rush: {ex.Message}");
        Console.ResetColor();
        Environment.ExitCode = 1;
    }
}

// ── Interactive TUI Commands ─────────────────────────────────────────

/// <summary>
/// Run a command directly with inherited stdio (no capture).
/// Used for all native commands — gives them real TTY access for
/// interactive programs, shells, TUIs, and regular CLI tools alike.
/// </summary>
static int RunInteractive(string command, CommandTranslator? translator = null,
    CancellationToken cancelToken = default, StderrInfo? stderrRedirect = null)
{
    try
    {
        var cmdParts = CommandTranslator.SplitCommandLine(command);
        var exe = cmdParts[0];

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
        };

        // Add args individually, stripping surrounding quotes that were
        // preserved for glob-expansion protection but shouldn't be passed
        // literally to the process (ProcessStartInfo doesn't use a shell).
        for (int i = 1; i < cmdParts.Length; i++)
        {
            var arg = cmdParts[i];
            if ((arg.StartsWith('\'') && arg.EndsWith('\'') && arg.Length >= 2) ||
                (arg.StartsWith('"') && arg.EndsWith('"') && arg.Length >= 2))
                arg = arg[1..^1];
            psi.ArgumentList.Add(arg);
        }

        // Handle 2>/dev/null and 2>file stderr redirection for native commands
        FileStream? stderrFile = null;
        if (stderrRedirect != null)
        {
            psi.RedirectStandardError = true;
            if (stderrRedirect.FilePath != "/dev/null")
            {
                stderrFile = new FileStream(stderrRedirect.FilePath,
                    stderrRedirect.Append ? FileMode.Append : FileMode.Create,
                    FileAccess.Write);
            }
        }

        var proc = Process.Start(psi);
        if (proc == null)
        {
            stderrFile?.Dispose();
            return 1;
        }

        // Drain stderr in background to prevent buffer deadlock
        Task? stderrTask = null;
        if (stderrRedirect != null)
        {
            var errFile = stderrFile; // capture for closure
            stderrTask = Task.Run(async () =>
            {
                try
                {
                    if (errFile != null)
                    {
                        using var writer = new StreamWriter(errFile, leaveOpen: false);
                        string? line;
                        while ((line = await proc.StandardError.ReadLineAsync()) != null)
                            await writer.WriteLineAsync(line);
                    }
                    else
                    {
                        // /dev/null — just drain
                        while (await proc.StandardError.ReadLineAsync() != null) { }
                    }
                }
                catch { }
            });
        }

        // Poll with cancellation so Ctrl+C can kill hung processes
        if (cancelToken != default)
        {
            while (!proc.WaitForExit(200))
            {
                if (cancelToken.IsCancellationRequested)
                {
                    try { proc.Kill(); } catch { }
                    stderrTask?.Wait(500);
                    proc.Dispose();
                    return 130; // Standard Ctrl+C exit code
                }
            }
        }
        else
        {
            proc.WaitForExit();
        }
        stderrTask?.Wait(1000);
        var exitCode = proc.ExitCode;
        proc.Dispose();
        return exitCode;
    }
    catch (System.ComponentModel.Win32Exception)
    {
        // Command not found — show error and suggest similar commands
        var firstSpace = command.IndexOf(' ');
        var exe = firstSpace > 0 ? command[..firstSpace] : command;
        Console.ForegroundColor = Theme.Current.Error;
        Console.Error.WriteLine($"  command not found: {exe}");
        Console.ResetColor();
        if (translator != null)
            ShowSuggestions(exe, translator);
        return 127;
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = Theme.Current.Error;
        Console.Error.WriteLine($"  {ex.Message}");
        Console.ResetColor();
        return 1;
    }
}

// ── Edit in $EDITOR ─────────────────────────────────────────────────

/// <summary>
/// Open content in $EDITOR (or vi), return edited result. Returns null on error or cancel.
/// </summary>
static string? OpenInEditor(string content)
{
    var editor = Environment.GetEnvironmentVariable("EDITOR") ?? "vi";
    string? tempFile = null;
    try
    {
        tempFile = Path.Combine(Path.GetTempPath(), $"rush-edit-{Guid.NewGuid():N}.rush");
        var originalContent = content + "\n";
        File.WriteAllText(tempFile, originalContent);

        var psi = new ProcessStartInfo(editor, tempFile) { UseShellExecute = false };
        var proc = Process.Start(psi);
        if (proc == null)
        {
            Console.ForegroundColor = Theme.Current.Error;
            Console.Error.WriteLine($"  could not start {editor}");
            Console.ResetColor();
            return null;
        }
        proc.WaitForExit();

        if (proc.ExitCode != 0)
        {
            Console.ForegroundColor = Theme.Current.Error;
            Console.Error.WriteLine("command cancelled");
            Console.ResetColor();
            return null;
        }
        proc.Dispose();

        var result = File.ReadAllText(tempFile);
        // If file unchanged (e.g. :q! in vim), treat as cancelled
        if (result == originalContent)
        {
            Console.ForegroundColor = Theme.Current.Error;
            Console.Error.WriteLine("command cancelled");
            Console.ResetColor();
            return null;
        }

        result = result.TrimEnd('\n', '\r');
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = Theme.Current.Error;
        Console.Error.WriteLine($"  editor: {ex.Message}");
        Console.ResetColor();
        return null;
    }
    finally
    {
        if (tempFile != null) try { File.Delete(tempFile); } catch { }
    }
}

/// <summary>
/// Auto-indent a Rush block for display in an editor.
/// Walks lines progressively, computing block depth to determine indent.
/// </summary>
static string IndentRushBlock(string code, ScriptEngine engine)
{
    var lines = code.Split('\n');
    var sb = new System.Text.StringBuilder();
    var accumulated = "";
    for (int i = 0; i < lines.Length; i++)
    {
        var line = lines[i].Trim();
        if (i == 0)
        {
            sb.AppendLine(line);
            accumulated = line;
            continue;
        }
        var depth = engine.GetBlockDepth(accumulated);
        if (depth < 0) depth = 1;
        // `end` gets outdented to match its block opener
        if (line.Equals("end", StringComparison.OrdinalIgnoreCase))
            depth = Math.Max(0, depth - 1);
        sb.AppendLine(new string(' ', depth * 2) + line);
        accumulated += "\n" + line;
    }
    return sb.ToString().TrimEnd('\n', '\r');
}

/// <summary>
/// Strip leading whitespace from each line (for execution after editor edit).
/// </summary>
static string StripLeadingWhitespace(string code)
{
    var lines = code.Split('\n');
    return string.Join("\n", lines.Select(l => l.TrimStart()));
}

// ── PATH Management ─────────────────────────────────────────────────

/// <summary>
/// Sync an environment variable to both .NET Environment and PowerShell runspace.
/// </summary>
static void SetEnvVar(string varName, string newValue, Runspace runspace)
{
    Environment.SetEnvironmentVariable(varName, newValue);
    using var ps = PowerShell.Create();
    ps.Runspace = runspace;
    var escaped = newValue.Replace("'", "''");
    ps.AddScript($"$env:{varName} = '{escaped}'");
    ps.Invoke();

    // Keep Rush $PATH variable in sync with native $env:PATH
    if (varName.Equals("PATH", StringComparison.OrdinalIgnoreCase))
    {
        var rushNorm = PathUtils.ImportPath(newValue);
        using var syncPs = PowerShell.Create();
        syncPs.Runspace = runspace;
        syncPs.AddScript($"$PATH = '{rushNorm.Replace("'", "''")}'");
        syncPs.Invoke();
    }
}

/// <summary>
/// Expand ~ to home directory in a path string.
/// </summary>
static string ExpandTildePath(string path)
{
    if (path == "~")
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    if (path.StartsWith("~/"))
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);
    return path;
}

/// <summary>
/// Save a "path add" line into init.rush's PATH section.
/// Inserts after the "# ── PATH" header, or creates the section at the top.
/// </summary>
static void SavePathToInit(string pathLine)
{
    try
    {
        var initPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "rush", "init.rush");

        if (!File.Exists(initPath))
        {
            // No init.rush — create with just the PATH section
            File.WriteAllText(initPath, $"# ── PATH ─────────────────────────────────────────────────\n{pathLine}\n");
            Console.ForegroundColor = Theme.Current.Muted;
            Console.WriteLine("  saved to:  ~/.config/rush/init.rush");
            Console.ResetColor();
            return;
        }

        var lines = File.ReadAllLines(initPath).ToList();

        // Find the PATH section header
        var pathSectionIdx = lines.FindIndex(l => l.TrimStart().StartsWith("# ── PATH"));

        if (pathSectionIdx >= 0)
        {
            // Find the last "path add" line in this section (or the header itself)
            var insertIdx = pathSectionIdx + 1;
            while (insertIdx < lines.Count)
            {
                var trimmed = lines[insertIdx].TrimStart();
                if (trimmed.StartsWith("path add", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("# path add", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("# export PATH", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("export PATH", StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(trimmed) && insertIdx == pathSectionIdx + 1)
                {
                    insertIdx++;
                }
                else break;
            }
            lines.Insert(insertIdx, pathLine);
        }
        else
        {
            // No PATH section — create one at the top (after any leading comments/shebang)
            var insertAt = 0;
            // Skip past initial comment block
            while (insertAt < lines.Count &&
                   (lines[insertAt].TrimStart().StartsWith('#') || string.IsNullOrWhiteSpace(lines[insertAt])))
            {
                insertAt++;
                // Stop after the first blank line following comments
                if (insertAt > 0 && string.IsNullOrWhiteSpace(lines[insertAt - 1]) &&
                    insertAt < lines.Count && !lines[insertAt].TrimStart().StartsWith('#'))
                    break;
            }
            lines.Insert(insertAt, "");
            lines.Insert(insertAt, pathLine);
            lines.Insert(insertAt, "# ── PATH ─────────────────────────────────────────────────");
        }

        File.WriteAllLines(initPath, lines);
        Console.ForegroundColor = Theme.Current.Muted;
        Console.WriteLine("  saved to:  ~/.config/rush/init.rush");
        Console.ResetColor();
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = Theme.Current.Error;
        Console.Error.WriteLine($"  save failed: {ex.Message}");
        Console.ResetColor();
    }
}

/// <summary>
/// Save the full PATH (or other var) from path edit --save.
/// Replaces the entire PATH section in init.rush with the new entries.
/// </summary>
static void SavePathEditToInit(string varName, List<string> entries)
{
    try
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var initPath = Path.Combine(home, ".config", "rush", "init.rush");
        var nameFlag = varName != "PATH" ? $"--name={varName} " : "";

        // Collapse home dir to ~ for cleaner init.rush
        var pathLines = entries.Select(e =>
        {
            var display = e.StartsWith(home) ? "~" + e[home.Length..] : e;
            return $"path add {nameFlag}{display}";
        }).ToList();

        if (!File.Exists(initPath))
        {
            File.WriteAllText(initPath,
                "# ── PATH ─────────────────────────────────────────────────\n" +
                string.Join("\n", pathLines) + "\n");
            return;
        }

        var lines = File.ReadAllLines(initPath).ToList();
        var pathSectionIdx = lines.FindIndex(l => l.TrimStart().StartsWith("# ── PATH"));

        if (pathSectionIdx >= 0)
        {
            // Remove existing path add/export PATH lines in this section
            var removeStart = pathSectionIdx + 1;
            while (removeStart < lines.Count)
            {
                var trimmed = lines[removeStart].TrimStart();
                if (trimmed.StartsWith("path add", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("path rm", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("# path add", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("export PATH", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("# export PATH", StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(trimmed))
                {
                    lines.RemoveAt(removeStart);
                }
                else break;
            }

            // Insert new entries after the header
            for (int i = pathLines.Count - 1; i >= 0; i--)
                lines.Insert(pathSectionIdx + 1, pathLines[i]);
        }
        else
        {
            // No PATH section — create at top
            var insertAt = 0;
            while (insertAt < lines.Count &&
                   (lines[insertAt].TrimStart().StartsWith('#') || string.IsNullOrWhiteSpace(lines[insertAt])))
            {
                insertAt++;
                if (insertAt > 0 && string.IsNullOrWhiteSpace(lines[insertAt - 1]) &&
                    insertAt < lines.Count && !lines[insertAt].TrimStart().StartsWith('#'))
                    break;
            }
            lines.Insert(insertAt, "");
            foreach (var pl in pathLines)
                lines.Insert(insertAt, pl);
            lines.Insert(insertAt, "# ── PATH ─────────────────────────────────────────────────");
        }

        File.WriteAllLines(initPath, lines);
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = Theme.Current.Error;
        Console.Error.WriteLine($"  save failed: {ex.Message}");
        Console.ResetColor();
    }
}

/// <summary>
/// Save an "export KEY=value" line into init.rush's Environment section.
/// Idempotent: replaces existing export for the same variable.
/// </summary>
static void SaveExportToInit(string varName, string varValue)
{
    try
    {
        var initPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "rush", "init.rush");

        // Construct the line to persist (quote value if it contains spaces)
        var exportLine = varValue.Contains(' ')
            ? $"export {varName}=\"{varValue}\""
            : $"export {varName}={varValue}";

        if (!File.Exists(initPath))
        {
            File.WriteAllText(initPath,
                "# ── Environment ──────────────────────────────────────────\n" +
                exportLine + "\n");
            Console.ForegroundColor = Theme.Current.Muted;
            Console.WriteLine("  saved to:  ~/.config/rush/init.rush");
            Console.ResetColor();
            return;
        }

        var lines = File.ReadAllLines(initPath).ToList();

        // 1. Check for existing export of same variable → replace in place
        var existingIdx = lines.FindIndex(l =>
        {
            var t = l.TrimStart();
            return t.StartsWith($"export {varName}=", StringComparison.OrdinalIgnoreCase);
        });

        if (existingIdx >= 0)
        {
            lines[existingIdx] = exportLine;
            File.WriteAllLines(initPath, lines);
            Console.ForegroundColor = Theme.Current.Muted;
            Console.WriteLine("  updated in: ~/.config/rush/init.rush");
            Console.ResetColor();
            return;
        }

        // 2. Find "# ── Environment" section → insert after section content
        var envSectionIdx = lines.FindIndex(l => l.TrimStart().StartsWith("# ── Environment"));
        if (envSectionIdx >= 0)
        {
            var insertIdx = envSectionIdx + 1;
            while (insertIdx < lines.Count)
            {
                var trimmed = lines[insertIdx].TrimStart();
                if (trimmed.StartsWith("export ", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("# export ", StringComparison.OrdinalIgnoreCase) ||
                    (string.IsNullOrWhiteSpace(trimmed) && insertIdx == envSectionIdx + 1))
                {
                    insertIdx++;
                }
                else break;
            }
            lines.Insert(insertIdx, exportLine);
        }
        else
        {
            // 3. No Environment section — create one (after PATH section or at end)
            var pathSectionIdx = lines.FindIndex(l => l.TrimStart().StartsWith("# ── PATH"));
            int insertAt;
            if (pathSectionIdx >= 0)
            {
                // Find end of PATH section content
                insertAt = pathSectionIdx + 1;
                while (insertAt < lines.Count)
                {
                    var t = lines[insertAt].TrimStart();
                    if (t.StartsWith("path add", StringComparison.OrdinalIgnoreCase) ||
                        t.StartsWith("# path add", StringComparison.OrdinalIgnoreCase) ||
                        t.StartsWith("# export PATH", StringComparison.OrdinalIgnoreCase) ||
                        t.StartsWith("export PATH", StringComparison.OrdinalIgnoreCase) ||
                        string.IsNullOrWhiteSpace(t))
                        insertAt++;
                    else break;
                }
            }
            else
            {
                insertAt = lines.Count;
            }
            lines.Insert(insertAt, exportLine);
            lines.Insert(insertAt, "# ── Environment ──────────────────────────────────────────");
            if (insertAt > 0 && !string.IsNullOrWhiteSpace(lines[insertAt - 1]))
                lines.Insert(insertAt, "");
        }

        File.WriteAllLines(initPath, lines);
        Console.ForegroundColor = Theme.Current.Muted;
        Console.WriteLine("  saved to:  ~/.config/rush/init.rush");
        Console.ResetColor();
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = Theme.Current.Error;
        Console.Error.WriteLine($"  save failed: {ex.Message}");
        Console.ResetColor();
    }
}

/// <summary>
/// Remove a "path add" line from init.rush matching the given directory.
/// Matches against both expanded and original (tilde) forms.
/// </summary>
static void RemovePathFromInit(string expandedDir, string originalDir, string varName = "PATH")
{
    try
    {
        var initPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "rush", "init.rush");

        if (!File.Exists(initPath)) return;

        var lines = File.ReadAllLines(initPath).ToList();
        var removed = lines.RemoveAll(l =>
        {
            var trimmed = l.TrimStart();
            if (!trimmed.StartsWith("path add ", StringComparison.OrdinalIgnoreCase)) return false;
            // Extract the dir from the line (strip flags)
            var lineArgs = trimmed[9..].Trim();

            // Extract --name= from line to match the right variable
            string lineVarName = "PATH";
            var lineNameMatch = System.Text.RegularExpressions.Regex.Match(lineArgs, @"--name=(\S+)");
            if (lineNameMatch.Success)
            {
                lineVarName = lineNameMatch.Groups[1].Value;
                lineArgs = lineArgs.Replace(lineNameMatch.Value, "").Trim();
            }
            if (!lineVarName.Equals(varName, StringComparison.OrdinalIgnoreCase)) return false;

            if (lineArgs.StartsWith("--front ", StringComparison.OrdinalIgnoreCase))
                lineArgs = lineArgs[8..].Trim();
            if (lineArgs.StartsWith("--save ", StringComparison.OrdinalIgnoreCase))
                lineArgs = lineArgs[7..].Trim();
            lineArgs = lineArgs.Trim().TrimEnd('/');
            // Match against expanded or original form
            var lineExpanded = ExpandTildePath(lineArgs).TrimEnd('/');
            return lineExpanded.Equals(expandedDir, StringComparison.Ordinal) ||
                   lineArgs.Equals(originalDir.TrimEnd('/'), StringComparison.Ordinal);
        });

        if (removed > 0)
        {
            File.WriteAllLines(initPath, lines);
            Console.ForegroundColor = Theme.Current.Muted;
            Console.WriteLine("  removed from: ~/.config/rush/init.rush");
            Console.ResetColor();
        }
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = Theme.Current.Error;
        Console.Error.WriteLine($"  save failed: {ex.Message}");
        Console.ResetColor();
    }
}

/// <summary>
/// Detect a bare "path add" or "path rm" line that starts a multi-line block (path add...end).
/// Returns true when the line has the subcommand + optional flags but NO directory argument,
/// indicating the user wants to list directories on subsequent lines terminated by "end".
/// </summary>
static bool IsPathBlock(string input)
{
    var trimmed = input.Trim();
    // Must start with "path add" or "path rm"/"path remove"
    if (!trimmed.StartsWith("path add", StringComparison.OrdinalIgnoreCase) &&
        !trimmed.StartsWith("path rm", StringComparison.OrdinalIgnoreCase) &&
        !trimmed.StartsWith("path remove", StringComparison.OrdinalIgnoreCase))
        return false;

    // Strip the "path add" / "path rm" / "path remove" prefix
    var rest = trimmed;
    if (rest.StartsWith("path remove", StringComparison.OrdinalIgnoreCase))
        rest = rest[11..].Trim();
    else if (rest.StartsWith("path add", StringComparison.OrdinalIgnoreCase))
        rest = rest[8..].Trim();
    else // path rm
        rest = rest[7..].Trim();

    // Strip any flags (--front, --save, --name=X)
    while (rest.StartsWith("--"))
    {
        var spaceIdx = rest.IndexOf(' ');
        if (spaceIdx < 0) { rest = ""; break; }
        rest = rest[(spaceIdx + 1)..].TrimStart();
    }

    // If nothing left after stripping subcommand and flags, it's a block start
    return string.IsNullOrEmpty(rest);
}

/// <summary>
/// Built-in path variable management: list, add, remove, edit.
/// Supports --name=VARNAME to target any colon-separated env var (default: PATH).
/// Returns true if the command failed.
/// </summary>
static bool HandlePathCommand(string args, Runspace runspace, bool quiet = false)
{
    // Extract --name=VARNAME flag from anywhere in args
    string varName = "PATH";
    var nameMatch = System.Text.RegularExpressions.Regex.Match(args, @"--name=(\S+)");
    if (nameMatch.Success)
    {
        varName = nameMatch.Groups[1].Value;
        args = args.Replace(nameMatch.Value, "").Trim();
        args = System.Text.RegularExpressions.Regex.Replace(args, @"\s{2,}", " ").Trim();
    }

    var currentValue = Environment.GetEnvironmentVariable(varName) ?? "";
    var entries = currentValue.Split(PathUtils.PathListSeparator).Where(e => !string.IsNullOrEmpty(e)).ToList();

    // ── path / path check — list entries with existence + duplicate indicators ──
    if (string.IsNullOrEmpty(args) || args.Equals("check", StringComparison.OrdinalIgnoreCase))
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int dupeCount = 0;
        int missingCount = 0;

        Console.ForegroundColor = Theme.Current.Muted;
        Console.WriteLine($"  {varName} entries:");
        Console.ResetColor();
        for (int i = 0; i < entries.Count; i++)
        {
            var dir = entries[i];
            var exists = Directory.Exists(dir);
            var isDupe = !seen.Add(dir);
            var num = (i + 1).ToString().PadLeft(3);

            if (isDupe) dupeCount++;
            if (!exists && !isDupe) missingCount++;

            Console.ForegroundColor = Theme.Current.Muted;
            Console.Write($"  {num}  ");

            var displayDir = PathUtils.FormatForDisplay(dir);
            if (isDupe)
            {
                Console.ForegroundColor = Theme.Current.Error;
                Console.Write("↑  ");
                Console.ForegroundColor = Theme.Current.Muted;
                Console.WriteLine(displayDir);
            }
            else
            {
                Console.ForegroundColor = exists ? Theme.Current.PromptSuccess : Theme.Current.Warning;
                Console.Write(exists ? "✓" : "✗");
                Console.Write("  ");
                Console.ForegroundColor = exists ? ConsoleColor.White : Theme.Current.Muted;
                Console.WriteLine(displayDir);
            }
        }

        // Summary
        Console.ForegroundColor = Theme.Current.Muted;
        Console.Write($"  {seen.Count} unique");
        if (dupeCount > 0)
        {
            Console.ForegroundColor = Theme.Current.Error;
            Console.Write($", {dupeCount} duplicates (↑)");
        }
        if (missingCount > 0)
        {
            Console.ForegroundColor = Theme.Current.Warning;
            Console.Write($", {missingCount} missing (✗)");
        }
        Console.ResetColor();
        Console.WriteLine();
        if (dupeCount > 0)
        {
            Console.ForegroundColor = Theme.Current.Muted;
            Console.WriteLine("  run 'path dedupe' to remove duplicates");
            Console.ResetColor();
        }
        return false;
    }

    // ── path dedupe [--save] — remove duplicate entries ─────────────
    if (args.Equals("dedupe", StringComparison.OrdinalIgnoreCase) ||
        args.Equals("dedupe --save", StringComparison.OrdinalIgnoreCase) ||
        args.Equals("--save dedupe", StringComparison.OrdinalIgnoreCase))
    {
        bool save = args.Contains("--save", StringComparison.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var deduped = new List<string>();
        int removed = 0;
        foreach (var entry in entries)
        {
            if (seen.Add(entry))
                deduped.Add(entry);
            else
                removed++;
        }

        if (removed == 0)
        {
            Console.ForegroundColor = Theme.Current.Muted;
            Console.WriteLine($"  {varName}: no duplicates found");
            Console.ResetColor();
            return false;
        }

        var newValue = string.Join(PathUtils.PathListSeparator.ToString(), deduped);
        SetEnvVar(varName, newValue, runspace);

        Console.ForegroundColor = Theme.Current.Muted;
        Console.WriteLine($"  {varName}: removed {removed} duplicates ({deduped.Count} entries)");
        Console.ResetColor();

        if (save)
        {
            SavePathEditToInit(varName, deduped);
            Console.ForegroundColor = Theme.Current.Muted;
            Console.WriteLine("  saved to:  ~/.config/rush/init.rush");
            Console.ResetColor();
        }

        return false;
    }

    // ── path add [--front] [--save] <dir|...end block> ─────────────
    if (args.StartsWith("add", StringComparison.OrdinalIgnoreCase) &&
        (args.Length == 3 || args[3] == ' ' || args[3] == '\t' || args[3] == '\n'))
    {
        var addArgs = args.Length > 3 ? args[4..].Trim() : "";
        bool front = false;
        bool save = false;

        // Parse flags
        while (addArgs.StartsWith("--"))
        {
            if (addArgs.StartsWith("--front", StringComparison.OrdinalIgnoreCase))
            {
                front = true;
                addArgs = addArgs[7..].TrimStart();
            }
            else if (addArgs.StartsWith("--save", StringComparison.OrdinalIgnoreCase))
            {
                save = true;
                addArgs = addArgs[6..].TrimStart();
            }
            else break;
        }

        // Multi-line block: "path add\n  /foo\n  /bar\nend"
        if (addArgs.Contains('\n'))
        {
            var dirs = addArgs.Split('\n')
                .Select(l => l.Trim())
                .Where(l => !string.IsNullOrEmpty(l) &&
                            !l.StartsWith('#') &&
                            !l.Equals("end", StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var d in dirs)
            {
                var dStripped = StripQuotes(d);
                var dExpanded = ExpandTildePath(dStripped).TrimEnd('/', '\\');
                if (entries.Contains(dExpanded)) continue;
                if (!Directory.Exists(dExpanded))
                {
                    if (quiet)
                        continue; // Silently skip in init.rush
                    Console.ForegroundColor = Theme.Current.Warning;
                    Console.Error.WriteLine($"  path add: {dExpanded} does not exist");
                    Console.ResetColor();
                    continue; // Skip non-existent paths
                }
                if (front)
                    entries.Insert(0, dExpanded);
                else
                    entries.Add(dExpanded);
            }

            var blockValue = string.Join(PathUtils.PathListSeparator.ToString(), entries);
            SetEnvVar(varName, blockValue, runspace);

            if (save)
                SavePathEditToInit(varName, entries);

            return false;
        }

        if (string.IsNullOrEmpty(addArgs))
        {
            Console.ForegroundColor = Theme.Current.Error;
            Console.Error.WriteLine("  path add: missing directory argument");
            Console.ResetColor();
            return true;
        }

        // Strip quotes, expand tilde, convert Rush-style path to native
        var dir = StripQuotes(addArgs);
        var expandedDir = ExpandTildePath(dir);
        // Unescape Rush-style paths: C:/Program\ Files → C:\Program Files
        if (OperatingSystem.IsWindows())
            expandedDir = expandedDir.Replace("\\ ", " ").Replace('/', '\\');

        // Normalize: strip trailing slash
        expandedDir = expandedDir.TrimEnd('/', '\\');

        // Skip silently if already in path
        if (entries.Contains(expandedDir))
            return false;

        // Check existence
        if (!Directory.Exists(expandedDir))
        {
            if (quiet)
                return false; // Silently skip in init.rush — common for cross-platform configs
            Console.ForegroundColor = Theme.Current.Warning;
            Console.Error.WriteLine($"  path add: {expandedDir} does not exist");
            Console.ResetColor();
            return true; // Signal failure
        }

        // Add to variable
        string newValue;
        if (front)
        {
            newValue = expandedDir + PathUtils.PathListSeparator + currentValue;
        }
        else
        {
            newValue = currentValue + PathUtils.PathListSeparator + expandedDir;
        }
        SetEnvVar(varName, newValue, runspace);

        // Persist to init.rush if --save
        if (save)
        {
            // Use the original (unexpanded) dir if it had ~ for portability
            var savedDir = dir.Contains('~') ? dir : expandedDir;
            var nameFlag = varName != "PATH" ? $"--name={varName} " : "";
            var pathLine = front
                ? $"path add --front {nameFlag}{savedDir}"
                : $"path add {nameFlag}{savedDir}";
            SavePathToInit(pathLine);
        }

        return false;
    }

    // ── path rm [--save] <dir|...end block> ────────────────────────
    if (args.StartsWith("rm", StringComparison.OrdinalIgnoreCase) &&
        (args.Length == 2 || args[2] == ' ' || args[2] == '\t' || args[2] == '\n') ||
        args.StartsWith("remove", StringComparison.OrdinalIgnoreCase) &&
        (args.Length == 6 || args[6] == ' ' || args[6] == '\t' || args[6] == '\n'))
    {
        var rmStart = args.IndexOf(' ');
        var rmArgs = rmStart >= 0 ? args[(rmStart + 1)..].Trim() : "";
        bool save = false;
        if (rmArgs.StartsWith("--save", StringComparison.OrdinalIgnoreCase))
        {
            save = true;
            var afterFlag = rmArgs.Length > 6 ? rmArgs[6..].TrimStart() : "";
            rmArgs = afterFlag;
        }

        // Multi-line block: "rm\n  /foo\n  /bar\nend"
        if (rmArgs.Contains('\n'))
        {
            var dirs = rmArgs.Split('\n')
                .Select(l => l.Trim())
                .Where(l => !string.IsNullOrEmpty(l) &&
                            !l.StartsWith('#') &&
                            !l.Equals("end", StringComparison.OrdinalIgnoreCase))
                .ToList();

            int totalRemoved = 0;
            foreach (var d in dirs)
            {
                var dStripped = StripQuotes(d);
                var dExpanded = ExpandTildePath(dStripped).TrimEnd('/', '\\');
                var before = entries.Count;
                entries.RemoveAll(e => e.TrimEnd('/').Equals(dExpanded, StringComparison.Ordinal));
                totalRemoved += before - entries.Count;
            }

            if (totalRemoved > 0)
            {
                var rmBlockValue = string.Join(PathUtils.PathListSeparator.ToString(), entries);
                SetEnvVar(varName, rmBlockValue, runspace);
            }

            if (save)
                SavePathEditToInit(varName, entries);

            return false;
        }

        if (string.IsNullOrEmpty(rmArgs))
        {
            Console.ForegroundColor = Theme.Current.Error;
            Console.Error.WriteLine("  path rm: missing directory argument");
            Console.ResetColor();
            return true;
        }

        var dir = StripQuotes(rmArgs);
        var expandedDir = ExpandTildePath(dir).TrimEnd('/');

        var before2 = entries.Count;
        entries.RemoveAll(e => e.TrimEnd('/').Equals(expandedDir, StringComparison.Ordinal));

        if (entries.Count == before2)
        {
            Console.ForegroundColor = Theme.Current.Warning;
            Console.Error.WriteLine($"  not found in {varName}: {expandedDir}");
            Console.ResetColor();
            return true;
        }

        var newValue = string.Join(PathUtils.PathListSeparator.ToString(), entries);
        SetEnvVar(varName, newValue, runspace);

        Console.ForegroundColor = Theme.Current.Muted;
        Console.Write("  removed:   ");
        Console.ResetColor();
        Console.WriteLine(expandedDir);

        if (save)
            RemovePathFromInit(expandedDir, dir, varName);

        return false;
    }

    // ── path edit [--save] — open in $EDITOR ────────────────────────
    if (args.Equals("edit", StringComparison.OrdinalIgnoreCase) ||
        args.Equals("edit --save", StringComparison.OrdinalIgnoreCase) ||
        args.Equals("--save edit", StringComparison.OrdinalIgnoreCase))
    {
        bool save = args.Contains("--save", StringComparison.OrdinalIgnoreCase);
        var editor = Environment.GetEnvironmentVariable("EDITOR") ?? "vi";
        string? tempFile = null;

        try
        {
            tempFile = Path.GetTempFileName();
            File.WriteAllText(tempFile,
                $"# Edit {varName} entries (one per line). Blank lines and #comments are ignored.\n" +
                string.Join("\n", entries) + "\n");

            // Open editor with inherited stdio
            var psi = new ProcessStartInfo(editor, tempFile) { UseShellExecute = false };
            var proc = Process.Start(psi);
            if (proc == null)
            {
                Console.ForegroundColor = Theme.Current.Error;
                Console.Error.WriteLine($"  path edit: could not start {editor}");
                Console.ResetColor();
                return true;
            }
            proc.WaitForExit();

            if (proc.ExitCode != 0)
            {
                Console.ForegroundColor = Theme.Current.Warning;
                Console.WriteLine($"  path edit: editor exited with error, {varName} unchanged");
                Console.ResetColor();
                return false;
            }
            proc.Dispose();

            // Read back, filter blanks and comments, dedupe
            var newEntriesSeen = new HashSet<string>(StringComparer.Ordinal);
            var newEntries = File.ReadAllLines(tempFile)
                .Select(l => l.Trim())
                .Where(l => !string.IsNullOrEmpty(l) && !l.StartsWith('#'))
                .Where(l => newEntriesSeen.Add(l))
                .ToList();

            var newValue = string.Join(PathUtils.PathListSeparator.ToString(), newEntries);
            SetEnvVar(varName, newValue, runspace);

            Console.ForegroundColor = Theme.Current.Muted;
            Console.WriteLine($"  {varName} updated ({newEntries.Count} entries)");
            Console.ResetColor();

            // Save to init.rush if --save
            if (save)
            {
                SavePathEditToInit(varName, newEntries);
                Console.ForegroundColor = Theme.Current.Muted;
                Console.WriteLine("  saved to:  ~/.config/rush/init.rush");
                Console.ResetColor();
            }

            return false;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = Theme.Current.Error;
            Console.Error.WriteLine($"  path edit: {ex.Message}");
            Console.ResetColor();
            return true;
        }
        finally
        {
            if (tempFile != null) try { File.Delete(tempFile); } catch { }
        }
    }

    // ── Unknown subcommand ──────────────────────────────────────────
    Console.ForegroundColor = Theme.Current.Error;
    Console.Error.WriteLine($"  path: unknown subcommand '{args.Split(' ')[0]}'");
    Console.ResetColor();
    Console.ForegroundColor = Theme.Current.Muted;
    Console.Error.WriteLine("  usage: path [check | dedupe | edit | add [--front] <dir> | rm <dir>] [--save]");
    Console.ResetColor();
    return true;
}

static string FormatDuration(TimeSpan elapsed)
{
    if (elapsed.TotalMinutes >= 1)
        return $"{elapsed.Minutes}m {elapsed.Seconds}s";
    return $"{elapsed.TotalSeconds:F1}s";
}

// ── printf Formatting ───────────────────────────────────────────────

/// <summary>
/// Format a printf-style string with C-style specifiers: %s %d %f %x %%
/// and escape sequences: \n \t \\
/// </summary>
static string PrintfFormat(string format, string[] args)
{
    var sb = new System.Text.StringBuilder();
    int argIdx = 0;

    for (int i = 0; i < format.Length; i++)
    {
        if (format[i] == '\\' && i + 1 < format.Length)
        {
            switch (format[i + 1])
            {
                case 'n': sb.Append('\n'); i++; continue;
                case 't': sb.Append('\t'); i++; continue;
                case 'e': sb.Append('\x1b'); i++; continue;
                case 'a': sb.Append('\a'); i++; continue;
                case 'r': sb.Append('\r'); i++; continue;
                case '\\': sb.Append('\\'); i++; continue;
                case '0': sb.Append('\0'); i++; continue;
            }
        }

        if (format[i] == '%' && i + 1 < format.Length)
        {
            var spec = format[i + 1];
            if (spec == '%') { sb.Append('%'); i++; continue; }

            var arg = argIdx < args.Length ? args[argIdx++] : "";
            switch (spec)
            {
                case 's': sb.Append(arg); break;
                case 'd': sb.Append(int.TryParse(arg, out var iv) ? iv : 0); break;
                case 'f': sb.Append(double.TryParse(arg, out var dv) ? dv.ToString("F6") : "0.000000"); break;
                case 'x': sb.Append(int.TryParse(arg, out var xv) ? xv.ToString("x") : "0"); break;
                default: sb.Append('%').Append(spec); break;
            }
            i++;
            continue;
        }

        sb.Append(format[i]);
    }

    return sb.ToString();
}

/// <summary>Strip surrounding single or double quotes from a string.</summary>
static string StripQuotes(string s)
{
    if (s.Length >= 2 &&
        ((s[0] == '\'' && s[^1] == '\'') || (s[0] == '"' && s[^1] == '"')))
        return s[1..^1];
    return s;
}

// ── Tilde Expansion ────────────────────────────────────────────────

// ── Brace Expansion ─────────────────────────────────────────────

/// <summary>
/// Expand brace patterns: file.{bak,txt} → file.bak file.txt
/// Handles nested braces: {a,{b,c}} → a b c
/// Respects quotes — no expansion inside quoted strings.
/// Must run before tilde expansion (bash canonical order).
/// </summary>
/// <summary>
/// Run the full expansion pipeline: brace, tilde, env vars, arithmetic,
/// process substitution, command substitution. Shared by interactive and
/// non-interactive paths.
/// </summary>
static (string expanded, List<string>? tempFiles) RunExpansionPipeline(
    string input, CommandTranslator translator, System.Management.Automation.Runspaces.Runspace runspace)
{
    input = ExpandBraces(input);
    input = ExpandTilde(input);
    input = ExpandEnvVars(input);
    input = ExpandArithmetic(input, runspace);
    List<string>? tempFiles = null;
    if (input.Contains("<("))
        (input, tempFiles) = ExpandProcessSubstitution(input, translator, runspace);
    input = ExpandCommandSubstitution(input, translator, runspace);

    // Windows: translate //server/share paths to \\server\share for native UNC
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        input = ExpandWindowsUnc(input);

    return (input, tempFiles);
}

/// <summary>
/// On Windows, translate //server/share style paths to \\server\share.
/// Only converts paths that start with // followed by a non-/ character
/// (to avoid interfering with //ssh: UNC or // comments).
/// </summary>
static string ExpandWindowsUnc(string input)
{
    if (!input.Contains("//")) return input;

    // Split on spaces (respecting quotes) and translate //server paths
    var result = new System.Text.StringBuilder();
    bool inQuote = false;
    int wordStart = 0;

    for (int i = 0; i <= input.Length; i++)
    {
        if (i < input.Length && input[i] == '"') inQuote = !inQuote;
        if (i == input.Length || (!inQuote && input[i] == ' '))
        {
            var word = input[wordStart..i];
            // Translate //server/share (not //ssh:) to \\server\share
            if (word.StartsWith("//") && word.Length > 2 && word[2] != '/'
                && !word.StartsWith("//ssh:", StringComparison.OrdinalIgnoreCase))
            {
                word = "\\\\" + word[2..].Replace('/', '\\');
            }
            result.Append(word);
            if (i < input.Length) result.Append(' ');
            wordStart = i + 1;
        }
    }
    return result.ToString().TrimEnd();
}

static string ExpandBraces(string input)
{
    if (!input.Contains('{')) return input;

    var parts = CommandTranslator.SplitCommandLine(input);
    var expanded = new List<string>();

    foreach (var part in parts)
    {
        // Skip quoted strings
        if (part.Length >= 2 &&
            ((part[0] == '\'' && part[^1] == '\'') ||
             (part[0] == '"' && part[^1] == '"')))
        {
            expanded.Add(part);
            continue;
        }

        expanded.AddRange(ExpandBraceWord(part));
    }

    return string.Join(' ', expanded);
}

static List<string> ExpandBraceWord(string word)
{
    // Find the first top-level { that has a matching } with at least one comma
    int braceStart = -1, braceEnd = -1;
    int depth = 0;

    for (int i = 0; i < word.Length; i++)
    {
        if (word[i] == '{')
        {
            if (depth == 0) braceStart = i;
            depth++;
        }
        else if (word[i] == '}')
        {
            depth--;
            if (depth == 0 && braceStart >= 0)
            {
                // Check for at least one comma at depth 0 inside braces
                bool hasComma = false;
                int d = 0;
                for (int j = braceStart + 1; j < i; j++)
                {
                    if (word[j] == '{') d++;
                    else if (word[j] == '}') d--;
                    else if (word[j] == ',' && d == 0) { hasComma = true; break; }
                }

                if (hasComma) { braceEnd = i; break; }
                braceStart = -1; // no comma — not a brace expansion
            }
        }
    }

    if (braceStart < 0 || braceEnd < 0)
        return new List<string> { word };

    var prefix = word[..braceStart];
    var suffix = word[(braceEnd + 1)..];
    var inner = word[(braceStart + 1)..braceEnd];

    // Split inner on top-level commas
    var alternatives = SplitBraceAlternatives(inner);

    var results = new List<string>();
    foreach (var alt in alternatives)
        results.AddRange(ExpandBraceWord(prefix + alt + suffix)); // recurse for nesting

    return results;
}

static List<string> SplitBraceAlternatives(string inner)
{
    var alts = new List<string>();
    var current = new System.Text.StringBuilder();
    int depth = 0;

    foreach (var ch in inner)
    {
        if (ch == '{') depth++;
        else if (ch == '}') depth--;
        else if (ch == ',' && depth == 0)
        {
            alts.Add(current.ToString());
            current.Clear();
            continue;
        }
        current.Append(ch);
    }
    alts.Add(current.ToString());
    return alts;
}

// ── Tilde Expansion ─────────────────────────────────────────────

/// <summary>
/// Expand ~ and ~/ to the user's home directory.
/// Only expands at the start of a word (after space or at start of input).
/// Respects quotes — no expansion inside quoted strings.
/// </summary>
static string ExpandTilde(string input)
{
    if (!input.Contains('~')) return input;

    var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    var sb = new System.Text.StringBuilder(input.Length);
    bool inSingleQuote = false;
    bool inDoubleQuote = false;

    for (int i = 0; i < input.Length; i++)
    {
        if (input[i] == '\'' && !inDoubleQuote) { inSingleQuote = !inSingleQuote; sb.Append('\''); continue; }
        if (input[i] == '"' && !inSingleQuote) { inDoubleQuote = !inDoubleQuote; sb.Append('"'); continue; }

        if (!inSingleQuote && !inDoubleQuote && input[i] == '~')
        {
            // Only expand at word boundary (start of input, after space, or after =)
            // After = only when it's assignment (VAR=~/path), not operator (=~ !~)
            // Assignment = is preceded by a word char; operator = is preceded by space/!/=
            bool atWordStart = i == 0 || input[i - 1] is ' ' or '\t'
                || (input[i - 1] == '=' && i >= 2 && char.IsLetterOrDigit(input[i - 2]));
            if (atWordStart)
            {
                // ~/... or standalone ~
                if (i + 1 >= input.Length || input[i + 1] is '/' or ' ' or '\t')
                {
                    sb.Append(home);
                    continue;
                }

                // ~username/... → /Users/username or /home/username
                if (i + 1 < input.Length && char.IsLetterOrDigit(input[i + 1]))
                {
                    int end = i + 1;
                    while (end < input.Length && input[end] is not '/' and not ' ' and not '\t')
                        end++;
                    var username = input[(i + 1)..end];
                    var usersDir = OperatingSystem.IsMacOS() ? "/Users" : "/home";
                    var candidate = Path.Combine(usersDir, username);
                    if (Directory.Exists(candidate))
                    {
                        sb.Append(candidate);
                        i = end - 1; // skip past username (loop will increment)
                        continue;
                    }
                    // Unknown user — leave ~username as-is
                }
            }
        }

        sb.Append(input[i]);
    }

    return sb.ToString();
}

// ── Environment Variable Expansion ──────────────────────────────────

/// <summary>
/// Expand $VAR patterns to environment variable values.
// ── Continuation Lines ───────────────────────────────────────────────

/// <summary>
/// Handle multiline input: trailing backslash, unclosed quotes, unclosed brackets.
/// Reads additional lines until the input is complete.
/// </summary>
/// <summary>
/// Detect simple variable assignments in Rush input and track them
/// for type-aware dot-completion (V2 static inference).
/// </summary>
static void TrackVariableAssignment(string input, TabCompleter tabCompleter)
{
    try
    {
        var trimmed = input.Trim();

        // Skip multi-line blocks, control flow, etc.
        if (trimmed.Contains('\n')) return;

        var eqPos = trimmed.IndexOf('=');
        if (eqPos <= 0) return;

        // Exclude ==, !=, <=, >=, =~, +=, -=, *=, /=
        if (eqPos + 1 < trimmed.Length && trimmed[eqPos + 1] is '=' or '~') return;
        if (trimmed[eqPos - 1] is '!' or '<' or '>' or '+' or '-' or '*' or '/') return;

        var varName = trimmed[..eqPos].Trim();
        var rhs = trimmed[(eqPos + 1)..].Trim();

        // Validate identifier: letters, digits, underscores only
        if (string.IsNullOrEmpty(varName)) return;
        if (!char.IsLetter(varName[0]) && varName[0] != '_') return;
        foreach (var ch in varName)
        {
            if (!char.IsLetterOrDigit(ch) && ch != '_') return;
        }

        tabCompleter.TrackAssignment(varName, rhs);
    }
    catch
    {
        // Assignment tracking is best-effort — never fail the REPL
    }
}

static string ReadContinuationLines(string input, LineEditor? editor = null)
{
    var sb = new System.Text.StringBuilder(input);

    while (true)
    {
        var current = sb.ToString();

        // Trailing backslash → line continuation
        if (current.TrimEnd().EndsWith('\\'))
        {
            var trimmed = current.TrimEnd();
            sb.Clear();
            sb.Append(trimmed[..^1]); // Strip the backslash
            Console.ForegroundColor = Theme.Current.Muted;
            Console.Write(Prompt.Continuation);
            Console.ResetColor();
            var next = editor != null ? editor.ReadLine() : Console.ReadLine();
            if (next == null) break;            // Ctrl+D
            if (next == "" && editor != null) return "";  // Ctrl+C — cancel
            sb.Append(next);
            continue;
        }

        // Unclosed quotes → continue reading
        if (HasUnclosedQuote(current))
        {
            Console.ForegroundColor = Theme.Current.Muted;
            Console.Write(Prompt.Continuation);
            Console.ResetColor();
            var next = editor != null ? editor.ReadLine() : Console.ReadLine();
            if (next == null) break;            // Ctrl+D
            if (next == "" && editor != null) return "";  // Ctrl+C — cancel
            sb.AppendLine();
            sb.Append(next);
            continue;
        }

        // Unclosed brackets → continue reading
        if (HasUnclosedBrackets(current))
        {
            Console.ForegroundColor = Theme.Current.Muted;
            Console.Write(Prompt.Continuation);
            Console.ResetColor();
            var next = editor != null ? editor.ReadLine() : Console.ReadLine();
            if (next == null) break;            // Ctrl+D
            if (next == "" && editor != null) return "";  // Ctrl+C — cancel
            sb.AppendLine();
            sb.Append(next);
            continue;
        }

        break;
    }

    return sb.ToString();
}

static bool HasUnclosedQuote(string input)
{
    bool inSingle = false, inDouble = false;
    for (int i = 0; i < input.Length; i++)
    {
        if (input[i] == '\'' && !inDouble) inSingle = !inSingle;
        else if (input[i] == '"' && !inSingle) inDouble = !inDouble;
    }
    return inSingle || inDouble;
}

static bool HasUnclosedBrackets(string input)
{
    int parenDepth = 0, braceDepth = 0;
    bool inSingle = false, inDouble = false;
    for (int i = 0; i < input.Length; i++)
    {
        if (input[i] == '\'' && !inDouble) { inSingle = !inSingle; continue; }
        if (input[i] == '"' && !inSingle) { inDouble = !inDouble; continue; }
        if (inSingle || inDouble) continue;
        if (input[i] == '(') parenDepth++;
        else if (input[i] == ')') parenDepth--;
        else if (input[i] == '{') braceDepth++;
        else if (input[i] == '}') braceDepth--;
    }
    return parenDepth > 0 || braceDepth > 0;
}

// ── Heredoc ─────────────────────────────────────────────────────────

/// <summary>
/// Detect <<WORD or <<'WORD' at end of command. If found, read the heredoc
/// body (lines until delimiter) and return the command without the <<WORD part.
/// </summary>
static string DetectAndReadHeredoc(string input, out string? heredocContent)
{
    heredocContent = null;

    // Find << outside of quotes
    int heredocPos = -1;
    bool inSingleQuote = false, inDoubleQuote = false;

    for (int i = 0; i < input.Length - 1; i++)
    {
        if (input[i] == '\'' && !inDoubleQuote) { inSingleQuote = !inSingleQuote; continue; }
        if (input[i] == '"' && !inSingleQuote) { inDoubleQuote = !inDoubleQuote; continue; }
        if (!inSingleQuote && !inDoubleQuote && input[i] == '<' && input[i + 1] == '<')
        {
            // Not <<< (herestring)
            if (i + 2 < input.Length && input[i + 2] == '<') continue;
            heredocPos = i;
        }
    }

    if (heredocPos < 0) return input;

    var command = input[..heredocPos].TrimEnd();
    var delimiterPart = input[(heredocPos + 2)..].Trim();

    if (string.IsNullOrEmpty(delimiterPart)) return input;

    // Quoted delimiter → no variable expansion in body
    bool noExpand = false;
    string delimiter;
    if ((delimiterPart.StartsWith('\'') && delimiterPart.EndsWith('\'')) ||
        (delimiterPart.StartsWith('"') && delimiterPart.EndsWith('"')))
    {
        noExpand = true;
        delimiter = delimiterPart[1..^1];
    }
    else
    {
        delimiter = delimiterPart;
    }

    // Read heredoc body
    var body = new System.Text.StringBuilder();
    while (true)
    {
        Console.ForegroundColor = Theme.Current.Muted;
        Console.Write("heredoc> ");
        Console.ResetColor();
        var line = Console.ReadLine();
        if (line == null) break; // EOF
        if (line.Trim() == delimiter) break;

        if (!noExpand)
            line = ExpandEnvVars(line);

        if (body.Length > 0) body.AppendLine();
        body.Append(line);
    }

    heredocContent = body.ToString();
    return command;
}

// ── Command Substitution ─────────────────────────────────────────────

/// <summary>
/// Expand $(...) and `...` substitutions. Inner commands are translated
/// through Rush's CommandTranslator before execution, so Unix syntax works
/// inside substitutions: echo $(ls | grep foo)
/// </summary>
static string ExpandCommandSubstitution(string input, CommandTranslator translator, Runspace runspace)
{
    if (!input.Contains("$(") && !input.Contains('`'))
        return input;

    var sb = new System.Text.StringBuilder(input.Length);
    bool inSingleQuote = false;

    for (int i = 0; i < input.Length; i++)
    {
        if (input[i] == '\'' && !inSingleQuote)
        {
            inSingleQuote = true;
            sb.Append('\'');
            continue;
        }
        if (input[i] == '\'' && inSingleQuote)
        {
            inSingleQuote = false;
            sb.Append('\'');
            continue;
        }

        // No substitution inside single quotes
        if (inSingleQuote)
        {
            sb.Append(input[i]);
            continue;
        }

        // $(...) substitution
        if (input[i] == '$' && i + 1 < input.Length && input[i + 1] == '(')
        {
            int depth = 1;
            int pos = i + 2;

            // Find matching closing paren (handle nesting)
            while (pos < input.Length && depth > 0)
            {
                if (input[pos] == '(') depth++;
                else if (input[pos] == ')') depth--;
                if (depth > 0) pos++;
            }

            if (depth == 0)
            {
                var innerCmd = input[(i + 2)..pos];
                var result = ExecuteSubstitution(innerCmd, translator, runspace);
                sb.Append(result);
                i = pos; // Skip past closing )
                continue;
            }
        }

        // Backtick substitution: `command`
        if (input[i] == '`')
        {
            int start = i + 1;
            int end = input.IndexOf('`', start);
            if (end > start)
            {
                var innerCmd = input[start..end];
                var result = ExecuteSubstitution(innerCmd, translator, runspace);
                sb.Append(result);
                i = end; // Skip past closing backtick
                continue;
            }
        }

        sb.Append(input[i]);
    }

    return sb.ToString();
}

/// <summary>
/// Execute a substitution's inner command: translate through Rush,
/// run in PowerShell, capture output as a string.
/// </summary>
static string ExecuteSubstitution(string innerCommand, CommandTranslator translator, Runspace runspace)
{
    try
    {
        // Recursively expand nested substitutions
        innerCommand = ExpandCommandSubstitution(innerCommand, translator, runspace);

        // If inner command is Rush syntax (e.g., File.read(), Dir.exists()),
        // transpile to PowerShell before executing
        var se = new ScriptEngine(translator);
        if (se.IsRushSyntax(innerCommand))
        {
            var transpiled = se.TranspileFile(innerCommand);
            if (transpiled != null)
                innerCommand = transpiled;
        }
        else
        {
            // Translate through Rush's Unix→PS translator
            var translated = translator.Translate(innerCommand);
            if (translated != null)
                innerCommand = translated;
        }

        using var ps = PowerShell.Create();
        ps.Runspace = runspace;
        ps.AddScript(innerCommand);
        var results = ps.Invoke();

        // Join results with spaces (standard shell behavior)
        // Trim trailing newlines like bash does
        return string.Join(" ", results
            .Select(r => r.ToString()?.Trim() ?? "")
            .Where(s => s.Length > 0));
    }
    catch
    {
        return ""; // On error, substitute empty string
    }
}

// ── Process Substitution ────────────────────────────────────────────

/// <summary>
/// Expand process substitution: diff &lt;(cmd1) &lt;(cmd2)
/// Executes each command, writes output to temp file, substitutes path.
/// </summary>
static (string expanded, List<string> tempFiles) ExpandProcessSubstitution(
    string input, CommandTranslator translator, Runspace runspace)
{
    var tempFiles = new List<string>();
    var sb = new System.Text.StringBuilder();
    bool inSingleQuote = false;

    for (int i = 0; i < input.Length; i++)
    {
        if (input[i] == '\'') { inSingleQuote = !inSingleQuote; sb.Append('\''); continue; }
        if (inSingleQuote) { sb.Append(input[i]); continue; }

        if (input[i] == '<' && i + 1 < input.Length && input[i + 1] == '(')
        {
            int depth = 1;
            int pos = i + 2;
            while (pos < input.Length && depth > 0)
            {
                if (input[pos] == '(') depth++;
                else if (input[pos] == ')') depth--;
                if (depth > 0) pos++;
            }

            if (depth == 0)
            {
                var innerCmd = input[(i + 2)..pos];
                var translated = translator.Translate(innerCmd) ?? innerCmd;
                string output;
                try
                {
                    using var ps = PowerShell.Create();
                    ps.Runspace = runspace;
                    ps.AddScript(translated);
                    var results = ps.Invoke();
                    output = string.Join('\n', results.Select(r => r?.ToString() ?? ""));
                }
                catch { output = ""; }

                var tmpFile = Path.GetTempFileName();
                File.WriteAllText(tmpFile, output);
                tempFiles.Add(tmpFile);
                sb.Append(tmpFile);
                i = pos;
                continue;
            }
        }

        sb.Append(input[i]);
    }

    return (sb.ToString(), tempFiles);
}

// ── Glob Expansion (native/passthrough commands only) ────────────────

/// <summary>
/// Expand glob patterns (*, ?, [...]) in command arguments for native commands.
/// PowerShell handles globs for its own cmdlets, but native commands need
/// shell-level expansion — just like bash/zsh do.
/// </summary>
static string ExpandGlobs(string command)
{
    // Quick bail: no glob characters at all
    if (!command.Contains('*') && !command.Contains('?') && !command.Contains('['))
        return command;

    var parts = CommandTranslator.SplitCommandLine(command);
    if (parts.Length == 0) return command;

    var expanded = new List<string>();
    expanded.Add(parts[0]); // Command name — never glob-expand

    for (int i = 1; i < parts.Length; i++)
    {
        var arg = parts[i];

        // Don't expand inside quotes
        if ((arg.StartsWith('\'') && arg.EndsWith('\'')) ||
            (arg.StartsWith('"') && arg.EndsWith('"')))
        {
            expanded.Add(arg);
            continue;
        }

        // No glob characters in this arg
        if (!arg.Contains('*') && !arg.Contains('?') && !arg.Contains('['))
        {
            expanded.Add(arg);
            continue;
        }

        var matches = ExpandSingleGlob(arg);
        if (matches.Count > 0)
        {
            // Quote filenames containing spaces so they survive word splitting
            foreach (var m in matches)
                expanded.Add(m.Contains(' ') ? $"\"{m}\"" : m);
        }
        else
            expanded.Add(arg); // No match: pass literal (bash default)
    }

    return string.Join(' ', expanded);
}

/// <summary>
/// Expand a single glob pattern using the filesystem.
/// Supports * and ? via Directory.EnumerateFileSystemEntries,
/// and ** for recursive globbing.
/// </summary>
static List<string> ExpandSingleGlob(string pattern)
{
    var results = new List<string>();

    // ** recursive glob
    if (pattern.Contains("**"))
    {
        var doubleStarIdx = pattern.IndexOf("**");
        var baseDir = pattern[..doubleStarIdx].TrimEnd('/', Path.DirectorySeparatorChar);
        if (string.IsNullOrEmpty(baseDir)) baseDir = ".";

        var afterDoubleStar = pattern[(doubleStarIdx + 2)..].TrimStart('/', Path.DirectorySeparatorChar);
        if (string.IsNullOrEmpty(afterDoubleStar)) afterDoubleStar = "*";

        if (Directory.Exists(baseDir))
        {
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(
                    baseDir, afterDoubleStar, SearchOption.AllDirectories))
                {
                    // Return relative paths when base is current dir
                    results.Add(baseDir == "." ? Path.GetRelativePath(".", entry) : entry);
                }
            }
            catch { } // Permission errors
        }
        results.Sort(StringComparer.OrdinalIgnoreCase);
        return results;
    }

    // Standard glob: split into directory + pattern
    var dir = Path.GetDirectoryName(pattern);
    var filePattern = Path.GetFileName(pattern);

    if (string.IsNullOrEmpty(dir)) dir = ".";
    if (string.IsNullOrEmpty(filePattern)) return results;

    if (!Directory.Exists(dir)) return results;

    try
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(dir, filePattern))
        {
            results.Add(dir == "." ? Path.GetFileName(entry) : entry);
        }
        results.Sort(StringComparer.OrdinalIgnoreCase);
    }
    catch { }

    return results;
}

/// Only expands variables that actually exist in the environment.
/// Respects single quotes (no expansion inside 'quoted strings').
/// </summary>
static string ExpandEnvVars(string input)
{
    if (!input.Contains('$')) return input;

    var sb = new System.Text.StringBuilder(input.Length);
    bool inSingleQuote = false;

    for (int i = 0; i < input.Length; i++)
    {
        if (input[i] == '\'')
        {
            inSingleQuote = !inSingleQuote;
            sb.Append('\'');
            continue;
        }

        if (!inSingleQuote && input[i] == '$' && i + 1 < input.Length
            && (char.IsLetter(input[i + 1]) || input[i + 1] == '_'))
        {
            // Read variable name
            int start = i + 1;
            int end = start;
            while (end < input.Length && (char.IsLetterOrDigit(input[end]) || input[end] == '_'))
                end++;

            var varName = input[start..end];

            // Skip PowerShell special variables
            if (varName is "_" or "null" or "true" or "false" or "PSVersionTable"
                or "ErrorActionPreference" or "ProgressPreference"
                or "env" or "global" or "script" or "using" or "this")
            {
                sb.Append(input[i]);
                continue;
            }

            var value = Environment.GetEnvironmentVariable(varName);
            if (value != null)
            {
                sb.Append(value);
                i = end - 1;
                continue;
            }
        }

        sb.Append(input[i]);
    }

    return sb.ToString();
}

// ── Arithmetic Expansion ────────────────────────────────────────────

/// <summary>
/// Expand $((expr)) arithmetic expressions.
/// Evaluates via the PowerShell runspace for full expression support.
/// Respects single quotes — no expansion inside.
/// </summary>
static string ExpandArithmetic(string input, Runspace runspace)
{
    if (!input.Contains("$((")) return input;

    var sb = new System.Text.StringBuilder(input.Length);
    bool inSingleQuote = false;

    for (int i = 0; i < input.Length; i++)
    {
        if (input[i] == '\'' && !inSingleQuote) { inSingleQuote = true; sb.Append('\''); continue; }
        if (input[i] == '\'' && inSingleQuote) { inSingleQuote = false; sb.Append('\''); continue; }
        if (inSingleQuote) { sb.Append(input[i]); continue; }

        if (i + 2 < input.Length && input[i] == '$' && input[i + 1] == '(' && input[i + 2] == '(')
        {
            // Find matching ))
            int depth = 1;
            int pos = i + 3;
            while (pos < input.Length - 1 && depth > 0)
            {
                if (input[pos] == '(' && input[pos + 1] == '(') { depth++; pos += 2; continue; }
                if (input[pos] == ')' && input[pos + 1] == ')') { depth--; if (depth == 0) break; pos += 2; continue; }
                pos++;
            }

            if (depth == 0)
            {
                var expr = input[(i + 3)..pos];
                try
                {
                    using var ps = PowerShell.Create();
                    ps.Runspace = runspace;
                    ps.AddScript(expr);
                    var results = ps.Invoke();
                    sb.Append(results.FirstOrDefault()?.ToString() ?? "0");
                }
                catch { sb.Append("0"); }
                i = pos + 1; // skip past ))
                continue;
            }
        }

        sb.Append(input[i]);
    }

    return sb.ToString();
}

// ── Chain Operators ─────────────────────────────────────────────────

/// <summary>
/// Split input on &&, ||, and ; operators, respecting quotes.
/// operators[i] is the operator between segments[i] and segments[i+1].
/// </summary>
static (List<string> segments, List<string> operators) SplitChainOperators(string input)
{
    var segments = new List<string>();
    var operators = new List<string>();
    var current = new System.Text.StringBuilder();
    bool inSingleQuote = false;
    bool inDoubleQuote = false;

    for (int i = 0; i < input.Length; i++)
    {
        char ch = input[i];

        if (ch == '\'' && !inDoubleQuote) { inSingleQuote = !inSingleQuote; current.Append(ch); continue; }
        if (ch == '"' && !inSingleQuote) { inDoubleQuote = !inDoubleQuote; current.Append(ch); continue; }

        if (!inSingleQuote && !inDoubleQuote)
        {
            if (ch == '&' && i + 1 < input.Length && input[i + 1] == '&')
            {
                segments.Add(current.ToString());
                operators.Add("&&");
                current.Clear();
                i++;
                continue;
            }
            if (ch == '|' && i + 1 < input.Length && input[i + 1] == '|')
            {
                segments.Add(current.ToString());
                operators.Add("||");
                current.Clear();
                i++;
                continue;
            }
            if (ch == ';')
            {
                segments.Add(current.ToString());
                operators.Add(";");
                current.Clear();
                continue;
            }
        }

        current.Append(ch);
    }

    if (current.Length > 0)
        segments.Add(current.ToString());

    return (segments, operators);
}

// ── cd ──────────────────────────────────────────────────────────────

static string? GetRunspaceDir(Runspace runspace)
{
    try
    {
        using var ps = PowerShell.Create();
        ps.Runspace = runspace;
        ps.AddCommand("Get-Location");
        var loc = ps.Invoke();
        return loc.Count > 0 ? loc[0].ToString() : null;
    }
    catch { return null; }
}

static void PrintDirStack(Runspace runspace, Stack<string> stack)
{
    var current = GetRunspaceDir(runspace) ?? ".";
    var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    string Shorten(string p) => p.StartsWith(home) ? "~" + p[home.Length..] : p;

    var parts = new List<string> { Shorten(current) };
    foreach (var d in stack) parts.Add(Shorten(d));
    Console.WriteLine(string.Join("  ", parts));
}

static (bool failed, string? newPreviousDir) HandleCd(Runspace runspace, string input, string? previousDirectory)
{
    var path = input.Length > 3 ? input[3..].Trim() : "~";

    // Strip surrounding quotes: cd "My Folder" or cd 'My Folder'
    if (path.Length >= 2 &&
        ((path[0] == '"' && path[^1] == '"') || (path[0] == '\'' && path[^1] == '\'')))
        path = path[1..^1];

    // Handle backslash-space escaping: cd My\ Folder → My Folder
    path = path.Replace("\\ ", " ");

    // Windows UNC: //server/share → \\server\share
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && path.StartsWith("//") && path.Length > 2 && path[2] != '/')
        path = "\\\\" + path[2..].Replace('/', '\\');

    string? currentDir = null;
    try
    {
        using var locPs = PowerShell.Create();
        locPs.Runspace = runspace;
        locPs.AddCommand("Get-Location");
        var loc = locPs.Invoke();
        currentDir = loc.Count > 0 ? loc[0].ToString() : null;
    }
    catch { }

    if (path == "-")
    {
        if (previousDirectory == null)
        {
            Console.ForegroundColor = Theme.Current.Warning;
            Console.WriteLine("cd: no previous directory");
            Console.ResetColor();
            return (true, null);
        }
        path = previousDirectory;
    }

    if (path == "~" || path.StartsWith("~/"))
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        path = path == "~" ? home : Path.Combine(home, path[2..]);
    }

    // ~user expansion (e.g., ~mark → /Users/mark or /home/mark or C:\Users\mark)
    if (path.StartsWith('~') && path.Length > 1 && char.IsLetterOrDigit(path[1]))
    {
        int end = 1;
        while (end < path.Length && path[end] is not '/' and not '\\' and not ' ') end++;
        var username = path[1..end];
        var rest = end < path.Length ? path[end..] : "";
        // Derive users directory from current user's home (works on all platforms)
        var myHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var usersDir = Path.GetDirectoryName(myHome); // /Users, /home, or C:\Users
        if (usersDir != null)
        {
            var candidate = Path.Combine(usersDir, username);
            if (Directory.Exists(candidate))
                path = candidate + rest;
        }
    }

    // CDPATH: if path is relative and doesn't exist in cwd, search CDPATH
    if (!Path.IsPathRooted(path) && !Directory.Exists(path))
    {
        var cdpath = Environment.GetEnvironmentVariable("CDPATH");
        if (cdpath != null)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            foreach (var rawDir in cdpath.Split(PathUtils.PathListSeparator))
            {
                var dir = rawDir;
                // Expand ~ in CDPATH entries
                if (dir == "~") dir = home;
                else if (dir.StartsWith("~/")) dir = Path.Combine(home, dir[2..]);

                var candidate = Path.Combine(dir, path);
                if (Directory.Exists(candidate)) { path = candidate; break; }
            }
        }
    }

    try
    {
        using var ps = PowerShell.Create();
        ps.Runspace = runspace;
        ps.AddCommand("Set-Location").AddParameter("Path", path);
        ps.Invoke();

        if (ps.HadErrors)
        {
            OutputRenderer.RenderErrors(ps.Streams);
            return (true, null);
        }

        // Sync .NET process working directory from PowerShell's resolved location.
        // Don't use Path.GetFullPath(path) — it resolves relative to the potentially
        // stale Environment.CurrentDirectory, which may differ from PS on symlinked paths.
        string resolvedDir = path;
        try
        {
            using var locPs2 = PowerShell.Create();
            locPs2.Runspace = runspace;
            locPs2.AddCommand("Get-Location");
            var newLoc = locPs2.Invoke();
            if (newLoc.Count > 0)
                resolvedDir = newLoc[0].ToString()!;
            Environment.CurrentDirectory = resolvedDir;
        }
        catch { /* ignore — Set-Location succeeded, this is best-effort */ }

        // Check for .rushbg in this directory or ancestors
        ApplyThemeForCd(resolvedDir, RushConfig.Load());

        return (false, currentDir);
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = Theme.Current.Error;
        Console.Error.WriteLine($"cd: {ex.Message}");
        Console.ResetColor();
        return (true, null);
    }
}

/// <summary>
/// Walk up from dir looking for .rushbg. Apply if found and different from current.
/// Reset to terminal default if no .rushbg found but one was previously active.
/// </summary>
/// <summary>
/// Single unified function for all theme/color setup. Called from startup,
/// reload, cd, set bg, set theme, setbg, non-interactive, and script mode.
///
/// Priority: .rushbg file > config.Bg > auto-detect.
/// Key rule: SetBackground() creates the theme. Initialize() only runs
/// when no explicit background is set (auto-detect path).
/// </summary>
static void ApplyTheme(RushConfig config, string? cwd = null, bool emitOsc = true)
{
    Theme.MinContrast = config.GetContrastRatio();

    // 1. Determine background color: .rushbg > config.Bg > auto-detect
    string? bgHex = null;
    string? rushBgFile = null;

    if (cwd != null)
    {
        rushBgFile = FindRushBgFile(cwd);
        if (rushBgFile != null)
        {
            try { bgHex = File.ReadAllText(rushBgFile).Trim(); }
            catch { rushBgFile = null; }
            if (string.IsNullOrEmpty(bgHex)) { bgHex = null; rushBgFile = null; }
        }
    }

    if (bgHex == null && !string.IsNullOrEmpty(config.Bg)
        && !string.Equals(config.Bg, "off", StringComparison.OrdinalIgnoreCase))
    {
        bgHex = config.Bg;
    }

    // 2. Apply background or reset to auto-detect
    if (bgHex != null)
    {
        // SetBackground creates the Theme with correct dark/light and RGB.
        // Do NOT call Initialize() after this — it would overwrite the theme.
        Theme.SetBackground(bgHex, emitOsc);
    }
    else
    {
        if (emitOsc) Theme.ResetBackground();
        // No explicit background — auto-detect and create theme
        Theme.Initialize(config.GetThemeOverride());
    }

    // Track active .rushbg file
    Theme.ActiveRushBgFile = rushBgFile;

    // 3. Root background (overrides for admin shells)
    if (emitOsc && Prompt.IsRoot())
        Theme.ApplyRootBackground(config.RootBackground);

    // 4. Update native command colors (LS_COLORS, GREP_COLORS, etc.)
    Theme.SetNativeColorEnvVars();
}

/// <summary>
/// Lightweight version for cd — only updates if .rushbg state changed.
/// Calls full ApplyTheme when background needs to change.
/// </summary>
static void ApplyThemeForCd(string dir, RushConfig config)
{
    var rushBgFile = FindRushBgFile(dir);
    var currentFile = Theme.ActiveRushBgFile;

    // No change — same .rushbg file (or still no .rushbg)
    if (string.Equals(rushBgFile, currentFile, StringComparison.Ordinal))
        return;

    // Background changed — full retheme
    ApplyTheme(config, dir);
}

/// <summary>
/// Walk up from dir to root looking for .rushbg file. Returns the path or null.
/// </summary>
static string? FindRushBgFile(string dir)
{
    var current = dir;
    while (!string.IsNullOrEmpty(current))
    {
        var candidate = Path.Combine(current, ".rushbg");
        if (File.Exists(candidate))
            return candidate;

        var parent = Path.GetDirectoryName(current);
        if (parent == current) break; // reached root
        current = parent;
    }
    return null;
}

// ── Redirection ─────────────────────────────────────────────────────
// ParseRedirection extracted to RedirectionParser.cs for testability.

static void WriteRedirectedOutput(IReadOnlyList<PSObject> results, RedirectInfo redirect)
{
    try
    {
        using var writer = redirect.Append
            ? File.AppendText(redirect.FilePath)
            : File.CreateText(redirect.FilePath);

        foreach (var result in results)
            writer.WriteLine(result.ToString());
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = Theme.Current.Error;
        Console.Error.WriteLine($"redirect: {ex.Message}");
        Console.ResetColor();
    }
}

// ── Error Correction ────────────────────────────────────────────────

static void ShowSuggestions(string cmd, CommandTranslator translator)
{
    var allCommands = translator.GetCommandNames().Concat(RushConstants.Builtins);

    var suggestions = allCommands
        .Where(c => LevenshteinDistance(cmd.ToLowerInvariant(), c.ToLowerInvariant()) <= 2)
        .OrderBy(c => LevenshteinDistance(cmd.ToLowerInvariant(), c.ToLowerInvariant()))
        .Take(3)
        .ToList();

    if (suggestions.Count > 0)
    {
        Console.ForegroundColor = Theme.Current.Muted;
        Console.Error.Write("  did you mean: ");
        Console.ForegroundColor = Theme.Current.Warning;
        Console.Error.WriteLine(string.Join(", ", suggestions));
        Console.ResetColor();
    }
}

static int LevenshteinDistance(string s, string t)
{
    int n = s.Length, m = t.Length;
    var d = new int[n + 1, m + 1];
    for (int i = 0; i <= n; i++) d[i, 0] = i;
    for (int j = 0; j <= m; j++) d[0, j] = j;
    for (int i = 1; i <= n; i++)
        for (int j = 1; j <= m; j++)
            d[i, j] = Math.Min(
                Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                d[i - 1, j - 1] + (s[i - 1] == t[j - 1] ? 0 : 1));
    return d[n, m];
}

// ── Display ─────────────────────────────────────────────────────────

static void ShowHistory(LineEditor editor)
{
    var history = editor.History;
    int start = Math.Max(0, history.Count - 50);
    for (int i = start; i < history.Count; i++)
    {
        Console.ForegroundColor = Theme.Current.Muted;
        Console.Write($"  {i + 1,4}  ");
        Console.ResetColor();
        Console.WriteLine(history[i]);
    }
}

static void ShowAliases(CommandTranslator translator)
{
    Console.ForegroundColor = Theme.Current.Banner;
    Console.WriteLine("Command Aliases:");
    Console.ResetColor();
    Console.WriteLine();

    foreach (var (alias, mapping) in translator.GetMappings().OrderBy(kv => kv.Key))
    {
        Console.ForegroundColor = Theme.Current.Accent;
        Console.Write($"  {alias,-12}");
        Console.ForegroundColor = Theme.Current.Muted;
        Console.Write(" → ");
        Console.ResetColor();
        Console.WriteLine(mapping.Cmdlet ?? "(native passthrough)");
    }
}

// ── Startup Tips ────────────────────────────────────────────────────

static string GetStartupTip(RushConfig config)
{
    var tips = new[]
    {
        // ── Navigation ──
        "cd -  -- jump back to previous directory",
        "pushd /tmp && popd  -- directory stack: push, then pop back",
        "cd ~/proj  -- tilde expands to home directory everywhere",

        // ── History ──
        "!!  -- repeat the last command",
        "!$  -- reuse the last argument from previous command",
        "!42  -- re-run command #42 from history",
        "Ctrl+R  -- search command history interactively",

        // ── Pipes & Filters ──
        "ps | where CPU > 10  -- filter objects by property",
        "ps | select ProcessName, CPU  -- pick specific columns",
        "ls | count  -- count items (also: sum, avg, min, max)",
        "ls | first 5  -- slice results (also: last, skip)",
        "ps | sort CPU | distinct  -- unique values (works on unsorted data)",
        "ps | .ProcessName  -- dot-notation extracts a single property",
        "ls | as json  -- format output as JSON (also: csv, table, list)",
        "cat data.json | from json  -- parse JSON input into objects",
        "ls | tee files.txt | count  -- save and pass through",

        // ── PATH Management ──
        "path  -- list all PATH entries with existence check",
        "path add ~/bin  -- add directory to PATH for this session",
        "path add --save --front /opt/bin  -- prepend to PATH and persist",
        "path add...end  -- add multiple directories (one per line, terminated by 'end')",
        "path rm /old/dir  -- remove a directory from PATH",
        "path rm...end  -- remove multiple directories (one per line)",
        "path dedupe  -- remove duplicate PATH entries (first occurrence wins)",
        "path edit  -- edit PATH in your $EDITOR (one entry per line)",

        // ── Settings ──
        "set  -- show all settings with descriptions and current values",
        "set --save editMode emacs  -- change a setting and persist it",
        "set -x  -- trace commands (shows + command before execution)",
        "set -e  -- stop on first error (like bash strict mode)",

        // ── Vi Mode ──
        "/pattern  -- search history forward (vi normal mode)",
        "?pattern  -- search history backward (vi normal mode)",
        "n/N  -- repeat last search forward/backward",

        // ── Completion ──
        "Tab  -- complete paths, commands, and flags",
        "Tab Tab  -- show all available completions",
        // ── Scripting ──
        "source file.rush  -- run a Rush script in current session",
        "$(ls | count)  -- command substitution: embed output inline",
        "export FOO=bar  -- set environment variable",
        "alias ll='ls -la'  -- define a command shortcut (--save to persist)",

        // ── Output ──
        "ls > files.txt  -- redirect output to file",
        "ls >> log.txt  -- append output to file",
        "cmd1 && cmd2  -- run cmd2 only if cmd1 succeeds",
        "cmd1 || echo 'failed'  -- run on failure only",
        "sleep 10 &  -- run in background (jobs/fg to manage)",

        // ── Help ──
        "alias --help  -- builtins have built-in help (also: path, cd, set, export)",
        "help xref  -- bash → Rush cross-reference (great for learning Rush)",
        "help  -- list all 28 help topics",

        // ── Config ──
        "~/.config/rush/config.json  -- all settings, commented with descriptions",
        "init  -- edit startup script in $EDITOR and reload",
        "~/.config/rush/secrets.rush  -- API keys & tokens (never synced)",
        "reload  -- reload config without restarting",
        "set --save showTips false  -- disable these startup tips",
    };

    // Rotate through tips based on day — each day shows a new tip
    var dayIndex = (int)(DateTime.Now.Ticks / TimeSpan.TicksPerDay) % tips.Length;
    return tips[dayIndex];
}

// ── Settings Display & Runtime Apply ────────────────────────────────

static void ShowAllSettings(ShellState state)
{
    Console.WriteLine();
    string? lastCategory = null;

    foreach (var s in RushConfig.AllSettings)
    {
        // Sync runtime flags into config for display
        var currentVal = s.Key switch
        {
            "stopOnError" => state.SetE.ToString().ToLowerInvariant(),
            "traceCommands" => state.SetX.ToString().ToLowerInvariant(),
            "pipefailMode" => state.SetPipefail.ToString().ToLowerInvariant(),
            _ => state.Config.GetValue(s.Key)
        };
        var isDefault = currentVal == s.DefaultValue;

        if (s.Category != lastCategory)
        {
            if (lastCategory != null) Console.WriteLine();
            Console.ForegroundColor = Theme.Current.Muted;
            Console.WriteLine($"  ── {s.Category} ──");
            Console.ResetColor();
            lastCategory = s.Category;
        }

        // Key
        Console.Write("  ");
        Console.ForegroundColor = Theme.Current.Banner;
        Console.Write(s.Key.PadRight(24));
        Console.ResetColor();

        // Value
        Console.ForegroundColor = isDefault ? Theme.Current.Muted : ConsoleColor.White;
        Console.Write(currentVal);
        Console.ResetColor();

        // Description (short)
        Console.ForegroundColor = Theme.Current.Muted;
        // Truncate description to first sentence
        var desc = s.Description;
        var periodPos = desc.IndexOf(". ");
        if (periodPos > 0) desc = desc[..(periodPos + 1)];
        Console.Write($"  {desc}");
        Console.ResetColor();
        Console.WriteLine();
    }

    // Aliases
    if (state.Config.Aliases.Count > 0)
    {
        Console.WriteLine();
        Console.ForegroundColor = Theme.Current.Muted;
        Console.WriteLine("  ── Aliases ──");
        Console.ResetColor();
        foreach (var (alias, cmd) in state.Config.Aliases)
        {
            Console.Write("  ");
            Console.ForegroundColor = Theme.Current.Banner;
            Console.Write(alias.PadRight(24));
            Console.ResetColor();
            Console.WriteLine(cmd);
        }
    }

    Console.WriteLine();
    Console.ForegroundColor = Theme.Current.Muted;
    Console.WriteLine("  set <key> <value>        change for this session");
    Console.WriteLine("  set --save <key> <value>  change and persist to config.json");
    Console.WriteLine($"  Config: {RushConfig.GetConfigPath()}");
    Console.ResetColor();
    Console.WriteLine();
}

static void ApplySettingToRuntime(string key, ShellState state)
{
    switch (key.ToLowerInvariant())
    {
        case "stoponerror": state.SetE = state.Config.StopOnError; break;
        case "tracecommands": state.SetX = state.Config.TraceCommands; break;
        case "pipefailmode": state.SetPipefail = state.Config.PipefailMode; break;
        case "editmode":
            if (state.LineEditor != null)
                state.LineEditor.Mode = state.Config.EditMode.Equals("emacs", StringComparison.OrdinalIgnoreCase)
                    ? EditMode.Emacs : EditMode.Vi;
            break;
        case "historysize":
            if (state.LineEditor != null)
                state.LineEditor.MaxHistory = state.Config.HistorySize;
            break;
        case "bg":
        case "theme":
        case "contrast":
            ApplyTheme(state.Config, Environment.CurrentDirectory);
            break;
    }
}

/// <summary>
/// Map a keyword to its help topic. Direct matches (file, dir, sql) pass through.
/// Keywords that don't match a topic directly are mapped via a lookup table.
/// Returns null for bare --help (shows topic list).
/// </summary>
static string? MapKeywordToHelpTopic(string? keyword)
{
    if (string.IsNullOrWhiteSpace(keyword)) return null;

    // Direct match — keyword IS a topic name (file, dir, time, sql, config, etc.)
    if (HelpCommand.GetTopic(keyword) != null)
        return keyword;

    // Keyword mapping for things that don't match topic names directly
    var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        // Control flow
        ["if"] = "control-flow", ["unless"] = "control-flow", ["case"] = "control-flow",
        ["match"] = "control-flow", ["else"] = "control-flow", ["elsif"] = "control-flow",
        // Loops
        ["for"] = "loops", ["while"] = "loops", ["until"] = "loops",
        ["loop"] = "loops", ["each"] = "loops", ["times"] = "loops",
        // Functions
        ["def"] = "functions", ["return"] = "functions", ["lambda"] = "functions",
        // Classes/enums
        ["class"] = "classes", ["enum"] = "enums",
        // Pipeline ops
        ["where"] = "pipeline-ops", ["select"] = "pipeline-ops",
        ["first"] = "pipeline-ops", ["last"] = "pipeline-ops",
        ["skip"] = "pipeline-ops", ["count"] = "pipeline-ops",
        ["sum"] = "pipeline-ops", ["avg"] = "pipeline-ops",
        ["min"] = "pipeline-ops", ["max"] = "pipeline-ops",
        ["distinct"] = "pipeline-ops", ["sort"] = "pipeline-ops",
        ["tee"] = "pipeline-ops", ["columns"] = "pipeline-ops",
        // Pipelines / format
        ["as"] = "pipelines", ["from"] = "pipelines", ["pipe"] = "pipelines",
        // Errors
        ["try"] = "errors", ["catch"] = "errors", ["raise"] = "errors",
        ["begin"] = "errors", ["rescue"] = "errors",
        // Builtins (each has its own help topic)
        ["alias"] = "alias", ["unalias"] = "alias",
        ["path"] = "path",
        ["export"] = "export", ["unset"] = "export",
        ["set"] = "set",
        ["cd"] = "cd", ["pushd"] = "cd", ["popd"] = "cd",
        ["init"] = "init", ["reload"] = "init",
        ["sync"] = "config",
        // Stdlib with dot notation
        ["File"] = "file", ["Dir"] = "dir", ["Time"] = "time",
        // Regex
        ["regex"] = "regex", ["=~"] = "regex",
        // Platforms
        ["macos"] = "platforms", ["linux"] = "platforms",
        ["windows"] = "platforms", ["win64"] = "platforms", ["win32"] = "platforms",
        // LLM mode
        ["llm"] = "llm-mode", ["lcat"] = "llm-mode", ["spool"] = "llm-mode",
        // MCP
        ["mcp"] = "mcp", ["mcp-ssh"] = "mcp", ["ssh"] = "mcp",
        // Known issues
        ["bugs"] = "known-issues", ["issues"] = "known-issues", ["workarounds"] = "known-issues",
        // Objectify
        ["objectify"] = "objectify",
        // Cross-reference
        ["xref"] = "xref", ["bash"] = "xref",
    };

    if (map.TryGetValue(keyword, out var topic))
        return topic;

    // Try prefix match on topic names (e.g., "pipe" → "pipelines")
    return HelpCommand.GetTopicNames()
        .FirstOrDefault(t => t.StartsWith(keyword, StringComparison.OrdinalIgnoreCase));
}

static void ShowHelp(LineEditor editor, CommandTranslator translator)
{
    Console.ForegroundColor = Theme.Current.Banner;
    Console.WriteLine("Rush — Unix-style commands on the PowerShell 7 engine");
    Console.ResetColor();
    Console.WriteLine();

    var commands = translator.GetCommandNames().ToList();
    Console.Write("  Commands: ");
    Console.WriteLine(string.Join(", ", commands));
    Console.WriteLine();

    Console.WriteLine("  Pipes:    ls | grep foo | head -5 | sort");
    Console.WriteLine("  Native:   grep, find, git, docker — all run natively");
    Console.WriteLine("  Objectify: netstat | objectify | where State == LISTEN");
    Console.WriteLine("  PS7:      Full PowerShell syntax works directly");
    Console.WriteLine("  Chain:    cmd1 && cmd2 || echo 'fallback'");
    Console.WriteLine("  Redirect: ls > files.txt   echo hi >> log.txt");
    Console.WriteLine("  Env vars: echo $HOME  ls $TMPDIR");
    Console.WriteLine("  Filter:   ps | where CPU > 10");
    Console.WriteLine("  Select:   ps | select ProcessName, CPU");
    Console.WriteLine("  Count:    ls | count       ps | sum WorkingSet64");
    Console.WriteLine("  Slice:    ls | first 5     ls | skip 3 | last 2");
    Console.WriteLine("  Format:   ps | as json    ps | as table");
    Console.WriteLine("  Parse:    cat data.json | from json");
    Console.WriteLine("  JSON:     json config.json | .settings");
    Console.WriteLine();

    Console.ForegroundColor = Theme.Current.Banner;
    Console.Write($"  Mode: {editor.Mode}");
    Console.ResetColor();
    Console.WriteLine("  (set vi | set emacs)");
    Console.WriteLine();

    Console.WriteLine("  Tab        — complete paths and commands");
    Console.WriteLine("  Ctrl+R     — reverse history search");
    Console.WriteLine("  cd -       — go to previous directory");
    Console.WriteLine("  !!         — repeat last command");
    Console.WriteLine("  !$         — last argument of previous command");
    Console.WriteLine("  .property  — dot-notation in pipes: ps | .ProcessName");
    Console.WriteLine("  where      — filter: ps | where CPU > 10  (also: <, =, !=, ~)");
    Console.WriteLine("  select     — pick properties: ps | select Id, ProcessName");
    Console.WriteLine("  as         — format output: ... | as json / csv / table / list");
    Console.WriteLine("  from       — parse input:   cat f.json | from json / csv");
    Console.WriteLine("  json       — read JSON: json config.json | .key");
    Console.WriteLine("  count      — count items: ls | count");
    Console.WriteLine("  first/last — slice: ls | first 5  (also: skip)");
    Console.WriteLine("  distinct   — unique values (works on unsorted data)");
    Console.WriteLine("  sum/avg    — math: ps | sum WorkingSet64  (also: min, max)");
    Console.WriteLine("  tee        — save & pass through: ls | tee out.txt | count");
    Console.WriteLine("  !N         — run Nth command from history");
    Console.WriteLine("  &&         — run next command only if previous succeeded");
    Console.WriteLine("  ||         — run next command only if previous failed");
    Console.WriteLine("  ;          — run next command regardless");
    Console.WriteLine("  > / >>     — redirect output to file (overwrite / append)");
    Console.WriteLine("  ~/path     — tilde expansion to home directory");
    Console.WriteLine("  $HOME      — environment variable expansion");
    Console.WriteLine("  export     — set env var: export FOO=bar (--save to persist)");
    Console.WriteLine("  unset      — remove env var: unset FOO");
    Console.WriteLine("  path       — manage PATH: path [add [--front] [--save] <dir> | rm [--save] <dir> | edit] (--name=VAR for other vars)");
    Console.WriteLine("  ai         — AI assistant: ai \"prompt\"  cat log | ai \"what went wrong?\"");
    Console.WriteLine("  sql        — query databases: sql @name \"SELECT ...\"  sql list | test | add");
    Console.WriteLine("  alias x=y  — define alias: alias ll='ls -la'");
    Console.WriteLine("  source     — run rush script: source file.rush");
    Console.WriteLine("  $(cmd)     — command substitution: echo $(ls | count)");
    Console.WriteLine("  <<EOF      — heredoc: cat <<EOF ... EOF");
    Console.WriteLine("  cmd &      — run in background: sleep 5 &");
    Console.WriteLine("  jobs       — list background jobs");
    Console.WriteLine("  fg N       — bring job N to foreground");
    Console.WriteLine("  kill %N    — kill background job N");
    Console.WriteLine("  Ctrl+C     — interrupt running command");
    Console.WriteLine("  \\          — line continuation (trailing backslash)");
    Console.WriteLine("  history    — show command history (persistent)");
    Console.WriteLine("  alias      — show command mappings (no args)");
    Console.WriteLine("  set        — show all settings (set <key> <value> to change, --save to persist)");
    Console.WriteLine("  set --secret KEY val — save API key/token to secrets.rush");
    Console.WriteLine("  set -e/-x  — stop on error / trace commands (bash-compatible)");
    Console.WriteLine("  sync       — config sync: sync init [github|ssh|path] | push | pull | status");
    Console.WriteLine("  init       — edit init.rush in $EDITOR, then reload");
    Console.WriteLine("  reload     — reload config");
    Console.WriteLine("  clear      — clear screen");
    Console.WriteLine("  help <topic> — detailed help (file, dir, strings, arrays, classes, loops, pipelines, sql, ...)");
    Console.WriteLine();

    if (editor.Mode == EditMode.Vi)
    {
        Console.ForegroundColor = Theme.Current.Muted;
        Console.WriteLine("  Vi: Esc=normal  i/a/A/I=insert  h/l=move  w/b/e=word");
        Console.WriteLine("      x=delete  D=del-to-end  C=change-to-end  f/F=find-char");
        Console.WriteLine("      0/$=begin/end  j/k=history  3w=count+motion");
        Console.ResetColor();
    }

    Console.WriteLine();
    Console.ForegroundColor = Theme.Current.Muted;
    Console.WriteLine($"  Config:  {RushConfig.GetConfigPath()}");
    Console.WriteLine($"  Startup: ~/.config/rush/init.rush");
    Console.WriteLine($"  Secrets: ~/.config/rush/secrets.rush (never synced)");
    Console.ResetColor();
}

// ── Non-interactive Mode ─────────────────────────────────────────────
static void RunNonInteractive(string command)
{
    var cfg = RushConfig.Load();
    ApplyTheme(cfg, Environment.CurrentDirectory, emitOsc: false);
    var ui = new RushHostUI();
    var h = new RushHost(ui);
    var ss = InitialSessionState.CreateDefault();
    using var rs = RunspaceFactory.CreateRunspace(h, ss);
    rs.Open();
    InjectRushEnvVars(rs, RushVersion.Full, false);
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
        ShimCoreutilsIfNeeded(rs, quiet: true);
        ShimDiffutilsIfNeeded(rs, quiet: true);
    }
    var objConfig = ObjectifyConfig.Load();
    var tr = new CommandTranslator(objConfig);
    var scriptEngine = new ScriptEngine(tr);

    // Handle builtins that need to work in -c mode.
    // These are REPL builtins that don't go through the transpiler.
    // ProcessCommand can't be used wholesale because it assumes interactive
    // stdio (native commands inherit the terminal). The -c path needs
    // captured output via PowerShell Invoke().
    var trimmedCommand = command.Trim();
    var firstWord = trimmedCommand.Split(' ', 2)[0].ToLowerInvariant();
    // Only intercept builtins for standalone single-line commands.
    // Chained (;, &&, ||) and multi-line commands go through the normal path
    // which handles export, cd, etc. via the transpiler or chain splitting.
    bool isStandalone = !command.Contains(';') && !command.Contains("&&")
        && !command.Contains("||") && !command.Contains('\n');

    if (isStandalone && firstWord == "help")
    {
        var helpArg = trimmedCommand.Length > 5 ? trimmedCommand[5..].Trim() : null;
        Console.WriteLine(HelpCommand.Execute(helpArg));
        return;
    }
    if (isStandalone && firstWord == "printf" && !CommandTranslator.HasUnquotedPipe(command))
    {
        var printfArgs = CommandTranslator.SplitCommandLine(trimmedCommand[7..]);
        if (printfArgs.Length >= 1)
        {
            var fmt = StripQuotes(printfArgs[0]);
            var fmtArgs = printfArgs.Skip(1).Select(StripQuotes).ToArray();
            Console.Write(PrintfFormat(fmt, fmtArgs));
        }
        return;
    }
    if (isStandalone && firstWord == "export" && trimmedCommand.Length > 7)
    {
        // export VAR=value → set in both PS and .NET env
        var exportBody = trimmedCommand[7..].Trim();
        var eqIdx = exportBody.IndexOf('=');
        if (eqIdx > 0)
        {
            var varName = exportBody[..eqIdx].Trim();
            var varValue = StripQuotes(exportBody[(eqIdx + 1)..].Trim());
            Environment.SetEnvironmentVariable(varName, varValue);
            using var ps = PowerShell.Create();
            ps.Runspace = rs;
            ps.AddScript($"$env:{varName} = '{varValue.Replace("'", "''")}'");
            ps.Invoke();
        }
        return;
    }
    if (isStandalone && firstWord == "mark")
    {
        var label = trimmedCommand.Length > 5 ? trimmedCommand[5..].Trim() : null;
        if (!string.IsNullOrEmpty(label))
            label = StripQuotes(label);
        EmitMark(string.IsNullOrEmpty(label) ? null : label);
        return;
    }
    if (isStandalone && (trimmedCommand == "---" || (trimmedCommand.StartsWith("---") && trimmedCommand.All(c => c == '-'))))
    {
        EmitMark(null);
        return;
    }
    if (isStandalone && firstWord == "path")
    {
        var pathArgs = trimmedCommand.Length > 4 ? trimmedCommand[4..].Trim() : "";
        HandlePathCommand(pathArgs, rs);
        return;
    }
    if (isStandalone && firstWord == "alias" && trimmedCommand.Length > 6)
    {
        var aliasBody = trimmedCommand[6..].Trim();
        var eqPos = aliasBody.IndexOf('=');
        if (eqPos > 0)
        {
            var aliasName = aliasBody[..eqPos].Trim();
            var aliasValue = StripQuotes(aliasBody[(eqPos + 1)..].Trim());
            tr.RegisterAlias(aliasName, aliasValue);
        }
        return;
    }

    // Check if the command contains Rush scripting syntax (multi-line blocks,
    // method chaining, assignments, etc.). If so, route through the transpiler
    // instead of the shell command path.
    // For multi-line scripts, check if ANY line is Rush syntax (not just the first).
    // TranspileFile handles mixed Rush/shell lines per-line, so this is safe.
    bool isRushScript;
    if (command.Contains('\n'))
    {
        var lines = command.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        isRushScript = lines.Any(l => scriptEngine.IsRushSyntax(l.Trim()));
    }
    else
    {
        isRushScript = scriptEngine.IsRushSyntax(command);
    }

    if (isRushScript)
    {
        var psCode = scriptEngine.TranspileFile(command);
        if (psCode != null)
        {
            using var rushPs = PowerShell.Create();
            rushPs.Runspace = rs;
            rushPs.AddScript(psCode);
            try
            {
                var results = rushPs.Invoke().Where(r => r != null).ToList();
                foreach (var r in results) Console.WriteLine(r);
                if (rushPs.HadErrors)
                {
                    OutputRenderer.RenderErrors(rushPs.Streams);
                    Environment.ExitCode = 1;
                }
            }
            catch (PipelineStoppedException)
            {
                Console.Error.WriteLine();
                Environment.ExitCode = 130;
            }
            catch (ActionPreferenceStopException ex)
            {
                Console.ForegroundColor = Theme.Current.Error;
                // Strip the verbose PS prefix: "The running command stopped because..."
                var innerMsg = ex.InnerException?.Message ?? ex.Message;
                // If still has the preference variable prefix, extract the actual error
                const string prefix = "The running command stopped because the preference variable";
                if (innerMsg.Contains(prefix))
                {
                    var colonIdx = innerMsg.IndexOf(": ", innerMsg.IndexOf("Stop:") + 1);
                    if (colonIdx > 0)
                        innerMsg = innerMsg[(colonIdx + 2)..].Trim();
                }
                Console.Error.WriteLine($"error: {innerMsg}");
                Console.ResetColor();
                Environment.ExitCode = 1;
            }
        }
        return;
    }

    // ── UNC path handling ──────────────────────────────────────
    var trimmedCmd = command.TrimStart();
    if (trimmedCmd.Contains("//ssh:") && UncHandler.TryHandle(trimmedCmd, out bool uncFailed))
    {
        if (uncFailed) Environment.ExitCode = 1;
        return;
    }

    // ── sql builtin (same dispatch as interactive REPL) ──
    if ((trimmedCmd.Equals("sql", StringComparison.OrdinalIgnoreCase) ||
         trimmedCmd.StartsWith("sql ", StringComparison.OrdinalIgnoreCase) ||
         trimmedCmd.StartsWith("sql\t", StringComparison.OrdinalIgnoreCase)) &&
        !CommandTranslator.HasUnquotedPipe(trimmedCmd))
    {
        var sqlArgs = trimmedCmd.Length > 3 ? trimmedCmd[3..].TrimStart() : "";
        if (!SqlCommand.Execute(sqlArgs))
            Environment.ExitCode = 1;
        return;
    }

    // ── cat builtin (same dispatch as interactive REPL) ──
    // Allow redirection (cat > file is a core use case), only fall through on pipe.
    // Also fall through for process substitution <(...) which needs expansion first.
    if ((trimmedCmd.Equals("cat", StringComparison.OrdinalIgnoreCase) ||
         trimmedCmd.StartsWith("cat ", StringComparison.OrdinalIgnoreCase) ||
         trimmedCmd.StartsWith("cat\t", StringComparison.OrdinalIgnoreCase)) &&
        !CommandTranslator.HasUnquotedPipe(trimmedCmd) &&
        !trimmedCmd.Contains("<("))
    {
        var (catCmd, catRedirect, catStdin, _) = RedirectionParser.Parse(trimmedCmd);
        string? catStdinContent = null;
        if (catStdin != null)
        {
            try { catStdinContent = File.ReadAllText(catStdin.FilePath); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"cat: {ex.Message}");
                Environment.ExitCode = 1;
                return;
            }
        }
        var catArgs = catCmd.Length > 3 ? catCmd[3..].TrimStart() : "";
        if (!CatCommand.Execute(catArgs, catRedirect, catStdinContent))
            Environment.ExitCode = 1;
        return;
    }

    // Split chain operators (&&, ||, ;) — same as interactive REPL
    var (chainSegments, chainOps) = SplitChainOperators(command);
    bool lastFailed = false;
    List<string>? allProcTempFiles = null;

    for (int ci = 0; ci < chainSegments.Count; ci++)
    {
        var segment = chainSegments[ci].Trim();
        if (string.IsNullOrEmpty(segment)) continue;

        // Check chain operator conditions
        if (ci > 0)
        {
            var op = chainOps[ci - 1];
            if (op == "&&" && lastFailed) continue;
            if (op == "||" && !lastFailed) continue;
        }

        // Full expansion pipeline (shared with interactive mode)
        List<string>? procTempFiles;
        (segment, procTempFiles) = RunExpansionPipeline(segment, tr, rs);
        if (procTempFiles != null)
        {
            allProcTempFiles ??= new List<string>();
            allProcTempFiles.AddRange(procTempFiles);
        }

        // Parse redirections before translation
        var (cmdPart, redirect, stdinRedirect, _) = RedirectionParser.Parse(segment);
        var translated = tr.Translate(cmdPart) ?? cmdPart;
        var commandToRun = translated;

        // Stdin redirection — pipe file content into command
        if (stdinRedirect != null)
        {
            try
            {
                var stdinContent = File.ReadAllText(stdinRedirect.FilePath);
                commandToRun = $"@'\n{stdinContent}\n'@ | {commandToRun}";
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = Theme.Current.Error;
                Console.Error.WriteLine($"redirect: {ex.Message}");
                Console.ResetColor();
                lastFailed = true;
                continue;
            }
        }

        // Quoted executable path — prepend & for PowerShell invocation
        if (commandToRun.StartsWith('"') || commandToRun.StartsWith('\''))
        {
            var cmdTrimmed = commandToRun.TrimStart();
            char q = cmdTrimmed[0];
            var closeIdx = cmdTrimmed.IndexOf(q, 1);
            if (closeIdx > 0)
            {
                var path = cmdTrimmed[1..closeIdx];
                if (path.Contains('/') || path.Contains('\\'))
                    commandToRun = "& " + commandToRun;
            }
        }

        // Stderr merge — inject PS redirect
        if (redirect?.MergeStderr == true)
            commandToRun += " 2>&1";

        using var ps = PowerShell.Create();
        ps.Runspace = rs;
        ps.AddScript(commandToRun);

        PowerShell? activePs = ps;
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            try { activePs?.Stop(); } catch { }
        };

        try
        {
            var results = ps.Invoke().ToList();
            if (results.Count > 0)
            {
                if (redirect != null)
                    WriteRedirectedOutput(results, redirect);
                else
                    OutputRenderer.Render(results.ToArray());
            }
            if (ps.Streams.Error.Count > 0) OutputRenderer.RenderErrors(ps.Streams);
            lastFailed = ps.HadErrors;
        }
        catch (PipelineStoppedException)
        {
            Console.Error.WriteLine();
            lastFailed = true;
        }
    }

    Environment.ExitCode = lastFailed ? 1 : 0;

    // Clean up process substitution temp files
    if (allProcTempFiles != null)
        foreach (var f in allProcTempFiles)
            try { File.Delete(f); } catch { }
}

// ── Types ────────────────────────────────────────────────────────────
// RedirectInfo and StdinInfo moved to RedirectionParser.cs

/// <summary>
/// Shared state bundle for the command dispatch pipeline.
/// Used by ProcessCommand, ExecuteTranspiledBlock, and the REPL loop.
/// Interactive-only fields (LineEditor, JobManager, etc.) are null in script contexts.
/// </summary>
// ── Windows Console ANSI Support ────────────────────────────────────

/// <summary>
/// Enable ANSI virtual terminal processing on Windows console.
/// Required for escape codes (\x1b[J, colors, cursor movement) to work
/// instead of appearing as literal text. No-op on non-Windows.
/// </summary>
static void EnableWindowsAnsi()
{
    if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
    try
    {
        var stdout = GetStdHandle(-11); // STD_OUTPUT_HANDLE
        if (GetConsoleMode(stdout, out uint mode))
        {
            mode |= 0x0004; // ENABLE_VIRTUAL_TERMINAL_PROCESSING
            SetConsoleMode(stdout, mode);
        }
        var stderr = GetStdHandle(-12); // STD_ERROR_HANDLE
        if (GetConsoleMode(stderr, out uint errMode))
        {
            errMode |= 0x0004;
            SetConsoleMode(stderr, errMode);
        }
    }
    catch { } // Best-effort — some environments may not support it
}

[DllImport("kernel32.dll", SetLastError = true)]
static extern IntPtr GetStdHandle(int nStdHandle);

[DllImport("kernel32.dll", SetLastError = true)]
static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

[DllImport("kernel32.dll", SetLastError = true)]
static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

/// <summary>
/// Open a file, URL, or directory with the system default handler.
/// Returns true on failure, false on success (matches lastSegmentFailed convention).
/// </summary>
/// <summary>
/// Extract a shell redirect (>> or >) from the end of a Rush output statement
/// (puts, print, warn). Returns the PowerShell redirect string, or null if no
/// redirect found. Modifies input to remove the redirect portion.
/// </summary>
static string? ExtractRushOutputRedirect(ref string input)
{
    var trimmed = input.TrimStart();
    // Only for Rush output builtins
    if (!trimmed.StartsWith("puts ", StringComparison.OrdinalIgnoreCase) &&
        !trimmed.StartsWith("print ", StringComparison.OrdinalIgnoreCase) &&
        !trimmed.StartsWith("warn ", StringComparison.OrdinalIgnoreCase))
        return null;

    // Find >> or > outside of quotes
    bool inSingle = false, inDouble = false;
    for (int i = 0; i < input.Length; i++)
    {
        if (input[i] == '\'' && !inDouble) inSingle = !inSingle;
        if (input[i] == '"' && !inSingle) inDouble = !inDouble;
        if (inSingle || inDouble) continue;

        if (i + 1 < input.Length && input[i] == '>' && input[i + 1] == '>')
        {
            var file = input[(i + 2)..].Trim();
            input = input[..i].TrimEnd();
            return $" | Add-Content -Path '{file}'";
        }
        if (input[i] == '>' && (i + 1 >= input.Length || input[i + 1] != '>'))
        {
            var file = input[(i + 1)..].Trim();
            input = input[..i].TrimEnd();
            return $" | Set-Content -Path '{file}'";
        }
    }
    return null;
}

/// <summary>
/// Extract inline env vars (VAR=value VAR2=value2 command) from the front of input.
/// Returns saved values for restoration, or null if no inline vars found.
/// Modifies input to contain just the command portion.
/// </summary>
static Dictionary<string, string?>? ApplyInlineEnvVars(ref string input)
{
    if (string.IsNullOrEmpty(input)) return null;

    // Pattern: one or more IDENTIFIER=VALUE (no spaces around =) followed by a command
    // Stop when we hit a token that doesn't match IDENTIFIER=VALUE
    var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length < 2) return null; // Need at least VAR=value + command

    var envVars = new List<(string key, string val)>();
    int commandStart = 0;

    for (int i = 0; i < parts.Length; i++)
    {
        var eqIdx = parts[i].IndexOf('=');
        if (eqIdx <= 0) break; // Not a VAR=value token — this is the command

        var key = parts[i][..eqIdx];
        // Key must be a valid env var name (letters, digits, underscore, starts with letter/underscore)
        if (!IsValidEnvVarName(key)) break;

        var val = parts[i][(eqIdx + 1)..];
        // Strip quotes from value
        if (val.Length >= 2 &&
            ((val[0] == '"' && val[^1] == '"') || (val[0] == '\'' && val[^1] == '\'')))
            val = val[1..^1];

        envVars.Add((key, val));
        commandStart = i + 1;
    }

    if (envVars.Count == 0 || commandStart >= parts.Length) return null;

    // Set env vars and save originals
    var saved = new Dictionary<string, string?>();
    foreach (var (key, val) in envVars)
    {
        saved[key] = Environment.GetEnvironmentVariable(key);
        Environment.SetEnvironmentVariable(key, val);
    }

    // Remove the env var assignments from input, leaving just the command
    input = string.Join(' ', parts[commandStart..]);
    return saved;
}

static void RestoreInlineEnvVars(Dictionary<string, string?>? saved)
{
    if (saved == null) return;
    foreach (var (key, val) in saved)
        Environment.SetEnvironmentVariable(key, val);
}

static bool IsValidEnvVarName(string name)
{
    if (string.IsNullOrEmpty(name)) return false;
    if (!char.IsLetter(name[0]) && name[0] != '_') return false;
    for (int i = 1; i < name.Length; i++)
    {
        if (!char.IsLetterOrDigit(name[i]) && name[i] != '_') return false;
    }
    return true;
}

static void EmitMark(string? label)
{
    var width = 65;
    try { width = Math.Max(40, Console.WindowWidth - 2); } catch { }
    var line = new string('═', width);
    if (!string.IsNullOrEmpty(label))
    {
        var prefix = "═══ ";
        var suffix = " " + new string('═', Math.Max(3, width - prefix.Length - label.Length - 1));
        line = prefix + label + suffix;
    }
    Console.ForegroundColor = Theme.Current.Muted;
    Console.WriteLine(line);
    Console.ResetColor();
}

static bool OpenWithSystem(string target)
{
    try
    {
        ProcessStartInfo psi;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Windows: Start-Process via cmd to handle URLs and files
            psi = new ProcessStartInfo("cmd", $"/c start \"\" \"{target}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            };
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            psi = new ProcessStartInfo("/usr/bin/open")
            {
                UseShellExecute = false,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add(target);
        }
        else
        {
            // Linux: xdg-open, suppress stderr (it's noisy)
            psi = new ProcessStartInfo("xdg-open")
            {
                UseShellExecute = false,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add(target);
        }

        var proc = Process.Start(psi);
        if (proc == null)
        {
            Console.ForegroundColor = Theme.Current.Error;
            Console.Error.WriteLine($"o: failed to open '{target}'");
            Console.ResetColor();
            return true;
        }

        // Don't wait for GUI apps — fire and forget
        // But drain stderr to avoid buffer deadlock
        _ = proc.StandardError.ReadToEndAsync();
        return false;
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = Theme.Current.Error;
        Console.Error.WriteLine($"o: {ex.Message}");
        Console.ResetColor();
        return true;
    }
}

static class RushConstants
{
    /// <summary>
    /// Authoritative list of all Rush shell builtins. Used by which/type and suggestions.
    /// </summary>
    public static readonly HashSet<string> Builtins = new(StringComparer.OrdinalIgnoreCase)
    {
        "exit", "quit", "help", "history", "alias", "unalias", "reload", "init",
        "clear", "cd", "export", "unset", "source", "jobs", "fg", "bg", "wait",
        "sync", "pushd", "popd", "dirs", "printf", "read", "exec", "trap",
        "path", "ai", "sql", "set", "which", "type", "setbg", "o", "mark"
    };
}

class ShellState
{
    public required System.Management.Automation.Runspaces.Runspace Runspace { get; init; }
    public required Rush.CommandTranslator Translator { get; init; }
    public required Rush.ScriptEngine ScriptEngine { get; init; }
    public required Rush.RushConfig Config { get; set; }

    // Interactive-only (null in scripts)
    public Rush.LineEditor? LineEditor { get; init; }
    public Rush.JobManager? JobManager { get; init; }
    public Rush.Prompt? Prompt { get; init; }
    public Rush.RushHost? Host { get; init; }
    public Rush.TabCompleter? TabCompleter { get; init; }

    // Mutable state
    public bool SetE { get; set; }
    public bool SetX { get; set; }
    public bool SetPipefail { get; set; }
    public string? PreviousDirectory { get; set; }
    public System.Collections.Generic.Stack<string> DirStack { get; set; } = new();
    public System.Collections.Generic.Dictionary<string, string> Traps { get; set; } = new(System.StringComparer.OrdinalIgnoreCase);
    public System.Threading.CancellationTokenSource BuiltinCts { get; set; } = new();
    public System.Management.Automation.PowerShell? RunningPs { get; set; }

    public bool IsInteractive => LineEditor != null;

    /// <summary>
    /// True when running init.rush / startup scripts. Causes path add to silently
    /// skip non-existent directories (common for cross-platform configs).
    /// </summary>
    public bool IsStartupScript { get; set; }
}

using System.Diagnostics;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Reflection;
using System.Runtime.InteropServices;
using Rush;

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

// ── LLM Mode Detection ─────────────────────────────────────────────
bool llmMode = args.Contains("--llm");
string? inheritPath = null;
var inheritIdx = Array.IndexOf(args, "--inherit");
if (inheritIdx >= 0 && inheritIdx + 1 < args.Length)
    inheritPath = args[inheritIdx + 1];
args = args.Where(a => a != "--llm" && a != "--inherit" && a != inheritPath).ToArray();

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
        Console.WriteLine("  rush --login         Start as login shell");
        Console.WriteLine("  rush --version       Show version");
        Console.WriteLine("  rush --help          Show this help");
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
    var tr = new CommandTranslator();
    var se = new ScriptEngine(tr);

    // Apply config aliases to translator (null editor — no LineEditor in LLM mode)
    llmConfig.Apply(null, tr);

    // Set Rush built-in variables ($os, $hostname, $rush_version, $is_login_shell)
    InjectRushEnvVars(rs, Version, isLoginShell);

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
                ReloadState.Restore(saved, rs, ref prevDir, ref dummyE, ref dummyX, ref dummyPf);
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

// ── Theme (detect terminal background for contrast-aware colors) ─────
Theme.Initialize(config.GetThemeOverride());

// ── Banner ───────────────────────────────────────────────────────────
Console.ForegroundColor = Theme.Current.Banner;
Console.WriteLine($"rush v{Version} — a modern-day warrior");
Console.ForegroundColor = Theme.Current.Muted;
Console.WriteLine($"PowerShell 7 engine | {config.EditMode} mode | Tab | Ctrl+R");
Console.ResetColor();

if (config.ShowTips)
{
    var tip = GetStartupTip(config);
    Console.WriteLine();
    Console.ForegroundColor = Theme.Current.Muted;
    Console.Write("Tip: ");
    Console.ResetColor();
    Console.WriteLine(tip);
}

// ── Initialize PowerShell Engine ─────────────────────────────────────
var hostUI = new RushHostUI();
var host = new RushHost(hostUI);
var iss = InitialSessionState.CreateDefault();
var runspace = RunspaceFactory.CreateRunspace(host, iss);
runspace.Open();

// ── Initialize Components ────────────────────────────────────────────
var jobManager = new Rush.JobManager(iss, host);
var translator = new CommandTranslator();
var scriptEngine = new ScriptEngine(translator);
var lineEditor = new LineEditor();
var prompt = new Prompt();
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

// ── Run Startup Scripts ─────────────────────────────────────────────
RunStartupScripts(runspace, scriptEngine);

// ── Capture State Baseline (for reload --hard) ──────────────────────
ReloadState.CaptureBaseline(runspace);

// ── State ────────────────────────────────────────────────────────────
string? previousDirectory = null;
var dirStack = new Stack<string>();
PowerShell? runningPs = null;
var traps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
bool setE = cfgStopOnError;   // set -e: exit on error (from config or set -e)
bool setX = cfgTrace;         // set -x: trace commands (from config or set -x)
bool setPipefail = cfgPipefail; // set -o pipefail (from config or set -o pipefail)
bool signalExit = false; // Set by SIGHUP/SIGTERM to trigger graceful exit

// ── Hot-Reload State Restoration ─────────────────────────────────────
if (isResuming)
{
    try
    {
        var saved = ReloadState.Load();
        if (saved != null)
        {
            ReloadState.Restore(saved, runspace, ref previousDirectory, ref setE, ref setX, ref setPipefail);
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
    if (runningPs != null)
    {
        try { runningPs.Stop(); } // Interrupt running PowerShell pipeline
        catch { }
    }
    // If at the prompt (runningPs == null), LineEditor already handles Ctrl+C
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
        try { runningPs?.Stop(); } catch { }
    });

    // SIGTERM — system shutdown or kill request
    sigtermReg = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
    {
        ctx.Cancel = true;
        signalExit = true;
        try { runningPs?.Stop(); } catch { }
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
    input = ReadContinuationLines(input);

    input = input.Trim();
    if (string.IsNullOrEmpty(input)) continue;

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

            // TODO: Status line will show contextual hints (esc v → $EDITOR, etc.)
            // See docs/status-line-design.md

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

        var psCode = scriptEngine.TranspileLine(input);
        if (psCode != null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using var ps = System.Management.Automation.PowerShell.Create();
                ps.Runspace = runspace;
                ps.AddScript(psCode);

                runningPs = ps;
                List<System.Management.Automation.PSObject> results;
                try
                {
                    results = ps.Invoke().ToList();
                }
                catch (System.Management.Automation.PipelineStoppedException)
                {
                    Console.WriteLine();
                    sw.Stop();
                    runningPs = null;
                    prompt.SetLastCommandFailed(true);
                    continue;
                }
                finally
                {
                    runningPs = null;
                }

                sw.Stop();

                if (ps.HadErrors)
                {
                    OutputRenderer.RenderErrors(ps.Streams);
                    prompt.SetLastCommandFailed(true);
                }
                else
                {
                    prompt.SetLastCommandFailed(false);

                    // Track variable assignments for type-aware dot-completion (V2)
                    TrackVariableAssignment(input, tabCompleter);
                }

                if (results.Count > 0)
                    OutputRenderer.Render(results.ToArray());

                if (host.ShouldExit) break;
            }
            catch (System.Management.Automation.PipelineStoppedException)
            {
                Console.WriteLine();
                prompt.SetLastCommandFailed(true);
            }
            catch (Exception ex)
            {
                prompt.SetLastCommandFailed(true);
                var msg = ex.InnerException?.Message ?? ex.Message;
                Console.ForegroundColor = Theme.Current.Error;
                Console.Error.WriteLine($"error: {msg}");
                Console.ResetColor();
            }

            if (sw.ElapsedMilliseconds > 500)
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

        lineEditor.SaveHistory();
        continue;
    }

    // ── Heredoc Detection ───────────────────────────────────────────
    string? heredocContent = null;
    input = DetectAndReadHeredoc(input, out heredocContent);
    if (heredocContent != null)
    {
        // Pipe heredoc content into the command as a PowerShell here-string
        input = $"@'\n{heredocContent}\n'@ | {input}";
    }

    // ── Bang Expansion ──────────────────────────────────────────────
    if (input.Contains('!'))
    {
        bool expanded = false;

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

    // ── Brace Expansion ──────────────────────────────────────────
    input = ExpandBraces(input);

    // ── Tilde Expansion ────────────────────────────────────────────
    input = ExpandTilde(input);

    // ── Environment Variable Expansion ──────────────────────────────
    input = ExpandEnvVars(input);

    // ── Arithmetic Expansion $((expr)) ──────────────────────────────
    input = ExpandArithmetic(input, runspace);

    // ── Process Substitution <(cmd) ────────────────────────────────
    List<string>? procSubTempFiles = null;
    if (input.Contains("<("))
        (input, procSubTempFiles) = ExpandProcessSubstitution(input, translator, runspace);

    // ── Command Substitution $(...) and `...` ────────────────────────
    input = ExpandCommandSubstitution(input, translator, runspace);

    // ── Split on Chain Operators (&&, ||, ;) ──────────────────────────
    var (chainSegments, chainOps) = SplitChainOperators(input);

    bool lastSegmentFailed = false;
    int lastExitCode = 0;
    bool shouldExit = false;

    for (int ci = 0; ci < chainSegments.Count; ci++)
    {
        var segment = chainSegments[ci].Trim();
        if (string.IsNullOrEmpty(segment)) continue;

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

        // ── Try Built-in Commands ───────────────────────────────────
        // Job control builtins
        if (segment.Equals("jobs", StringComparison.OrdinalIgnoreCase))
        {
            var allJobs = jobManager.GetJobs();
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
            lastSegmentFailed = false;
            continue;
        }

        if (segment.StartsWith("fg ", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("fg", StringComparison.OrdinalIgnoreCase))
        {
            var idStr = segment.Length > 3 ? segment[3..].Trim().TrimStart('%') : "";
            int fgId;
            if (!int.TryParse(idStr, out fgId))
            {
                var recent = jobManager.GetJobs()
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

            var job = jobManager.GetJob(fgId);
            if (job == null)
            {
                Console.ForegroundColor = Theme.Current.Error;
                Console.Error.WriteLine($"fg: no such job: {fgId}");
                Console.ResetColor();
                lastSegmentFailed = true;
                continue;
            }

            // Bring background job to foreground — wait for it
            var fgResults = jobManager.WaitForJob(fgId);
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
            var idStr = segment[6..].Trim();
            if (int.TryParse(idStr, out var killId))
            {
                if (jobManager.KillJob(killId))
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
            if (segment.Equals("wait", StringComparison.OrdinalIgnoreCase))
            {
                // Wait for ALL running jobs
                var running = jobManager.GetJobs().Where(j => !j.IsCompleted).ToList();
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
                        var results = jobManager.WaitForJob(job.JobId);
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
                    var results = jobManager.WaitForJob(waitId);
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

        if (segment.Equals("help", StringComparison.OrdinalIgnoreCase))
        {
            ShowHelp(lineEditor, translator);
            lastSegmentFailed = false;
            continue;
        }

        // ── which — check builtins before falling through to Get-Command ──
        if (segment.StartsWith("which ", StringComparison.OrdinalIgnoreCase) ||
            segment.StartsWith("type ", StringComparison.OrdinalIgnoreCase))
        {
            var whichCmd = segment.IndexOf(' ') is var sp && sp > 0 ? segment[(sp + 1)..].Trim() : "";
            if (!string.IsNullOrEmpty(whichCmd))
            {
                var rushBuiltins = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "exit", "quit", "help", "history", "alias", "unalias", "reload", "init",
                    "clear", "cd", "export", "unset", "source", "jobs", "fg", "bg", "wait",
                    "sync", "pushd", "popd", "dirs", "printf", "read", "exec", "trap",
                    "path", "ai", "set", "which", "type"
                };

                if (rushBuiltins.Contains(whichCmd))
                {
                    Console.WriteLine($"{whichCmd}: rush builtin");
                    lastSegmentFailed = false;
                }
                else if (config.Aliases.ContainsKey(whichCmd))
                {
                    Console.WriteLine($"{whichCmd}: aliased to '{config.Aliases[whichCmd]}'");
                    lastSegmentFailed = false;
                }
                else
                {
                    // Fall through to Get-Command for external commands
                    using var ps = PowerShell.Create();
                    ps.Runspace = runspace;
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
                lineEditor.Mode = EditMode.Vi;
                config.EditMode = "vi";
                Console.ForegroundColor = Theme.Current.Muted;
                Console.WriteLine("  editMode = vi");
                Console.ResetColor();
            }
            else if (setArg.Equals("emacs", StringComparison.OrdinalIgnoreCase))
            {
                lineEditor.Mode = EditMode.Emacs;
                config.EditMode = "emacs";
                Console.ForegroundColor = Theme.Current.Muted;
                Console.WriteLine("  editMode = emacs");
                Console.ResetColor();
            }
            else if (setArg == "-e") { setE = true; config.StopOnError = true; Console.ForegroundColor = Theme.Current.Muted; Console.WriteLine("  stopOnError = true"); Console.ResetColor(); }
            else if (setArg == "+e") { setE = false; config.StopOnError = false; Console.ForegroundColor = Theme.Current.Muted; Console.WriteLine("  stopOnError = false"); Console.ResetColor(); }
            else if (setArg == "-x") { setX = true; config.TraceCommands = true; Console.ForegroundColor = Theme.Current.Muted; Console.WriteLine("  traceCommands = true"); Console.ResetColor(); }
            else if (setArg == "+x") { setX = false; config.TraceCommands = false; Console.ForegroundColor = Theme.Current.Muted; Console.WriteLine("  traceCommands = false"); Console.ResetColor(); }
            else if (setArg == "-o pipefail") { setPipefail = true; config.PipefailMode = true; Console.ForegroundColor = Theme.Current.Muted; Console.WriteLine("  pipefailMode = true"); Console.ResetColor(); }
            else if (setArg == "+o pipefail") { setPipefail = false; config.PipefailMode = false; Console.ForegroundColor = Theme.Current.Muted; Console.WriteLine("  pipefailMode = false"); Console.ResetColor(); }
            else if (string.IsNullOrEmpty(setArg))
            {
                // set (no args) — show all settings grouped by category
                ShowAllSettings(config, setE, setX, setPipefail);
            }
            else if (setArg.StartsWith("--save ", StringComparison.OrdinalIgnoreCase))
            {
                // set --save key value — change and persist
                var rest = setArg[7..].Trim();
                var parts = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2)
                {
                    if (config.SetValue(parts[0], parts[1]))
                    {
                        ApplySettingToRuntime(parts[0], config, ref setE, ref setX, ref setPipefail, lineEditor);
                        config.Save();
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
                        var val = config.GetValue(info.Key);
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
                    // Set value for session
                    if (config.SetValue(parts[0], parts[1]))
                    {
                        ApplySettingToRuntime(parts[0], config, ref setE, ref setX, ref setPipefail, lineEditor);
                        Console.ForegroundColor = Theme.Current.Muted;
                        Console.WriteLine($"  {parts[0]} = {parts[1]}");
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
            }
            if (!lastSegmentFailed) lastSegmentFailed = false;
            continue;
        }

        if (segment.Equals("history -c", StringComparison.OrdinalIgnoreCase))
        {
            lineEditor.ClearHistory();
            Console.ForegroundColor = Theme.Current.Muted;
            Console.WriteLine("  history cleared");
            Console.ResetColor();
            lastSegmentFailed = false;
            continue;
        }

        if (segment.Equals("history", StringComparison.OrdinalIgnoreCase))
        {
            ShowHistory(lineEditor);
            lastSegmentFailed = false;
            continue;
        }

        if (segment.Equals("alias", StringComparison.OrdinalIgnoreCase))
        {
            ShowAliases(translator);
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
                    var state = ReloadState.Capture(runspace, config, previousDirectory, setE, setX, setPipefail);
                    ReloadState.Save(state);
                    lineEditor.SaveHistory();

                    var currentBinary = Environment.ProcessPath ?? "rush";
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
                config = RushConfig.Load();
                var (reloadE, reloadX, reloadPf) = config.Apply(lineEditor, translator);
                setE = reloadE; setX = reloadX; setPipefail = reloadPf;
                Theme.Initialize(config.GetThemeOverride());
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
                    RunStartupRushFile(runspace, scriptEngine, "init.rush");
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

        // ── Interactive alias definition ────────────────────────────
        // alias ll='ls -la'  or  alias ll=ls -la
        if (segment.StartsWith("alias ", StringComparison.OrdinalIgnoreCase) && segment.Contains('='))
        {
            var aliasBody = segment[6..].Trim();
            var eqPos = aliasBody.IndexOf('=');
            if (eqPos > 0)
            {
                var aliasName = aliasBody[..eqPos].Trim();
                var aliasValue = aliasBody[(eqPos + 1)..].Trim();
                // Strip surrounding quotes
                if ((aliasValue.StartsWith('\'') && aliasValue.EndsWith('\'')) ||
                    (aliasValue.StartsWith('"') && aliasValue.EndsWith('"')))
                    aliasValue = aliasValue[1..^1];

                translator.RegisterAlias(aliasName, aliasValue);
                config.Aliases[aliasName] = aliasValue;
                config.Save();
                Console.ForegroundColor = Theme.Current.Muted;
                Console.WriteLine($"  alias {aliasName} → {aliasValue}");
                Console.ResetColor();
            }
            lastSegmentFailed = false;
            continue;
        }

        if (segment.StartsWith("unalias ", StringComparison.OrdinalIgnoreCase))
        {
            var name = segment[8..].Trim();
            if (config.Aliases.Remove(name))
            {
                config.Save();
                Console.ForegroundColor = Theme.Current.Muted;
                Console.WriteLine($"  unalias {name} (effective after reload)");
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
                ps.Runspace = runspace;
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
            ps.Runspace = runspace;
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
            var value = Console.ReadLine() ?? "";

            using var readPs = PowerShell.Create();
            readPs.Runspace = runspace;
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
                lineEditor.SaveHistory();
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
                traps[signal] = trapCmd;
                Console.ForegroundColor = Theme.Current.Muted;
                Console.WriteLine($"  trap {signal} → {trapCmd}");
                Console.ResetColor();
            }
            else if (trapArgs.Length == 1 && trapArgs[0] == "-l")
            {
                foreach (var (sig, cmd) in traps)
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
                        var psCode = scriptEngine.TranspileFile(scriptSource);
                        if (psCode != null)
                        {
                            using var ps = PowerShell.Create();
                            ps.Runspace = runspace;
                            ps.AddScript(psCode);
                            var scriptResults = ps.Invoke().ToList();
                            if (scriptResults.Count > 0) OutputRenderer.Render(scriptResults);
                            if (ps.HadErrors) OutputRenderer.RenderErrors(ps.Streams);

                            // Reset ErrorActionPreference — TranspileFile sets it to 'Stop'
                            using var reset = PowerShell.Create();
                            reset.Runspace = runspace;
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
                            var scriptTranslated = translator.Translate(scriptLine) ?? scriptLine;
                            using var ps = PowerShell.Create();
                            ps.Runspace = runspace;
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
            var currentDir = GetRunspaceDir(runspace);
            if (segment.Equals("pushd", StringComparison.OrdinalIgnoreCase))
            {
                // No arg: swap current with top of stack
                if (dirStack.Count == 0)
                {
                    Console.ForegroundColor = Theme.Current.Error;
                    Console.Error.WriteLine("pushd: no other directory");
                    Console.ResetColor();
                    lastSegmentFailed = true;
                }
                else
                {
                    var top = dirStack.Pop();
                    if (currentDir != null) dirStack.Push(currentDir);
                    var (f, _) = HandleCd(runspace, $"cd {top}", null);
                    lastSegmentFailed = f;
                    if (!f) PrintDirStack(runspace, dirStack);
                }
            }
            else
            {
                // pushd <dir>: push current, cd to target
                var target = segment[6..].Trim();
                if (currentDir != null) dirStack.Push(currentDir);
                var (f, _) = HandleCd(runspace, $"cd {target}", null);
                if (f) { if (currentDir != null) dirStack.Pop(); } // undo push on failure
                else PrintDirStack(runspace, dirStack);
                lastSegmentFailed = f;
            }
            continue;
        }

        if (segment.Equals("popd", StringComparison.OrdinalIgnoreCase))
        {
            if (dirStack.Count == 0)
            {
                Console.ForegroundColor = Theme.Current.Error;
                Console.Error.WriteLine("popd: directory stack empty");
                Console.ResetColor();
                lastSegmentFailed = true;
            }
            else
            {
                var target = dirStack.Pop();
                var (f, _) = HandleCd(runspace, $"cd {target}", null);
                lastSegmentFailed = f;
                if (!f) PrintDirStack(runspace, dirStack);
            }
            continue;
        }

        if (segment.Equals("dirs", StringComparison.OrdinalIgnoreCase) ||
            segment.StartsWith("dirs ", StringComparison.OrdinalIgnoreCase))
        {
            bool verbose = segment.Contains("-v", StringComparison.OrdinalIgnoreCase);
            var currentDir = GetRunspaceDir(runspace) ?? ".";
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            if (verbose)
            {
                string Shorten(string p) => p.StartsWith(home) ? "~" + p[home.Length..] : p;
                Console.WriteLine($"  0  {Shorten(currentDir)}");
                int idx = 1;
                foreach (var d in dirStack)
                    Console.WriteLine($"  {idx++}  {Shorten(d)}");
            }
            else
            {
                PrintDirStack(runspace, dirStack);
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

        // ── path: PATH management ─────────────────────────────────────
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
            var (aiCmd, _, aiStdin) = RedirectionParser.Parse(segment);
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
                    var llm = new LlmMode(runspace, scriptEngine, translator, Version);
                    var (aiOk, _) = AiCommand.ExecuteAgentAsync(aiArgs, llm, config,
                        lineEditor.History, aiCts.Token).GetAwaiter().GetResult();
                    lastSegmentFailed = !aiOk;
                }
                else
                {
                    var (aiOk, _) = AiCommand.ExecuteAsync(aiArgs, pipedInput, config,
                        lineEditor.History, aiCts.Token).GetAwaiter().GetResult();
                    lastSegmentFailed = !aiOk;
                }
            }
            finally
            {
                Console.CancelKeyPress -= aiCancel;
            }
            continue;
        }

        if (segment.Equals("path", StringComparison.OrdinalIgnoreCase) ||
            segment.StartsWith("path ", StringComparison.OrdinalIgnoreCase))
        {
            var pathArgs = segment.Length > 4 ? segment[4..].Trim() : "";
            lastSegmentFailed = HandlePathCommand(pathArgs, runspace);
            continue;
        }

        // ── cd (with - support) ─────────────────────────────────────
        if (segment.StartsWith("cd ", StringComparison.OrdinalIgnoreCase) || segment == "cd")
        {
            var (cdFailed, newPrev) = HandleCd(runspace, segment, previousDirectory);
            if (!cdFailed && newPrev != null) previousDirectory = newPrev;
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

        // ── ls builtin (when not piped) ─────────────────────────
        // Direct .NET file enumeration with multi-column/long format.
        // When piped (ls | grep), falls through to Get-ChildItem path below.
        if ((segment.Equals("ls", StringComparison.OrdinalIgnoreCase) ||
             segment.StartsWith("ls ", StringComparison.OrdinalIgnoreCase) ||
             segment.StartsWith("ls\t", StringComparison.OrdinalIgnoreCase)) &&
            !segment.Contains('|'))
        {
            var lsArgs = segment.Length > 2 ? segment[2..].TrimStart() : "";
            lastSegmentFailed = !FileListCommand.Execute(lsArgs);
            lastExitCode = lastSegmentFailed ? 1 : 0;
            continue;
        }

        // ── set -x trace ─────────────────────────────────────────
        if (setX)
        {
            Console.ForegroundColor = Theme.Current.Muted;
            Console.Error.WriteLine($"+ {segment}");
            Console.ResetColor();
        }

        // ── Parse Redirection ─────────────────────────────────────
        var (cmdPart, redirect, stdinRedirect) = RedirectionParser.Parse(segment);

        // ── Translate & Execute (with timing) ────────────────────────
        var translated = translator.Translate(cmdPart);
        var commandToRun = translated ?? cmdPart;

        // Glob expansion: only for passthrough (native) commands
        if (translated == null)
            commandToRun = ExpandGlobs(commandToRun);

        // ── Background Job ─────────────────────────────────────────
        if (runInBackground)
        {
            var jobId = jobManager.StartBackground(segment, commandToRun);
            Console.ForegroundColor = Theme.Current.Muted;
            Console.WriteLine($"  [{jobId}] started: {segment}");
            Console.ResetColor();
            lastSegmentFailed = false;
            continue;
        }

        // ── Native Command Execution ─────────────────────────────
        // Native commands (not translated to PowerShell cmdlets) run directly
        // with inherited stdio, giving them real TTY access. This handles
        // shells, TUI apps, REPLs, and any program — no allowlist needed.
        // PowerShell is only used when we need its pipeline/capture features.
        bool needsPowerShell = translated != null
            || redirect != null
            || stdinRedirect != null
            || CommandTranslator.HasUnquotedPipe(cmdPart);

        // ── Pipe-to-AI interception ── cmd | ai "prompt" ──────────
        if (CommandTranslator.HasUnquotedPipe(cmdPart))
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
                    ps.Runspace = runspace;
                    // Translate the prefix pipeline
                    var preTrans = translator.Translate(prePipeline) ?? prePipeline;
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
                    var (aiPipeOk, _) = AiCommand.ExecuteAsync(aiPipeArgs, pipedContent, config,
                        lineEditor.History, aiPipeCts.Token).GetAwaiter().GetResult();
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
            var nativeExitCode = RunInteractive(commandToRun, translator);
            lastSegmentFailed = nativeExitCode != 0;
            lastExitCode = nativeExitCode;
            sw2.Stop();
            if (config.ShowTiming && sw2.Elapsed.TotalSeconds >= 0.5)
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

        // ── Stderr Merge ── inject PS redirect so errors flow into output
        if (redirect?.MergeStderr == true)
            commandToRun += " 2>&1";

        try
        {
            using var ps = PowerShell.Create();
            ps.Runspace = runspace;
            ps.AddScript(commandToRun);

            // Track for Ctrl+C interruption
            runningPs = ps;
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
                runningPs = null;
                lastSegmentFailed = true;
                lastExitCode = 130;
                continue;
            }
            finally
            {
                runningPs = null;
            }

            sw.Stop();

            if (ps.HadErrors)
            {
                OutputRenderer.RenderErrors(ps.Streams);
                lastSegmentFailed = true;
                // Try to get the actual exit code from $LASTEXITCODE (set by native commands)
                try
                {
                    var lec = runspace.SessionStateProxy.GetVariable("LASTEXITCODE");
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
                        if (target != null) ShowSuggestions(target, translator);
                    }
                }
            }
            else
            {
                lastSegmentFailed = false;
                lastExitCode = 0;

                // set -o pipefail: treat native command pipeline failures as errors
                // even when PowerShell doesn't report HadErrors
                if (setPipefail)
                {
                    try
                    {
                        var lec = runspace.SessionStateProxy.GetVariable("LASTEXITCODE");
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
            if (host.ShouldExit)
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
        if (config.ShowTiming && sw.ElapsedMilliseconds > 500)
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

    if (shouldExit || signalExit) break;

    // Clean up process substitution temp files
    if (procSubTempFiles != null)
        foreach (var f in procSubTempFiles)
            try { File.Delete(f); } catch { }

    // Normalize exit code: ensure consistency with failure flag
    if (lastSegmentFailed && lastExitCode == 0) lastExitCode = 1;
    if (!lastSegmentFailed) lastExitCode = 0;

    // set -e: exit on error
    if (setE && lastSegmentFailed)
    {
        Console.ForegroundColor = Theme.Current.Error;
        Console.Error.WriteLine($"  exit (set -e): command failed with status {lastExitCode}");
        Console.ResetColor();
        break;
    }

    prompt.SetLastCommandFailed(lastSegmentFailed, lastExitCode);
    lineEditor.SaveHistory();
}

// ── Graceful Exit ────────────────────────────────────────────────────

// Fire EXIT trap if registered
if (traps.TryGetValue("EXIT", out var exitTrap))
{
    var exitTranslated = translator.Translate(exitTrap) ?? exitTrap;
    using var exitPs = PowerShell.Create();
    exitPs.Runspace = runspace;
    exitPs.AddScript(exitTranslated);
    try { exitPs.Invoke(); } catch { }
}

jobManager.Dispose();
lineEditor.SaveHistory();
Console.Write("\x1b[0 q"); // Reset cursor shape
if (host.ShouldExit)
    Environment.ExitCode = host.ExitCode;
if (!signalExit) // Don't write to a dead terminal (SIGHUP)
    Console.WriteLine("bye.");

// Dispose signal registrations
sigtstpReg?.Dispose();
sighupReg?.Dispose();
sigtermReg?.Dispose();

// ═══════════════════════════════════════════════════════════════════
// Helper Methods
// ═══════════════════════════════════════════════════════════════════

// ── Startup Scripts ─────────────────────────────────────────────────

/// <summary>
/// Execute a .rush startup script through the scripting engine (transpiled).
/// </summary>
static void RunStartupRushFile(Runspace runspace, ScriptEngine engine, string filename)
{
    var path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "rush", filename);

    if (!File.Exists(path)) return;

    try
    {
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
            // for script error handling, but we don't want it leaking into
            // the interactive runspace where it produces verbose error messages.
            using var reset = PowerShell.Create();
            reset.Runspace = runspace;
            reset.AddScript("$ErrorActionPreference = 'Continue'");
            reset.Invoke();
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
static void RunStartupScripts(Runspace runspace, ScriptEngine engine)
{
    RunStartupRushFile(runspace, engine, "init.rush");
    RunStartupRushFile(runspace, engine, "secrets.rush");
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
    ps.AddScript($"$os = '{osName}'; $hostname = '{Environment.MachineName.ToLowerInvariant()}'; $rush_version = '{version}'; $is_login_shell = {loginVal}");
    ps.Invoke();
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
        Theme.Initialize(null);

        var source = File.ReadAllText(path);
        var iss = InitialSessionState.CreateDefault();
        var hostUI = new RushHostUI();
        var host = new RushHost(hostUI);
        var runspace = RunspaceFactory.CreateRunspace(host, iss);
        runspace.Open();

        // Inject Rush environment variables (same as REPL startup)
        {
            using var initPs = PowerShell.Create();
            initPs.Runspace = runspace;
            var osName = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                System.Runtime.InteropServices.OSPlatform.OSX) ? "macos" :
                System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                System.Runtime.InteropServices.OSPlatform.Linux) ? "linux" : "windows";
            initPs.AddScript($"$os = '{osName}'; $hostname = '{Environment.MachineName.ToLowerInvariant()}'; $rush_version = '{RushVersion.Full}'");
            initPs.Invoke();
        }

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

        var translator = new CommandTranslator();
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
static int RunInteractive(string command, CommandTranslator? translator = null)
{
    try
    {
        var firstSpace = command.IndexOf(' ');
        var exe = firstSpace > 0 ? command[..firstSpace] : command;
        var args = firstSpace > 0 ? command[(firstSpace + 1)..].Trim() : "";

        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
        };

        var proc = Process.Start(psi);
        if (proc == null) return 1;

        proc.WaitForExit();
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
/// Built-in path variable management: list, add, remove, edit.
/// Supports --name=VARNAME to target any colon-separated env var (default: PATH).
/// Returns true if the command failed.
/// </summary>
static bool HandlePathCommand(string args, Runspace runspace)
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
    var entries = currentValue.Split(':').Where(e => !string.IsNullOrEmpty(e)).ToList();

    // ── path / path check — list entries with existence indicators ──
    if (string.IsNullOrEmpty(args) || args.Equals("check", StringComparison.OrdinalIgnoreCase))
    {
        Console.ForegroundColor = Theme.Current.Muted;
        Console.WriteLine($"  {varName} entries:");
        Console.ResetColor();
        for (int i = 0; i < entries.Count; i++)
        {
            var dir = entries[i];
            var exists = Directory.Exists(dir);
            var num = (i + 1).ToString().PadLeft(3);

            Console.ForegroundColor = Theme.Current.Muted;
            Console.Write($"  {num}  ");
            Console.ForegroundColor = exists ? Theme.Current.PromptSuccess : Theme.Current.Warning;
            Console.Write(exists ? "✓" : "✗");
            Console.Write("  ");
            Console.ForegroundColor = exists ? ConsoleColor.White : Theme.Current.Muted;
            Console.WriteLine(dir);
        }
        Console.ResetColor();
        return false;
    }

    // ── path add [--front] [--save] <dir> ───────────────────────────
    if (args.StartsWith("add ", StringComparison.OrdinalIgnoreCase) ||
        args.StartsWith("add\t", StringComparison.OrdinalIgnoreCase))
    {
        var addArgs = args[4..].Trim();
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

        if (string.IsNullOrEmpty(addArgs))
        {
            Console.ForegroundColor = Theme.Current.Error;
            Console.Error.WriteLine("  path add: missing directory argument");
            Console.ResetColor();
            return true;
        }

        // Strip quotes and expand tilde
        var dir = StripQuotes(addArgs);
        var expandedDir = ExpandTildePath(dir);

        // Normalize: strip trailing slash
        expandedDir = expandedDir.TrimEnd('/');

        // Check for duplicates
        if (entries.Contains(expandedDir))
        {
            Console.ForegroundColor = Theme.Current.Warning;
            Console.WriteLine($"  note: {expandedDir} already in {varName}");
            Console.ResetColor();
        }

        // Check existence
        if (!Directory.Exists(expandedDir))
        {
            Console.ForegroundColor = Theme.Current.Warning;
            Console.WriteLine($"  note: {expandedDir} does not exist (adding anyway)");
            Console.ResetColor();
        }

        // Add to variable
        string newValue;
        if (front)
        {
            newValue = expandedDir + ":" + currentValue;
        }
        else
        {
            newValue = currentValue + ":" + expandedDir;
        }
        SetEnvVar(varName, newValue, runspace);

        Console.ForegroundColor = Theme.Current.Muted;
        Console.Write(front ? "  prepended: " : "  appended:  ");
        Console.ResetColor();
        Console.WriteLine(expandedDir);

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

    // ── path rm [--save] <dir> ──────────────────────────────────────
    if (args.StartsWith("rm ", StringComparison.OrdinalIgnoreCase) ||
        args.StartsWith("remove ", StringComparison.OrdinalIgnoreCase))
    {
        var rmStart = args.IndexOf(' ') + 1;
        var rmArgs = args[rmStart..].Trim();
        bool save = false;
        if (rmArgs.StartsWith("--save ", StringComparison.OrdinalIgnoreCase) ||
            rmArgs.StartsWith("--save\t", StringComparison.OrdinalIgnoreCase))
        {
            save = true;
            rmArgs = rmArgs[7..].Trim();
        }

        var dir = StripQuotes(rmArgs);
        var expandedDir = ExpandTildePath(dir).TrimEnd('/');

        var before = entries.Count;
        entries.RemoveAll(e => e.TrimEnd('/').Equals(expandedDir, StringComparison.Ordinal));

        if (entries.Count == before)
        {
            Console.ForegroundColor = Theme.Current.Warning;
            Console.Error.WriteLine($"  not found in {varName}: {expandedDir}");
            Console.ResetColor();
            return true;
        }

        var newValue = string.Join(":", entries);
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

            // Read back, filter blanks and comments
            var newEntries = File.ReadAllLines(tempFile)
                .Select(l => l.Trim())
                .Where(l => !string.IsNullOrEmpty(l) && !l.StartsWith('#'))
                .ToList();

            var newValue = string.Join(":", newEntries);
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
    Console.Error.WriteLine("  usage: path [--name=VAR] [add [--front] [--save] <dir> | rm [--save] <dir> | edit | check]");
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
            bool atWordStart = i == 0 || input[i - 1] is ' ' or '\t' or '=';
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

static string ReadContinuationLines(string input)
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
            var next = Console.ReadLine();
            if (next == null) break;
            sb.Append(next);
            continue;
        }

        // Unclosed quotes → continue reading
        if (HasUnclosedQuote(current))
        {
            Console.ForegroundColor = Theme.Current.Muted;
            Console.Write(Prompt.Continuation);
            Console.ResetColor();
            var next = Console.ReadLine();
            if (next == null) break;
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
            var next = Console.ReadLine();
            if (next == null) break;
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
            expanded.AddRange(matches);
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

    // ~user expansion (e.g., ~mark → /Users/mark or /home/mark)
    if (path.StartsWith('~') && path.Length > 1 && char.IsLetterOrDigit(path[1]))
    {
        int end = 1;
        while (end < path.Length && path[end] is not '/' and not ' ') end++;
        var username = path[1..end];
        var rest = end < path.Length ? path[end..] : "";
        var usersDir = OperatingSystem.IsMacOS() ? "/Users" : "/home";
        var candidate = Path.Combine(usersDir, username);
        if (Directory.Exists(candidate))
            path = candidate + rest;
    }

    // CDPATH: if path is relative and doesn't exist in cwd, search CDPATH
    if (!Path.IsPathRooted(path) && !Directory.Exists(path))
    {
        var cdpath = Environment.GetEnvironmentVariable("CDPATH");
        if (cdpath != null)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            foreach (var rawDir in cdpath.Split(':'))
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

        // Sync .NET process working directory so native commands (ls builtin,
        // Process.Start for TUI programs) see the correct current directory.
        try { Environment.CurrentDirectory = Path.GetFullPath(path); }
        catch { /* ignore — Set-Location succeeded, this is best-effort */ }

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
    var builtins = new[] { "exit", "quit", "help", "history", "alias", "unalias", "reload", "init", "clear", "cd", "export", "unset", "source", "jobs", "fg", "bg", "wait", "sync", "pushd", "popd", "dirs", "printf", "read", "exec", "trap", "path", "ai" };
    var allCommands = translator.GetCommandNames().Concat(builtins);

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
        "cd -  ← jump back to previous directory",
        "pushd /tmp && popd  ← directory stack: push, then pop back",
        "cd ~/proj  ← tilde expands to home directory everywhere",

        // ── History ──
        "!!  ← repeat the last command",
        "!$  ← reuse the last argument from previous command",
        "!42  ← re-run command #42 from history",
        "Ctrl+R  ← search command history interactively",

        // ── Pipes & Filters ──
        "ps | where CPU > 10  ← filter objects by property",
        "ps | select ProcessName, CPU  ← pick specific columns",
        "ls | count  ← count items (also: sum, avg, min, max)",
        "ls | first 5  ← slice results (also: last, skip)",
        "ls | distinct  ← unique values (works on unsorted data)",
        "ps | .ProcessName  ← dot-notation extracts a single property",
        "ls | as json  ← format output as JSON (also: csv, table, list)",
        "cat data.json | from json  ← parse JSON input into objects",
        "ls | tee files.txt | count  ← save and pass through",

        // ── PATH Management ──
        "path  ← list all PATH entries with existence check (✓/✗)",
        "path add ~/bin  ← add directory to PATH for this session",
        "path add --save --front /opt/bin  ← prepend to PATH and persist",
        "path edit  ← edit PATH in your $EDITOR (one entry per line)",
        "path rm /old/dir  ← remove a directory from PATH",

        // ── Settings ──
        "set  ← show all settings with descriptions and current values",
        "set --save editMode emacs  ← change a setting and persist it",
        "set -x  ← trace commands (shows + command before execution)",
        "set -e  ← stop on first error (like bash strict mode)",

        // ── Vi Mode ──
        "/pattern  ← search history forward (vi normal mode)",
        "?pattern  ← search history backward (vi normal mode)",
        "n/N  ← repeat last search forward/backward",

        // ── Completion ──
        "Tab  ← complete paths, commands, and flags",
        "Tab Tab  ← show all available completions",
        // ── Scripting ──
        "source file.rush  ← run a Rush script in current session",
        "$(ls | count)  ← command substitution: embed output inline",
        "export FOO=bar  ← set environment variable",
        "alias ll='ls -la'  ← define a command shortcut",

        // ── Output ──
        "ls > files.txt  ← redirect output to file",
        "ls >> log.txt  ← append output to file",
        "cmd1 && cmd2  ← run cmd2 only if cmd1 succeeds",
        "cmd1 || echo 'failed'  ← run on failure only",
        "sleep 10 &  ← run in background (jobs/fg to manage)",

        // ── Config ──
        "~/.config/rush/config.json  ← all settings, commented with descriptions",
        "init  ← edit startup script in $EDITOR and reload",
        "~/.config/rush/secrets.rush  ← API keys & tokens (never synced)",
        "reload  ← reload config without restarting",
        "set --save showTips false  ← disable these startup tips",
    };

    // Rotate through tips based on day — each day shows a new tip
    var dayIndex = (int)(DateTime.Now.Ticks / TimeSpan.TicksPerDay) % tips.Length;
    return tips[dayIndex];
}

// ── Settings Display & Runtime Apply ────────────────────────────────

static void ShowAllSettings(RushConfig config, bool setE, bool setX, bool setPipefail)
{
    Console.WriteLine();
    string? lastCategory = null;

    foreach (var s in RushConfig.AllSettings)
    {
        // Sync runtime flags into config for display
        var currentVal = s.Key switch
        {
            "stopOnError" => setE.ToString().ToLowerInvariant(),
            "traceCommands" => setX.ToString().ToLowerInvariant(),
            "pipefailMode" => setPipefail.ToString().ToLowerInvariant(),
            _ => config.GetValue(s.Key)
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
    if (config.Aliases.Count > 0)
    {
        Console.WriteLine();
        Console.ForegroundColor = Theme.Current.Muted;
        Console.WriteLine("  ── Aliases ──");
        Console.ResetColor();
        foreach (var (alias, cmd) in config.Aliases)
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

static void ApplySettingToRuntime(string key, RushConfig config, ref bool setE, ref bool setX, ref bool setPipefail, LineEditor lineEditor)
{
    switch (key.ToLowerInvariant())
    {
        case "stoponerror": setE = config.StopOnError; break;
        case "tracecommands": setX = config.TraceCommands; break;
        case "pipefailmode": setPipefail = config.PipefailMode; break;
        case "editmode":
            lineEditor.Mode = config.EditMode.Equals("emacs", StringComparison.OrdinalIgnoreCase)
                ? EditMode.Emacs : EditMode.Vi;
            break;
        case "historysize":
            lineEditor.MaxHistory = config.HistorySize;
            break;
    }
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
    Console.WriteLine("  Flags:    ls -la → Get-ChildItem -Force");
    Console.WriteLine("  Native:   git, docker, kubectl just work");
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
    Theme.Initialize(cfg.GetThemeOverride());
    var ui = new RushHostUI();
    var h = new RushHost(ui);
    var ss = InitialSessionState.CreateDefault();
    using var rs = RunspaceFactory.CreateRunspace(h, ss);
    rs.Open();
    var tr = new CommandTranslator();
    var scriptEngine = new ScriptEngine(tr);

    // Check if the command contains Rush scripting syntax (multi-line blocks,
    // method chaining, assignments, etc.). If so, route through the transpiler
    // instead of the shell command path.
    bool isRushScript = command.Contains('\n')
        ? scriptEngine.IsRushSyntax(command.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim())
        : scriptEngine.IsRushSyntax(command);

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

    // ── ls builtin (same dispatch as interactive REPL) ──
    var trimmedCmd = command.TrimStart();
    if ((trimmedCmd.Equals("ls", StringComparison.OrdinalIgnoreCase) ||
         trimmedCmd.StartsWith("ls ", StringComparison.OrdinalIgnoreCase) ||
         trimmedCmd.StartsWith("ls\t", StringComparison.OrdinalIgnoreCase)) &&
        !CommandTranslator.HasUnquotedPipe(trimmedCmd) &&
        !CommandTranslator.HasUnquotedRedirection(trimmedCmd))
    {
        var lsArgs = trimmedCmd.Length > 2 ? trimmedCmd[2..].TrimStart() : "";
        if (!FileListCommand.Execute(lsArgs))
            Environment.ExitCode = 1;
        return;
    }

    // Full expansion pipeline (same order as interactive mode)
    command = ExpandBraces(command);
    command = ExpandTilde(command);
    command = ExpandEnvVars(command);
    command = ExpandArithmetic(command, rs);
    var (procExpanded, procTempFiles) = ExpandProcessSubstitution(command, tr, rs);
    command = procExpanded;
    command = ExpandCommandSubstitution(command, tr, rs);

    // Parse redirections before translation
    var (cmdPart, redirect, stdinRedirect) = RedirectionParser.Parse(command);
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
            Environment.ExitCode = 1;
            return;
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
        Environment.ExitCode = ps.HadErrors ? 1 : 0;
    }
    catch (PipelineStoppedException)
    {
        Console.Error.WriteLine();
        Environment.ExitCode = 130; // Standard Ctrl+C exit code
    }
    finally
    {
        // Clean up process substitution temp files
        if (procTempFiles != null)
            foreach (var f in procTempFiles)
                try { File.Delete(f); } catch { }
    }
}

// ── Types ────────────────────────────────────────────────────────────
// RedirectInfo and StdinInfo moved to RedirectionParser.cs

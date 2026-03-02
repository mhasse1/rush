using System.Diagnostics;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Runtime.InteropServices;
using Rush;

const string Version = "1.1.0";

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
    // Script file execution: rush script.rush
    if (args[0].EndsWith(".rush", StringComparison.OrdinalIgnoreCase) && File.Exists(args[0]))
    {
        RunScriptFile(args[0]);
        return;
    }
}

// ── Load Config ──────────────────────────────────────────────────────
var config = RushConfig.Load();

// ── Theme (detect terminal background for contrast-aware colors) ─────
Theme.Initialize(config.GetThemeOverride());

// ── Banner ───────────────────────────────────────────────────────────
Console.ForegroundColor = Theme.Current.Banner;
Console.WriteLine($"rush v{Version} — a modern-day warrior");
Console.ForegroundColor = Theme.Current.Muted;
Console.WriteLine($"PowerShell 7 engine | {config.EditMode} mode | Tab | Ctrl+R | autosuggestions");
Console.ResetColor();

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
var tabCompleter = new TabCompleter(runspace, translator);
var highlighter = new SyntaxHighlighter(translator);

// Apply config (sets edit mode, custom aliases)
config.Apply(lineEditor, translator);

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
// Expose os, hostname, rush_version as PS variables so Rush scripts can use them
{
    using var ps = PowerShell.Create();
    ps.Runspace = runspace;
    var osName = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
        System.Runtime.InteropServices.OSPlatform.OSX) ? "macos" :
        System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
        System.Runtime.InteropServices.OSPlatform.Linux) ? "linux" : "windows";
    ps.AddScript($"$os = '{osName}'; $hostname = '{Environment.MachineName.ToLowerInvariant()}'; $rush_version = '{Version}'");
    ps.Invoke();
}

// ── Run Startup Scripts ─────────────────────────────────────────────
RunConfigRush(runspace, scriptEngine);
RunStartupScript(runspace);

// ── State ────────────────────────────────────────────────────────────
string? previousDirectory = null;
var dirStack = new Stack<string>();
PowerShell? runningPs = null;
bool sigtstpReceived = false;

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

// Ctrl+Z (SIGTSTP) — suspend foreground job instead of stopping Rush
PosixSignalRegistration? sigtstpReg = null;
if (!OperatingSystem.IsWindows())
{
    sigtstpReg = PosixSignalRegistration.Create(PosixSignal.SIGTSTP, _ =>
    {
        sigtstpReceived = true;
        if (runningPs != null)
        {
            try { runningPs.Stop(); } // Will throw PipelineStoppedException
            catch { }
        }
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
    if (input == null) break; // EOF (Ctrl+D)

    // ── Continuation Lines (trailing \, unclosed quotes/brackets) ──
    input = ReadContinuationLines(input);

    input = input.Trim();
    if (string.IsNullOrEmpty(input)) continue;

    // ── Rush Scripting Language Triage ─────────────────────────────
    // Check if input is Rush syntax (if/for/def/assignment/method chains).
    // If so, accumulate multi-line blocks, parse, transpile to PS, and execute.
    if (scriptEngine.IsRushSyntax(input))
    {
        // Accumulate multi-line blocks (if/end, def/end, etc.)
        while (scriptEngine.IsIncomplete(input))
        {
            Console.ForegroundColor = Theme.Current.Muted;
            Console.Write(Prompt.Continuation);
            Console.ResetColor();
            var continuation = lineEditor.ReadLine();
            if (continuation == null) break;
            input += "\n" + continuation;
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

    // ── Tilde Expansion ────────────────────────────────────────────
    input = ExpandTilde(input);

    // ── Environment Variable Expansion ──────────────────────────────
    input = ExpandEnvVars(input);

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
                    var status = job.IsSuspended ? "suspended"
                               : job.IsCompleted ? "done" : "running";
                    var elapsed = DateTime.Now - job.StartTime;
                    Console.ForegroundColor = Theme.Current.Muted;
                    Console.Write($"  [{job.JobId}] ");
                    Console.ForegroundColor = job.IsSuspended ? Theme.Current.Warning
                                            : job.IsCompleted ? Theme.Current.Accent
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
                // Default to most recent non-completed job
                var recent = jobManager.GetJobs()
                    .Where(j => !j.IsCompleted || j.IsSuspended)
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

            if (job.IsSuspended && job.SuspendedProcess != null)
            {
                // Resume suspended native process
                Console.ForegroundColor = Theme.Current.Muted;
                Console.WriteLine($"  [{job.JobId}] resumed: {job.Command}");
                Console.ResetColor();
                Posix.SendCONT(job.SuspendedProcess.Id);
                job.IsSuspended = false;

                while (!job.SuspendedProcess.HasExited)
                {
                    if (sigtstpReceived)
                    {
                        sigtstpReceived = false;
                        job.IsSuspended = true;
                        Console.ForegroundColor = Theme.Current.Muted;
                        Console.WriteLine($"\n  [{job.JobId}] suspended: {job.Command}");
                        Console.ResetColor();
                        break;
                    }
                    job.SuspendedProcess.WaitForExit(100);
                }

                if (!job.IsSuspended)
                {
                    lastExitCode = job.SuspendedProcess.ExitCode;
                    lastSegmentFailed = lastExitCode != 0;
                    job.Reported = true;
                }
            }
            else if (job.IsSuspended && job.SuspendedCommand != null)
            {
                // Re-run suspended PS pipeline in foreground
                Console.ForegroundColor = Theme.Current.Muted;
                Console.WriteLine($"  [{job.JobId}] resumed: {job.Command}");
                Console.ResetColor();
                using var fgPs = PowerShell.Create();
                fgPs.Runspace = runspace;
                fgPs.AddScript(job.SuspendedCommand);
                runningPs = fgPs;
                try
                {
                    var fgResults = fgPs.Invoke().ToList();
                    if (fgResults.Count > 0) OutputRenderer.Render(fgResults.ToArray());
                    if (fgPs.HadErrors) OutputRenderer.RenderErrors(fgPs.Streams);
                }
                catch (PipelineStoppedException) { Console.WriteLine(); }
                finally { runningPs = null; }
                job.IsSuspended = false;
                job.Reported = true;
            }
            else
            {
                // Running background job — wait for it
                var fgResults = jobManager.WaitForJob(fgId);
                if (fgResults != null && fgResults.Count > 0)
                    OutputRenderer.Render(fgResults);
            }
            continue;
        }

        // ── bg ──────────────────────────────────────────────────────
        if (segment.StartsWith("bg ", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("bg", StringComparison.OrdinalIgnoreCase))
        {
            var idStr = segment.Length > 3 ? segment[3..].Trim().TrimStart('%') : "";
            int bgId;
            if (!int.TryParse(idStr, out bgId))
            {
                var recent = jobManager.GetJobs()
                    .Where(j => j.IsSuspended)
                    .OrderByDescending(j => j.JobId)
                    .FirstOrDefault();
                if (recent != null) bgId = recent.JobId;
                else
                {
                    Console.ForegroundColor = Theme.Current.Error;
                    Console.Error.WriteLine("bg: no suspended job");
                    Console.ResetColor();
                    lastSegmentFailed = true;
                    continue;
                }
            }

            var bgJob = jobManager.GetJob(bgId);
            if (bgJob == null || !bgJob.IsSuspended)
            {
                Console.ForegroundColor = Theme.Current.Error;
                Console.Error.WriteLine($"bg: job {bgId} not suspended");
                Console.ResetColor();
                lastSegmentFailed = true;
                continue;
            }

            if (bgJob.SuspendedProcess != null)
            {
                Posix.SendCONT(bgJob.SuspendedProcess.Id);
                bgJob.IsSuspended = false;
            }
            else if (bgJob.SuspendedCommand != null)
            {
                jobManager.StartBackground(bgJob.Command, bgJob.SuspendedCommand);
                bgJob.IsSuspended = false;
                bgJob.Reported = true;
            }

            Console.ForegroundColor = Theme.Current.Muted;
            Console.WriteLine($"  [{bgJob.JobId}] running: {bgJob.Command}");
            Console.ResetColor();
            lastSegmentFailed = false;
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

        if (segment.Equals("set vi", StringComparison.OrdinalIgnoreCase))
        {
            lineEditor.Mode = EditMode.Vi;
            Console.WriteLine("Switched to vi mode");
            lastSegmentFailed = false;
            continue;
        }
        if (segment.Equals("set emacs", StringComparison.OrdinalIgnoreCase))
        {
            lineEditor.Mode = EditMode.Emacs;
            Console.WriteLine("Switched to emacs mode");
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

        if (segment.Equals("reload", StringComparison.OrdinalIgnoreCase))
        {
            config = RushConfig.Load();
            config.Apply(lineEditor, translator);
            Theme.Initialize(config.GetThemeOverride());
            Console.WriteLine("Config reloaded");
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
                Console.ForegroundColor = Theme.Current.Muted;
                Console.WriteLine($"  alias {aliasName} → {aliasValue}");
                Console.ResetColor();
            }
            lastSegmentFailed = false;
            continue;
        }

        // ── export: set environment variable ────────────────────────
        // export FOO=bar  or  export FOO="bar baz"
        if (segment.StartsWith("export ", StringComparison.OrdinalIgnoreCase) && segment.Contains('='))
        {
            var exportBody = segment[7..].Trim();
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

        // ── Parse Redirection ─────────────────────────────────────
        var (cmdPart, redirect, stdinRedirect) = ParseRedirection(segment);

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

        // ── Interactive TUI Commands ──────────────────────────────
        // Programs that need direct terminal access (editors, pagers, etc.)
        // must bypass PowerShell's pipeline to get a real tty.
        if (translated == null && IsInteractiveTui(cmdPart) && redirect == null && stdinRedirect == null)
        {
            var sw2 = Stopwatch.StartNew();
            var (tuiExitCode, wasSuspended, suspendedProc) =
                RunInteractive(commandToRun, ref sigtstpReceived);
            if (wasSuspended && suspendedProc != null)
            {
                var jobId = jobManager.RegisterSuspendedProcess(cmdPart, suspendedProc);
                Console.ForegroundColor = Theme.Current.Muted;
                Console.WriteLine($"\n  [{jobId}] suspended: {cmdPart}");
                Console.ResetColor();
                lastSegmentFailed = false;
            }
            else
            {
                lastSegmentFailed = tuiExitCode != 0;
                lastExitCode = tuiExitCode;
            }
            sw2.Stop();
            if (sw2.Elapsed.TotalSeconds >= 0.5 && !wasSuspended)
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
                Console.WriteLine();
                sw.Stop();
                runningPs = null;
                if (sigtstpReceived)
                {
                    // Ctrl+Z — register as suspended job
                    sigtstpReceived = false;
                    var jobId = jobManager.RegisterSuspendedPipeline(segment, commandToRun);
                    Console.ForegroundColor = Theme.Current.Muted;
                    Console.WriteLine($"  [{jobId}] suspended: {segment}");
                    Console.ResetColor();
                    lastSegmentFailed = false;
                    lastExitCode = 0;
                }
                else
                {
                    // Ctrl+C — cancel
                    lastSegmentFailed = true;
                    lastExitCode = 130;
                }
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

    if (shouldExit) break;

    // Normalize exit code: ensure consistency with failure flag
    if (lastSegmentFailed && lastExitCode == 0) lastExitCode = 1;
    if (!lastSegmentFailed) lastExitCode = 0;

    prompt.SetLastCommandFailed(lastSegmentFailed, lastExitCode);
    lineEditor.SaveHistory();
}

// ── Graceful Exit ────────────────────────────────────────────────────
jobManager.Dispose();
lineEditor.SaveHistory();
Console.Write("\x1b[0 q"); // Reset cursor shape
if (host.ShouldExit)
    Environment.ExitCode = host.ExitCode;
Console.WriteLine("bye.");

// ═══════════════════════════════════════════════════════════════════
// Helper Methods
// ═══════════════════════════════════════════════════════════════════

// ── Startup Script ──────────────────────────────────────────────────

/// <summary>
/// Execute ~/.config/rush/config.rush through the scripting engine.
/// This is the portable config file with OS conditionals, aliases, etc.
/// Runs before init.rush so init.rush can override.
/// </summary>
static void RunConfigRush(Runspace runspace, ScriptEngine engine)
{
    var configPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "rush", "config.rush");

    if (!File.Exists(configPath)) return;

    try
    {
        var source = File.ReadAllText(configPath);
        var psCode = engine.TranspileFile(source);
        if (psCode != null)
        {
            using var ps = PowerShell.Create();
            ps.Runspace = runspace;
            ps.AddScript(psCode);
            ps.Invoke();
        }
    }
    catch { } // Silently ignore config script errors on startup
}

static void RunStartupScript(Runspace runspace)
{
    var scriptPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "rush", "init.rush");

    if (!File.Exists(scriptPath)) return;

    try
    {
        var lines = File.ReadAllLines(scriptPath);
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith('#')) continue;

            using var ps = PowerShell.Create();
            ps.Runspace = runspace;
            ps.AddScript(line);
            ps.Invoke();
        }
    }
    catch { } // Silently ignore startup script errors
}

// ── Script File Execution ──────────────────────────────────────────

/// <summary>
/// Execute a .rush script file non-interactively.
/// Used for: rush script.rush
/// </summary>
static void RunScriptFile(string path)
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
            initPs.AddScript($"$os = '{osName}'; $hostname = '{Environment.MachineName.ToLowerInvariant()}'; $rush_version = '1.1.0'");
            initPs.Invoke();
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
/// Programs that need direct terminal access (editors, pagers, etc.)
/// must be launched via Process.Start with inherited stdio, bypassing
/// PowerShell's pipeline which captures stdout.
/// </summary>
static bool IsInteractiveTui(string cmdPart)
{
    var tuiCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "vi", "vim", "nvim", "nano", "pico", "emacs", "helix", "hx", "micro", "joe", "ne",
        "less", "more", "most",
        "top", "htop", "btop",
        "man",
        "sudo",
        "ssh", "tmux", "screen",
        "python", "python3", "node", "irb", "lua", "ghci",  // REPLs
        "fzf", "tig", "lazygit", "nnn", "ranger", "mc"
    };

    // Extract the command name (first word)
    var firstSpace = cmdPart.IndexOf(' ');
    var cmd = firstSpace > 0 ? cmdPart[..firstSpace] : cmdPart;
    // Strip path (e.g., /usr/bin/vi → vi)
    cmd = Path.GetFileName(cmd);
    return tuiCommands.Contains(cmd);
}

/// <summary>
/// Run a command directly with inherited stdio (no capture).
/// Used for interactive/TUI programs that need a real terminal.
/// Returns (exitCode, wasSuspended, process). When suspended via Ctrl+Z,
/// the process handle is returned so it can be registered as a job.
/// </summary>
static (int exitCode, bool suspended, Process? process) RunInteractive(
    string command, ref bool sigtstpFlag)
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
        if (proc == null) return (1, false, null);

        // Poll instead of blocking WaitForExit — allows detecting Ctrl+Z
        while (!proc.HasExited)
        {
            if (sigtstpFlag)
            {
                sigtstpFlag = false;
                return (0, true, proc); // Process is stopped, return handle
            }
            proc.WaitForExit(100);
        }

        var exitCode = proc.ExitCode;
        proc.Dispose();
        return (exitCode, false, null);
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = Theme.Current.Error;
        Console.Error.WriteLine($"  {ex.Message}");
        Console.ResetColor();
        return (1, false, null);
    }
}

static string FormatDuration(TimeSpan elapsed)
{
    if (elapsed.TotalMinutes >= 1)
        return $"{elapsed.Minutes}m {elapsed.Seconds}s";
    return $"{elapsed.TotalSeconds:F1}s";
}

// ── Tilde Expansion ────────────────────────────────────────────────

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

        // Translate through Rush's Unix→PS translator
        var translated = translator.Translate(innerCommand) ?? innerCommand;

        using var ps = PowerShell.Create();
        ps.Runspace = runspace;
        ps.AddScript(translated);
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

static (string command, RedirectInfo? redirect, StdinInfo? stdin) ParseRedirection(string input)
{
    var trimmed = input.TrimEnd();
    if (string.IsNullOrEmpty(trimmed)) return (input, null, null);

    bool inSQ = false, inDQ = false;

    // Phase 1: Scan for redirect operators (respecting quotes).
    // 2> and 2>> are recognised so their '>' isn't mistaken for stdout,
    // but they stay in the command string for PowerShell to handle natively.
    var ops = new List<(int pos, string op)>();

    for (int i = 0; i < trimmed.Length; i++)
    {
        char ch = trimmed[i];
        if (ch == '\'' && !inDQ) { inSQ = !inSQ; continue; }
        if (ch == '"' && !inSQ) { inDQ = !inDQ; continue; }
        if (inSQ || inDQ) continue;

        // 2>&1 — tracked (may need stripping when combined with stdout redirect)
        if (ch == '2' && i + 3 < trimmed.Length
            && trimmed[i + 1] == '>' && trimmed[i + 2] == '&' && trimmed[i + 3] == '1')
        { ops.Add((i, "2>&1")); i += 3; }
        // 2>> — skip past but leave in command for PowerShell
        else if (ch == '2' && i + 2 < trimmed.Length
                 && trimmed[i + 1] == '>' && trimmed[i + 2] == '>')
        { i += 2; }
        // 2> — skip past but leave in command for PowerShell
        else if (ch == '2' && i + 1 < trimmed.Length && trimmed[i + 1] == '>')
        { i += 1; }
        // >>
        else if (ch == '>' && i + 1 < trimmed.Length && trimmed[i + 1] == '>')
        { ops.Add((i, ">>")); i += 1; }
        // >
        else if (ch == '>')
        { ops.Add((i, ">")); }
        // <
        else if (ch == '<')
        { ops.Add((i, "<")); }
    }

    if (ops.Count == 0) return (input, null, null);

    // Phase 2: Process operators — parse file targets, decide what to strip.
    RedirectInfo? stdoutRedirect = null;
    StdinInfo? stdinRedirect = null;
    bool hasMerge = false;
    var stripRanges = new List<(int start, int end)>(); // [start, end)

    foreach (var (pos, op) in ops)
    {
        int opEnd = pos + op.Length;

        if (op == "2>&1")
        {
            hasMerge = true;
            // Stripping decision deferred to Phase 3
            continue;
        }

        // For >, >>, < — parse the target file path
        int j = opEnd;
        while (j < trimmed.Length && trimmed[j] == ' ') j++;
        if (j >= trimmed.Length) continue; // no target — leave as-is

        string filePath;
        if (trimmed[j] is '\'' or '"')
        {
            char q = trimmed[j];
            int pathStart = j + 1;
            j++;
            while (j < trimmed.Length && trimmed[j] != q) j++;
            filePath = trimmed[pathStart..j];
            if (j < trimmed.Length) j++; // skip closing quote
        }
        else
        {
            int pathStart = j;
            while (j < trimmed.Length
                   && trimmed[j] != ' ' && trimmed[j] != '\t'
                   && trimmed[j] != '|' && trimmed[j] != ';'
                   && trimmed[j] != '>' && trimmed[j] != '<')
                j++;
            filePath = trimmed[pathStart..j];
        }

        if (string.IsNullOrEmpty(filePath)) continue;

        // Resolve ~ paths
        if (filePath == "~" || filePath.StartsWith("~/"))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            filePath = filePath == "~" ? home : Path.Combine(home, filePath[2..]);
        }

        stripRanges.Add((pos, j));

        if (op == "<")
            stdinRedirect = new StdinInfo(filePath);
        else
            stdoutRedirect = new RedirectInfo(filePath, op == ">>");
    }

    // Phase 3: If 2>&1 appears with a stdout redirect, merge stderr into the
    // captured output and strip 2>&1 from the command.  Without a stdout
    // redirect, leave 2>&1 in the command for PowerShell to handle inline.
    if (hasMerge && stdoutRedirect != null)
    {
        stdoutRedirect = stdoutRedirect with { MergeStderr = true };
        foreach (var (pos, op) in ops)
            if (op == "2>&1") stripRanges.Add((pos, pos + 4));
    }

    if (stripRanges.Count == 0)
        return (input, stdoutRedirect, stdinRedirect);

    // Phase 4: Build clean command by removing strip ranges.
    var sorted = stripRanges.OrderBy(r => r.start).ToList();
    var sb = new System.Text.StringBuilder();
    int cursor = 0;
    foreach (var (start, end) in sorted)
    {
        if (start > cursor) sb.Append(trimmed[cursor..start]);
        cursor = end;
    }
    if (cursor < trimmed.Length) sb.Append(trimmed[cursor..]);

    return (sb.ToString().Trim(), stdoutRedirect, stdinRedirect);
}

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
    var builtins = new[] { "exit", "quit", "help", "history", "alias", "reload", "clear", "cd", "export", "unset", "source", "jobs", "fg", "bg", "sync", "pushd", "popd", "dirs" };
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
    Console.WriteLine("  →/End      — accept autosuggestion (fish-style ghost text)");
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
    Console.WriteLine("  export     — set env var: export FOO=bar");
    Console.WriteLine("  unset      — remove env var: unset FOO");
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
    Console.WriteLine("  sync       — config sync: sync init [github|ssh|path] | push | pull | status");
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
    Console.WriteLine($"  Config: {RushConfig.GetConfigPath()}");
    Console.WriteLine($"  Startup: ~/.config/rush/init.rush");
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

    // Parse redirections before translation
    var (cmdPart, redirect, stdinRedirect) = ParseRedirection(command);
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
}

// ── Types ────────────────────────────────────────────────────────────
record RedirectInfo(string FilePath, bool Append, bool MergeStderr = false);
record StdinInfo(string FilePath);

using System.Management.Automation;
using System.Management.Automation.Runspaces;
using Rush;

// ── Load Config ──────────────────────────────────────────────────────
var config = RushConfig.Load();

// ── Banner ───────────────────────────────────────────────────────────
Console.ForegroundColor = ConsoleColor.Cyan;
Console.WriteLine("rush v0.2.0 — a better shell");
Console.ForegroundColor = ConsoleColor.DarkGray;
Console.WriteLine($"PowerShell 7 engine | {config.EditMode} mode | Tab | Ctrl+R | autosuggestions");
Console.ResetColor();
Console.WriteLine();

// ── Initialize PowerShell Engine ─────────────────────────────────────
var hostUI = new RushHostUI();
var host = new RushHost(hostUI);
var iss = InitialSessionState.CreateDefault();
var runspace = RunspaceFactory.CreateRunspace(host, iss);
runspace.Open();

// ── Initialize Components ────────────────────────────────────────────
var translator = new CommandTranslator();
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

// ── State ────────────────────────────────────────────────────────────
string? previousDirectory = null;

// ── REPL ─────────────────────────────────────────────────────────────
while (true)
{
    prompt.Render(runspace);
    tabCompleter.Reset();

    var input = lineEditor.ReadLine();
    if (input == null) break; // EOF (Ctrl+D)

    input = input.Trim();
    if (string.IsNullOrEmpty(input)) continue;

    // ── Bang Expansion ──────────────────────────────────────────────
    if (input.Contains("!!") || input.Contains("!$"))
    {
        // Find the most recent history entry that isn't the current raw input
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

            lineEditor.ReplaceLastHistory(input);

            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"  → {input}");
            Console.ResetColor();
        }
    }

    // ── Split on Chain Operators (&&, ||) ────────────────────────────
    var (chainSegments, chainOps) = SplitChainOperators(input);

    bool lastSegmentFailed = false;
    bool shouldExit = false;

    for (int ci = 0; ci < chainSegments.Count; ci++)
    {
        var segment = chainSegments[ci].Trim();
        if (string.IsNullOrEmpty(segment)) continue;

        // Chain logic: && skips on failure, || skips on success
        if (ci > 0)
        {
            if (chainOps[ci - 1] == "&&" && lastSegmentFailed) continue;
            if (chainOps[ci - 1] == "||" && !lastSegmentFailed) continue;
        }

        // ── Try Built-in Commands ───────────────────────────────────
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

        // ── cd (with - support) ─────────────────────────────────────
        if (segment.StartsWith("cd ", StringComparison.OrdinalIgnoreCase) || segment == "cd")
        {
            var (cdFailed, newPrev) = HandleCd(runspace, segment, previousDirectory);
            if (!cdFailed && newPrev != null) previousDirectory = newPrev;
            lastSegmentFailed = cdFailed;
            continue;
        }

        // ── Parse Redirection ─────────────────────────────────────
        var (cmdPart, redirect) = ParseRedirection(segment);

        // ── Translate & Execute ─────────────────────────────────────
        var translated = translator.Translate(cmdPart);
        var commandToRun = translated ?? cmdPart;

        try
        {
            using var ps = PowerShell.Create();
            ps.Runspace = runspace;
            ps.AddScript(commandToRun);

            var results = ps.Invoke();

            if (ps.HadErrors)
            {
                OutputRenderer.RenderErrors(ps.Streams);
                lastSegmentFailed = true;
            }
            else
            {
                lastSegmentFailed = false;
            }

            if (results.Count > 0)
            {
                if (redirect != null)
                    WriteRedirectedOutput(results, redirect);
                else
                    OutputRenderer.Render(results.ToArray());
            }
        }
        catch (Exception ex)
        {
            lastSegmentFailed = true;
            var msg = ex.InnerException?.Message ?? ex.Message;
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"error: {msg}");
            Console.ResetColor();
        }
    }

    if (shouldExit) break;

    prompt.SetLastCommandFailed(lastSegmentFailed);
    lineEditor.SaveHistory();
}

Console.Write("\x1b[0 q"); // Reset cursor shape
Console.WriteLine("bye.");

// ═══════════════════════════════════════════════════════════════════
// Helper Methods
// ═══════════════════════════════════════════════════════════════════

/// <summary>
/// Split input on && and || operators, respecting quotes.
/// Returns the segments and the operators between them.
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
                i++; // skip second &
                continue;
            }
            if (ch == '|' && i + 1 < input.Length && input[i + 1] == '|')
            {
                segments.Add(current.ToString());
                operators.Add("||");
                current.Clear();
                i++; // skip second |
                continue;
            }
        }

        current.Append(ch);
    }

    if (current.Length > 0)
        segments.Add(current.ToString());

    return (segments, operators);
}

static (bool failed, string? newPreviousDir) HandleCd(Runspace runspace, string input, string? previousDirectory)
{
    var path = input.Length > 3 ? input[3..].Trim() : "~";

    // Get current dir before changing
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

    // Handle cd -
    if (path == "-")
    {
        if (previousDirectory == null)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("cd: no previous directory");
            Console.ResetColor();
            return (true, null);
        }
        path = previousDirectory;
    }

    // Expand ~
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
        else
        {
            // Success — return the pre-change dir as new previousDirectory
            return (false, currentDir);
        }
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine($"cd: {ex.Message}");
        Console.ResetColor();
        return (true, null);
    }
}

static void ShowHistory(LineEditor editor)
{
    var history = editor.History;
    int start = Math.Max(0, history.Count - 50); // Show last 50
    for (int i = start; i < history.Count; i++)
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write($"  {i + 1,4}  ");
        Console.ResetColor();
        Console.WriteLine(history[i]);
    }
}

static void ShowAliases(CommandTranslator translator)
{
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine("Command Aliases:");
    Console.ResetColor();
    Console.WriteLine();

    foreach (var (alias, mapping) in translator.GetMappings().OrderBy(kv => kv.Key))
    {
        Console.ForegroundColor = ConsoleColor.White;
        Console.Write($"  {alias,-12}");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write(" → ");
        Console.ResetColor();
        Console.WriteLine(mapping.Cmdlet ?? "(native passthrough)");
    }
}

// ── Redirection ──────────────────────────────────────────────────────

/// <summary>
/// Parse > or >> redirection from the end of a command.
/// Returns the command without redirection, and the redirect info (or null).
/// </summary>
static (string command, RedirectInfo? redirect) ParseRedirection(string input)
{
    var trimmed = input.TrimEnd();
    bool inSingleQuote = false;
    bool inDoubleQuote = false;
    int lastRedirectPos = -1;
    bool lastIsAppend = false;

    for (int i = 0; i < trimmed.Length; i++)
    {
        if (trimmed[i] == '\'' && !inDoubleQuote) inSingleQuote = !inSingleQuote;
        else if (trimmed[i] == '"' && !inSingleQuote) inDoubleQuote = !inDoubleQuote;

        if (!inSingleQuote && !inDoubleQuote)
        {
            if (i + 1 < trimmed.Length && trimmed[i] == '>' && trimmed[i + 1] == '>')
            {
                lastRedirectPos = i;
                lastIsAppend = true;
                i++; // skip second >
            }
            else if (trimmed[i] == '>' && (i == 0 || trimmed[i - 1] != '2'))
            {
                lastRedirectPos = i;
                lastIsAppend = false;
            }
        }
    }

    if (lastRedirectPos < 0) return (input, null);

    var commandPart = trimmed[..lastRedirectPos].TrimEnd();
    var filePart = trimmed[(lastRedirectPos + (lastIsAppend ? 2 : 1))..].Trim();

    if (string.IsNullOrEmpty(filePart)) return (input, null); // No filename yet

    // Strip quotes from filename
    if ((filePart.StartsWith('\'') && filePart.EndsWith('\'')) ||
        (filePart.StartsWith('"') && filePart.EndsWith('"')))
        filePart = filePart[1..^1];

    // Expand ~
    if (filePart == "~" || filePart.StartsWith("~/"))
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        filePart = filePart == "~" ? home : Path.Combine(home, filePart[2..]);
    }

    return (commandPart, new RedirectInfo(filePart, lastIsAppend));
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
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine($"redirect: {ex.Message}");
        Console.ResetColor();
    }
}

static void ShowHelp(LineEditor editor, CommandTranslator translator)
{
    Console.ForegroundColor = ConsoleColor.Cyan;
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
    Console.WriteLine();

    Console.ForegroundColor = ConsoleColor.Cyan;
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
    Console.WriteLine("  &&         — run next command only if previous succeeded");
    Console.WriteLine("  ||         — run next command only if previous failed");
    Console.WriteLine("  > / >>     — redirect output to file (overwrite / append)");
    Console.WriteLine("  history    — show command history (persistent)");
    Console.WriteLine("  alias      — show command mappings");
    Console.WriteLine("  reload     — reload config");
    Console.WriteLine("  clear      — clear screen");
    Console.WriteLine();

    if (editor.Mode == EditMode.Vi)
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("  Vi: Esc=normal  i/a/A/I=insert  h/l=move  w/b/e=word");
        Console.WriteLine("      x=delete  D=del-to-end  C=change-to-end  f/F=find-char");
        Console.WriteLine("      0/$=begin/end  j/k=history  3w=count+motion");
        Console.ResetColor();
    }

    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.WriteLine($"  Config: {RushConfig.GetConfigPath()}");
    Console.ResetColor();
}

// ── Types ────────────────────────────────────────────────────────────
record RedirectInfo(string FilePath, bool Append);

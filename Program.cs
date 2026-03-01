using System.Management.Automation;
using System.Management.Automation.Runspaces;
using Rush;

// ── Load Config ──────────────────────────────────────────────────────
var config = RushConfig.Load();

// ── Banner ───────────────────────────────────────────────────────────
Console.ForegroundColor = ConsoleColor.Cyan;
Console.WriteLine("rush v0.1.0 — a better shell");
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

// Apply config (sets edit mode, custom aliases)
config.Apply(lineEditor, translator);

// Load persistent history
lineEditor.LoadHistory();

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

    bool commandFailed = false;

    // ── Built-in Commands ────────────────────────────────────────────
    if (input.Equals("exit", StringComparison.OrdinalIgnoreCase) ||
        input.Equals("quit", StringComparison.OrdinalIgnoreCase))
        break;

    if (input.Equals("help", StringComparison.OrdinalIgnoreCase))
    {
        ShowHelp(lineEditor, translator);
        continue;
    }

    if (input.Equals("set vi", StringComparison.OrdinalIgnoreCase))
    {
        lineEditor.Mode = EditMode.Vi;
        Console.WriteLine("Switched to vi mode");
        continue;
    }
    if (input.Equals("set emacs", StringComparison.OrdinalIgnoreCase))
    {
        lineEditor.Mode = EditMode.Emacs;
        Console.WriteLine("Switched to emacs mode");
        continue;
    }

    if (input.Equals("history", StringComparison.OrdinalIgnoreCase))
    {
        ShowHistory(lineEditor);
        continue;
    }

    if (input.Equals("alias", StringComparison.OrdinalIgnoreCase))
    {
        ShowAliases(translator);
        continue;
    }

    if (input.Equals("reload", StringComparison.OrdinalIgnoreCase))
    {
        config = RushConfig.Load();
        config.Apply(lineEditor, translator);
        Console.WriteLine("Config reloaded");
        continue;
    }

    if (input.Equals("clear", StringComparison.OrdinalIgnoreCase))
    {
        Console.Clear();
        continue;
    }

    // ── cd (with - support) ──────────────────────────────────────────
    if (input.StartsWith("cd ", StringComparison.OrdinalIgnoreCase) || input == "cd")
    {
        HandleCd(runspace, input, ref previousDirectory);
        continue;
    }

    // ── Translate & Execute ──────────────────────────────────────────
    var translated = translator.Translate(input);
    var commandToRun = translated ?? input;

    try
    {
        using var ps = PowerShell.Create();
        ps.Runspace = runspace;
        ps.AddScript(commandToRun);

        var results = ps.Invoke();

        if (ps.HadErrors)
        {
            OutputRenderer.RenderErrors(ps.Streams);
            commandFailed = true;
        }

        if (results.Count > 0)
            OutputRenderer.Render(results.ToArray());
    }
    catch (Exception ex)
    {
        commandFailed = true;
        // Clean error messages — strip .NET noise
        var msg = ex.Message;
        if (ex.InnerException != null)
            msg = ex.InnerException.Message;

        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine($"error: {msg}");
        Console.ResetColor();
    }

    prompt.SetLastCommandFailed(commandFailed);
    lineEditor.SaveHistory();
}

Console.Write("\x1b[0 q"); // Reset cursor shape
Console.WriteLine("bye.");

// ═══════════════════════════════════════════════════════════════════
// Helper Methods
// ═══════════════════════════════════════════════════════════════════

static void HandleCd(Runspace runspace, string input, ref string? previousDirectory)
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
            return;
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
        }
        else
        {
            // Success — update previousDirectory
            previousDirectory = currentDir;
        }
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine($"cd: {ex.Message}");
        Console.ResetColor();
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

    Console.WriteLine("  Pipes:   ls | grep foo | head -5 | sort");
    Console.WriteLine("  Flags:   ls -la → Get-ChildItem -Force");
    Console.WriteLine("  Native:  git, docker, kubectl just work");
    Console.WriteLine("  PS7:     Full PowerShell syntax works directly");
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

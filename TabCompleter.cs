using System.Management.Automation;
using System.Management.Automation.Runspaces;

namespace Rush;

/// <summary>
/// Tab completion engine for Rush.
/// Handles file/directory paths, command names, PATH binaries,
/// environment variables, flags, and PowerShell completions.
/// </summary>
public class TabCompleter
{
    private readonly Runspace _runspace;
    private readonly CommandTranslator _translator;

    // Completion cycling state
    private string? _lastCompletionInput;
    private int _completionIndex;
    private List<string> _completions = new();
    private int _completionStart; // Where the completed token starts in the input

    // PATH binary cache
    private List<string>? _pathBinaries;
    private string? _cachedPath;

    public TabCompleter(Runspace runspace, CommandTranslator translator)
    {
        _runspace = runspace;
        _translator = translator;
    }

    /// <summary>
    /// Get the next completion for the current input and cursor position.
    /// Returns (newInput, newCursorPosition) or null if no completion.
    /// </summary>
    public (string newInput, int newCursor)? Complete(string input, int cursor)
    {
        // If input hasn't changed since last Tab, cycle to next completion
        if (_lastCompletionInput == input && _completions.Count > 0)
        {
            _completionIndex = (_completionIndex + 1) % _completions.Count;
            return ApplyCompletion(input, _completions[_completionIndex]);
        }

        // New completion request
        _completions.Clear();
        _completionIndex = 0;

        // Extract the token being completed
        var (token, tokenStart) = ExtractToken(input, cursor);
        _completionStart = tokenStart;

        if (string.IsNullOrEmpty(token) && tokenStart == 0)
            return null;

        // Determine context
        var beforeToken = input[..tokenStart].TrimEnd();
        bool isFirstToken = !beforeToken.Contains(' ');
        var firstWord = ExtractFirstWord(beforeToken);

        if (token.StartsWith('$'))
        {
            // $VAR completion
            CompleteEnvironmentVariables(token);
        }
        else if (token.StartsWith('-') && !isFirstToken)
        {
            // Flag completion
            CompleteFlags(firstWord, token);
        }
        else if (isFirstToken)
        {
            // Complete command names (builtins + translator + PATH binaries)
            CompleteCommands(token);
        }
        else if (firstWord.Equals("cd", StringComparison.OrdinalIgnoreCase) ||
                 firstWord.Equals("pushd", StringComparison.OrdinalIgnoreCase))
        {
            // cd/pushd: directories only
            CompleteDirectoriesOnly(token);
        }
        else
        {
            // Complete file/directory paths
            CompletePaths(token);
        }

        // If no local completions, try PowerShell's built-in completion
        if (_completions.Count == 0)
        {
            CompletePowerShell(input, cursor);
        }

        if (_completions.Count == 0)
            return null;

        _lastCompletionInput = null; // Will be set after applying
        var result = ApplyCompletion(input, _completions[0]);
        return result;
    }

    /// <summary>
    /// Show all available completions (triggered by double-Tab or when there are many).
    /// </summary>
    public void ShowCompletions()
    {
        if (_completions.Count <= 1) return;

        Console.WriteLine();
        var maxLen = _completions.Max(c => c.Length) + 2;
        int cols;
        try { cols = Math.Max(1, Console.WindowWidth / maxLen); }
        catch { cols = 4; }

        for (int i = 0; i < _completions.Count; i++)
        {
            var comp = _completions[i];
            if (comp.EndsWith(Path.DirectorySeparatorChar) || comp.EndsWith('/'))
            {
                Console.ForegroundColor = Theme.Current.Directory;
                Console.Write(comp.PadRight(maxLen));
                Console.ResetColor();
            }
            else
            {
                Console.Write(comp.PadRight(maxLen));
            }
            if ((i + 1) % cols == 0) Console.WriteLine();
        }
        if (_completions.Count % cols != 0) Console.WriteLine();
    }

    public int CompletionCount => _completions.Count;

    public void Reset()
    {
        _lastCompletionInput = null;
        _completions.Clear();
        _completionIndex = 0;
    }

    private (string newInput, int newCursor)? ApplyCompletion(string originalInput, string completion)
    {
        // Replace the token in the original input with the completion
        var before = originalInput[.._completionStart];
        var afterTokenEnd = FindTokenEnd(originalInput, _completionStart);
        var after = originalInput[afterTokenEnd..];

        // Add trailing space if it's a complete match (not a directory)
        var suffix = completion.EndsWith('/') || completion.EndsWith(Path.DirectorySeparatorChar) ? "" : " ";
        if (!string.IsNullOrEmpty(after)) suffix = ""; // Don't add space if there's already text after

        var newInput = before + completion + suffix + after;
        var newCursor = before.Length + completion.Length + suffix.Length;

        _lastCompletionInput = newInput;
        return (newInput, newCursor);
    }

    // ── Command Completion ──────────────────────────────────────────────

    private void CompleteCommands(string prefix)
    {
        // Rush built-in commands
        var builtins = new[] { "exit", "quit", "help", "set", "cd", "history", "alias", "reload", "clear", "pushd", "popd", "dirs" };
        foreach (var cmd in builtins)
        {
            if (cmd.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                _completions.Add(cmd);
        }

        // Translated command aliases
        var aliases = _translator.GetCommandNames();
        foreach (var alias in aliases)
        {
            if (alias.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !_completions.Contains(alias))
                _completions.Add(alias);
        }

        // PATH binaries
        foreach (var bin in GetPathBinaries())
        {
            if (bin.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !_completions.Contains(bin))
                _completions.Add(bin);
        }

        _completions.Sort(StringComparer.OrdinalIgnoreCase);
    }

    // ── PATH Binary Scanning ────────────────────────────────────────────

    private List<string> GetPathBinaries()
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        if (_pathBinaries != null && _cachedPath == pathEnv)
            return _pathBinaries;

        _cachedPath = pathEnv;
        var binaries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in pathEnv.Split(':'))
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
            try
            {
                foreach (var file in Directory.GetFiles(dir))
                {
                    var name = Path.GetFileName(file);
                    if (!string.IsNullOrEmpty(name))
                        binaries.Add(name);
                }
            }
            catch { } // Permission errors, etc.
        }

        _pathBinaries = binaries.OrderBy(b => b, StringComparer.OrdinalIgnoreCase).ToList();
        return _pathBinaries;
    }

    // ── Directory-Only Completion (for cd, pushd) ───────────────────────

    private void CompleteDirectoriesOnly(string prefix)
    {
        try
        {
            string dir;
            string filePrefix;

            var expandedPrefix = prefix;
            if (expandedPrefix.StartsWith("~/") || expandedPrefix == "~")
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                expandedPrefix = expandedPrefix == "~" ? home : Path.Combine(home, expandedPrefix[2..]);
            }

            if (expandedPrefix.Contains('/') || expandedPrefix.Contains(Path.DirectorySeparatorChar))
            {
                dir = Path.GetDirectoryName(expandedPrefix) ?? ".";
                filePrefix = Path.GetFileName(expandedPrefix);
            }
            else
            {
                dir = GetCurrentDirectory();
                filePrefix = expandedPrefix;
            }

            if (!Directory.Exists(dir)) return;

            foreach (var d in Directory.GetDirectories(dir))
            {
                var name = Path.GetFileName(d);
                if (name.StartsWith(filePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    if (prefix.Contains('/') || prefix.Contains(Path.DirectorySeparatorChar))
                    {
                        var dirPart = Path.GetDirectoryName(prefix) ?? "";
                        _completions.Add(Path.Combine(dirPart, name) + "/");
                    }
                    else
                    {
                        _completions.Add(name + "/");
                    }
                }
            }
        }
        catch { }
    }

    // ── $VAR Completion ─────────────────────────────────────────────────

    private void CompleteEnvironmentVariables(string token)
    {
        var varPrefix = token[1..]; // Strip leading $
        foreach (var key in Environment.GetEnvironmentVariables().Keys)
        {
            var name = key.ToString()!;
            if (name.StartsWith(varPrefix, StringComparison.OrdinalIgnoreCase))
                _completions.Add("$" + name);
        }
        _completions.Sort(StringComparer.OrdinalIgnoreCase);
    }

    // ── Flag Completion ─────────────────────────────────────────────────

    private void CompleteFlags(string command, string flagPrefix)
    {
        var flags = _translator.GetFlagsForCommand(command);
        foreach (var flag in flags)
        {
            if (flag.StartsWith(flagPrefix, StringComparison.OrdinalIgnoreCase))
                _completions.Add(flag);
        }
        _completions.Sort(StringComparer.OrdinalIgnoreCase);
    }

    // ── Path Completion ─────────────────────────────────────────────────

    private void CompletePaths(string prefix)
    {
        try
        {
            string dir;
            string filePrefix;

            // Handle ~ expansion
            var expandedPrefix = prefix;
            if (expandedPrefix.StartsWith("~/") || expandedPrefix == "~")
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                expandedPrefix = expandedPrefix == "~" ? home : Path.Combine(home, expandedPrefix[2..]);
            }

            if (expandedPrefix.Contains('/') || expandedPrefix.Contains(Path.DirectorySeparatorChar))
            {
                dir = Path.GetDirectoryName(expandedPrefix) ?? ".";
                filePrefix = Path.GetFileName(expandedPrefix);
            }
            else
            {
                // Get current directory from the runspace
                dir = GetCurrentDirectory();
                filePrefix = expandedPrefix;
            }

            if (!Directory.Exists(dir)) return;

            // Directories first
            foreach (var d in Directory.GetDirectories(dir))
            {
                var name = Path.GetFileName(d);
                if (name.StartsWith(filePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    if (prefix.Contains('/') || prefix.Contains(Path.DirectorySeparatorChar))
                    {
                        var dirPart = Path.GetDirectoryName(prefix) ?? "";
                        _completions.Add(Path.Combine(dirPart, name) + "/");
                    }
                    else
                    {
                        _completions.Add(name + "/");
                    }
                }
            }

            // Then files
            foreach (var f in Directory.GetFiles(dir))
            {
                var name = Path.GetFileName(f);
                if (name.StartsWith(filePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    if (prefix.Contains('/') || prefix.Contains(Path.DirectorySeparatorChar))
                    {
                        var dirPart = Path.GetDirectoryName(prefix) ?? "";
                        _completions.Add(Path.Combine(dirPart, name));
                    }
                    else
                    {
                        _completions.Add(name);
                    }
                }
            }
        }
        catch
        {
            // Ignore completion errors silently
        }
    }

    private void CompletePowerShell(string input, int cursor)
    {
        try
        {
            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;
            var completions = CommandCompletion.CompleteInput(input, cursor, null, ps);

            if (completions?.CompletionMatches != null)
            {
                _completionStart = completions.ReplacementIndex;
                foreach (var match in completions.CompletionMatches)
                {
                    var text = match.CompletionText;
                    if (!_completions.Contains(text))
                        _completions.Add(text);
                }
            }
        }
        catch
        {
            // PowerShell completion can throw — ignore
        }
    }

    private string GetCurrentDirectory()
    {
        try
        {
            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;
            ps.AddCommand("Get-Location");
            var result = ps.Invoke();
            return result.Count > 0 ? result[0].ToString()! : Directory.GetCurrentDirectory();
        }
        catch
        {
            return Directory.GetCurrentDirectory();
        }
    }

    // ── Token Extraction ────────────────────────────────────────────────

    private static string ExtractFirstWord(string text)
    {
        // Get the command name from text before the current token
        // Handles pipes: "ls | grep " → "grep"
        var trimmed = text.TrimEnd();
        var pipePos = trimmed.LastIndexOf('|');
        if (pipePos >= 0) trimmed = trimmed[(pipePos + 1)..].TrimStart();
        var spacePos = trimmed.IndexOf(' ');
        return spacePos > 0 ? trimmed[..spacePos] : trimmed;
    }

    private static (string token, int startIndex) ExtractToken(string input, int cursor)
    {
        if (cursor <= 0 || string.IsNullOrEmpty(input))
            return ("", 0);

        // Walk backwards from cursor to find token start
        int pos = Math.Min(cursor, input.Length) - 1;
        while (pos >= 0 && input[pos] != ' ') pos--;
        int start = pos + 1;

        var token = input[start..Math.Min(cursor, input.Length)];
        return (token, start);
    }

    private static int FindTokenEnd(string input, int tokenStart)
    {
        int pos = tokenStart;
        while (pos < input.Length && input[pos] != ' ') pos++;
        return pos;
    }
}

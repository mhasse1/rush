using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Reflection;

namespace Rush;

/// <summary>
/// Tab completion engine for Rush.
/// Handles file/directory paths, command names, PATH binaries,
/// environment variables, flags, dot-completion (type-aware), and PowerShell completions.
/// </summary>
public class TabCompleter
{
    private readonly Runspace _runspace;
    private readonly CommandTranslator _translator;
    private readonly RushConfig _config;

    // Completion cycling state
    private string? _lastCompletionInput;
    private int _completionIndex;
    private List<string> _completions = new();
    private int _completionStart; // Where the completed token starts in the input

    // PATH binary cache
    private List<string>? _pathBinaries;
    private string? _cachedPath;

    // V2: Static type inference — tracks variable → inferred type from assignments
    private readonly Dictionary<string, Type?> _symbolTable = new(StringComparer.OrdinalIgnoreCase);

    // Rush method lists for dot-completion (type-aware)
    private static readonly string[] RushCollectionMethods =
    {
        "each", "select", "reject", "map", "flat_map",
        "sort_by", "sort", "first", "last", "count",
        "any?", "all?", "group_by", "uniq", "reverse",
        "join", "include?", "skip", "skip_while",
        "push", "compact", "flatten", "print", "puts"
    };

    private static readonly string[] RushStringMethods =
    {
        "strip", "lstrip", "rstrip", "upcase", "downcase",
        "split", "split_whitespace", "lines", "trim_end",
        "start_with?", "end_with?", "empty?", "nil?",
        "ljust", "rjust", "replace", "sub", "gsub", "scan", "match",
        "to_i", "to_f", "to_s", "include?",
        "red", "green", "blue", "cyan", "yellow", "magenta", "white", "gray",
        "print", "puts"
    };

    private static readonly string[] RushNumericMethods =
    {
        "round", "abs", "times", "to_currency", "to_filesize", "to_percent",
        "hours", "minutes", "seconds", "days",
        "to_i", "to_f", "to_s",
        "print", "puts"
    };

    // Object base methods to exclude from .NET reflection results
    private static readonly HashSet<string> ExcludedMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "GetType", "ToString", "Equals", "GetHashCode", "MemberwiseClone", "Finalize",
        "ReferenceEquals", "get_Length", "get_Count" // Property accessors shown as properties instead
    };

    public TabCompleter(Runspace runspace, CommandTranslator translator, RushConfig? config = null)
    {
        _runspace = runspace;
        _translator = translator;
        _config = config ?? new RushConfig();
    }

    private StringComparison CompareMode =>
        _config.CompletionIgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

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

        // Strip leading quote from token for matching purposes
        // (ApplyCompletion handles re-quoting the result)
        var matchToken = token.StartsWith('"') ? token[1..] : token;

        // Determine context
        var beforeToken = input[..tokenStart].TrimEnd();
        bool isFirstToken = string.IsNullOrEmpty(beforeToken);
        var firstWord = ExtractFirstWord(beforeToken);
        bool isPathLike = matchToken.Contains('/') || matchToken.Contains(Path.DirectorySeparatorChar)
            || matchToken.StartsWith("./") || matchToken.StartsWith("../");

        // Dot-completion: variable.method or receiver.property
        var dotPos = matchToken.LastIndexOf('.');
        if (dotPos > 0 && !isPathLike && !matchToken.StartsWith('-'))
        {
            var receiver = matchToken[..dotPos];
            var memberPrefix = matchToken[(dotPos + 1)..];
            _completionStart = tokenStart + dotPos + 1 + (token.StartsWith('"') ? 1 : 0);
            CompleteDotMembers(receiver, memberPrefix);
        }
        else if (matchToken.StartsWith('$'))
        {
            // $VAR completion
            CompleteEnvironmentVariables(matchToken);
        }
        else if (matchToken.StartsWith('-') && !isFirstToken)
        {
            // Flag completion
            CompleteFlags(firstWord, matchToken);
        }
        else if (isPathLike)
        {
            // Path-like tokens (contain / or start with ./ ../) always get path completion
            CompletePaths(matchToken);
        }
        else if (IsAfterPipe(beforeToken))
        {
            // After a pipe: complete Rush pipeline operators first, then paths
            CompletePipelineOps(matchToken);
            if (_completions.Count == 0)
                CompletePaths(matchToken);
        }
        else if (isFirstToken)
        {
            // Complete command names (builtins + translator + PATH binaries)
            CompleteCommands(matchToken);
        }
        else if (firstWord.Equals("cd", StringComparison.OrdinalIgnoreCase) ||
                 firstWord.Equals("pushd", StringComparison.OrdinalIgnoreCase))
        {
            // cd/pushd: directories only
            CompleteDirectoriesOnly(matchToken);
        }
        else
        {
            // Complete file/directory paths
            CompletePaths(matchToken);
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
            bool isDir = comp.EndsWith(Path.DirectorySeparatorChar) || comp.EndsWith('/');
            if (isDir)
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

        // If the completion contains spaces, wrap in double quotes
        var quoted = completion;
        bool needsQuoting = completion.Contains(' ');
        bool alreadyQuoted = _completionStart < originalInput.Length
            && originalInput[_completionStart] == '"';

        if (needsQuoting && alreadyQuoted)
        {
            // User already started a quote — include opening quote and add closing quote
            // FindTokenEnd consumed the opening " so we must re-add it
            quoted = $"\"{completion}\"";
        }
        else if (needsQuoting)
        {
            // Wrap in double quotes
            quoted = $"\"{completion}\"";
        }

        // Add trailing space if it's a complete match (not a directory or dot-completion)
        bool isDotCompletion = _completionStart > 0
            && _completionStart <= originalInput.Length
            && originalInput[_completionStart - 1] == '.';
        bool isDir = completion.EndsWith('/') || completion.EndsWith(Path.DirectorySeparatorChar);
        var suffix = (isDir || isDotCompletion) ? "" : " ";
        if (!string.IsNullOrEmpty(after)) suffix = ""; // Don't add space if there's already text after

        var newInput = before + quoted + suffix + after;
        var newCursor = before.Length + quoted.Length + suffix.Length;

        _lastCompletionInput = newInput;
        return (newInput, newCursor);
    }

    // ── Command Completion ──────────────────────────────────────────────

    private void CompleteCommands(string prefix)
    {
        // Rush built-in commands
        var builtins = new[] { "exit", "quit", "help", "set", "cd", "history", "alias", "unalias", "reload", "clear", "pushd", "popd", "dirs", "jobs", "fg", "bg", "wait", "export", "unset", "source", "printf", "read", "exec", "trap", "path" };
        foreach (var cmd in builtins)
        {
            if (cmd.StartsWith(prefix, CompareMode))
                _completions.Add(cmd);
        }

        // Translated command aliases
        var aliases = _translator.GetCommandNames();
        foreach (var alias in aliases)
        {
            if (alias.StartsWith(prefix, CompareMode) && !_completions.Contains(alias))
                _completions.Add(alias);
        }

        // PATH binaries
        foreach (var bin in GetPathBinaries())
        {
            if (bin.StartsWith(prefix, CompareMode) && !_completions.Contains(bin))
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

        foreach (var dir in pathEnv.Split(PathUtils.PathListSeparator))
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
                if (name.StartsWith(filePrefix, CompareMode))
                {
                    if (prefix.Contains('/') || prefix.Contains(Path.DirectorySeparatorChar))
                    {
                        var dirPart = Path.GetDirectoryName(prefix) ?? "";
                        _completions.Add(Path.Combine(dirPart, name).Replace('\\', '/') + "/");
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
            if (name.StartsWith(varPrefix, CompareMode))
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
            if (flag.StartsWith(flagPrefix, CompareMode))
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
                if (name.StartsWith(filePrefix, CompareMode))
                {
                    if (prefix.Contains('/') || prefix.Contains(Path.DirectorySeparatorChar))
                    {
                        var dirPart = Path.GetDirectoryName(prefix) ?? "";
                        _completions.Add(Path.Combine(dirPart, name).Replace('\\', '/') + "/");
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
                if (name.StartsWith(filePrefix, CompareMode))
                {
                    if (prefix.Contains('/') || prefix.Contains(Path.DirectorySeparatorChar))
                    {
                        var dirPart = Path.GetDirectoryName(prefix) ?? "";
                        _completions.Add(Path.Combine(dirPart, name).Replace('\\', '/'));
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

    private static bool IsAfterPipe(string beforeToken)
    {
        // Check if the last non-space character before the current token is '|'
        var trimmed = beforeToken.TrimEnd();
        return trimmed.Length > 0 && trimmed[^1] == '|';
    }

    private static readonly string[] PipelineOps = new[]
    {
        "where", "select", "sort", "first", "last", "skip", "count",
        "distinct", "sum", "avg", "min", "max", "tee", "columns",
        "as", "from", "objectify", "grep", "head", "tail",
    };

    private void CompletePipelineOps(string prefix)
    {
        foreach (var op in PipelineOps)
        {
            if (op.StartsWith(prefix, CompareMode) && !_completions.Contains(op))
                _completions.Add(op);
        }
    }

    private void CompletePowerShell(string input, int cursor)
    {
        try
        {
            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;
            var completions = CommandCompletion.CompleteInput(input, cursor, null, ps);

            if (completions?.CompletionMatches != null && completions.CompletionMatches.Count > 0)
            {
                // Extract the text PowerShell is replacing to filter spurious matches
                var replIndex = completions.ReplacementIndex;
                var replLen = completions.ReplacementLength;
                var originalToken = (replIndex >= 0 && replIndex + replLen <= input.Length)
                    ? input.Substring(replIndex, replLen) : null;

                _completionStart = replIndex;
                foreach (var match in completions.CompletionMatches)
                {
                    var text = match.CompletionText;
                    // PowerShell directory completions lack trailing / — add it
                    if (match.ResultType == CompletionResultType.ProviderContainer
                        && !text.EndsWith('/') && !text.EndsWith(Path.DirectorySeparatorChar))
                    {
                        text += "/";
                    }
                    // Only accept completions that actually match the typed prefix —
                    // PowerShell can return unrelated "best effort" results
                    if (originalToken != null && !text.StartsWith(originalToken, CompareMode))
                        continue;
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

    // ── Dot Completion (Type-Aware) ────────────────────────────────────

    /// <summary>
    /// Complete methods and properties after a dot: variable.prefix → suggestions.
    /// Uses runtime introspection (V1) with static inference fallback (V2).
    /// </summary>
    private void CompleteDotMembers(string receiver, string prefix)
    {
        Type? type = null;

        // V1: Runtime introspection — query the PowerShell runspace for the variable's actual type
        type = GetRuntimeType(receiver);

        // V2: Static inference fallback — check the symbol table for unexecuted variables
        if (type == null && _symbolTable.TryGetValue(receiver, out var inferredType))
            type = inferredType;

        // Add Rush methods appropriate to the type
        AddRushMethods(type, prefix);

        // Add .NET members from reflection if we have a type
        if (type != null)
            AddDotNetMembers(type, prefix);

        // Deduplicate and sort: Rush methods first, then .NET members
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deduped = new List<string>();
        foreach (var c in _completions)
        {
            if (seen.Add(c))
                deduped.Add(c);
        }
        _completions = deduped;
    }

    /// <summary>
    /// Get the .NET type of a variable from the PowerShell runspace at runtime.
    /// Handles simple variables (name) and chained expressions (obj.prop).
    /// </summary>
    private Type? GetRuntimeType(string receiver)
    {
        try
        {
            // Simple variable name: query SessionStateProxy directly (fast, no execution)
            if (IsSimpleIdentifier(receiver))
            {
                var value = _runspace.SessionStateProxy.GetVariable(receiver);
                if (value != null)
                {
                    // Unwrap PSObject if needed
                    if (value is PSObject pso)
                        return pso.BaseObject?.GetType();
                    return value.GetType();
                }
                return null;
            }

            // Chained expression (e.g., files.first): evaluate in the runspace
            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;
            ps.AddScript($"${receiver}");
            var results = ps.Invoke();
            if (results.Count > 0 && results[0] != null)
            {
                var obj = results[0].BaseObject ?? results[0];
                return obj.GetType();
            }
        }
        catch { } // Runspace errors are non-fatal for completion
        return null;
    }

    /// <summary>
    /// Add Rush-specific methods based on the inferred or runtime type.
    /// When type is unknown, adds all Rush methods as candidates.
    /// </summary>
    private void AddRushMethods(Type? type, string prefix)
    {
        string[] methods;

        if (type == null)
        {
            // Unknown type — offer all Rush methods
            methods = RushCollectionMethods
                .Concat(RushStringMethods)
                .Concat(RushNumericMethods)
                .Distinct().ToArray();
        }
        else if (type == typeof(string))
        {
            methods = RushStringMethods;
        }
        else if (IsNumericType(type))
        {
            methods = RushNumericMethods;
        }
        else if (IsCollectionType(type))
        {
            methods = RushCollectionMethods;
        }
        else
        {
            // Known .NET type but not a standard category — offer common methods
            methods = RushCollectionMethods
                .Concat(RushStringMethods)
                .Concat(RushNumericMethods)
                .Distinct().ToArray();
        }

        foreach (var m in methods)
        {
            if (m.StartsWith(prefix, CompareMode) && !_completions.Contains(m, StringComparer.OrdinalIgnoreCase))
                _completions.Add(m);
        }
    }

    /// <summary>
    /// Add .NET properties and methods from reflection on the given type.
    /// </summary>
    private void AddDotNetMembers(Type type, string prefix)
    {
        // Properties
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.Name.StartsWith(prefix, CompareMode)
                && !_completions.Contains(prop.Name, StringComparer.OrdinalIgnoreCase))
            {
                _completions.Add(prop.Name);
            }
        }

        // Methods (excluding property accessors and Object base methods)
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (method.IsSpecialName) continue; // Skip get_/set_ accessors
            if (ExcludedMethods.Contains(method.Name)) continue;
            if (!method.Name.StartsWith(prefix, CompareMode)) continue;
            if (_completions.Contains(method.Name, StringComparer.OrdinalIgnoreCase)) continue;

            // Add parens hint for methods that require arguments
            var requiredParams = method.GetParameters().Count(p => !p.IsOptional);
            _completions.Add(requiredParams > 0 ? $"{method.Name}()" : method.Name);
        }
    }

    /// <summary>
    /// V2: Track variable assignments for static type inference.
    /// Called from the REPL loop after each assignment is executed.
    /// </summary>
    public void TrackAssignment(string varName, string rushRhs)
    {
        var rhs = rushRhs.Trim();
        Type? inferredType = null;

        if ((rhs.StartsWith('"') && rhs.EndsWith('"')) || (rhs.StartsWith('\'') && rhs.EndsWith('\'')))
            inferredType = typeof(string);
        else if (rhs == "true" || rhs == "false")
            inferredType = typeof(bool);
        else if (rhs == "nil")
            inferredType = null;
        else if (rhs.StartsWith('[') && rhs.EndsWith(']'))
            inferredType = typeof(object[]);
        else if (rhs.StartsWith('{') && rhs.EndsWith('}'))
            inferredType = typeof(System.Collections.Hashtable);
        else if (long.TryParse(rhs, out _))
            inferredType = typeof(long);
        else if (double.TryParse(rhs, out _))
            inferredType = typeof(double);
        // Stdlib patterns
        else if (rhs.StartsWith("File.read_lines", StringComparison.OrdinalIgnoreCase)
                || rhs.StartsWith("file.read_lines", StringComparison.OrdinalIgnoreCase))
            inferredType = typeof(string[]);
        else if (rhs.StartsWith("File.read", StringComparison.OrdinalIgnoreCase)
                || rhs.StartsWith("file.read", StringComparison.OrdinalIgnoreCase))
            inferredType = typeof(string);
        else if (rhs.StartsWith("Dir.list", StringComparison.OrdinalIgnoreCase)
                || rhs.StartsWith("dir.list", StringComparison.OrdinalIgnoreCase))
            inferredType = typeof(System.IO.FileSystemInfo[]);
        else if (rhs.StartsWith("Time.now", StringComparison.OrdinalIgnoreCase)
                || rhs.StartsWith("time.now", StringComparison.OrdinalIgnoreCase)
                || rhs.StartsWith("Time.utc_now", StringComparison.OrdinalIgnoreCase)
                || rhs.StartsWith("Time.today", StringComparison.OrdinalIgnoreCase))
            inferredType = typeof(DateTime);

        _symbolTable[varName] = inferredType;
    }

    private static bool IsSimpleIdentifier(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        if (!char.IsLetter(s[0]) && s[0] != '_') return false;
        for (int i = 1; i < s.Length; i++)
        {
            if (!char.IsLetterOrDigit(s[i]) && s[i] != '_') return false;
        }
        return true;
    }

    private static bool IsNumericType(Type type)
    {
        return type == typeof(int) || type == typeof(long) || type == typeof(double)
            || type == typeof(float) || type == typeof(decimal)
            || type == typeof(short) || type == typeof(byte);
    }

    private static bool IsCollectionType(Type type)
    {
        if (type.IsArray) return true;
        return typeof(System.Collections.IEnumerable).IsAssignableFrom(type) && type != typeof(string);
    }

    // ── Token Extraction ────────────────────────────────────────────────

    internal static string ExtractFirstWord(string text)
    {
        // Get the command name from text before the current token
        // Handles pipes: "ls | grep " → "grep"
        var trimmed = text.TrimEnd();
        var pipePos = trimmed.LastIndexOf('|');
        if (pipePos >= 0) trimmed = trimmed[(pipePos + 1)..].TrimStart();
        var spacePos = trimmed.IndexOf(' ');
        return spacePos > 0 ? trimmed[..spacePos] : trimmed;
    }

    internal static (string token, int startIndex) ExtractToken(string input, int cursor)
    {
        if (cursor <= 0 || string.IsNullOrEmpty(input))
            return ("", 0);

        int end = Math.Min(cursor, input.Length);

        // Check if cursor is inside a quoted string by counting quotes up to cursor
        int quoteCount = 0;
        int lastQuotePos = -1;
        for (int i = 0; i < end; i++)
        {
            if (input[i] == '"')
            {
                quoteCount++;
                lastQuotePos = i;
            }
        }

        if (quoteCount % 2 == 1)
        {
            // Inside an open quote — token starts at the opening quote
            // Find the opening quote by walking back
            int qPos = end - 1;
            int qCount = 0;
            while (qPos >= 0)
            {
                if (input[qPos] == '"')
                {
                    qCount++;
                    if (qCount % 2 == 1)
                    {
                        // This is the unmatched opening quote
                        var token = input[qPos..end];
                        return (token, qPos);
                    }
                }
                qPos--;
            }
        }

        // Standard: walk backwards from cursor to find token start (space-delimited)
        int pos = end - 1;
        while (pos >= 0 && input[pos] != ' ') pos--;
        int start = pos + 1;

        var tok = input[start..end];
        return (tok, start);
    }

    internal static int FindTokenEnd(string input, int tokenStart)
    {
        int pos = tokenStart;
        if (pos < input.Length && input[pos] == '"')
        {
            // Quoted token — find closing quote
            pos++;
            while (pos < input.Length && input[pos] != '"') pos++;
            if (pos < input.Length) pos++; // skip closing quote
            return pos;
        }
        while (pos < input.Length && input[pos] != ' ') pos++;
        return pos;
    }
}

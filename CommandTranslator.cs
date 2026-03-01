namespace Rush;

/// <summary>
/// Translates concise Unix-style commands and flags into PowerShell cmdlet calls.
/// This is the core of Rush's syntax layer.
/// </summary>
public class CommandTranslator
{
    private readonly Dictionary<string, CommandMapping> _commands = new(StringComparer.OrdinalIgnoreCase);

    public CommandTranslator()
    {
        RegisterDefaults();
    }

    /// <summary>
    /// Attempt to translate a user input line into a PowerShell command.
    /// Handles pipes by translating each segment independently.
    /// Returns null if no translation is needed (passthrough to native).
    /// </summary>
    public string? Translate(string input)
    {
        input = input.Trim();
        if (string.IsNullOrEmpty(input)) return null;

        // Split on pipe (respecting quotes)
        var segments = SplitOnPipe(input);

        if (segments.Length == 1)
            return TranslateSegment(segments[0].Trim(), isAfterPipe: false);

        // Multi-segment pipeline: translate each part
        var translated = new List<string>();
        bool anyTranslated = false;

        for (int i = 0; i < segments.Length; i++)
        {
            var segment = segments[i].Trim();

            // Dot-notation: .property → ForEach-Object { $_.property }
            // Enables: ps | .ProcessName   or   data | .items[].id
            if (i > 0 && segment.StartsWith('.') && !segment.StartsWith(".."))
            {
                var property = segment[1..]; // Remove leading dot
                if (property.Contains("[]."))
                {
                    // Array expansion: .items[].id → expand each level
                    var parts = property.Split(new[] { "[]." }, StringSplitOptions.None);
                    var stages = parts.Select(p => $"ForEach-Object {{ $_.{p} }}");
                    translated.Add(string.Join(" | ", stages));
                }
                else if (property.EndsWith("[]"))
                {
                    translated.Add($"ForEach-Object {{ $_.{property[..^2]} }}");
                }
                else
                {
                    translated.Add($"ForEach-Object {{ $_.{property} }}");
                }
                anyTranslated = true;
                continue;
            }

            var result = TranslateSegment(segment, isAfterPipe: i > 0);
            if (result != null)
            {
                translated.Add(result);
                anyTranslated = true;
            }
            else
            {
                translated.Add(segment); // Keep original
            }
        }

        return anyTranslated ? string.Join(" | ", translated) : null;
    }

    /// <summary>
    /// Translate a single command segment (no pipes).
    /// When isAfterPipe is true, grep becomes Where-Object (filtering pipeline objects)
    /// instead of Select-String (searching file content).
    /// </summary>
    private string? TranslateSegment(string segment, bool isAfterPipe)
    {
        var parts = SplitCommandLine(segment);
        if (parts.Length == 0) return null;

        var command = parts[0];
        var args = parts.Length > 1 ? parts[1..] : Array.Empty<string>();

        // Special: grep after a pipe filters pipeline objects by string match
        if (isAfterPipe && command.Equals("grep", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length > 0)
            {
                var pattern = args[0];
                // Check for -i flag (case insensitive)
                bool caseInsensitive = args.Any(a => a == "-i");
                var actualPattern = args.FirstOrDefault(a => !a.StartsWith('-')) ?? pattern;

                if (caseInsensitive)
                    return $"Where-Object {{ $_ -match '{actualPattern}' }}";
                return $"Where-Object {{ $_ -cmatch '{actualPattern}' }}";
            }
            return null;
        }

        // Special: head after a pipe → Select-Object -First N
        if (isAfterPipe && command.Equals("head", StringComparison.OrdinalIgnoreCase))
        {
            var count = "10";
            foreach (var arg in args)
            {
                if (arg.StartsWith('-') && int.TryParse(arg[1..], out _))
                    count = arg[1..];
                else if (arg == "-n" && args.Length > 1)
                    count = args[^1]; // take last positional
            }
            return $"Select-Object -First {count}";
        }

        // Special: tail after a pipe → Select-Object -Last N
        if (isAfterPipe && command.Equals("tail", StringComparison.OrdinalIgnoreCase))
        {
            var count = "10";
            foreach (var arg in args)
            {
                if (arg.StartsWith('-') && int.TryParse(arg[1..], out _))
                    count = arg[1..];
            }
            return $"Select-Object -Last {count}";
        }

        // Special: sort after a pipe → Sort-Object
        if (isAfterPipe && command.Equals("sort", StringComparison.OrdinalIgnoreCase))
        {
            bool reverse = args.Any(a => a == "-r");
            var prop = args.FirstOrDefault(a => !a.StartsWith('-'));
            var cmd = "Sort-Object";
            if (prop != null) cmd += $" -Property {prop}";
            if (reverse) cmd += " -Descending";
            return cmd;
        }

        // Special: wc after a pipe → Measure-Object
        if (isAfterPipe && command.Equals("wc", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Contains("-l")) return "Measure-Object -Line";
            if (args.Contains("-w")) return "Measure-Object -Word";
            if (args.Contains("-c")) return "Measure-Object -Character";
            return "Measure-Object";
        }

        // Special: uniq after a pipe → Select-Object -Unique
        if (isAfterPipe && command.Equals("uniq", StringComparison.OrdinalIgnoreCase))
            return "Select-Object -Unique";

        // Standard translation
        if (!_commands.TryGetValue(command, out var mapping))
            return null;

        return mapping.Translate(args);
    }

    private void RegisterDefaults()
    {
        // File system
        Register("ls", "Get-ChildItem", new Dictionary<string, string>
        {
            ["-l"] = "",           // default formatting handles this
            ["-a"] = "-Force",
            ["-la"] = "-Force",
            ["-al"] = "-Force",
            ["-r"] = "-Recurse",
            ["-R"] = "-Recurse",
        });

        Register("cat", "Get-Content");
        Register("pwd", "Get-Location");
        Register("cd", "Set-Location");
        Register("cp", "Copy-Item", new Dictionary<string, string>
        {
            ["-r"] = "-Recurse",
            ["-R"] = "-Recurse",
        });
        Register("mv", "Move-Item");
        Register("rm", "Remove-Item", new Dictionary<string, string>
        {
            ["-r"] = "-Recurse",
            ["-rf"] = "-Recurse -Force",
            ["-f"] = "-Force",
        });
        Register("touch", "New-Item -ItemType File -Path");
        Register("mkdir", "New-Item -ItemType Directory -Path");

        // Process management
        Register("ps", "Get-Process");
        Register("kill", "Stop-Process -Id");

        // Text/search
        Register("grep", "Select-String -Pattern");
        Register("echo", "Write-Output", quotePositionalArgs: true);

        // Environment
        Register("env", "Get-ChildItem Env:");
        Register("which", "Get-Command");
        Register("type", "Get-Command");

        // Networking
        Register("curl", null); // passthrough to native curl
        Register("wget", null); // passthrough to native wget

        // System info
        Register("whoami", "[Environment]::UserName");
        Register("hostname", "[Environment]::MachineName");
        Register("df", "Get-PSDrive -PSProvider FileSystem");
        Register("uptime", "(Get-Date) - (Get-Process -Id $PID).StartTime");

        // Standalone head/tail (not just after pipe)
        Register("head", "Get-Content", new Dictionary<string, string>
        {
            ["-n"] = "-TotalCount",
        });
        Register("tail", "Get-Content", new Dictionary<string, string>
        {
            ["-n"] = "-Tail",
        });

        // Shell
        Register("clear", "Clear-Host");

        // File search
        Register("find", "Get-ChildItem -Recurse", new Dictionary<string, string>
        {
            ["-name"] = "-Filter",
            ["-type"] = "",  // handled specially
        });
    }

    /// <summary>
    /// Register a custom alias (from config or user command).
    /// </summary>
    public void RegisterAlias(string alias, string command)
    {
        _commands[alias] = new CommandMapping(alias, command, new());
    }

    /// <summary>
    /// Get all registered command names (for tab completion).
    /// </summary>
    public IEnumerable<string> GetCommandNames()
    {
        return _commands.Keys.OrderBy(k => k);
    }

    /// <summary>
    /// Get all command mappings (for the 'alias' built-in).
    /// </summary>
    public IReadOnlyDictionary<string, CommandMapping> GetMappings()
    {
        return _commands;
    }

    private void Register(string alias, string? cmdlet, Dictionary<string, string>? flagMap = null, bool quotePositionalArgs = false)
    {
        _commands[alias] = new CommandMapping(alias, cmdlet, flagMap ?? new(), quotePositionalArgs);
    }

    /// <summary>
    /// Split input on pipe characters, respecting quotes.
    /// </summary>
    private static string[] SplitOnPipe(string input)
    {
        var segments = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inSingleQuote = false;
        bool inDoubleQuote = false;

        foreach (var ch in input)
        {
            if (ch == '\'' && !inDoubleQuote)
                inSingleQuote = !inSingleQuote;
            else if (ch == '"' && !inSingleQuote)
                inDoubleQuote = !inDoubleQuote;

            if (ch == '|' && !inSingleQuote && !inDoubleQuote)
            {
                segments.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(ch);
            }
        }

        if (current.Length > 0)
            segments.Add(current.ToString());

        return segments.ToArray();
    }

    private static string[] SplitCommandLine(string input)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inSingleQuote = false;
        bool inDoubleQuote = false;

        foreach (var ch in input)
        {
            if (ch == '\'' && !inDoubleQuote)
            {
                inSingleQuote = !inSingleQuote;
                current.Append(ch);
            }
            else if (ch == '"' && !inSingleQuote)
            {
                inDoubleQuote = !inDoubleQuote;
                current.Append(ch);
            }
            else if (ch == ' ' && !inSingleQuote && !inDoubleQuote)
            {
                if (current.Length > 0)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(ch);
            }
        }

        if (current.Length > 0)
            parts.Add(current.ToString());

        return parts.ToArray();
    }
}

/// <summary>
/// Mapping from a Unix-style command + flags to a PowerShell cmdlet + parameters.
/// </summary>
public class CommandMapping
{
    public string Alias { get; }
    public string? Cmdlet { get; }
    public Dictionary<string, string> FlagMap { get; }
    public bool QuotePositionalArgs { get; }

    public CommandMapping(string alias, string? cmdlet, Dictionary<string, string> flagMap, bool quotePositionalArgs = false)
    {
        Alias = alias;
        Cmdlet = cmdlet;
        FlagMap = flagMap;
        QuotePositionalArgs = quotePositionalArgs;
    }

    /// <summary>
    /// Translate Unix-style arguments into a full PowerShell command string.
    /// Returns null if this should be passed through to native execution.
    /// </summary>
    public string? Translate(string[] args)
    {
        if (Cmdlet == null) return null; // Explicit passthrough

        var psArgs = new List<string>();
        var positionalArgs = new List<string>();

        foreach (var arg in args)
        {
            if (arg.StartsWith('-') && FlagMap.TryGetValue(arg, out var psFlag))
            {
                if (!string.IsNullOrEmpty(psFlag))
                    psArgs.Add(psFlag);
            }
            else if (arg.StartsWith('-'))
            {
                // Unknown flag — pass it through as-is to PowerShell
                psArgs.Add(arg);
            }
            else
            {
                positionalArgs.Add(arg);
            }
        }

        var parts = new List<string> { Cmdlet };
        parts.AddRange(psArgs);

        if (QuotePositionalArgs && positionalArgs.Count > 0)
        {
            // Join all positional args into a single quoted string
            var joined = string.Join(' ', positionalArgs);
            parts.Add($"'{joined}'");
        }
        else
        {
            parts.AddRange(positionalArgs);
        }

        return string.Join(' ', parts);
    }
}

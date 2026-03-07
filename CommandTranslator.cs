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

        // Special: count after a pipe → Measure-Object then Count property
        // Syntax: ls | count → count of items
        if (isAfterPipe && command.Equals("count", StringComparison.OrdinalIgnoreCase))
            return "Measure-Object | ForEach-Object { $_.Count }";

        // Special: first/last as aliases for head/tail in pipes
        if (isAfterPipe && command.Equals("first", StringComparison.OrdinalIgnoreCase))
        {
            var count = args.Length > 0 ? args[0] : "1";
            return $"Select-Object -First {count}";
        }
        if (isAfterPipe && command.Equals("last", StringComparison.OrdinalIgnoreCase))
        {
            var count = args.Length > 0 ? args[0] : "1";
            return $"Select-Object -Last {count}";
        }

        // Special: skip after a pipe → Select-Object -Skip N
        if (isAfterPipe && command.Equals("skip", StringComparison.OrdinalIgnoreCase))
        {
            var count = args.Length > 0 ? args[0] : "1";
            return $"Select-Object -Skip {count}";
        }

        // Special: tee after a pipe → Tee-Object -FilePath
        // Syntax: ls | tee output.txt → saves to file AND passes through pipeline
        if (isAfterPipe && command.Equals("tee", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length > 0)
            {
                bool append = args.Contains("-a");
                var file = args.FirstOrDefault(a => !a.StartsWith('-')) ?? args[0];
                var cmd = $"Tee-Object -FilePath {file}";
                if (append) cmd += " -Append";
                return cmd;
            }
            return null;
        }

        // Special: distinct after a pipe → Group-Object + ForEach-Object
        // Like uniq but works on unsorted data
        if (isAfterPipe && command.Equals("distinct", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length > 0)
                return $"Sort-Object -Property {args[0]} -Unique";
            return "Sort-Object -Unique";
        }

        // Special: sum/avg/min/max after a pipe — quick math on properties
        if (isAfterPipe && command.Equals("sum", StringComparison.OrdinalIgnoreCase) && args.Length > 0)
            return $"Measure-Object -Property {args[0]} -Sum | ForEach-Object {{ $_.Sum }}";
        if (isAfterPipe && command.Equals("avg", StringComparison.OrdinalIgnoreCase) && args.Length > 0)
            return $"Measure-Object -Property {args[0]} -Average | ForEach-Object {{ $_.Average }}";
        if (isAfterPipe && command.Equals("min", StringComparison.OrdinalIgnoreCase) && args.Length > 0)
            return $"Measure-Object -Property {args[0]} -Minimum | ForEach-Object {{ $_.Minimum }}";
        if (isAfterPipe && command.Equals("max", StringComparison.OrdinalIgnoreCase) && args.Length > 0)
            return $"Measure-Object -Property {args[0]} -Maximum | ForEach-Object {{ $_.Maximum }}";

        // Special: where after a pipe → Where-Object with Unix-style operators
        // Syntax: where PROPERTY OPERATOR VALUE
        // Example: ps | where CPU > 10  →  Where-Object { $_.CPU -gt 10 }
        if (isAfterPipe && command.Equals("where", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length >= 3)
            {
                var prop = args[0];
                var op = TranslateWhereOperator(args[1]);
                var val = string.Join(' ', args[2..]);

                // Don't quote if already quoted or numeric/PS literal
                if (!val.StartsWith('\'') && !val.StartsWith('"') && !IsNumericOrPsLiteral(val))
                    val = $"'{val}'";

                return $"Where-Object {{ $_.{prop} {op} {val} }}";
            }
            // Fall through to standard translation for PS-style where
        }

        // Special: select after a pipe → Select-Object
        // Syntax: select PROPERTY1, PROPERTY2 or select -first N
        if (isAfterPipe && command.Equals("select", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length > 0)
                return $"Select-Object {string.Join(' ', args)}";
            return null;
        }

        // Special: as after a pipe → format conversion
        // Syntax: as json | as csv | as table | as list
        if (isAfterPipe && command.Equals("as", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length > 0)
            {
                var result = args[0].ToLowerInvariant() switch
                {
                    "json" => "ConvertTo-Json -Depth 5",
                    "csv" => "ConvertTo-Csv -NoTypeInformation",
                    "table" => "Format-Table -AutoSize",
                    "list" => "Format-List",
                    _ => (string?)null
                };
                if (result != null) return result;
            }
        }

        // Special: from after a pipe → parse conversion
        // Syntax: from json | from csv
        if (isAfterPipe && command.Equals("from", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length > 0)
            {
                var result = args[0].ToLowerInvariant() switch
                {
                    "json" => "ConvertFrom-Json",
                    "csv" => "ConvertFrom-Csv",
                    _ => (string?)null
                };
                if (result != null) return result;
            }
        }

        // Special: json — read and parse JSON files
        if (!isAfterPipe && command.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length > 0)
                return $"Get-Content {string.Join(' ', args)} | ConvertFrom-Json";
            return null;
        }
        if (isAfterPipe && command.Equals("json", StringComparison.OrdinalIgnoreCase))
            return "ConvertFrom-Json";

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

        // Pipe shorthands (also register for standalone use / tab completion)
        Register("where", "Where-Object");
        Register("select", "Select-Object");
        Register("sort", "Sort-Object");
        Register("count", null); // Special handling in TranslateSegment
        Register("first", null); // Special handling
        Register("last", null);  // Special handling
        Register("skip", null);  // Special handling
        Register("tee", null);   // Special handling
        Register("distinct", null); // Special handling
        Register("sum", null);   // Special handling
        Register("avg", null);   // Special handling
        Register("min", null);   // Special handling
        Register("max", null);   // Special handling
        Register("json", null);  // Special handling in TranslateSegment
    }

    // ── Where Operator Translation ──────────────────────────────────────

    private static string TranslateWhereOperator(string op) => op switch
    {
        ">" => "-gt",
        "<" => "-lt",
        ">=" => "-ge",
        "<=" => "-le",
        "=" or "==" => "-eq",
        "!=" => "-ne",
        "~" or "=~" => "-match",
        "!~" => "-notmatch",
        "contains" => "-contains",
        _ => op // Pass through PS-style operators (-eq, -gt, etc.)
    };

    private static bool IsNumericOrPsLiteral(string val)
    {
        if (double.TryParse(val, out _)) return true;
        if (val.StartsWith('$')) return true; // PS variable
        // PS size literals: 100KB, 50MB, 1GB, etc.
        if (val.Length > 2)
        {
            var suffix = val[^2..].ToUpperInvariant();
            if (suffix is "KB" or "MB" or "GB" or "TB")
                return double.TryParse(val[..^2], out _);
        }
        return false;
    }

    /// <summary>
    /// Register a custom alias (from config or user command).
    /// </summary>
    public void RegisterAlias(string alias, string command)
    {
        _commands[alias] = new CommandMapping(alias, command, new());
    }

    /// <summary>
    /// Check if a command is registered (for syntax highlighting).
    /// </summary>
    public bool IsKnownCommand(string command) => _commands.ContainsKey(command);

    /// <summary>
    /// Get all registered command names (for tab completion).
    /// </summary>
    public IEnumerable<string> GetCommandNames()
    {
        return _commands.Keys.OrderBy(k => k);
    }

    /// <summary>Get known flags for a command (e.g., "-l", "-a" for ls).</summary>
    public IEnumerable<string> GetFlagsForCommand(string command)
    {
        if (_commands.TryGetValue(command, out var mapping))
            return mapping.FlagMap.Keys;
        return Enumerable.Empty<string>();
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
    /// <summary>
    /// Check if a command string contains an unquoted pipe character.
    /// Used to determine whether PowerShell is needed for pipeline execution.
    /// </summary>
    public static bool HasUnquotedPipe(string input) => SplitOnPipe(input).Length > 1;

    /// <summary>
    /// Check if a command contains unquoted shell redirection operators (>, >>).
    /// Used to determine whether PowerShell is needed for redirection.
    /// </summary>
    public static bool HasUnquotedRedirection(string input)
    {
        bool inSingleQuote = false;
        bool inDoubleQuote = false;
        for (int i = 0; i < input.Length; i++)
        {
            var ch = input[i];
            if (ch == '\'' && !inDoubleQuote)
                inSingleQuote = !inSingleQuote;
            else if (ch == '"' && !inSingleQuote)
                inDoubleQuote = !inDoubleQuote;
            else if (ch == '>' && !inSingleQuote && !inDoubleQuote)
                return true;
        }
        return false;
    }

    internal static string[] SplitOnPipe(string input)
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

    internal static string[] SplitCommandLine(string input)
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
                // Exact match in flag map (e.g., -l, -a, -la)
                if (!string.IsNullOrEmpty(psFlag))
                    psArgs.Add(psFlag);
            }
            else if (arg.StartsWith('-') && !arg.StartsWith("--") && arg.Length > 2)
            {
                // Decompose combined single-char flags: -lah → -l, -a, -h
                foreach (var ch in arg[1..])
                {
                    var singleFlag = $"-{ch}";
                    if (FlagMap.TryGetValue(singleFlag, out var mapped))
                    {
                        if (!string.IsNullOrEmpty(mapped))
                            psArgs.Add(mapped);
                    }
                    // Unknown single-char flags silently ignored (e.g., -h for ls)
                }
            }
            else if (arg.StartsWith('-'))
            {
                // Long flag or unrecognized — pass through to PowerShell
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

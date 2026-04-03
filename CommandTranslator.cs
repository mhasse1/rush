using System.Text.RegularExpressions;

namespace Rush;

/// <summary>
/// Translates concise Unix-style commands and flags into PowerShell cmdlet calls.
/// This is the core of Rush's syntax layer.
/// </summary>
public class CommandTranslator
{
    private readonly Dictionary<string, CommandMapping> _commands = new(StringComparer.OrdinalIgnoreCase);
    private readonly ObjectifyConfig? _objectifyConfig;

    /// <summary>Track the first segment's command for --save support.</summary>
    private string? _lastPipelineFirstCommand;

    public CommandTranslator(ObjectifyConfig? objectifyConfig = null)
    {
        _objectifyConfig = objectifyConfig;
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

        // Track first command for objectify --save support
        _lastPipelineFirstCommand = SplitCommandLine(segments[0].Trim()).FirstOrDefault();

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

            // Auto-objectify: if first segment is a known tabular command and has
            // downstream pipe consumers, inject objectify block transparently.
            // This makes "netstat | where State == LISTEN" just work.
            if (i == 0 && segments.Length > 1 && _objectifyConfig != null)
            {
                var cmdLine = segment;
                if (_objectifyConfig.TryGetHint(cmdLine, out var hintFlags))
                {
                    // Add the original command, then inject objectify block
                    var cmdResult = TranslateSegment(segment, isAfterPipe: false);
                    translated.Add(cmdResult ?? segment);
                    translated.Add(GenerateObjectify(hintFlags));
                    anyTranslated = true;
                    continue;
                }
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

        // wc: fall through to native (Unix) or coreutils shim (Windows).
        // Previously translated to Measure-Object, but that produces PowerShell-
        // formatted output with extra blank columns — not the Unix experience.

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

        // Special: each after a pipe → ForEach-Object with body
        // Syntax: ls | each { Write-Host "file: $it" }
        // $it is automatically replaced with $_ (current pipeline item)
        // Note: |var| block parameter syntax can't be used here because
        // pipes are split before translation. Use $it or $_ directly.
        if (isAfterPipe && command.Equals("each", StringComparison.OrdinalIgnoreCase))
        {
            var body = string.Join(' ', args);
            if (body.StartsWith('{') && body.EndsWith('}'))
                body = body[1..^1].Trim();

            // Replace $it with $_ for PowerShell pipeline variable
            body = body.Replace("$it", "$_");

            return $"ForEach-Object {{ {body} }}";
        }

        // Special: times after a pipe → repeat input N times
        // Syntax: echo "hi" | times 3  →  outputs "hi" three times
        if (isAfterPipe && command.Equals("times", StringComparison.OrdinalIgnoreCase))
        {
            var count = args.Length > 0 ? args[0] : "2";
            return $"ForEach-Object {{ $line = $_; 1..{count} | ForEach-Object {{ $line }} }}";
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
            // Single-arg regex: where /pattern/ → Where-Object { $_ -match 'pattern' }
            if (args.Length == 1 && args[0].StartsWith('/') && args[0].EndsWith('/') && args[0].Length > 2)
            {
                var regex = args[0][1..^1];
                return $"Where-Object {{ $_ -match '{regex}' }}";
            }
            // Two-arg with regex: where PROPERTY /pattern/
            if (args.Length == 2 && args[1].StartsWith('/') && args[1].EndsWith('/') && args[1].Length > 2)
            {
                var prop = args[0];
                var regex = args[1][1..^1];
                return $"Where-Object {{ $_.{prop} -match '{regex}' }}";
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

        // Special: objectify after a pipe → parse text lines into PSCustomObjects
        // Syntax: command | objectify [--fixed] [--delim REGEX] [--cols a,b,c] [--no-header] [--skip N] [--save]
        if (isAfterPipe && command.Equals("objectify", StringComparison.OrdinalIgnoreCase))
        {
            // Handle --save: persist hint to user config, then objectify as usual
            if (args.Contains("--save") && _objectifyConfig != null && _lastPipelineFirstCommand != null)
            {
                var flagsWithoutSave = args.Where(a => a != "--save").ToArray();
                _objectifyConfig.SaveUserHint(_lastPipelineFirstCommand, flagsWithoutSave);
            }
            var objectifyArgs = args.Where(a => a != "--save").ToArray();
            return GenerateObjectify(objectifyArgs);
        }

        // Special: columns after a pipe → select properties by 1-based index
        // Syntax: command | objectify | columns 1,2,5
        if (isAfterPipe && command.Equals("columns", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length > 0)
            {
                try
                {
                    var indices = string.Join(',', args)
                        .Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => int.Parse(s.Trim()) - 1) // 1-based → 0-based
                        .ToArray();
                    var selectExprs = string.Join("; ", indices.Select(idx => $"$__p[{idx}]"));
                    return $"ForEach-Object {{ $__p = @($_.PSObject.Properties.Name); $_ | Select-Object @({selectExprs}) }}";
                }
                catch (FormatException)
                {
                    // Invalid index — fall through to standard translation
                }
            }
            return null;
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
        // Standard Unix commands run natively — no translation needed.
        // PS7 has built-in aliases on Windows (ls→Get-ChildItem, etc.).
        // The isAfterPipe special cases (grep→Where-Object, head→Select-Object,
        // sort→Sort-Object, etc.) are independent of registrations and always
        // work in pipeline context.

        // echo: always translate — needs Write-Output for PS variable expansion
        Register("echo", "Write-Output", quotePositionalArgs: true);

        // Passthrough markers (run natively on all platforms)
        Register("curl", null);
        Register("wget", null);

        // Rush pipeline operators — always registered
        Register("where", "Where-Object");
        Register("select", "Select-Object");
        Register("count", null); // Special handling in TranslateSegment
        Register("first", null); // Special handling
        Register("last", null);  // Special handling
        Register("times", null); // Special handling
        Register("each", null);  // Special handling
        Register("skip", null);  // Special handling
        Register("tee", null);   // Special handling
        Register("distinct", null); // Special handling
        Register("sum", null);   // Special handling
        Register("avg", null);   // Special handling
        Register("min", null);   // Special handling
        Register("max", null);   // Special handling
        Register("json", null);  // Special handling in TranslateSegment
        Register("objectify", null); // Special handling — text → PSCustomObjects
        Register("columns", null);  // Special handling — index-based column selection
    }


    // ── Objectify PS Code Generation ────────────────────────────────────

    /// <summary>
    /// Generate a PowerShell ForEach-Object script block that converts text lines
    /// into PSCustomObjects. Supports whitespace-delimited, fixed-width, and
    /// custom delimiter modes.
    /// </summary>
    internal static string GenerateObjectify(string[] args)
    {
        // Parse flags
        string? delimiter = null;
        bool noHeader = false;
        bool fixedWidth = false;
        string? fixedPositions = null;
        string? columnNames = null;
        int skipLines = 0;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--delim" when i + 1 < args.Length:
                    delimiter = args[++i];
                    break;
                case "--no-header":
                    noHeader = true;
                    break;
                case "--fixed":
                    fixedWidth = true;
                    // Check if next arg is column positions (e.g., "6,13,20")
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                        fixedPositions = args[++i];
                    break;
                case "--cols" when i + 1 < args.Length:
                    columnNames = args[++i];
                    noHeader = true; // --cols implies --no-header
                    break;
                case "--skip" when i + 1 < args.Length:
                    int.TryParse(args[++i], out skipLines);
                    break;
            }
        }

        if (fixedWidth)
            return GenerateFixedWidthObjectify(fixedPositions, columnNames, skipLines);

        return GenerateDelimitedObjectify(delimiter ?? @"\s+", noHeader, columnNames, skipLines);
    }

    /// <summary>Generate objectify for delimited text (whitespace, comma, tab, etc.).</summary>
    private static string GenerateDelimitedObjectify(string delimiter, bool noHeader, string? columnNames, int skipLines)
    {
        // Escape single quotes in delimiter for PS string
        var psDelim = delimiter.Replace("'", "''");

        var headerSetup = noHeader
            ? (columnNames != null
                ? $"$__hdr = @('{string.Join("','", columnNames.Split(',').Select(c => c.Trim().Replace("'", "''")))}')"
                : "$__hdr = @(); for ($__n = 0; $__n -lt (($__oLines[$__ds] -split '{psDelim}').Where({{ $_ -ne '' }}).Count); $__n++) {{ $__hdr += \"col$($__n + 1)\" }}")
            : $"$__hdr = @(($__oLines[$__ds] -split '{psDelim}').Where({{ $_ -ne '' }}))";

        var dataStart = noHeader ? "$__ds" : "($__ds + 1)";

        return "ForEach-Object -Begin { $__oLines = [System.Collections.ArrayList]@() } " +
               "-Process { [void]$__oLines.Add($_.ToString()) } " +
               "-End { " +
               $"$__ds = {skipLines}; " +
               $"if ($__oLines.Count -le $__ds) {{ return }}; " +
               $"{headerSetup}; " +
               $"for ($__i = {dataStart}; $__i -lt $__oLines.Count; $__i++) {{ " +
               $"if ($__oLines[$__i].Trim() -eq '') {{ continue }}; " +
               $"$__v = @(($__oLines[$__i] -split '{psDelim}').Where({{ $_ -ne '' }})); " +
               "$__o = [ordered]@{}; " +
               "for ($__j = 0; $__j -lt $__hdr.Count; $__j++) { " +
               "$__val = if ($__j -eq ($__hdr.Count - 1) -and $__v.Count -gt $__hdr.Count) { " +
               "[string]::Join(' ', $__v[$__j..($__v.Count - 1)]) " +
               "} elseif ($__j -lt $__v.Count) { $__v[$__j] } else { $null }; " +
               "if ($null -ne $__val -and $__val -match '^\\d+$') { $__val = [long]$__val }; " +
               "$__o[$__hdr[$__j]] = $__val " +
               "}; " +
               "[PSCustomObject]$__o " +
               "} " +
               "}";
    }

    /// <summary>Generate objectify for fixed-width column text.</summary>
    private static string GenerateFixedWidthObjectify(string? positions, string? columnNames, int skipLines)
    {
        if (positions != null)
        {
            // Explicit column positions: --fixed 6,13,20
            var posArray = positions.Split(',').Select(p => p.Trim()).ToArray();
            var posStr = string.Join(',', posArray);

            return "ForEach-Object -Begin { $__oLines = [System.Collections.ArrayList]@() } " +
                   "-Process { [void]$__oLines.Add($_.ToString()) } " +
                   "-End { " +
                   $"$__ds = {skipLines}; " +
                   $"if ($__oLines.Count -le $__ds) {{ return }}; " +
                   $"$__starts = @({posStr}); " +
                   // Detect header names from fixed positions
                   (columnNames != null
                       ? $"$__names = @('{string.Join("','", columnNames.Split(',').Select(c => c.Trim().Replace("'", "''")))}')"
                       : "$__names = @(); for ($__c = 0; $__c -lt $__starts.Count; $__c++) { " +
                         "$__end = if ($__c + 1 -lt $__starts.Count) { $__starts[$__c + 1] } else { $__oLines[$__ds].Length }; " +
                         "$__nm = $__oLines[$__ds].Substring($__starts[$__c], [Math]::Max(0, $__end - $__starts[$__c])).Trim(); " +
                         "if ($__nm -eq '') { $__nm = \"col$($__c + 1)\" }; " +
                         "$__names += $__nm }") +
                   "; " +
                   $"for ($__i = {(columnNames != null ? "$__ds" : "($__ds + 1)")}; $__i -lt $__oLines.Count; $__i++) {{ " +
                   "if ($__oLines[$__i].Trim() -eq '') { continue }; " +
                   "$__line = $__oLines[$__i]; " +
                   "$__o = [ordered]@{}; " +
                   "for ($__c = 0; $__c -lt $__starts.Count; $__c++) { " +
                   "if ($__starts[$__c] -ge $__line.Length) { $__o[$__names[$__c]] = $null; continue }; " +
                   "$__end = if ($__c + 1 -lt $__starts.Count) { [Math]::Min($__starts[$__c + 1], $__line.Length) } else { $__line.Length }; " +
                   "$__val = $__line.Substring($__starts[$__c], $__end - $__starts[$__c]).Trim(); " +
                   "if ($__val -match '^\\d+$') { $__val = [long]$__val }; " +
                   "if ($__val -eq '') { $__val = $null }; " +
                   "$__o[$__names[$__c]] = $__val " +
                   "}; " +
                   "[PSCustomObject]$__o " +
                   "} " +
                   "}";
        }

        // Auto-detect fixed-width columns from header character positions
        return "ForEach-Object -Begin { $__oLines = [System.Collections.ArrayList]@() } " +
               "-Process { [void]$__oLines.Add($_.ToString()) } " +
               "-End { " +
               $"$__ds = {skipLines}; " +
               $"if ($__oLines.Count -le $__ds) {{ return }}; " +
               "$__hdr = $__oLines[$__ds]; " +
               // Detect column start positions from header word boundaries
               "$__starts = [System.Collections.ArrayList]@(); " +
               "$__names = [System.Collections.ArrayList]@(); " +
               "$__inWord = $false; " +
               "for ($__c = 0; $__c -lt $__hdr.Length; $__c++) { " +
               "if ($__hdr[$__c] -ne ' ' -and -not $__inWord) { " +
               "[void]$__starts.Add($__c); $__inWord = $true " +
               "} elseif ($__hdr[$__c] -eq ' ' -and $__inWord) { " +
               "[void]$__names.Add($__hdr.Substring($__starts[$__starts.Count-1], $__c - $__starts[$__starts.Count-1]).Trim()); " +
               "$__inWord = $false " +
               "} }; " +
               "if ($__inWord) { [void]$__names.Add($__hdr.Substring($__starts[$__starts.Count-1]).Trim()) }; " +
               // Parse data rows using detected positions
               "for ($__i = ($__ds + 1); $__i -lt $__oLines.Count; $__i++) { " +
               "if ($__oLines[$__i].Trim() -eq '') { continue }; " +
               "$__line = $__oLines[$__i]; " +
               "$__o = [ordered]@{}; " +
               "for ($__c = 0; $__c -lt $__starts.Count; $__c++) { " +
               "if ($__starts[$__c] -ge $__line.Length) { $__o[$__names[$__c]] = $null; continue }; " +
               "$__end = if ($__c + 1 -lt $__starts.Count) { [Math]::Min($__starts[$__c + 1], $__line.Length) } else { $__line.Length }; " +
               "$__val = $__line.Substring($__starts[$__c], $__end - $__starts[$__c]).Trim(); " +
               "if ($__val -match '^\\d+$') { $__val = [long]$__val }; " +
               "if ($__val -eq '') { $__val = $null }; " +
               "$__o[$__names[$__c]] = $__val " +
               "}; " +
               "[PSCustomObject]$__o " +
               "} " +
               "}";
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
        _commands[alias] = new CommandMapping(alias, command, new(), isUserAlias: true);
    }

    /// <summary>
    /// Remove a user alias. Returns true if the alias existed.
    /// </summary>
    public bool UnregisterAlias(string alias)
    {
        if (_commands.TryGetValue(alias, out var mapping) && mapping.IsUserAlias)
            return _commands.Remove(alias);
        return false;
    }

    /// <summary>
    /// Check if a command is a user-defined alias (should run natively, not through PowerShell).
    /// </summary>
    public bool IsUserAlias(string command)
    {
        return _commands.TryGetValue(command, out var mapping) && mapping.IsUserAlias;
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
    public bool IsUserAlias { get; }

    public CommandMapping(string alias, string? cmdlet, Dictionary<string, string> flagMap, bool quotePositionalArgs = false, bool isUserAlias = false)
    {
        Alias = alias;
        Cmdlet = cmdlet;
        FlagMap = flagMap;
        IsUserAlias = isUserAlias;
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
        var redirectionParts = new List<string>();
        bool inRedirection = false;

        foreach (var arg in args)
        {
            // Shell redirection operators — pass through unquoted after the command
            if (!inRedirection && (arg == ">" || arg == ">>" || arg == "2>" || arg == "2>>"))
            {
                inRedirection = true;
                redirectionParts.Add(arg);
                continue;
            }
            if (inRedirection)
            {
                redirectionParts.Add(arg);
                continue;
            }

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
            // Join positional args into a double-quoted string so PowerShell
            // expands $variables: echo $x → Write-Output "$x"
            // Strip outer quotes from individual args to avoid double-wrapping
            var processed = positionalArgs.Select(a =>
            {
                if (a.Length >= 2 &&
                    ((a[0] == '\'' && a[^1] == '\'') || (a[0] == '"' && a[^1] == '"')))
                    return a[1..^1];
                return a;
            });
            var joined = string.Join(' ', processed);
            parts.Add($"\"{joined}\"");
        }
        else
        {
            parts.AddRange(positionalArgs);
        }

        // Append redirection operators after the command (not inside quotes)
        parts.AddRange(redirectionParts);

        return string.Join(' ', parts);
    }
}

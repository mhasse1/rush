namespace Rush;

/// <summary>
/// Orchestrates the Rush scripting language pipeline: triage → lex → parse → transpile.
/// Sits between user input and the PowerShell execution engine.
/// </summary>
public class ScriptEngine
{
    private readonly CommandTranslator _translator;
    private readonly RushTranspiler _transpiler;

    /// <summary>
    /// Rush keywords that start multi-line block constructs.
    /// When a line starts with one of these, the REPL enters block accumulation mode.
    /// </summary>
    private static readonly HashSet<string> BlockStartKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "if", "unless", "for", "while", "until", "loop", "def", "try", "case",
        "begin", "match", "class", "enum",
        "macos", "win64", "win32", "linux"
    };

    /// <summary>
    /// Rush keywords used in single-line contexts (not block starts).
    /// </summary>
    private static readonly HashSet<string> RushKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "if", "elsif", "else", "end", "unless",
        "for", "in", "while", "until", "loop",
        "case", "when", "match",
        "def", "return",
        "try", "rescue", "ensure", "begin",
        "do", "and", "or", "not",
        "true", "false", "nil",
        "next", "continue", "break",
        "class", "attr", "self", "super", "enum",
        "macos", "win64", "win32", "linux"
    };

    /// <summary>
    /// Stdlib class names that, when followed by a dot, indicate Rush syntax.
    /// e.g., File.read("test.txt"), Dir.mkdir("new_dir"), Time.now
    /// </summary>
    private static readonly HashSet<string> StdlibReceivers = new(StringComparer.OrdinalIgnoreCase)
    {
        "File", "Dir", "Time"
    };

    /// <summary>
    /// Rush method names that trigger method-chaining mode when seen after a dot.
    /// </summary>
    private static readonly HashSet<string> RushMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        // Collection/pipeline
        "each", "select", "reject", "map", "flat_map",
        "sort_by", "first", "last", "count",
        "any?", "all?", "group_by", "uniq", "reverse",
        "join", "to_json", "to_csv", "include?",
        "sort", "skip", "skip_while", "push", "compact", "flatten",
        // String methods
        "strip", "lstrip", "rstrip", "upcase", "downcase",
        "split", "split_whitespace", "lines", "trim_end",
        "start_with?", "end_with?", "empty?", "nil?",
        "ljust", "rjust", "replace",
        "sub", "gsub", "scan", "match",
        // Numeric methods
        "round", "abs", "times", "to_currency", "to_filesize", "to_percent",
        // Duration methods
        "hours", "minutes", "seconds", "days",
        // Time stdlib methods
        "now", "utc_now", "today",
        // Type conversion
        "to_i", "to_f", "to_s",
        // Color methods
        "red", "green", "blue", "cyan", "yellow", "magenta", "white", "gray",
        // Class instantiation
        "new"
    };

    public ScriptEngine(CommandTranslator translator)
    {
        _translator = translator;
        _transpiler = new RushTranspiler(translator);
    }

    /// <summary>
    /// Determine if input is Rush scripting syntax (vs a shell command).
    /// This is the critical triage function.
    ///
    /// Rules:
    /// 1. Starts with a Rush block keyword (if, for, while, def, try, case, unless, until)
    /// 2. Matches assignment pattern: IDENTIFIER = EXPR (where IDENTIFIER is not a known command/path)
    /// 3. Contains .method() with a Rush method name (each, select, map, etc.)
    /// 4. Starts with 'end' (closing a block)
    /// 5. Starts with 'return'
    ///
    /// Everything else passes through to the existing shell pipeline.
    /// </summary>
    public bool IsRushSyntax(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return false;

        var trimmed = input.TrimStart();

        // Get the first word
        var firstSpace = trimmed.IndexOfAny(new[] { ' ', '\t', '(' });
        var firstWord = firstSpace >= 0 ? trimmed[..firstSpace] : trimmed;

        // Rule 1: Block-start keywords (also check base word before '.' for platform.property syntax)
        if (BlockStartKeywords.Contains(firstWord))
            return true;
        var dotIdx = firstWord.IndexOf('.');
        if (dotIdx > 0 && BlockStartKeywords.Contains(firstWord[..dotIdx]))
            return true;

        // Rule 4: 'end' keyword (closing a block in REPL)
        if (firstWord.Equals("end", StringComparison.OrdinalIgnoreCase))
            return true;

        // Rule 5: 'return' keyword
        if (firstWord.Equals("return", StringComparison.OrdinalIgnoreCase))
            return true;

        // Rule 6: Loop control keywords
        if (firstWord.Equals("next", StringComparison.OrdinalIgnoreCase)
            || firstWord.Equals("continue", StringComparison.OrdinalIgnoreCase)
            || firstWord.Equals("break", StringComparison.OrdinalIgnoreCase))
            return true;

        // Rule 7: Built-in functions used without parens
        if (IsBuiltinFunction(firstWord))
            return true;

        // Rule 2: Assignment — IDENTIFIER = EXPR, IDENTIFIER += EXPR,
        //         or multiple: a, b, c = 1, 2, 3
        if (IsMultipleAssignment(trimmed))
            return true;
        if (IsAssignment(trimmed, firstWord))
            return true;

        // Rule 8: Compound assignment — IDENTIFIER += EXPR or IDENTIFIER -= EXPR
        if (IsCompoundAssignment(trimmed, firstWord))
            return true;

        // Rule 3: Method chaining — contains .rushMethod{ or .rushMethod(
        if (ContainsRushMethodCall(trimmed))
            return true;

        // Rule 9: Stdlib receiver — File.xxx, Dir.xxx, Time.xxx
        if (ContainsStdlibCall(trimmed))
            return true;

        // Rule 10: Method call on variable — identifier.method() pattern
        // e.g., c.increment(), person.greet(), obj.get_value()
        // Safe: no shell command uses variable.method() syntax
        if (IsMethodCallOnVariable(trimmed))
            return true;

        // Rule 11: Static method/property on uppercase receiver — ClassName.method() or ClassName.prop
        // e.g., Math.add(2, 3), Color.red — no shell command starts with UppercaseWord.identifier
        if (IsClassMemberAccess(trimmed))
            return true;

        return false;
    }

    /// <summary>
    /// Check if input looks like an assignment: bare_word = expr
    /// where bare_word is a plain identifier and = immediately follows.
    /// The pattern WORD = EXPR is unambiguously an assignment — no shell command
    /// takes "= something" as its first arguments. This is safe even when WORD
    /// matches a known command name (e.g., count = 0, sort = "name").
    /// </summary>
    /// <summary>
    /// Check if input is a multiple assignment: a, b, c = 1, 2, 3
    /// </summary>
    private static bool IsMultipleAssignment(string input)
    {
        // Must have both , and = with comma before =
        var commaPos = input.IndexOf(',');
        var eqPos = input.IndexOf('=');
        if (commaPos < 0 || eqPos < 0 || commaPos >= eqPos) return false;

        // The = must not be part of ==, !=, >=, <=
        if (eqPos > 0 && input[eqPos - 1] is '!' or '<' or '>') return false;
        if (eqPos + 1 < input.Length && input[eqPos + 1] == '=') return false;

        // Everything before = must be comma-separated identifiers
        var leftSide = input[..eqPos].TrimEnd();
        var parts = leftSide.Split(',');
        if (parts.Length < 2) return false;

        foreach (var part in parts)
        {
            var name = part.Trim();
            if (name.Length == 0) return false;
            if (!char.IsLetter(name[0]) && name[0] != '_') return false;
            if (name.Any(c => !char.IsLetterOrDigit(c) && c != '_')) return false;
        }

        return true;
    }

    private bool IsAssignment(string input, string firstWord)
    {
        // Must have = somewhere after the first word
        var eqPos = input.IndexOf('=');
        if (eqPos < 0) return false;

        // The = must not be part of ==, !=, >=, <=
        if (eqPos > 0 && input[eqPos - 1] is '!' or '<' or '>') return false;
        if (eqPos + 1 < input.Length && input[eqPos + 1] == '=') return false;

        // First word must be a plain identifier (no -, /, .)
        if (firstWord.Length == 0) return false;
        if (!char.IsLetter(firstWord[0]) && firstWord[0] != '_') return false;
        if (firstWord.Any(c => c is '-' or '/' or '\\')) return false;

        // Must NOT be a shell builtin (cd, export, set, etc. — these have
        // special semantics where = could be meaningful)
        if (IsShellBuiltin(firstWord)) return false;

        // The = must come right after the first word (with optional spaces)
        var afterWord = input[firstWord.Length..].TrimStart();
        if (afterWord.Length == 0 || afterWord[0] != '=') return false;

        // Not ==
        if (afterWord.Length > 1 && afterWord[1] == '=') return false;

        return true;
    }

    /// <summary>
    /// Check if input contains a Rush method call (e.g., .each { or .select {)
    /// Only matches when the receiver is in expression position (start of line,
    /// or after =, (, [, comma) — NOT when it's a shell argument like `cat report.sort`.
    /// </summary>
    private static bool ContainsRushMethodCall(string input)
    {
        int dotPos = input.IndexOf('.');
        while (dotPos >= 0 && dotPos < input.Length - 1)
        {
            // The char before the dot must be a valid identifier char
            if (dotPos > 0 && (char.IsLetterOrDigit(input[dotPos - 1]) || input[dotPos - 1] == '_'))
            {
                // Walk back to find the start of the receiver token
                int recStart = dotPos - 1;
                while (recStart > 0 && (char.IsLetterOrDigit(input[recStart - 1]) || input[recStart - 1] == '_'))
                    recStart--;

                // Receiver must be at start of input, or preceded by an expression
                // context char (=, (, [, comma). This prevents matching shell
                // arguments like `cat report.sort` where report is after a space
                // following a command word.
                bool validPosition = recStart == 0;
                if (!validPosition && recStart > 0)
                {
                    // Skip whitespace backwards to find the context char
                    int ctx = recStart - 1;
                    while (ctx >= 0 && input[ctx] == ' ') ctx--;
                    if (ctx >= 0)
                        validPosition = input[ctx] is '=' or '(' or '[' or ',';
                }

                if (validPosition)
                {
                    // Extract the method name after the dot
                    int nameStart = dotPos + 1;
                    int nameEnd = nameStart;
                    while (nameEnd < input.Length && (char.IsLetterOrDigit(input[nameEnd]) || input[nameEnd] == '_' || input[nameEnd] == '?'))
                        nameEnd++;

                    if (nameEnd > nameStart)
                    {
                        var methodName = input[nameStart..nameEnd];
                        if (RushMethods.Contains(methodName))
                            return true;
                    }
                }
            }

            // Advance to next dot
            dotPos = input.IndexOf('.', dotPos + 1);
        }
        return false;
    }

    /// <summary>
    /// Check if input starts with a stdlib receiver call (e.g., File.read, Dir.mkdir).
    /// Only matches the first word before the first dot — precise to avoid false positives.
    /// </summary>
    private static bool ContainsStdlibCall(string input)
    {
        int dotPos = input.IndexOf('.');
        if (dotPos > 0 && dotPos < input.Length - 1)
        {
            var receiver = input[..dotPos];
            if (StdlibReceivers.Contains(receiver))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Check if input looks like a method call on a variable: identifier.method(...)
    /// e.g., c.increment(), person.greet(), obj.get_value()
    /// The receiver must start with a lowercase letter or underscore (not a path like ./script
    /// or a command flag). This is unambiguous — no shell command uses this syntax.
    /// </summary>
    private static bool IsMethodCallOnVariable(string input)
    {
        // Must start with a lowercase letter or underscore (variable name)
        if (input.Length == 0 || !(char.IsLower(input[0]) || input[0] == '_'))
            return false;

        // Find the dot
        int dotPos = input.IndexOf('.');
        if (dotPos <= 0 || dotPos >= input.Length - 1) return false;

        // Receiver must be a simple identifier (letters, digits, underscores only)
        for (int i = 0; i < dotPos; i++)
        {
            char ch = input[i];
            if (!char.IsLetterOrDigit(ch) && ch != '_') return false;
        }

        // After dot, must have an identifier followed by '('
        int nameEnd = dotPos + 1;
        while (nameEnd < input.Length && (char.IsLetterOrDigit(input[nameEnd]) || input[nameEnd] == '_'))
            nameEnd++;

        if (nameEnd == dotPos + 1) return false; // no method name
        if (nameEnd >= input.Length || input[nameEnd] != '(') return false;

        return true;
    }

    /// <summary>
    /// Check if input starts with an uppercase identifier followed by .member — indicating
    /// a class static method call (Math.add()) or enum/class property access (Color.red).
    /// This is unambiguous: no shell command starts with UppercaseWord.identifier.
    /// </summary>
    private static bool IsClassMemberAccess(string input)
    {
        if (input.Length == 0 || !char.IsUpper(input[0]))
            return false;

        int dotPos = input.IndexOf('.');
        if (dotPos <= 0 || dotPos >= input.Length - 1) return false;

        // Receiver must be a simple identifier (letters, digits, underscores only)
        for (int i = 0; i < dotPos; i++)
        {
            char ch = input[i];
            if (!char.IsLetterOrDigit(ch) && ch != '_') return false;
        }

        // After dot, must have a letter (method/property name start)
        if (!char.IsLetter(input[dotPos + 1]) && input[dotPos + 1] != '_')
            return false;

        // Exclude known stdlib receivers — they're already handled by ContainsStdlibCall
        var receiver = input[..dotPos];
        if (StdlibReceivers.Contains(receiver))
            return false;

        // Exclude ALL_CAPS names (ARGV, PATH) — these are variables, not class names
        // Class names are PascalCase and contain at least one lowercase letter
        if (!receiver.Any(char.IsLower))
            return false;

        return true;
    }

    /// <summary>
    /// Check if input looks like a compound assignment: bare_word += expr or bare_word -= expr
    /// </summary>
    private bool IsCompoundAssignment(string input, string firstWord)
    {
        if (firstWord.Length == 0 || !char.IsLetter(firstWord[0]) && firstWord[0] != '_')
            return false;
        if (firstWord.Any(c => c is '-' or '/' or '\\'))
            return false;

        var afterWord = input[firstWord.Length..].TrimStart();
        return afterWord.StartsWith("+=") || afterWord.StartsWith("-=");
    }

    /// <summary>
    /// Rush built-in functions that should be parsed as Rush syntax, not shell commands.
    /// These are recognized when used without parentheses: puts "hello", warn "error", etc.
    /// </summary>
    private static bool IsBuiltinFunction(string word)
    {
        return word.Equals("puts", StringComparison.OrdinalIgnoreCase)
            || word.Equals("warn", StringComparison.OrdinalIgnoreCase)
            || word.Equals("die", StringComparison.OrdinalIgnoreCase)
            || word.Equals("print", StringComparison.OrdinalIgnoreCase)
            || word.Equals("ask", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsShellBuiltin(string word)
    {
        return word.Equals("cd", StringComparison.OrdinalIgnoreCase)
            || word.Equals("exit", StringComparison.OrdinalIgnoreCase)
            || word.Equals("quit", StringComparison.OrdinalIgnoreCase)
            || word.Equals("help", StringComparison.OrdinalIgnoreCase)
            || word.Equals("history", StringComparison.OrdinalIgnoreCase)
            || word.Equals("alias", StringComparison.OrdinalIgnoreCase)
            || word.Equals("export", StringComparison.OrdinalIgnoreCase)
            || word.Equals("unset", StringComparison.OrdinalIgnoreCase)
            || word.Equals("source", StringComparison.OrdinalIgnoreCase)
            || word.Equals("reload", StringComparison.OrdinalIgnoreCase)
            || word.Equals("clear", StringComparison.OrdinalIgnoreCase)
            || word.Equals("set", StringComparison.OrdinalIgnoreCase)
            || word.Equals("jobs", StringComparison.OrdinalIgnoreCase)
            || word.Equals("fg", StringComparison.OrdinalIgnoreCase)
            || word.Equals("bg", StringComparison.OrdinalIgnoreCase)
            || word.Equals("kill", StringComparison.OrdinalIgnoreCase)
            || word.Equals("sync", StringComparison.OrdinalIgnoreCase)
            || word.Equals("pushd", StringComparison.OrdinalIgnoreCase)
            || word.Equals("popd", StringComparison.OrdinalIgnoreCase)
            || word.Equals("dirs", StringComparison.OrdinalIgnoreCase)
            || word.Equals("wait", StringComparison.OrdinalIgnoreCase)
            || word.Equals("unalias", StringComparison.OrdinalIgnoreCase)
            || word.Equals("printf", StringComparison.OrdinalIgnoreCase)
            || word.Equals("read", StringComparison.OrdinalIgnoreCase)
            || word.Equals("exec", StringComparison.OrdinalIgnoreCase)
            || word.Equals("trap", StringComparison.OrdinalIgnoreCase)
            || word.Equals("path", StringComparison.OrdinalIgnoreCase)
            || word.Equals("sql", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Check if the input is an incomplete Rush construct that needs more lines.
    /// Counts block-start keywords vs 'end' keywords.
    /// </summary>
    /// <summary>
    /// Get the current block nesting depth of the input.
    /// Returns -1 if lexing fails (treat as incomplete).
    /// </summary>
    public int GetBlockDepth(string input)
    {
        int depth = 0;
        try
        {
            var lexer = new Lexer(input);
            var tokens = lexer.Tokenize();

            foreach (var token in tokens)
            {
                switch (token.Type)
                {
                    case RushTokenType.If:
                    case RushTokenType.Unless:
                    case RushTokenType.For:
                    case RushTokenType.While:
                    case RushTokenType.Until:
                    case RushTokenType.Loop:
                    case RushTokenType.Def:
                    case RushTokenType.Try:
                    case RushTokenType.Begin:
                    case RushTokenType.Case:
                    case RushTokenType.Do:
                    case RushTokenType.Class:
                    case RushTokenType.Enum:
                    case RushTokenType.Macos:
                    case RushTokenType.Win64:
                    case RushTokenType.Win32:
                    case RushTokenType.Linux:
                        depth++;
                        break;
                    case RushTokenType.End:
                        depth--;
                        break;
                }
            }
        }
        catch
        {
            return -1;
        }

        return depth;
    }

    public bool IsIncomplete(string input) => GetBlockDepth(input) > 0;

    /// <summary>
    /// Parse and transpile a single REPL input (may be multi-line) to PowerShell code.
    /// </summary>
    public string? TranspileLine(string input)
    {
        try
        {
            var lexer = new Lexer(input);
            var tokens = lexer.Tokenize();
            var parser = new Parser(tokens, input);
            var statements = parser.Parse();

            if (statements.Count == 0)
                return null;

            return _transpiler.Transpile(statements);
        }
        catch (RushParseException ex)
        {
            // Return error as a PowerShell Write-Error so it renders through the normal error path
            var escaped = ex.Message.Replace("'", "''");
            var hint = "";
            if (ex.Message.Contains("Pipe") && input.Contains("ai"))
                hint = " Tip: to send code to ai, save it to a file first: ai \"question\" < file.rush";
            return $"Write-Error 'Rush syntax error: {escaped}{hint}'";
        }
    }

    /// <summary>
    /// Parse and transpile an entire .rush script file to PowerShell code.
    /// Uses per-line triage to handle mixed Rush syntax and shell commands.
    /// Rush blocks (if/for/while/def/etc.) are accumulated until complete,
    /// while shell commands are translated through CommandTranslator.
    /// </summary>
    public string? TranspileFile(string source)
    {
        try
        {
            // Strip shebang if present
            if (source.StartsWith("#!"))
            {
                var firstNewline = source.IndexOf('\n');
                if (firstNewline >= 0)
                    source = source[(firstNewline + 1)..];
            }

            // Normalize line endings and split
            source = source.Replace("\r\n", "\n");
            var lines = source.Split('\n');
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("$ErrorActionPreference = 'Stop'");

            // Accumulator for multi-line Rush blocks (if/for/while/def/begin/match/etc.)
            var rushBlock = new System.Text.StringBuilder();

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.TrimStart();

                // If accumulating a multi-line Rush block, add every line
                // (including empty lines, comments, and lines that look like shell commands)
                if (rushBlock.Length > 0)
                {
                    rushBlock.Append(line);
                    rushBlock.Append('\n');
                    if (!IsIncomplete(rushBlock.ToString()))
                    {
                        // Block is complete — transpile the accumulated Rush code
                        var transpiled = TranspileLine(rushBlock.ToString().TrimEnd());
                        if (transpiled != null)
                            sb.AppendLine(transpiled);
                        rushBlock.Clear();
                    }
                    continue;
                }

                // Top-level: skip empty lines and comment-only lines
                if (string.IsNullOrWhiteSpace(trimmed))
                    continue;
                if (trimmed.StartsWith('#'))
                    continue;

                // Triage: Rush syntax or shell command?
                if (IsRushSyntax(trimmed))
                {
                    // Start accumulating Rush code
                    rushBlock.Append(line);
                    rushBlock.Append('\n');
                    if (!IsIncomplete(rushBlock.ToString()))
                    {
                        // Single-line Rush statement — transpile immediately
                        var transpiled = TranspileLine(rushBlock.ToString().TrimEnd());
                        if (transpiled != null)
                            sb.AppendLine(transpiled);
                        rushBlock.Clear();
                    }
                }
                else
                {
                    // Shell builtins — translate to PowerShell equivalents
                    var builtinPs = TranslateBuiltin(trimmed);
                    if (builtinPs != null)
                    {
                        sb.AppendLine(builtinPs);
                    }
                    else
                    {
                        // Shell command — translate through CommandTranslator
                        var translated = _translator.Translate(trimmed);
                        sb.AppendLine(translated ?? trimmed);
                    }
                }
            }

            // Handle any remaining incomplete Rush block (unterminated — likely an error)
            if (rushBlock.Length > 0)
            {
                var transpiled = TranspileLine(rushBlock.ToString().TrimEnd());
                if (transpiled != null)
                    sb.AppendLine(transpiled);
            }

            var result = sb.ToString().TrimEnd();
            // Return null if only the ErrorActionPreference header was emitted
            if (result == "$ErrorActionPreference = 'Stop'" || string.IsNullOrWhiteSpace(result))
                return null;
            return result;
        }
        catch (RushParseException ex)
        {
            var escaped = ex.Message.Replace("'", "''");
            return $"Write-Error 'Rush parse error: {escaped}'";
        }
    }

    /// <summary>
    /// Translate shell builtins (export, path add, alias) to PowerShell equivalents
    /// for use in transpiled scripts (init.rush, source'd files).
    /// Returns null if not a recognized builtin.
    /// </summary>
    private static string? TranslateBuiltin(string line)
    {
        // export [--save] VAR=value → set both PowerShell and .NET environment
        if (line.StartsWith("export ", StringComparison.OrdinalIgnoreCase))
        {
            var assignment = line[7..].Trim();
            // Strip --save flag (runtime-only, irrelevant in scripts)
            if (assignment.StartsWith("--save ", StringComparison.OrdinalIgnoreCase) ||
                assignment.StartsWith("--save\t", StringComparison.OrdinalIgnoreCase))
                assignment = assignment[6..].TrimStart();
            var eqPos = assignment.IndexOf('=');
            if (eqPos > 0)
            {
                var varName = assignment[..eqPos].Trim();
                var value = assignment[(eqPos + 1)..].Trim();
                // Strip quotes
                if ((value.StartsWith('"') && value.EndsWith('"')) ||
                    (value.StartsWith('\'') && value.EndsWith('\'')))
                    value = value[1..^1];
                // Handle $PATH / $VAR references → PowerShell $env:VAR
                var psValue = System.Text.RegularExpressions.Regex.Replace(
                    value, @"\$(\w+)", m => "$env:" + m.Groups[1].Value);
                var escaped = psValue.Replace("'", "''");
                return $"$env:{varName} = \"{psValue}\"; [Environment]::SetEnvironmentVariable('{varName}', \"{psValue}\")";
            }
        }

        // unset VAR → remove from both PowerShell and .NET environment
        if (line.StartsWith("unset ", StringComparison.OrdinalIgnoreCase))
        {
            var varName = line[6..].Trim();
            return $"Remove-Item Env:{varName} -ErrorAction SilentlyContinue; [Environment]::SetEnvironmentVariable('{varName}', $null)";
        }

        // path add [--front] [--save] [--name=VAR] <dir> → manipulate path variable
        if (line.StartsWith("path add ", StringComparison.OrdinalIgnoreCase))
        {
            var args = line[9..].Trim();
            bool front = false;
            string targetVar = "PATH";

            // Parse flags (order-independent)
            while (args.StartsWith("--"))
            {
                if (args.StartsWith("--front", StringComparison.OrdinalIgnoreCase))
                {
                    front = true;
                    args = args[7..].TrimStart();
                }
                else if (args.StartsWith("--save", StringComparison.OrdinalIgnoreCase))
                {
                    args = args[6..].TrimStart();
                }
                else if (args.StartsWith("--name=", StringComparison.OrdinalIgnoreCase))
                {
                    var spaceIdx = args.IndexOf(' ');
                    if (spaceIdx < 0) spaceIdx = args.Length;
                    targetVar = args[7..spaceIdx];
                    args = spaceIdx < args.Length ? args[spaceIdx..].TrimStart() : "";
                }
                else break;
            }

            // Strip quotes
            if ((args.StartsWith('"') && args.EndsWith('"')) ||
                (args.StartsWith('\'') && args.EndsWith('\'')))
                args = args[1..^1];

            // Expand ~ to home dir in PowerShell
            var psDir = args.Replace("~", "$HOME");
            // Use ${env:VAR} braces to prevent PowerShell from parsing the colon
            // separator as part of the provider path (e.g., $env:PATH:/dir would
            // try to access "PATH:/dir" in the env provider and return $null).
            if (front)
                return $"$env:{targetVar} = \"{psDir}:${{env:{targetVar}}}\"; [Environment]::SetEnvironmentVariable('{targetVar}', $env:{targetVar})";
            else
                return $"$env:{targetVar} = \"${{env:{targetVar}}}:{psDir}\"; [Environment]::SetEnvironmentVariable('{targetVar}', $env:{targetVar})";
        }

        // alias name='command' → CommandTranslator handled at runtime, skip in scripts
        if (line.StartsWith("alias ", StringComparison.OrdinalIgnoreCase))
        {
            // Aliases are loaded from config.json; in scripts, they're a no-op
            // but emit a comment so the user knows
            return "# alias (handled by config.json at startup)";
        }

        return null;
    }

    /// <summary>
    /// Get all Rush keywords (for syntax highlighting and tab completion).
    /// </summary>
    public static IEnumerable<string> GetKeywords() => RushKeywords;

    /// <summary>
    /// Get all Rush method names (for tab completion after dots).
    /// </summary>
    public static IEnumerable<string> GetMethodNames() => RushMethods;
}

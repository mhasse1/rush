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
        "if", "unless", "for", "while", "until", "def", "try", "case",
        "begin", "match"
    };

    /// <summary>
    /// Rush keywords used in single-line contexts (not block starts).
    /// </summary>
    private static readonly HashSet<string> RushKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "if", "elsif", "else", "end", "unless",
        "for", "in", "while", "until",
        "case", "when", "match",
        "def", "return",
        "try", "rescue", "ensure", "begin",
        "do", "and", "or", "not",
        "true", "false", "nil",
        "next", "continue", "break"
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
        // Type conversion
        "to_i", "to_f", "to_s",
        // Color methods
        "red", "green", "blue", "cyan", "yellow", "magenta", "white", "gray"
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

        // Rule 1: Block-start keywords
        if (BlockStartKeywords.Contains(firstWord))
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

        // Rule 2: Assignment — IDENTIFIER = EXPR or IDENTIFIER += EXPR
        if (IsAssignment(trimmed, firstWord))
            return true;

        // Rule 8: Compound assignment — IDENTIFIER += EXPR or IDENTIFIER -= EXPR
        if (IsCompoundAssignment(trimmed, firstWord))
            return true;

        // Rule 3: Method chaining — contains .rushMethod{ or .rushMethod(
        if (ContainsRushMethodCall(trimmed))
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
    /// </summary>
    private static bool ContainsRushMethodCall(string input)
    {
        int dotPos = input.IndexOf('.');
        while (dotPos >= 0 && dotPos < input.Length - 1)
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

            dotPos = input.IndexOf('.', nameEnd);
        }
        return false;
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
            || word.Equals("dirs", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Check if the input is an incomplete Rush construct that needs more lines.
    /// Counts block-start keywords vs 'end' keywords.
    /// </summary>
    public bool IsIncomplete(string input)
    {
        int depth = 0;
        // Simple keyword counting — handles nesting
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
                    case RushTokenType.Def:
                    case RushTokenType.Try:
                    case RushTokenType.Begin:
                    case RushTokenType.Case:
                    case RushTokenType.Do:
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
            // If lexing fails, assume incomplete (let user keep typing)
            return true;
        }

        return depth > 0;
    }

    /// <summary>
    /// Parse and transpile a single REPL input (may be multi-line) to PowerShell code.
    /// </summary>
    public string? TranspileLine(string input)
    {
        try
        {
            var lexer = new Lexer(input);
            var tokens = lexer.Tokenize();
            var parser = new Parser(tokens);
            var statements = parser.Parse();

            if (statements.Count == 0)
                return null;

            return _transpiler.Transpile(statements);
        }
        catch (RushParseException ex)
        {
            // Return error as a PowerShell Write-Error so it renders through the normal error path
            var escaped = ex.Message.Replace("'", "''");
            return $"Write-Error 'Rush syntax error: {escaped}'";
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
                    // Shell command — translate through CommandTranslator
                    var translated = _translator.Translate(trimmed);
                    sb.AppendLine(translated ?? trimmed);
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
    /// Get all Rush keywords (for syntax highlighting and tab completion).
    /// </summary>
    public static IEnumerable<string> GetKeywords() => RushKeywords;

    /// <summary>
    /// Get all Rush method names (for tab completion after dots).
    /// </summary>
    public static IEnumerable<string> GetMethodNames() => RushMethods;
}

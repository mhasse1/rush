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
        "if", "unless", "for", "while", "until", "def", "try", "case"
    };

    /// <summary>
    /// Rush keywords used in single-line contexts (not block starts).
    /// </summary>
    private static readonly HashSet<string> RushKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "if", "elsif", "else", "end", "unless",
        "for", "in", "while", "until",
        "case", "when",
        "def", "return",
        "try", "rescue", "ensure",
        "do", "and", "or", "not",
        "true", "false", "nil"
    };

    /// <summary>
    /// Rush method names that trigger method-chaining mode when seen after a dot.
    /// </summary>
    private static readonly HashSet<string> RushMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "each", "select", "reject", "map", "flat_map",
        "sort_by", "first", "last", "count",
        "any?", "all?", "group_by", "uniq", "reverse",
        "join", "to_json", "to_csv", "include?"
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

        // Rule 2: Assignment — IDENTIFIER = EXPR
        if (IsAssignment(trimmed, firstWord))
            return true;

        // Rule 3: Method chaining — contains .rushMethod{ or .rushMethod(
        if (ContainsRushMethodCall(trimmed))
            return true;

        return false;
    }

    /// <summary>
    /// Check if input looks like an assignment: bare_word = expr
    /// where bare_word is NOT a known command, NOT a path, NOT a flag.
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

        // First word must NOT be a known shell command
        if (_translator.IsKnownCommand(firstWord)) return false;

        // Must NOT be a shell builtin
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
            || word.Equals("sync", StringComparison.OrdinalIgnoreCase);
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
    /// Adds fail-fast error handling for script mode.
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

            var lexer = new Lexer(source);
            var tokens = lexer.Tokenize();
            var parser = new Parser(tokens);
            var statements = parser.Parse();

            if (statements.Count == 0)
                return null;

            // Script mode: fail-fast on errors
            var psCode = "$ErrorActionPreference = 'Stop'\n" + _transpiler.Transpile(statements);
            return psCode;
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

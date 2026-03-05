namespace Rush;

/// <summary>
/// Real-time syntax highlighting using ANSI escape codes.
/// Colorizes input as the user types — commands, flags, strings, pipes, operators.
/// </summary>
public class SyntaxHighlighter
{
    private readonly CommandTranslator _translator;

    // ANSI color codes — resolved from Theme at access time
    private static string Reset => Theme.Current.AnsiReset;
    private static string Cyan => Theme.Current.AnsiKnownCommand;
    private static string BrightCyan => Theme.Current.AnsiFilePath;
    private static string Yellow => Theme.Current.AnsiFlag;
    private static string Green => Theme.Current.AnsiString;
    private static string Magenta => Theme.Current.AnsiOperator;
    private static string DarkGray => Theme.Current.AnsiPipe;
    private static string White => Theme.Current.AnsiUnknownCommand;
    private static string Bang => Theme.Current.AnsiBang;
    private static string Keyword => Theme.Current.AnsiKeyword;

    private static readonly HashSet<string> BuiltinCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "exit", "quit", "help", "history", "alias", "unalias", "reload", "clear", "cd", "set",
        "as", "from", // Pipe-context format commands
        "export", "unset", "source", // Shell builtins
        "count", "first", "last", "skip", "tee", "distinct", // Pipe utilities
        "sum", "avg", "min", "max", // Math aggregations
        "jobs", "fg", "bg", "wait", // Job control
        "pushd", "popd", "dirs", // Directory stack
        "printf", "read", "exec", "trap", // Shell builtins
        "path" // PATH management
    };

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
        "class", "attr", "self"
    };

    public SyntaxHighlighter(CommandTranslator translator)
    {
        _translator = translator;
    }

    /// <summary>
    /// Return the input string wrapped in ANSI color codes.
    /// ANSI codes are zero-width in the terminal, so cursor math stays correct.
    /// </summary>
    public string Colorize(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;

        var sb = new System.Text.StringBuilder(input.Length * 2);
        var tokens = Tokenize(input);
        bool expectCommand = true;

        foreach (var token in tokens)
        {
            switch (token.Type)
            {
                case TokenType.Pipe:
                    sb.Append(DarkGray).Append(token.Text).Append(Reset);
                    expectCommand = true;
                    break;

                case TokenType.Operator:
                    sb.Append(Magenta).Append(token.Text).Append(Reset);
                    if (token.Text is "&&" or "||" or ";") expectCommand = true;
                    break;

                case TokenType.String:
                    sb.Append(Green).Append(token.Text).Append(Reset);
                    expectCommand = false;
                    break;

                case TokenType.Bang:
                    sb.Append(Bang).Append(token.Text).Append(Reset);
                    break;

                case TokenType.Keyword:
                    sb.Append(Keyword).Append(token.Text).Append(Reset);
                    expectCommand = false;
                    break;

                case TokenType.Regex:
                    sb.Append(Magenta).Append(token.Text).Append(Reset);
                    expectCommand = false;
                    break;

                case TokenType.Flag:
                    sb.Append(Yellow).Append(token.Text).Append(Reset);
                    break;

                case TokenType.Word:
                    if (expectCommand)
                    {
                        if (token.Text.StartsWith('.') && !token.Text.StartsWith(".."))
                            sb.Append(BrightCyan).Append(token.Text).Append(Reset);
                        else if (IsKnownCommand(token.Text))
                            sb.Append(Cyan).Append(token.Text).Append(Reset);
                        else
                            sb.Append(White).Append(token.Text).Append(Reset);
                        expectCommand = false;
                    }
                    else
                    {
                        sb.Append(token.Text);
                    }
                    break;

                case TokenType.Whitespace:
                    sb.Append(token.Text);
                    break;
            }
        }

        return sb.ToString();
    }

    private bool IsKnownCommand(string command)
    {
        return _translator.IsKnownCommand(command) || BuiltinCommands.Contains(command);
    }

    // ── Tokenizer ─────────────────────────────────────────────────────────

    internal enum TokenType { Word, Flag, String, Pipe, Operator, Bang, Whitespace, Keyword, Regex }
    internal record Token(TokenType Type, string Text);

    internal static List<Token> Tokenize(string input)
    {
        var tokens = new List<Token>();
        int i = 0;

        while (i < input.Length)
        {
            char ch = input[i];

            // Whitespace
            if (ch is ' ' or '\t')
            {
                int start = i;
                while (i < input.Length && input[i] is ' ' or '\t') i++;
                tokens.Add(new Token(TokenType.Whitespace, input[start..i]));
                continue;
            }

            // && operator
            if (ch == '&' && i + 1 < input.Length && input[i + 1] == '&')
            {
                tokens.Add(new Token(TokenType.Operator, "&&"));
                i += 2;
                continue;
            }

            // || operator (before single |)
            if (ch == '|' && i + 1 < input.Length && input[i + 1] == '|')
            {
                tokens.Add(new Token(TokenType.Operator, "||"));
                i += 2;
                continue;
            }

            // Single pipe
            if (ch == '|')
            {
                tokens.Add(new Token(TokenType.Pipe, "|"));
                i++;
                continue;
            }

            // 2>&1 redirection
            if (ch == '2' && i + 3 < input.Length
                && input[i + 1] == '>' && input[i + 2] == '&' && input[i + 3] == '1')
            {
                tokens.Add(new Token(TokenType.Operator, "2>&1"));
                i += 4;
                continue;
            }

            // 2>> stderr append redirect
            if (ch == '2' && i + 2 < input.Length
                && input[i + 1] == '>' && input[i + 2] == '>')
            {
                tokens.Add(new Token(TokenType.Operator, "2>>"));
                i += 3;
                continue;
            }

            // 2> stderr redirect
            if (ch == '2' && i + 1 < input.Length && input[i + 1] == '>')
            {
                tokens.Add(new Token(TokenType.Operator, "2>"));
                i += 2;
                continue;
            }

            // >> append redirect
            if (ch == '>' && i + 1 < input.Length && input[i + 1] == '>')
            {
                tokens.Add(new Token(TokenType.Operator, ">>"));
                i += 2;
                continue;
            }

            // > redirect
            if (ch == '>')
            {
                tokens.Add(new Token(TokenType.Operator, ">"));
                i++;
                continue;
            }

            // < stdin redirect
            if (ch == '<')
            {
                tokens.Add(new Token(TokenType.Operator, "<"));
                i++;
                continue;
            }

            // Semicolon separator
            if (ch == ';')
            {
                tokens.Add(new Token(TokenType.Operator, ";"));
                i++;
                continue;
            }

            // Bang expansion: !! or !$
            if (ch == '!' && i + 1 < input.Length && input[i + 1] is '!' or '$')
            {
                tokens.Add(new Token(TokenType.Bang, input[i..(i + 2)]));
                i += 2;
                continue;
            }

            // Quoted strings
            if (ch is '\'' or '"')
            {
                int start = i;
                char quote = ch;
                i++;
                while (i < input.Length && input[i] != quote) i++;
                if (i < input.Length) i++; // closing quote
                tokens.Add(new Token(TokenType.String, input[start..i]));
                continue;
            }

            // Regex literal: /pattern/flags
            if (ch == '/' && IsRegexContext(tokens))
            {
                int start = i;
                i++; // skip opening /
                while (i < input.Length && input[i] != '/' && input[i] != '\n')
                {
                    if (input[i] == '\\' && i + 1 < input.Length)
                        i += 2; // skip escaped char
                    else
                        i++;
                }
                if (i < input.Length && input[i] == '/') i++; // skip closing /
                while (i < input.Length && input[i] is 'i' or 'm' or 'x') i++; // flags
                tokens.Add(new Token(TokenType.Regex, input[start..i]));
                continue;
            }

            // Word, flag, or keyword
            {
                int start = i;
                while (i < input.Length && !IsBreak(input, i)) i++;
                var text = input[start..i];
                if (text.Length > 0)
                {
                    TokenType type;
                    if (text.StartsWith('-'))
                        type = TokenType.Flag;
                    else if (RushKeywords.Contains(text))
                        type = TokenType.Keyword;
                    else
                        type = TokenType.Word;
                    tokens.Add(new Token(type, text));
                }
            }
        }

        return tokens;
    }

    internal static bool IsBreak(string input, int i)
    {
        char ch = input[i];
        if (ch is ' ' or '\t' or '|' or '>' or '<' or '\'' or '"' or ';') return true;
        if (ch == '&' && i + 1 < input.Length && input[i + 1] == '&') return true;
        if (ch == '!' && i + 1 < input.Length && input[i + 1] is '!' or '$') return true;
        return false;
    }

    /// <summary>
    /// Determine if '/' should be treated as a regex literal start in the highlighter.
    /// After operators, keywords, pipe → regex. After words → division.
    /// </summary>
    private static bool IsRegexContext(List<Token> tokens)
    {
        for (int i = tokens.Count - 1; i >= 0; i--)
        {
            if (tokens[i].Type == TokenType.Whitespace) continue;
            return tokens[i].Type is TokenType.Operator or TokenType.Keyword
                or TokenType.Pipe or TokenType.Bang;
        }
        return true; // start of line → regex context
    }
}

namespace Rush;

/// <summary>
/// Real-time syntax highlighting using ANSI escape codes.
/// Colorizes input as the user types — commands, flags, strings, pipes, operators.
/// </summary>
public class SyntaxHighlighter
{
    private readonly CommandTranslator _translator;

    // ANSI color codes
    private const string Reset = "\x1b[0m";
    private const string Cyan = "\x1b[36m";
    private const string BrightCyan = "\x1b[96m";
    private const string Yellow = "\x1b[33m";
    private const string Green = "\x1b[32m";
    private const string Magenta = "\x1b[35m";
    private const string DarkGray = "\x1b[90m";
    private const string White = "\x1b[37m";

    private static readonly HashSet<string> BuiltinCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "exit", "quit", "help", "history", "alias", "reload", "clear", "cd", "set",
        "as", "from", // Pipe-context format commands
        "export", "unset", "source" // Shell builtins
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
                    sb.Append(Magenta).Append(token.Text).Append(Reset);
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

    private enum TokenType { Word, Flag, String, Pipe, Operator, Bang, Whitespace }
    private record Token(TokenType Type, string Text);

    private static List<Token> Tokenize(string input)
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

            // Word or flag
            {
                int start = i;
                while (i < input.Length && !IsBreak(input, i)) i++;
                var text = input[start..i];
                if (text.Length > 0)
                {
                    tokens.Add(new Token(
                        text.StartsWith('-') ? TokenType.Flag : TokenType.Word,
                        text));
                }
            }
        }

        return tokens;
    }

    private static bool IsBreak(string input, int i)
    {
        char ch = input[i];
        if (ch is ' ' or '\t' or '|' or '>' or '\'' or '"' or ';') return true;
        if (ch == '&' && i + 1 < input.Length && input[i + 1] == '&') return true;
        if (ch == '!' && i + 1 < input.Length && input[i + 1] is '!' or '$') return true;
        return false;
    }
}

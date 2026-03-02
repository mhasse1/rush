namespace Rush;

/// <summary>
/// Token types for the Rush scripting language.
/// </summary>
public enum RushTokenType
{
    // Literals
    Integer, Float, StringLiteral, Symbol,

    // Identifiers and keywords
    Identifier,
    If, Elsif, Else, End, For, In, While, Unless, Until,
    Case, When, Def, Return,
    Try, Rescue, Ensure,
    Do, And, Or, Not, True, False, Nil,
    Next, Continue, Break, Begin,

    // Operators
    Assign,         // =
    Equals,         // ==
    NotEquals,      // !=
    LessThan,       // <
    GreaterThan,    // >
    LessEqual,      // <=
    GreaterEqual,   // >=
    Plus, Minus, Star, Slash, Percent,
    Match,          // ~
    MatchOp,        // =~
    NotMatch,       // !~
    Dot,            // .
    DotDot,         // .. (range)
    Pipe,           // |
    Ampersand,      // &
    AmpAmp,         // &&
    PipePipe,       // ||
    PlusAssign,     // +=
    MinusAssign,    // -=
    SafeNav,        // &.

    // Delimiters
    LParen, RParen,
    LBracket, RBracket,
    LBrace, RBrace,
    Comma, Colon, Semicolon,
    Newline,
    HashBrace,      // #{ (interpolation start)
    DollarParen,    // $( command substitution
    DollarQuestion, // $? exit status

    // Special
    ShellCommand,   // Entire line passed through to shell pipeline
    EOF
}

/// <summary>
/// A single token from the Rush lexer.
/// </summary>
public class RushToken
{
    public RushTokenType Type { get; }
    public string Value { get; }
    public int Position { get; }

    public RushToken(RushTokenType type, string value, int position)
    {
        Type = type;
        Value = value;
        Position = position;
    }

    public override string ToString() => $"{Type}({Value})";
}

/// <summary>
/// Tokenizes Rush source code into a stream of tokens.
/// The lexer's critical job is line triage — deciding if input is Rush syntax
/// or a shell command that should pass through to the existing pipeline.
/// </summary>
public class Lexer
{
    private static readonly Dictionary<string, RushTokenType> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["if"] = RushTokenType.If,
        ["elsif"] = RushTokenType.Elsif,
        ["else"] = RushTokenType.Else,
        ["end"] = RushTokenType.End,
        ["for"] = RushTokenType.For,
        ["in"] = RushTokenType.In,
        ["while"] = RushTokenType.While,
        ["unless"] = RushTokenType.Unless,
        ["until"] = RushTokenType.Until,
        ["case"] = RushTokenType.Case,
        ["match"] = RushTokenType.Case,   // modern alias for case
        ["when"] = RushTokenType.When,
        ["def"] = RushTokenType.Def,
        ["return"] = RushTokenType.Return,
        ["try"] = RushTokenType.Try,
        ["rescue"] = RushTokenType.Rescue,
        ["ensure"] = RushTokenType.Ensure,
        ["do"] = RushTokenType.Do,
        ["and"] = RushTokenType.And,
        ["or"] = RushTokenType.Or,
        ["not"] = RushTokenType.Not,
        ["true"] = RushTokenType.True,
        ["false"] = RushTokenType.False,
        ["nil"] = RushTokenType.Nil,
        ["next"] = RushTokenType.Next,
        ["continue"] = RushTokenType.Continue,
        ["break"] = RushTokenType.Break,
        ["begin"] = RushTokenType.Begin,
    };

    private readonly string _source;
    private int _pos;

    public Lexer(string source)
    {
        _source = source;
        _pos = 0;
    }

    /// <summary>
    /// Tokenize the entire source into a list of tokens.
    /// </summary>
    public List<RushToken> Tokenize()
    {
        var tokens = new List<RushToken>();
        while (_pos < _source.Length)
        {
            var token = NextToken();
            if (token != null)
                tokens.Add(token);
        }
        tokens.Add(new RushToken(RushTokenType.EOF, "", _pos));
        return tokens;
    }

    private RushToken? NextToken()
    {
        // Skip spaces and tabs (NOT newlines)
        while (_pos < _source.Length && _source[_pos] is ' ' or '\t')
            _pos++;

        if (_pos >= _source.Length)
            return null;

        var ch = _source[_pos];
        var start = _pos;

        // Newlines
        if (ch == '\n')
        {
            _pos++;
            return new RushToken(RushTokenType.Newline, "\n", start);
        }
        if (ch == '\r')
        {
            _pos++;
            if (_pos < _source.Length && _source[_pos] == '\n')
                _pos++;
            return new RushToken(RushTokenType.Newline, "\n", start);
        }

        // Comments — skip to end of line
        if (ch == '#' && (_pos + 1 >= _source.Length || _source[_pos + 1] != '{'))
        {
            while (_pos < _source.Length && _source[_pos] != '\n')
                _pos++;
            return NextToken(); // Skip comment, get next token
        }

        // String literals
        if (ch == '"')
            return ReadDoubleQuotedString();
        if (ch == '\'')
            return ReadSingleQuotedString();

        // Numbers
        if (char.IsDigit(ch))
            return ReadNumber();

        // Symbols (:name)
        if (ch == ':' && _pos + 1 < _source.Length && char.IsLetter(_source[_pos + 1]))
            return ReadSymbol();

        // Identifiers and keywords
        if (char.IsLetter(ch) || ch == '_')
            return ReadIdentifierOrKeyword();

        // Dollar sign — command substitution $() and exit status $?
        if (ch == '$')
        {
            if (_pos + 1 < _source.Length && _source[_pos + 1] == '(')
                return ReadCommandSubstitution();
            if (_pos + 1 < _source.Length && _source[_pos + 1] == '?')
            {
                _pos += 2;
                return new RushToken(RushTokenType.DollarQuestion, "$?", start);
            }
        }

        // Two-character operators (check before single-char)
        if (_pos + 1 < _source.Length)
        {
            var twoChar = _source.Substring(_pos, 2);
            switch (twoChar)
            {
                case "==":
                    _pos += 2;
                    return new RushToken(RushTokenType.Equals, "==", start);
                case "!=":
                    _pos += 2;
                    return new RushToken(RushTokenType.NotEquals, "!=", start);
                case "<=":
                    _pos += 2;
                    return new RushToken(RushTokenType.LessEqual, "<=", start);
                case ">=":
                    _pos += 2;
                    return new RushToken(RushTokenType.GreaterEqual, ">=", start);
                case "!~":
                    _pos += 2;
                    return new RushToken(RushTokenType.NotMatch, "!~", start);
                case "&&":
                    _pos += 2;
                    return new RushToken(RushTokenType.AmpAmp, "&&", start);
                case "||":
                    _pos += 2;
                    return new RushToken(RushTokenType.PipePipe, "||", start);
                case "..":
                    _pos += 2;
                    return new RushToken(RushTokenType.DotDot, "..", start);
                case "#{":
                    _pos += 2;
                    return new RushToken(RushTokenType.HashBrace, "#{", start);
                case "+=":
                    _pos += 2;
                    return new RushToken(RushTokenType.PlusAssign, "+=", start);
                case "-=":
                    _pos += 2;
                    return new RushToken(RushTokenType.MinusAssign, "-=", start);
                case "=~":
                    _pos += 2;
                    return new RushToken(RushTokenType.MatchOp, "=~", start);
                case "&.":
                    _pos += 2;
                    return new RushToken(RushTokenType.SafeNav, "&.", start);
            }
        }

        // Single-character operators
        _pos++;
        return ch switch
        {
            '=' => new RushToken(RushTokenType.Assign, "=", start),
            '<' => new RushToken(RushTokenType.LessThan, "<", start),
            '>' => new RushToken(RushTokenType.GreaterThan, ">", start),
            '+' => new RushToken(RushTokenType.Plus, "+", start),
            '-' => new RushToken(RushTokenType.Minus, "-", start),
            '*' => new RushToken(RushTokenType.Star, "*", start),
            '/' => new RushToken(RushTokenType.Slash, "/", start),
            '%' => new RushToken(RushTokenType.Percent, "%", start),
            '~' => new RushToken(RushTokenType.Match, "~", start),
            '.' => new RushToken(RushTokenType.Dot, ".", start),
            '|' => new RushToken(RushTokenType.Pipe, "|", start),
            '&' => new RushToken(RushTokenType.Ampersand, "&", start),
            '(' => new RushToken(RushTokenType.LParen, "(", start),
            ')' => new RushToken(RushTokenType.RParen, ")", start),
            '[' => new RushToken(RushTokenType.LBracket, "[", start),
            ']' => new RushToken(RushTokenType.RBracket, "]", start),
            '{' => new RushToken(RushTokenType.LBrace, "{", start),
            '}' => new RushToken(RushTokenType.RBrace, "}", start),
            ',' => new RushToken(RushTokenType.Comma, ",", start),
            ':' => new RushToken(RushTokenType.Colon, ":", start),
            ';' => new RushToken(RushTokenType.Semicolon, ";", start),
            _ => new RushToken(RushTokenType.Identifier, ch.ToString(), start), // Unknown char → pass through
        };
    }

    private RushToken ReadDoubleQuotedString()
    {
        var start = _pos;
        _pos++; // skip opening "
        var sb = new System.Text.StringBuilder();
        sb.Append('"');

        while (_pos < _source.Length && _source[_pos] != '"')
        {
            if (_source[_pos] == '\\' && _pos + 1 < _source.Length)
            {
                sb.Append(_source[_pos]);
                sb.Append(_source[_pos + 1]);
                _pos += 2;
            }
            else
            {
                sb.Append(_source[_pos]);
                _pos++;
            }
        }

        if (_pos < _source.Length)
        {
            sb.Append('"');
            _pos++; // skip closing "
        }

        return new RushToken(RushTokenType.StringLiteral, sb.ToString(), start);
    }

    private RushToken ReadSingleQuotedString()
    {
        var start = _pos;
        _pos++; // skip opening '
        var sb = new System.Text.StringBuilder();
        sb.Append('\'');

        while (_pos < _source.Length && _source[_pos] != '\'')
        {
            sb.Append(_source[_pos]);
            _pos++;
        }

        if (_pos < _source.Length)
        {
            sb.Append('\'');
            _pos++; // skip closing '
        }

        return new RushToken(RushTokenType.StringLiteral, sb.ToString(), start);
    }

    private RushToken ReadNumber()
    {
        var start = _pos;
        bool isFloat = false;

        while (_pos < _source.Length && (char.IsDigit(_source[_pos]) || _source[_pos] == '.'))
        {
            if (_source[_pos] == '.')
            {
                if (isFloat) break; // second dot = not part of number
                // Check for .. (range operator)
                if (_pos + 1 < _source.Length && _source[_pos + 1] == '.')
                    break;
                // Dot must be followed by a digit to be a decimal point.
                // Otherwise it's a method call: 3.times, 42.to_s, etc.
                if (_pos + 1 >= _source.Length || !char.IsDigit(_source[_pos + 1]))
                    break;
                isFloat = true;
            }
            _pos++;
        }

        var value = _source[start.._pos];
        return new RushToken(isFloat ? RushTokenType.Float : RushTokenType.Integer, value, start);
    }

    private RushToken ReadSymbol()
    {
        var start = _pos;
        _pos++; // skip :
        while (_pos < _source.Length && (char.IsLetterOrDigit(_source[_pos]) || _source[_pos] == '_'))
            _pos++;
        return new RushToken(RushTokenType.Symbol, _source[start.._pos], start);
    }

    private RushToken ReadIdentifierOrKeyword()
    {
        var start = _pos;
        while (_pos < _source.Length && (char.IsLetterOrDigit(_source[_pos]) || _source[_pos] == '_' || _source[_pos] == '?'))
            _pos++;

        var value = _source[start.._pos];

        if (Keywords.TryGetValue(value, out var keywordType))
            return new RushToken(keywordType, value, start);

        return new RushToken(RushTokenType.Identifier, value, start);
    }

    /// <summary>
    /// Read a $(...) command substitution — captures everything between $( and matching ).
    /// The Value of the resulting token is the raw command string (without $( and )).
    /// </summary>
    private RushToken ReadCommandSubstitution()
    {
        var start = _pos;
        _pos += 2; // skip $(
        int depth = 1;
        var sb = new System.Text.StringBuilder();
        while (_pos < _source.Length && depth > 0)
        {
            if (_source[_pos] == '(') depth++;
            else if (_source[_pos] == ')')
            {
                depth--;
                if (depth == 0) { _pos++; break; }
            }
            sb.Append(_source[_pos]);
            _pos++;
        }
        return new RushToken(RushTokenType.DollarParen, sb.ToString().Trim(), start);
    }
}

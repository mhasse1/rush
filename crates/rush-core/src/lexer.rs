use crate::token::{Token, TokenType};

/// Keyword lookup — case-insensitive matching.
fn keyword_type(word: &str) -> Option<TokenType> {
    match word.to_ascii_lowercase().as_str() {
        "if" => Some(TokenType::If),
        "elsif" => Some(TokenType::Elsif),
        "else" => Some(TokenType::Else),
        "end" => Some(TokenType::End),
        "for" => Some(TokenType::For),
        "in" => Some(TokenType::In),
        "while" => Some(TokenType::While),
        "unless" => Some(TokenType::Unless),
        "until" => Some(TokenType::Until),
        "loop" => Some(TokenType::Loop),
        "case" | "match" => Some(TokenType::Case),
        "when" => Some(TokenType::When),
        "def" => Some(TokenType::Def),
        "return" => Some(TokenType::Return),
        "try" => Some(TokenType::Try),
        "rescue" => Some(TokenType::Rescue),
        "ensure" => Some(TokenType::Ensure),
        "do" => Some(TokenType::Do),
        "and" => Some(TokenType::And),
        "or" => Some(TokenType::Or),
        "not" => Some(TokenType::Not),
        "true" => Some(TokenType::True),
        "false" => Some(TokenType::False),
        "nil" => Some(TokenType::Nil),
        "next" => Some(TokenType::Next),
        "continue" => Some(TokenType::Continue),
        "break" => Some(TokenType::Break),
        "begin" => Some(TokenType::Begin),
        "class" => Some(TokenType::Class),
        "attr" => Some(TokenType::Attr),
        "self" => Some(TokenType::SelfKw),
        "super" => Some(TokenType::Super),
        "enum" => Some(TokenType::Enum),
        "macos" => Some(TokenType::Macos),
        "win64" => Some(TokenType::Win64),
        "win32" => Some(TokenType::Win32),
        "linux" => Some(TokenType::Linux),
        "isssh" => Some(TokenType::Isssh),
        "parallel" => Some(TokenType::Parallel),
        "orchestrate" => Some(TokenType::Orchestrate),
        "task" => Some(TokenType::Task),
        "plugin" => Some(TokenType::Plugin),
        _ => None,
    }
}

/// Tokenizes Rush source code into a stream of tokens.
pub struct Lexer {
    source: Vec<char>,
    pos: usize,
    last_token_type: Option<TokenType>,
}

impl Lexer {
    pub fn new(source: &str) -> Self {
        Self {
            source: source.chars().collect(),
            pos: 0,
            last_token_type: None,
        }
    }

    /// Tokenize the entire source into a list of tokens.
    pub fn tokenize(&mut self) -> Vec<Token> {
        let mut tokens = Vec::new();
        while self.pos < self.source.len() {
            if let Some(token) = self.next_token() {
                self.last_token_type = Some(token.token_type);
                tokens.push(token);
            }
        }
        tokens.push(Token::new(TokenType::Eof, "", self.pos));
        tokens
    }

    fn ch(&self) -> char {
        self.source[self.pos]
    }

    fn peek(&self, offset: usize) -> Option<char> {
        self.source.get(self.pos + offset).copied()
    }

    fn next_token(&mut self) -> Option<Token> {
        // Skip spaces and tabs (NOT newlines)
        while self.pos < self.source.len() && matches!(self.ch(), ' ' | '\t') {
            self.pos += 1;
        }

        if self.pos >= self.source.len() {
            return None;
        }

        let ch = self.ch();
        let start = self.pos;

        // Newlines
        if ch == '\n' {
            self.pos += 1;
            return Some(Token::new(TokenType::Newline, "\n", start));
        }
        if ch == '\r' {
            self.pos += 1;
            if self.peek(0) == Some('\n') {
                self.pos += 1;
            }
            return Some(Token::new(TokenType::Newline, "\n", start));
        }

        // Comments — skip to end of line
        if ch == '#' && self.peek(1) != Some('{') {
            while self.pos < self.source.len() && self.ch() != '\n' {
                self.pos += 1;
            }
            return self.next_token();
        }

        // String literals
        if ch == '"' {
            return Some(self.read_double_quoted_string());
        }
        if ch == '\'' {
            return Some(self.read_single_quoted_string());
        }

        // Numbers
        if ch.is_ascii_digit() {
            return Some(self.read_number());
        }

        // Symbols (:name) — but only when NOT immediately after an identifier.
        if ch == ':' && self.peek(1).is_some_and(|c| c.is_ascii_alphabetic()) {
            // If the previous char was a word char or '?', this is a colon (named arg)
            if self.pos > 0 {
                let prev = self.source[self.pos - 1];
                if prev.is_ascii_alphanumeric() || prev == '_' || prev == '?' {
                    // Fall through to operator section
                } else {
                    return Some(self.read_symbol());
                }
            } else {
                return Some(self.read_symbol());
            }
        }

        // Identifiers and keywords
        if ch.is_ascii_alphabetic() || ch == '_' {
            return Some(self.read_identifier_or_keyword());
        }

        // Backtick command substitution
        if ch == '`' {
            return Some(self.read_backtick_substitution());
        }

        // Dollar sign
        if ch == '$' {
            if self.peek(1) == Some('(') {
                return Some(self.read_command_substitution());
            }
            if self.peek(1) == Some('?') {
                self.pos += 2;
                return Some(Token::new(TokenType::DollarQuestion, "$?", start));
            }
            // $identifier — builtin variable
            if self.peek(1).is_some_and(|c| c.is_ascii_alphabetic() || c == '_') {
                self.pos += 1; // skip $
                while self.pos < self.source.len()
                    && (self.ch().is_ascii_alphanumeric() || self.ch() == '_')
                {
                    self.pos += 1;
                }
                let value: String = self.source[start..self.pos].iter().collect();
                return Some(Token::new(TokenType::Identifier, value, start));
            }
        }

        // Two-character operators
        if let Some(next_ch) = self.peek(1) {
            let two: String = [ch, next_ch].iter().collect();
            let result = match two.as_str() {
                "==" => Some(TokenType::Equals),
                "!=" => Some(TokenType::NotEquals),
                "<=" => Some(TokenType::LessEqual),
                ">=" => Some(TokenType::GreaterEqual),
                "!~" => Some(TokenType::NotMatch),
                "&&" => Some(TokenType::AmpAmp),
                "||" => Some(TokenType::PipePipe),
                "+=" => Some(TokenType::PlusAssign),
                "-=" => Some(TokenType::MinusAssign),
                "*=" => Some(TokenType::StarAssign),
                "/=" => Some(TokenType::SlashAssign),
                "=~" => Some(TokenType::MatchOp),
                "&." => Some(TokenType::SafeNav),
                "::" => Some(TokenType::DoubleColon),
                "#{" => Some(TokenType::HashBrace),
                ".." => {
                    // Check for ... (three dots)
                    if self.peek(2) == Some('.') {
                        self.pos += 3;
                        return Some(Token::new(TokenType::DotDotDot, "...", start));
                    }
                    Some(TokenType::DotDot)
                }
                _ => None,
            };
            if let Some(tt) = result {
                self.pos += 2;
                return Some(Token::new(tt, &two, start));
            }
        }

        // Regex literal: /pattern/flags — disambiguated from division
        if ch == '/' && self.slash_is_regex() {
            return Some(self.read_regex_literal());
        }

        // Single-character operators
        self.pos += 1;
        let (tt, val) = match ch {
            '=' => (TokenType::Assign, "="),
            '<' => (TokenType::LessThan, "<"),
            '>' => (TokenType::GreaterThan, ">"),
            '+' => (TokenType::Plus, "+"),
            '-' => (TokenType::Minus, "-"),
            '*' => (TokenType::Star, "*"),
            '/' => (TokenType::Slash, "/"),
            '%' => (TokenType::Percent, "%"),
            '~' => (TokenType::Match, "~"),
            '.' => (TokenType::Dot, "."),
            '|' => (TokenType::Pipe, "|"),
            '&' => (TokenType::Ampersand, "&"),
            '(' => (TokenType::LParen, "("),
            ')' => (TokenType::RParen, ")"),
            '[' => (TokenType::LBracket, "["),
            ']' => (TokenType::RBracket, "]"),
            '{' => (TokenType::LBrace, "{"),
            '}' => (TokenType::RBrace, "}"),
            ',' => (TokenType::Comma, ","),
            ':' => (TokenType::Colon, ":"),
            ';' => (TokenType::Semicolon, ";"),
            '?' => (TokenType::QuestionMark, "?"),
            _ => {
                // Unknown char → pass through as identifier
                return Some(Token::new(
                    TokenType::Identifier,
                    ch.to_string(),
                    start,
                ));
            }
        };
        Some(Token::new(tt, val, start))
    }

    fn read_double_quoted_string(&mut self) -> Token {
        let start = self.pos;
        self.pos += 1; // skip opening "
        let mut value = String::from('"');
        let mut interp_depth = 0; // track #{...} nesting

        while self.pos < self.source.len() {
            let ch = self.ch();

            // Only end string at " when not inside #{...}
            if ch == '"' && interp_depth == 0 {
                break;
            }

            if ch == '\\' && self.pos + 1 < self.source.len() {
                value.push(ch);
                self.pos += 1;
                value.push(self.ch());
                self.pos += 1;
                continue;
            }

            // Track #{...} interpolation depth
            if ch == '#' && self.peek(1) == Some('{') && interp_depth == 0 {
                interp_depth = 1;
                value.push(ch);
                self.pos += 1;
                value.push(self.ch()); // {
                self.pos += 1;
                continue;
            }

            if interp_depth > 0 {
                if ch == '{' {
                    interp_depth += 1;
                } else if ch == '}' {
                    interp_depth -= 1;
                }
            }

            value.push(ch);
            self.pos += 1;
        }

        if self.pos < self.source.len() {
            value.push('"');
            self.pos += 1; // skip closing "
        }

        Token::new(TokenType::StringLiteral, value, start)
    }

    fn read_single_quoted_string(&mut self) -> Token {
        let start = self.pos;
        self.pos += 1; // skip opening '
        let mut value = String::from('\'');

        while self.pos < self.source.len() && self.ch() != '\'' {
            value.push(self.ch());
            self.pos += 1;
        }

        if self.pos < self.source.len() {
            value.push('\'');
            self.pos += 1; // skip closing '
        }

        Token::new(TokenType::StringLiteral, value, start)
    }

    fn read_number(&mut self) -> Token {
        let start = self.pos;
        let mut is_float = false;

        while self.pos < self.source.len()
            && (self.ch().is_ascii_digit() || self.ch() == '.')
        {
            if self.ch() == '.' {
                if is_float {
                    break; // second dot = not part of number
                }
                // Check for .. (range operator)
                if self.peek(1) == Some('.') {
                    break;
                }
                // Dot must be followed by a digit to be a decimal point
                if !self.peek(1).is_some_and(|c| c.is_ascii_digit()) {
                    break;
                }
                is_float = true;
            }
            self.pos += 1;
        }

        // Check for size suffix (kb, mb, gb, tb)
        if self.pos + 1 < self.source.len() {
            let next2: String = self.source[self.pos..self.pos + 2].iter().collect();
            let lower = next2.to_ascii_lowercase();
            if matches!(lower.as_str(), "kb" | "mb" | "gb" | "tb") {
                self.pos += 2;
            }
        }

        let value: String = self.source[start..self.pos].iter().collect();
        let tt = if is_float {
            TokenType::Float
        } else {
            TokenType::Integer
        };
        Token::new(tt, value, start)
    }

    fn read_symbol(&mut self) -> Token {
        let start = self.pos;
        self.pos += 1; // skip :
        while self.pos < self.source.len()
            && (self.ch().is_ascii_alphanumeric() || self.ch() == '_')
        {
            self.pos += 1;
        }
        let value: String = self.source[start..self.pos].iter().collect();
        Token::new(TokenType::Symbol, value, start)
    }

    fn read_identifier_or_keyword(&mut self) -> Token {
        let start = self.pos;
        while self.pos < self.source.len()
            && (self.ch().is_ascii_alphanumeric() || self.ch() == '_' || self.ch() == '?')
        {
            self.pos += 1;
        }

        let value: String = self.source[start..self.pos].iter().collect();

        if let Some(kw) = keyword_type(&value) {
            Token::new(kw, value, start)
        } else {
            Token::new(TokenType::Identifier, value, start)
        }
    }

    /// Determine if a '/' should be lexed as a regex literal or division operator.
    fn slash_is_regex(&self) -> bool {
        let Some(last) = self.last_token_type else {
            return true; // start of input
        };
        matches!(
            last,
            // After operators → regex
            TokenType::Assign
                | TokenType::Equals
                | TokenType::NotEquals
                | TokenType::LessThan
                | TokenType::GreaterThan
                | TokenType::LessEqual
                | TokenType::GreaterEqual
                | TokenType::MatchOp
                | TokenType::NotMatch
                | TokenType::Match
                | TokenType::Plus
                | TokenType::Minus
                | TokenType::Star
                | TokenType::Slash
                | TokenType::Percent
                | TokenType::AmpAmp
                | TokenType::PipePipe
                | TokenType::Pipe
                | TokenType::PlusAssign
                | TokenType::MinusAssign
                | TokenType::StarAssign
                | TokenType::SlashAssign
                // After opening delimiters, comma, semicolon, newline
                | TokenType::LParen
                | TokenType::LBracket
                | TokenType::LBrace
                | TokenType::Comma
                | TokenType::Semicolon
                | TokenType::Newline
                // After keywords
                | TokenType::If
                | TokenType::Elsif
                | TokenType::Unless
                | TokenType::While
                | TokenType::Until
                | TokenType::When
                | TokenType::Return
                | TokenType::And
                | TokenType::Or
                | TokenType::Not
                | TokenType::In
        )
    }

    /// Read a regex literal: /pattern/flags
    /// Value is stored as "pattern\0flags" when flags are present.
    fn read_regex_literal(&mut self) -> Token {
        let start = self.pos;
        self.pos += 1; // skip opening /
        let mut pattern = String::new();

        while self.pos < self.source.len() && self.ch() != '/' && self.ch() != '\n' {
            if self.ch() == '\\' && self.pos + 1 < self.source.len() {
                pattern.push(self.ch());
                self.pos += 1;
                pattern.push(self.ch());
                self.pos += 1;
            } else {
                pattern.push(self.ch());
                self.pos += 1;
            }
        }

        if self.pos < self.source.len() && self.ch() == '/' {
            self.pos += 1; // skip closing /
        }

        // Read flags (i, m, x)
        let mut flags = String::new();
        while self.pos < self.source.len() && matches!(self.ch(), 'i' | 'm' | 'x') {
            flags.push(self.ch());
            self.pos += 1;
        }

        let value = if flags.is_empty() {
            pattern
        } else {
            format!("{pattern}\0{flags}")
        };

        Token::new(TokenType::Regex, value, start)
    }

    /// Read a `command` backtick substitution.
    fn read_backtick_substitution(&mut self) -> Token {
        let start = self.pos;
        self.pos += 1; // skip opening backtick
        let mut value = String::new();
        while self.pos < self.source.len() && self.ch() != '`' {
            value.push(self.ch());
            self.pos += 1;
        }
        if self.pos < self.source.len() {
            self.pos += 1; // skip closing backtick
        }
        Token::new(TokenType::DollarParen, value.trim().to_string(), start)
    }

    /// Read a $(...) command substitution.
    fn read_command_substitution(&mut self) -> Token {
        let start = self.pos;
        self.pos += 2; // skip $(
        let mut depth = 1;
        let mut value = String::new();
        while self.pos < self.source.len() && depth > 0 {
            if self.ch() == '(' {
                depth += 1;
            } else if self.ch() == ')' {
                depth -= 1;
                if depth == 0 {
                    self.pos += 1;
                    break;
                }
            }
            value.push(self.ch());
            self.pos += 1;
        }
        Token::new(TokenType::DollarParen, value.trim().to_string(), start)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::token::TokenType::*;

    fn lex(input: &str) -> Vec<Token> {
        Lexer::new(input).tokenize()
    }

    fn types(input: &str) -> Vec<TokenType> {
        lex(input).into_iter().map(|t| t.token_type).collect()
    }

    // ── Keywords ────────────────────────────────────────────────────

    #[test]
    fn keywords_tokenized_correctly() {
        let cases = [
            ("if", If),
            ("elsif", Elsif),
            ("else", Else),
            ("end", End),
            ("for", For),
            ("in", In),
            ("while", While),
            ("unless", Unless),
            ("until", Until),
            ("def", Def),
            ("return", Return),
            ("try", Try),
            ("rescue", Rescue),
            ("true", True),
            ("false", False),
            ("nil", Nil),
            ("next", Next),
            ("break", Break),
        ];
        for (input, expected) in cases {
            let tokens = lex(input);
            assert_eq!(tokens.len(), 2, "input: {input}"); // keyword + EOF
            assert_eq!(tokens[0].token_type, expected, "input: {input}");
        }
    }

    #[test]
    fn keywords_case_insensitive() {
        assert_eq!(lex("IF")[0].token_type, If);
        assert_eq!(lex("While")[0].token_type, While);
        assert_eq!(lex("TRUE")[0].token_type, True);
    }

    // ── Integer Literals ────────────────────────────────────────────

    #[test]
    fn integer_literals() {
        for input in ["42", "0", "1000000"] {
            let tokens = lex(input);
            assert_eq!(tokens[0].token_type, Integer);
            assert_eq!(tokens[0].value, input);
        }
    }

    // ── Float Literals ──────────────────────────────────────────────

    #[test]
    fn float_literals() {
        for input in ["3.14", "0.5"] {
            let tokens = lex(input);
            assert_eq!(tokens[0].token_type, Float);
            assert_eq!(tokens[0].value, input);
        }
    }

    // ── String Literals ─────────────────────────────────────────────

    #[test]
    fn single_quoted_string() {
        let tokens = lex("'hello world'");
        assert_eq!(tokens[0].token_type, StringLiteral);
        assert_eq!(tokens[0].value, "'hello world'");
    }

    #[test]
    fn double_quoted_string() {
        let tokens = lex("\"hello world\"");
        assert_eq!(tokens[0].token_type, StringLiteral);
        assert_eq!(tokens[0].value, "\"hello world\"");
    }

    // ── Operators ───────────────────────────────────────────────────

    #[test]
    fn operators_tokenized() {
        let cases = [
            ("=", Assign),
            ("==", Equals),
            ("!=", NotEquals),
            ("<", LessThan),
            (">", GreaterThan),
            ("<=", LessEqual),
            (">=", GreaterEqual),
            ("+", Plus),
            ("-", Minus),
            ("*", Star),
            ("%", Percent),
            (".", Dot),
            ("..", DotDot),
            ("|", Pipe),
            ("&&", AmpAmp),
            ("||", PipePipe),
            ("+=", PlusAssign),
            ("-=", MinusAssign),
        ];
        for (input, expected) in cases {
            let tokens = lex(input);
            assert_eq!(tokens[0].token_type, expected, "input: {input}");
        }
    }

    // ── Delimiters ──────────────────────────────────────────────────

    #[test]
    fn delimiters_tokenized() {
        let cases = [
            ("(", LParen),
            (")", RParen),
            ("[", LBracket),
            ("]", RBracket),
            ("{", LBrace),
            ("}", RBrace),
            (",", Comma),
            (":", Colon),
        ];
        for (input, expected) in cases {
            let tokens = lex(input);
            assert_eq!(tokens[0].token_type, expected, "input: {input}");
        }
    }

    // ── Identifiers ─────────────────────────────────────────────────

    #[test]
    fn identifiers_parsed() {
        for input in ["foo", "my_var", "camelCase", "empty?"] {
            let tokens = lex(input);
            assert_eq!(tokens[0].token_type, Identifier, "input: {input}");
            assert_eq!(tokens[0].value, input, "input: {input}");
        }
    }

    // ── Comments ────────────────────────────────────────────────────

    #[test]
    fn comment_skipped() {
        let tokens = lex("# this is a comment");
        assert_eq!(tokens.len(), 1);
        assert_eq!(tokens[0].token_type, Eof);
    }

    #[test]
    fn inline_comment_only_comment_skipped() {
        let tokens = lex("x = 5 # assign five");
        let non_eof: Vec<_> = tokens.iter().filter(|t| t.token_type != Eof).collect();
        assert_eq!(non_eof.len(), 3);
        assert_eq!(non_eof[0].token_type, Identifier);
        assert_eq!(non_eof[1].token_type, Assign);
        assert_eq!(non_eof[2].token_type, Integer);
    }

    // ── Complex Expressions ─────────────────────────────────────────

    #[test]
    fn assignment_tokenized() {
        let tokens = lex("x = 42");
        let non_eof: Vec<_> = tokens.iter().filter(|t| t.token_type != Eof).collect();
        assert_eq!(non_eof.len(), 3);
        assert_eq!(non_eof[0].value, "x");
        assert_eq!(non_eof[1].token_type, Assign);
        assert_eq!(non_eof[2].value, "42");
    }

    #[test]
    fn method_call_tokenized() {
        let tokens = lex("items.each");
        let non_eof: Vec<_> = tokens.iter().filter(|t| t.token_type != Eof).collect();
        assert_eq!(non_eof.len(), 3);
        assert_eq!(non_eof[0].token_type, Identifier);
        assert_eq!(non_eof[1].token_type, Dot);
        assert_eq!(non_eof[2].token_type, Identifier);
    }

    #[test]
    fn if_condition_tokenized() {
        let tokens = lex("if x > 5");
        let non_eof: Vec<_> = tokens.iter().filter(|t| t.token_type != Eof).collect();
        assert_eq!(non_eof.len(), 4);
        assert_eq!(non_eof[0].token_type, If);
        assert_eq!(non_eof[1].token_type, Identifier);
        assert_eq!(non_eof[2].token_type, GreaterThan);
        assert_eq!(non_eof[3].token_type, Integer);
    }

    // ── Special Tokens ──────────────────────────────────────────────

    #[test]
    fn dollar_question_tokenized() {
        assert_eq!(lex("$?")[0].token_type, DollarQuestion);
    }

    #[test]
    fn safe_navigation_tokenized() {
        assert_eq!(lex("&.")[0].token_type, SafeNav);
    }

    #[test]
    fn empty_input_only_eof() {
        let tokens = lex("");
        assert_eq!(tokens.len(), 1);
        assert_eq!(tokens[0].token_type, Eof);
    }

    // ── Symbols ─────────────────────────────────────────────────────

    #[test]
    fn symbol_tokenized() {
        let tokens = lex(":name");
        assert_eq!(tokens[0].token_type, Symbol);
        assert_eq!(tokens[0].value, ":name");
    }

    // ── Range Operator ──────────────────────────────────────────────

    #[test]
    fn range_between_integers() {
        let non_eof: Vec<_> = lex("1..10")
            .into_iter()
            .filter(|t| t.token_type != Eof)
            .collect();
        assert_eq!(non_eof.len(), 3);
        assert_eq!(non_eof[0].token_type, Integer);
        assert_eq!(non_eof[1].token_type, DotDot);
        assert_eq!(non_eof[2].token_type, Integer);
    }

    // ── Exclusive Range ─────────────────────────────────────────────

    #[test]
    fn exclusive_range() {
        let non_eof: Vec<_> = lex("1...10")
            .into_iter()
            .filter(|t| t.token_type != Eof)
            .collect();
        assert_eq!(non_eof.len(), 3);
        assert_eq!(non_eof[0].token_type, Integer);
        assert_eq!(non_eof[1].token_type, DotDotDot);
        assert_eq!(non_eof[2].token_type, Integer);
    }

    // ── Regex ───────────────────────────────────────────────────────

    #[test]
    fn regex_literal() {
        // Regex at start of input (no previous token)
        let tokens = lex("/hello/");
        assert_eq!(tokens[0].token_type, Regex);
        assert_eq!(tokens[0].value, "hello");
    }

    #[test]
    fn regex_with_flags() {
        let tokens = lex("/pattern/im");
        assert_eq!(tokens[0].token_type, Regex);
        assert_eq!(tokens[0].value, "pattern\0im");
    }

    #[test]
    fn slash_after_identifier_is_division() {
        // After an identifier, / is division not regex
        let t = types("x / 2");
        assert_eq!(t, [Identifier, Slash, Integer, Eof]);
    }

    // ── Named Args vs Symbols ───────────────────────────────────────

    #[test]
    fn named_arg_colon() {
        // name:value → Identifier Colon Identifier
        let t = types("name:value");
        assert_eq!(t, [Identifier, Colon, Identifier, Eof]);
    }

    #[test]
    fn standalone_symbol() {
        // :symbol at start → Symbol
        let t = types(":files");
        assert_eq!(t, [Symbol, Eof]);
    }

    // ── Command Substitution ────────────────────────────────────────

    #[test]
    fn dollar_paren_substitution() {
        let tokens = lex("$(echo hello)");
        assert_eq!(tokens[0].token_type, DollarParen);
        assert_eq!(tokens[0].value, "echo hello");
    }

    #[test]
    fn backtick_substitution() {
        let tokens = lex("`echo hello`");
        assert_eq!(tokens[0].token_type, DollarParen);
        assert_eq!(tokens[0].value, "echo hello");
    }

    // ── Builtin Variables ───────────────────────────────────────────

    #[test]
    fn dollar_identifier() {
        let tokens = lex("$os");
        assert_eq!(tokens[0].token_type, Identifier);
        assert_eq!(tokens[0].value, "$os");
    }

    // ── Size Suffixes ───────────────────────────────────────────────

    #[test]
    fn size_suffix() {
        for (input, expected) in [("1mb", "1mb"), ("100kb", "100kb"), ("2gb", "2gb")] {
            let tokens = lex(input);
            assert_eq!(tokens[0].token_type, Integer, "input: {input}");
            assert_eq!(tokens[0].value, expected, "input: {input}");
        }
    }

    // ── Number followed by dot method ───────────────────────────────

    #[test]
    fn number_dot_method() {
        // 3.times → Integer Dot Identifier (not Float)
        let t = types("3.times");
        assert_eq!(t, [Integer, Dot, Identifier, Eof]);
    }

    // ── Double Colon ────────────────────────────────────────────────

    #[test]
    fn double_colon() {
        let t = types("File::read");
        assert_eq!(t, [Identifier, DoubleColon, Identifier, Eof]);
    }

    // ── Newlines ────────────────────────────────────────────────────

    #[test]
    fn newlines_tokenized() {
        let t = types("a\nb");
        assert_eq!(t, [Identifier, Newline, Identifier, Eof]);
    }

    // ── Escape in double-quoted string ──────────────────────────────

    #[test]
    fn escape_in_double_quoted_string() {
        let tokens = lex(r#""hello \"world\"""#);
        assert_eq!(tokens[0].token_type, StringLiteral);
        assert_eq!(tokens[0].value, r#""hello \"world\"""#);
    }

    // ── Interpolation with nested quotes ────────────────────────────

    #[test]
    fn interpolation_with_quotes_inside() {
        // "Evens: #{evens.join(", ")}" — the ", " should NOT end the string
        let input = r#""result: #{x.join(", ")}""#;
        let tokens = lex(input);
        assert_eq!(tokens[0].token_type, StringLiteral);
        // Should be a single string token, not split
        assert_eq!(tokens[0].value, input);
        assert_eq!(tokens[1].token_type, Eof);
    }

    #[test]
    fn interpolation_with_method_call() {
        let input = r#""count: #{arr.length}""#;
        let tokens = lex(input);
        assert_eq!(tokens[0].token_type, StringLiteral);
        assert_eq!(tokens[0].value, input);
    }

    #[test]
    fn nested_braces_in_interpolation() {
        let input = r#""val: #{{a: 1}.keys}""#;
        let tokens = lex(input);
        assert_eq!(tokens[0].token_type, StringLiteral);
        assert_eq!(tokens[0].value, input);
    }
}

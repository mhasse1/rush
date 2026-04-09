use serde::{Deserialize, Serialize};

/// Token types for the Rush scripting language.
/// Mirrors the C# `RushTokenType` enum in `dotnet/Lexer.cs`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub enum TokenType {
    // Literals
    Integer,
    Float,
    StringLiteral,
    Symbol,
    Regex,

    // Identifiers and keywords
    Identifier,
    If,
    Elsif,
    Else,
    End,
    For,
    In,
    While,
    Unless,
    Until,
    Loop,
    Case,
    When,
    Def,
    Return,
    Try,
    Rescue,
    Ensure,
    Do,
    And,
    Or,
    Not,
    True,
    False,
    Nil,
    Next,
    Continue,
    Break,
    Begin,
    Class,
    Attr,
    SelfKw,
    Super,
    Enum,
    Macos,
    Win64,
    Win32,
    Linux,
    Isssh,
    Ps,
    Ps5,
    Plugin,

    // Operators
    Assign,       // =
    Equals,       // ==
    NotEquals,     // !=
    LessThan,     // <
    GreaterThan,  // >
    LessEqual,    // <=
    GreaterEqual, // >=
    Plus,
    Minus,
    Star,
    Slash,
    Percent,
    Match,        // ~
    MatchOp,      // =~
    NotMatch,     // !~
    Dot,          // .
    DotDot,       // .. (range)
    DotDotDot,    // ... (exclusive range)
    Pipe,         // |
    Ampersand,    // &
    AmpAmp,       // &&
    PipePipe,     // ||
    PlusAssign,   // +=
    MinusAssign,  // -=
    StarAssign,   // *=
    SlashAssign,  // /=
    SafeNav,      // &.
    DoubleColon,  // ::
    QuestionMark, // ?

    // Delimiters
    LParen,
    RParen,
    LBracket,
    RBracket,
    LBrace,
    RBrace,
    Comma,
    Colon,
    Semicolon,
    Newline,
    HashBrace,      // #{ (interpolation start)
    DollarParen,    // $( command substitution
    DollarQuestion, // $? exit status

    // Special
    ShellCommand, // Entire line passed through to shell
    Eof,
}

/// A single token from the Rush lexer.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Token {
    pub token_type: TokenType,
    pub value: String,
    pub position: usize,
}

impl Token {
    pub fn new(token_type: TokenType, value: impl Into<String>, position: usize) -> Self {
        Self {
            token_type,
            value: value.into(),
            position,
        }
    }
}

impl std::fmt::Display for Token {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "{:?}({})", self.token_type, self.value)
    }
}

use nu_ansi_term::{Color, Style};
use reedline::{Highlighter, StyledText};
use rush_core::lexer::Lexer;
use rush_core::token::TokenType;

/// Syntax highlighter for Rush using the lexer.
pub struct RushHighlighter;

impl Highlighter for RushHighlighter {
    fn highlight(&self, line: &str, _cursor: usize) -> StyledText {
        let mut styled = StyledText::new();
        let tokens = Lexer::new(line).tokenize();

        let mut last_end = 0;

        for token in &tokens {
            if token.token_type == TokenType::Eof {
                break;
            }

            let start = token.position;
            if start > last_end && last_end < line.len() {
                let gap_end = start.min(line.len());
                styled.push((Style::default(), line[last_end..gap_end].to_string()));
            }

            let end = (start + token.value.len()).min(line.len());
            let text = &line[start..end];

            let style = match token.token_type {
                // Keywords — bold blue
                TokenType::If | TokenType::Elsif | TokenType::Else | TokenType::End
                | TokenType::For | TokenType::In | TokenType::While | TokenType::Until
                | TokenType::Unless | TokenType::Loop | TokenType::Def | TokenType::Return
                | TokenType::Class | TokenType::Attr | TokenType::Enum | TokenType::Case
                | TokenType::When | TokenType::Try | TokenType::Rescue | TokenType::Ensure
                | TokenType::Begin | TokenType::Do | TokenType::And | TokenType::Or
                | TokenType::Not | TokenType::Break | TokenType::Next | TokenType::Continue
                | TokenType::Macos | TokenType::Linux | TokenType::Win64 | TokenType::Win32
                | TokenType::Ps | TokenType::Ps5 | TokenType::SelfKw | TokenType::Super => {
                    Style::new().bold().fg(Color::Blue)
                }

                // Bool/nil — cyan
                TokenType::True | TokenType::False | TokenType::Nil => {
                    Style::new().fg(Color::Cyan)
                }

                // Numbers — cyan
                TokenType::Integer | TokenType::Float => Style::new().fg(Color::Cyan),

                // Strings — green
                TokenType::StringLiteral => Style::new().fg(Color::Green),

                // Symbols — magenta
                TokenType::Symbol => Style::new().fg(Color::Magenta),

                // Operators — yellow
                TokenType::Plus | TokenType::Minus | TokenType::Star | TokenType::Slash
                | TokenType::Percent | TokenType::Equals | TokenType::NotEquals
                | TokenType::LessThan | TokenType::GreaterThan | TokenType::LessEqual
                | TokenType::GreaterEqual | TokenType::Assign | TokenType::PlusAssign
                | TokenType::MinusAssign | TokenType::Pipe | TokenType::AmpAmp
                | TokenType::PipePipe => Style::new().fg(Color::Yellow),

                _ => Style::default(),
            };

            styled.push((style, text.to_string()));
            last_end = end;
        }

        if last_end < line.len() {
            styled.push((Style::default(), line[last_end..].to_string()));
        }

        styled
    }
}

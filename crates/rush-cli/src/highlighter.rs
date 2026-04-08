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

            // Safety: token positions are byte offsets from the lexer which operates
            // on chars. If the input contains multi-byte UTF-8 (emoji, unicode),
            // the byte position might not be a char boundary. Skip if invalid.
            if !line.is_char_boundary(start) {
                continue;
            }

            if start > last_end && last_end < line.len() && line.is_char_boundary(last_end) {
                let gap_end = start.min(line.len());
                if line.is_char_boundary(gap_end) {
                    styled.push((Style::default(), line[last_end..gap_end].to_string()));
                }
            }

            let end = (start + token.value.len()).min(line.len());
            if !line.is_char_boundary(end) {
                // Token spans a multi-byte char boundary — use the token value directly
                styled.push((Style::default(), token.value.clone()));
                last_end = end;
                continue;
            }
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

        if last_end < line.len() && line.is_char_boundary(last_end) {
            styled.push((Style::default(), line[last_end..].to_string()));
        } else if last_end < line.len() {
            // Find next valid char boundary
            let mut safe = last_end;
            while safe < line.len() && !line.is_char_boundary(safe) { safe += 1; }
            if safe < line.len() {
                styled.push((Style::default(), line[safe..].to_string()));
            }
        }

        styled
    }
}

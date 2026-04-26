use nu_ansi_term::{Color, Style};
use rushline::{Highlighter, StyledText};
use rush_core::triage;

/// Syntax highlighter for Rush — handles both Rush syntax and shell commands.
pub struct RushHighlighter;

impl Highlighter for RushHighlighter {
    fn highlight(&self, line: &str, _cursor: usize) -> StyledText {
        let mut styled = StyledText::new();

        if line.is_empty() {
            return styled;
        }

        // Use triage to decide if this is Rush or shell
        if triage::is_rush_syntax(line) {
            highlight_rush(&mut styled, line);
        } else {
            highlight_shell(&mut styled, line);
        }

        styled
    }
}

/// Highlight Rush syntax using the lexer.
fn highlight_rush(styled: &mut StyledText, line: &str) {
    use rush_core::lexer::Lexer;
    use rush_core::token::TokenType;

    let tokens = Lexer::new(line).tokenize();
    let mut last_end = 0;

    for token in &tokens {
        if token.token_type == TokenType::Eof { break; }

        let start = token.position;
        if !line.is_char_boundary(start) { continue; }

        // Gap between tokens
        if start > last_end && line.is_char_boundary(last_end) && line.is_char_boundary(start) {
            styled.push((Style::default(), line[last_end..start].to_string()));
        }

        let end = (start + token.value.len()).min(line.len());
        if !line.is_char_boundary(end) {
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
            | TokenType::SelfKw | TokenType::Super => {
                Style::new().bold().fg(Color::Blue)
            }
            TokenType::True | TokenType::False | TokenType::Nil => Style::new().fg(Color::Cyan),
            TokenType::Integer | TokenType::Float => Style::new().fg(Color::Cyan),
            TokenType::StringLiteral => Style::new().fg(Color::Green),
            TokenType::Symbol => Style::new().fg(Color::Magenta),
            TokenType::Pipe => Style::new().fg(Color::DarkGray),
            TokenType::AmpAmp | TokenType::PipePipe => Style::new().fg(Color::Magenta),
            TokenType::Plus | TokenType::Minus | TokenType::Star | TokenType::Slash
            | TokenType::Percent | TokenType::Equals | TokenType::NotEquals
            | TokenType::LessThan | TokenType::GreaterThan | TokenType::LessEqual
            | TokenType::GreaterEqual | TokenType::Assign | TokenType::PlusAssign
            | TokenType::MinusAssign => Style::new().fg(Color::Yellow),
            _ => Style::default(),
        };

        styled.push((style, text.to_string()));
        last_end = end;
    }

    // Trailing text
    if last_end < line.len() && line.is_char_boundary(last_end) {
        styled.push((Style::default(), line[last_end..].to_string()));
    }
}

/// Highlight shell commands: command, flags, strings, pipes, operators.
fn highlight_shell(styled: &mut StyledText, line: &str) {
    let mut pos = 0;
    let chars: Vec<char> = line.chars().collect();
    let mut is_first_word = true;
    let mut after_pipe = false;

    while pos < chars.len() {
        let ch = chars[pos];

        // Whitespace — pass through
        if ch == ' ' || ch == '\t' {
            let start = pos;
            while pos < chars.len() && (chars[pos] == ' ' || chars[pos] == '\t') { pos += 1; }
            styled.push((Style::default(), chars[start..pos].iter().collect()));
            continue;
        }

        // Comment
        if ch == '#' && (pos == 0 || chars[pos - 1] == ' ') {
            let rest: String = chars[pos..].iter().collect();
            styled.push((Style::new().fg(Color::DarkGray), rest));
            break;
        }

        // Strings
        if ch == '\'' || ch == '"' {
            let start = pos;
            let quote = ch;
            pos += 1;
            while pos < chars.len() && chars[pos] != quote {
                if chars[pos] == '\\' && pos + 1 < chars.len() { pos += 1; }
                pos += 1;
            }
            if pos < chars.len() { pos += 1; } // closing quote
            let text: String = chars[start..pos].iter().collect();
            styled.push((Style::new().fg(Color::Green), text));
            is_first_word = false;
            continue;
        }

        // Pipe
        if ch == '|' {
            if pos + 1 < chars.len() && chars[pos + 1] == '|' {
                styled.push((Style::new().fg(Color::Magenta), "||".to_string()));
                pos += 2;
            } else {
                styled.push((Style::new().fg(Color::DarkGray), "|".to_string()));
                pos += 1;
                is_first_word = true;
                after_pipe = true;
            }
            continue;
        }

        // Operators: && ; > >> < 2> 2>&1
        if ch == '&' && pos + 1 < chars.len() && chars[pos + 1] == '&' {
            styled.push((Style::new().fg(Color::Magenta), "&&".to_string()));
            pos += 2;
            is_first_word = true;
            continue;
        }
        if ch == ';' {
            styled.push((Style::new().fg(Color::Magenta), ";".to_string()));
            pos += 1;
            is_first_word = true;
            continue;
        }
        if ch == '>' || ch == '<' {
            let start = pos;
            pos += 1;
            while pos < chars.len() && (chars[pos] == '>' || chars[pos] == '&' || chars[pos] == '|') {
                pos += 1;
            }
            let text: String = chars[start..pos].iter().collect();
            styled.push((Style::new().fg(Color::Magenta), text));
            continue;
        }
        if ch == '2' && pos + 1 < chars.len() && chars[pos + 1] == '>' {
            let start = pos;
            pos += 2;
            while pos < chars.len() && (chars[pos] == '>' || chars[pos] == '&' || chars[pos].is_ascii_digit()) {
                pos += 1;
            }
            let text: String = chars[start..pos].iter().collect();
            styled.push((Style::new().fg(Color::Magenta), text));
            continue;
        }

        // Bang: !! !$
        if ch == '!' {
            let start = pos;
            pos += 1;
            while pos < chars.len() && !chars[pos].is_whitespace() { pos += 1; }
            let text: String = chars[start..pos].iter().collect();
            styled.push((Style::new().fg(Color::Magenta), text));
            continue;
        }

        // Word — command, flag, or argument
        let start = pos;
        while pos < chars.len() && !chars[pos].is_whitespace()
            && chars[pos] != '|' && chars[pos] != ';'
            && chars[pos] != '>' && chars[pos] != '<'
            && chars[pos] != '\'' && chars[pos] != '"'
            && !(chars[pos] == '&' && pos + 1 < chars.len() && chars[pos + 1] == '&')
        {
            pos += 1;
        }
        let word: String = chars[start..pos].iter().collect();

        let style = if is_first_word || after_pipe {
            // Command name: check if it's a known command
            if is_known_command(&word) {
                Style::new().fg(Color::Cyan)
            } else {
                Style::new().fg(Color::White)
            }
        } else if word.starts_with('-') {
            // Flag
            Style::new().fg(Color::Yellow)
        } else if word.starts_with('$') {
            // Variable
            Style::new().fg(Color::Cyan)
        } else {
            Style::default()
        };

        styled.push((style, word));
        is_first_word = false;
        after_pipe = false;
    }
}

/// Check if a word is a known command (builtin or on PATH).
fn is_known_command(word: &str) -> bool {
    // Builtins
    const BUILTINS: &[&str] = &[
        "cd", "export", "unset", "source", "alias", "unalias", "history",
        "pushd", "popd", "dirs", "jobs", "fg", "bg", "wait", "kill",
        "set", "setbg", "help", "which", "type", "command", "eval", "exec",
        "read", "trap", "umask", "ulimit", "hash", "fc", "pwd", "clear",
        "exit", "quit", "reload", "o", "open", "shift", "times", "getopts",
        "puts", "print", "warn", "die", "ask", "sleep", "ai",
    ];
    if BUILTINS.contains(&word) { return true; }

    // Common commands (avoid PATH lookup on every keystroke)
    const COMMON: &[&str] = &[
        "ls", "cat", "grep", "find", "echo", "mkdir", "rm", "cp", "mv",
        "touch", "head", "tail", "sort", "wc", "cut", "sed", "awk",
        "git", "docker", "ssh", "scp", "curl", "wget", "tar", "zip",
        "unzip", "make", "cargo", "npm", "python", "python3", "node",
        "ruby", "go", "rustc", "vi", "vim", "nano", "less", "more",
        "man", "ps", "top", "df", "du", "env", "whoami", "hostname",
        "date", "cal", "diff", "patch", "chmod", "chown", "ln", "file",
        "xargs", "tee", "tr", "bc", "true", "false", "test",
    ];
    COMMON.contains(&word)
}

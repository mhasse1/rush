//! Multi-line input validator for reedline.
//! Detects incomplete Rush blocks (if/def/class/for/while without matching end).

use reedline::{ValidationResult, Validator};

/// Checks if input is a complete Rush expression or needs more lines.
pub struct RushValidator;

impl Validator for RushValidator {
    fn validate(&self, line: &str) -> ValidationResult {
        if is_incomplete(line) {
            ValidationResult::Incomplete
        } else {
            ValidationResult::Complete
        }
    }
}

/// Check if Rush source is incomplete (unclosed blocks, strings, etc.)
fn is_incomplete(source: &str) -> bool {
    let mut depth: i32 = 0;
    let mut in_single_quote = false;
    let mut in_double_quote = false;
    // Track block openers/closers
    for line in source.lines() {
        let trimmed = line.trim();

        // Skip empty lines
        if trimmed.is_empty() {
            continue;
        }

        // Walk character by character for string tracking
        let mut i = 0;
        let chars: Vec<char> = trimmed.chars().collect();
        let mut line_words = Vec::new();
        let mut word = String::new();

        while i < chars.len() {
            let ch = chars[i];

            if in_single_quote {
                if ch == '\'' {
                    in_single_quote = false;
                }
                i += 1;
                continue;
            }

            if in_double_quote {
                if ch == '\\' && i + 1 < chars.len() {
                    i += 2; // skip escape
                    continue;
                }
                if ch == '"' {
                    in_double_quote = false;
                }
                i += 1;
                continue;
            }

            if ch == '#' {
                break; // rest of line is comment
            }

            if ch == '\'' {
                in_single_quote = true;
                i += 1;
                continue;
            }

            if ch == '"' {
                in_double_quote = true;
                i += 1;
                continue;
            }

            if ch.is_alphanumeric() || ch == '_' {
                word.push(ch);
            } else {
                if !word.is_empty() {
                    line_words.push(std::mem::take(&mut word));
                }
            }

            i += 1;
        }
        if !word.is_empty() {
            line_words.push(word);
        }

        // Count block depth changes from keywords
        for w in &line_words {
            match w.to_lowercase().as_str() {
                "if" | "unless" | "while" | "until" | "for" | "loop"
                | "def" | "class" | "enum" | "case" | "match"
                | "try" | "begin" | "do"
                | "macos" | "linux" | "win64" | "win32" | "ps" | "ps5" => {
                    depth += 1;
                }
                "end" => {
                    depth -= 1;
                }
                _ => {}
            }
        }
    }

    // Incomplete if: open blocks, or unclosed strings
    depth > 0 || in_single_quote || in_double_quote
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn complete_single_line() {
        assert!(!is_incomplete("puts \"hello\""));
        assert!(!is_incomplete("x = 42"));
        assert!(!is_incomplete("ls -la"));
    }

    #[test]
    fn incomplete_if() {
        assert!(is_incomplete("if x > 5"));
        assert!(is_incomplete("if x > 5\n  puts x"));
        assert!(!is_incomplete("if x > 5\n  puts x\nend"));
    }

    #[test]
    fn incomplete_def() {
        assert!(is_incomplete("def greet(name)"));
        assert!(!is_incomplete("def greet(name)\n  puts name\nend"));
    }

    #[test]
    fn incomplete_class() {
        assert!(is_incomplete("class Dog"));
        assert!(!is_incomplete("class Dog\nend"));
    }

    #[test]
    fn incomplete_for() {
        assert!(is_incomplete("for x in [1,2,3]"));
        assert!(!is_incomplete("for x in [1,2,3]\n  puts x\nend"));
    }

    #[test]
    fn nested_blocks() {
        assert!(is_incomplete("if true\n  for x in [1]\n    puts x\n  end"));
        assert!(!is_incomplete("if true\n  for x in [1]\n    puts x\n  end\nend"));
    }

    #[test]
    fn unclosed_string() {
        assert!(is_incomplete("puts \"hello"));
        assert!(!is_incomplete("puts \"hello\""));
    }

    #[test]
    fn comment_doesnt_count() {
        assert!(!is_incomplete("# if this were code"));
        assert!(!is_incomplete("x = 5 # if comment"));
    }

    #[test]
    fn string_content_ignored() {
        // "end" inside a string shouldn't close a block
        assert!(is_incomplete("if true\n  puts \"end\""));
    }
}

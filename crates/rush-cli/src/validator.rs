//! Multi-line input validator for reedline.
//! Detects incomplete Rush blocks (if/def/class/for/while without matching end).

use rushline::{ValidationResult, Validator};

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

        // Count block depth changes from keywords.
        // Only treat block-opener keywords when they appear as the FIRST word
        // of the line (matching triage's classification). Otherwise a shell
        // command like `mkdir foo-linux` — where the tokenizer splits on `-`
        // and produces ["mkdir", "foo", "linux"] — would incorrectly treat
        // `linux` as a platform block opener, flip the line to incomplete,
        // and drop rush into multi-line mode. Which, combined with terminal
        // cursor-position echoing, triggered an infinite repaint loop.
        //
        // `end` is allowed anywhere since it can legitimately close a prior
        // block regardless of position on the current line.
        if let Some(first_word) = line_words.first() {
            let lower = first_word.to_lowercase();
            match lower.as_str() {
                "if" | "unless" | "while" | "until" | "for" | "loop"
                | "def" | "class" | "enum" | "case" | "match"
                | "try" | "begin" | "do"
                | "macos" | "linux" | "win64" | "win32"
                | "plugin" => {
                    depth += 1;
                }
                // ps / ps5 are block keywords ONLY when bare (no args) —
                // this mirrors triage::is_rush_syntax. Without this gate
                // the unix `ps` command (`ps`, `ps -ef`, `ps aux`, etc.)
                // is misclassified as an open block, the Submit path
                // appends `\n` and waits forever for `end`. Caused #282-
                // adjacent "ps never returns" reports on Spark.
                "ps" | "ps5" if line_words.len() == 1 => {
                    depth += 1;
                }
                _ => {}
            }
        }
        for w in &line_words {
            if w.eq_ignore_ascii_case("end") {
                depth -= 1;
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

    #[test]
    fn keyword_as_arg_does_not_trigger_block() {
        // Regression: shell command with a hyphenated arg that contains a
        // platform keyword (linux / macos / win64 / etc) must not be treated
        // as opening a block. Previously `foo-linux` tokenized to ["foo",
        // "linux"] and the `linux` match pushed depth=1, sending rush into
        // multi-line mode, which combined with terminal cursor-position
        // echoes triggered an unbounded repaint loop and OOM.
        assert!(!is_incomplete("mkdir foo-linux"));
        assert!(!is_incomplete("mkdir xremap-linux-aarch64-gnome"));
        assert!(!is_incomplete("cd foo-macos"));
        assert!(!is_incomplete("rm -rf test-win64"));
        assert!(!is_incomplete("ls /opt/ps-bin"));
        assert!(!is_incomplete("/usr/local/bin/xremap linux-gnome"));
    }

    #[test]
    fn platform_keyword_as_first_word_still_opens_block() {
        // Legitimate platform blocks must still be detected.
        assert!(is_incomplete("linux"));
        assert!(is_incomplete("macos\n  puts \"mac\""));
        assert!(!is_incomplete("linux\n  puts \"unix\"\nend"));
    }

    #[test]
    fn end_closes_block_anywhere_on_line() {
        // `end` on the same line as its opener should still close the block.
        assert!(!is_incomplete("if true; puts x; end"));
    }

    #[test]
    fn ps_with_args_is_shell_not_block() {
        // The unix `ps` command must not be eaten by the multi-line
        // continuation gate. Only bare `ps` opens a Rush ps-block,
        // matching triage's classification. Caused real-world hangs
        // where typing `ps` or `ps -ef | grep …` would silently drop
        // rush into multi-line capture and never submit until the user
        // typed `end`.
        assert!(!is_incomplete("ps -ef"));
        assert!(!is_incomplete("ps aux"));
        assert!(!is_incomplete("ps -ef | grep rush"));
        assert!(!is_incomplete("ps5 -SomeArg"));
        // Bare `ps` / `ps5` still legitimately open a block.
        assert!(is_incomplete("ps"));
        assert!(is_incomplete("ps5"));
    }
}

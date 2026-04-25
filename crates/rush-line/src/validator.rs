//! Multi-line input continuation.
//!
//! When the user hits Enter, the engine asks the [`Validator`]
//! whether the buffer is "complete" (ready to submit) or "incomplete"
//! (an unclosed quote, an unbalanced brace, a trailing `\` line
//! continuation, etc.). On `Incomplete`, the engine inserts a `\n`
//! into the buffer instead of submitting, so the user keeps typing
//! on a new line within the same edit session.
//!
//! The default validator never reports incomplete — every Enter
//! submits. Hosts that need shell-style continuation (Rush, bash) hand
//! in their own implementation.

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ValidationResult {
    /// Buffer is ready; Enter should submit.
    Complete,
    /// Buffer needs more input; Enter should insert a newline and
    /// keep editing.
    Incomplete,
}

pub trait Validator {
    fn validate(&self, line: &str) -> ValidationResult;
}

/// Always reports `Complete`. Default when no validator is attached.
#[derive(Debug, Default, Clone, Copy)]
pub struct AlwaysComplete;

impl Validator for AlwaysComplete {
    fn validate(&self, _line: &str) -> ValidationResult {
        ValidationResult::Complete
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn always_complete_is_always_complete() {
        let v = AlwaysComplete;
        assert_eq!(v.validate(""), ValidationResult::Complete);
        assert_eq!(v.validate("ls"), ValidationResult::Complete);
        assert_eq!(v.validate("ls \\"), ValidationResult::Complete);
    }
}

//! Completion API.
//!
//! ## Model
//!
//! [`Completer::complete`] takes the current buffer text and cursor
//! byte offset, and returns a list of [`Suggestion`]s. Each suggestion
//! describes a replacement: the [`Span`] of bytes in the buffer to
//! replace, and the `value` to put there (plus whether to append a
//! space after — typical for command names and matched directories).
//!
//! The engine applies suggestions with a bash-style policy:
//!
//! - First Tab: if there's exactly one suggestion, apply it. If there
//!   are several but they share a longer common prefix than what the
//!   user already typed, extend to that common prefix.
//! - Subsequent Tabs (no other key in between): cycle through the
//!   suggestions one at a time, replacing the previous attempt.
//! - Any non-Tab key resets the cycle.
//!
//! Listing all available completions in a menu overlay isn't in this
//! phase — Tab on an already-at-common-prefix ambiguous match starts
//! cycling instead of opening a list. Adding a menu is additive: a
//! later phase can render below the buffer using the painter's
//! existing relative paint area.
//!
//! ## Why not Send
//!
//! Like [`crate::history::History`], the completer is held by the
//! single-threaded engine. Dropping `Send` keeps the trait-object
//! lifetime story simple (`&mut dyn Completer + 'static`) without
//! pushing variance hazards into callers.

#[derive(Debug, Default, Clone, Copy, PartialEq, Eq, Ord, PartialOrd, Hash)]
pub struct Span {
    /// Inclusive start byte offset in the buffer.
    pub start: usize,
    /// Exclusive end byte offset.
    pub end: usize,
}

impl Span {
    pub fn new(start: usize, end: usize) -> Self {
        debug_assert!(end >= start);
        Self { start, end }
    }
}

#[derive(Debug, Default, Clone, PartialEq, Eq)]
pub struct Suggestion {
    /// Replacement text to insert at `span`.
    pub value: String,
    /// Buffer range the replacement covers.
    pub span: Span,
    /// `true` if a space should be appended after `value` (typical
    /// for command names and directories — saves a keystroke when
    /// chaining).
    pub append_whitespace: bool,
}

pub trait Completer {
    /// Return suggestions for the cursor at byte position `pos` in
    /// `line`. Empty vec means "no completions" — the engine treats
    /// this as a no-op (silent; no terminal bell, since we can't
    /// reliably emit one on every emulator).
    fn complete(&mut self, line: &str, pos: usize) -> Vec<Suggestion>;
}

/// Longest common prefix of all `values`. Empty if `values` is empty.
pub fn longest_common_prefix<'a>(values: impl IntoIterator<Item = &'a str>) -> String {
    let mut iter = values.into_iter();
    let Some(first) = iter.next() else {
        return String::new();
    };
    let mut prefix_end = first.len();
    for s in iter {
        let mut new_end = 0;
        for (a, b) in first.bytes().zip(s.bytes()).take(prefix_end) {
            if a == b {
                new_end += 1;
            } else {
                break;
            }
        }
        prefix_end = new_end;
        if prefix_end == 0 {
            break;
        }
    }
    // Snap to a UTF-8 char boundary in case we cut mid-multibyte.
    let mut end = prefix_end;
    while end > 0 && !first.is_char_boundary(end) {
        end -= 1;
    }
    first[..end].to_string()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn lcp_empty_iter_is_empty() {
        let v: Vec<&str> = Vec::new();
        assert_eq!(longest_common_prefix(v), "");
    }

    #[test]
    fn lcp_single_returns_full() {
        assert_eq!(longest_common_prefix(["hello"]), "hello");
    }

    #[test]
    fn lcp_common_prefix() {
        assert_eq!(longest_common_prefix(["foobar", "foobaz", "foo"]), "foo");
    }

    #[test]
    fn lcp_no_common_returns_empty() {
        assert_eq!(longest_common_prefix(["abc", "xyz"]), "");
    }

    #[test]
    fn lcp_respects_utf8_boundaries() {
        // "café" and "café\u{0301}" share "café" as bytes.
        let a = "café";
        let b = "café\u{0301}";
        let lcp = longest_common_prefix([a, b]);
        assert_eq!(lcp, "café");
    }

    #[test]
    fn span_new_records_range() {
        let s = Span::new(2, 5);
        assert_eq!(s.start, 2);
        assert_eq!(s.end, 5);
    }
}

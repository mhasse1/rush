//! Syntax highlighter for the input line.
//!
//! ## Contract
//!
//! [`Highlighter::highlight`] takes a buffer segment (a `&str`) and
//! returns a string with ANSI escape sequences mixed in for color /
//! style. The engine renders the result verbatim. ANSI escapes don't
//! contribute visible width, so they don't affect the painter's
//! measurement (which strips ANSI before counting).
//!
//! ## Why segment, not full buffer?
//!
//! The engine splits the buffer at the cursor (`before_cursor` /
//! `after_cursor`) so that `cursor::SavePosition` can land between
//! them. Calling the highlighter on each segment is the simplest
//! integration: the highlighter runs once on each side of the cursor.
//! Most syntax highlighters tokenize independently and tolerate
//! partial input — the worst case is a slightly different highlight
//! when a token (e.g. a quoted string) spans the cursor. If that
//! becomes a real problem in practice, we can refactor to highlight
//! the full buffer once and split the highlighted output skipping
//! ANSI escapes — but the cost there is non-trivial and not worth
//! paying for a visual quirk.
//!
//! ## Default
//!
//! No highlighter is attached by default. The host opts in via
//! [`crate::LineEditor::with_highlighter`].

pub trait Highlighter {
    fn highlight(&self, segment: &str) -> String;
}

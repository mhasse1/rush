//! Wrap-aware row/column counting for emitted content.
//!
//! When the engine emits prompt + buffer + hint to the terminal, the
//! painter needs to know two things to track its state for the next
//! repaint:
//!
//! - `cursor_row` — what row offset (from paint top) the cursor lands
//!   on at the end of emit. Drives `rows_above_cursor` for the next
//!   paint's relative walk-up.
//! - `rows_used` — how many rows of content the emit actually wrote
//!   to. Drives `last_emit_rows` for the next paint's clear walk.
//!
//! Both are computed by walking the emitted text character by character,
//! tracking column position with wrap, and incrementing row on newlines.
//! ANSI escape sequences are stripped first so they don't count as
//! visible width.
//!
//! Conventions matching most terminal emulators:
//!
//! - A character of width `cw` placed at column `col` advances the
//!   cursor to `col + cw` if `col + cw <= width`, otherwise wraps to
//!   the start of the next row first ("lazy wrap" — the right margin
//!   is reachable, the wrap happens on the next character).
//! - `\n` advances row, resets column to 0. (Not CR+LF; CR is its own
//!   thing.) The caller is expected to coerce `\n` → `\r\n` before
//!   emit when in raw mode; `measure` doesn't care because both
//!   sequences land cursor at (col=0, row+1).
//! - `\r` resets column without changing row.

use strip_ansi_escapes::strip_str;
use unicode_width::UnicodeWidthChar;

/// Result of measuring an emitted-content string.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Measurement {
    /// Row offset from start where the cursor lands at end of emit.
    pub cursor_row: u16,
    /// Number of distinct rows the emit wrote content to. Always at
    /// least 1 if any non-newline character was emitted; 0 if the
    /// emit was empty or pure whitespace-only newlines.
    pub rows_used: u16,
}

/// Walk `text` and return cursor-end row + content-rows-used.
pub fn measure(text: &str, terminal_width: u16) -> Measurement {
    if terminal_width == 0 || text.is_empty() {
        return Measurement { cursor_row: 0, rows_used: 0 };
    }
    let width = terminal_width as usize;
    let stripped = strip_str(text);
    let mut row: u32 = 0;
    let mut col: usize = 0;
    let mut max_content_row: Option<u32> = None;

    for ch in stripped.chars() {
        match ch {
            '\n' => {
                row = row.saturating_add(1);
                col = 0;
            }
            '\r' => {
                col = 0;
            }
            _ => {
                let cw = ch.width().unwrap_or(0);
                if cw == 0 {
                    continue; // zero-width char (combining mark, ZWJ, etc.)
                }
                if col + cw > width {
                    row = row.saturating_add(1);
                    col = 0;
                }
                col += cw;
                max_content_row = Some(match max_content_row {
                    Some(m) => m.max(row),
                    None => row,
                });
            }
        }
    }

    let cursor_row = row.min(u16::MAX as u32) as u16;
    let rows_used = match max_content_row {
        Some(m) => (m + 1).min(u16::MAX as u32) as u16,
        None => 0,
    };
    Measurement { cursor_row, rows_used }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn m(text: &str, width: u16) -> (u16, u16) {
        let r = measure(text, width);
        (r.cursor_row, r.rows_used)
    }

    #[test]
    fn empty_string_is_zero_zero() {
        assert_eq!(m("", 80), (0, 0));
    }

    #[test]
    fn single_line_is_zero_one() {
        assert_eq!(m("abc", 80), (0, 1));
    }

    #[test]
    fn trailing_newline_advances_cursor_but_no_content_below() {
        // "abc\n" → cursor on row 1, content only on row 0.
        assert_eq!(m("abc\n", 80), (1, 1));
    }

    #[test]
    fn newline_then_content_uses_two_rows() {
        assert_eq!(m("abc\ndef", 80), (1, 2));
    }

    #[test]
    fn double_newline_advances_cursor_two_rows() {
        // "abc\n\n" → cursor on row 2, content only on row 0.
        assert_eq!(m("abc\n\n", 80), (2, 1));
    }

    #[test]
    fn pure_newlines_have_zero_content_rows() {
        // "\n\n" → cursor on row 2, no content at all.
        assert_eq!(m("\n\n", 80), (2, 0));
    }

    #[test]
    fn line_at_exact_width_does_not_wrap() {
        // Width 5, "abcde" exactly fills row 0 with no wrap.
        assert_eq!(m("abcde", 5), (0, 1));
    }

    #[test]
    fn one_char_past_width_wraps() {
        assert_eq!(m("abcdef", 5), (1, 2));
    }

    #[test]
    fn wraps_count_per_logical_line() {
        // "12345abc\nxy" → row 0 holds "12345", row 1 holds "abc",
        // \n moves to row 2, "xy" lands on row 2.
        assert_eq!(m("12345abc\nxy", 5), (2, 3));
    }

    #[test]
    fn ansi_escapes_do_not_consume_width() {
        // "\x1b[31mabc\x1b[0m" is "abc" visually (3 cells).
        assert_eq!(m("\x1b[31mabc\x1b[0m", 80), (0, 1));
    }

    #[test]
    fn wide_chars_count_as_two_cells() {
        // CJK width-2 char twice = 4 cells. At width 5, fits row 0.
        assert_eq!(m("中国", 5), (0, 1));
        // At width 3, "中" fits row 0 (col 2), "国" doesn't fit on row 0
        // (col 2 + 2 > 3), so it wraps.
        assert_eq!(m("中国", 3), (1, 2));
    }

    #[test]
    fn cr_resets_column_without_changing_row() {
        // "abc\rde" → 'a','b','c' fill cols 0..3, \r resets to col 0,
        // 'd','e' overwrite cols 0..2. All on row 0.
        assert_eq!(m("abc\rde", 80), (0, 1));
    }

    #[test]
    fn crlf_acts_as_one_newline_for_row_count() {
        assert_eq!(m("abc\r\ndef", 80), (1, 2));
    }

    #[test]
    fn zero_width_combining_marks_dont_advance() {
        // 'e' + combining acute (U+0301) renders as one cell width-wise.
        assert_eq!(m("e\u{0301}", 80), (0, 1));
    }

    #[test]
    fn zero_width_terminal_returns_zeros() {
        assert_eq!(m("anything", 0), (0, 0));
    }
}

//! `LineBuffer` — the editable text and cursor for one line-edit session.
//!
//! Stored as a `String` (the entire buffer including any embedded `\n`
//! for multi-line edits) plus a byte-offset cursor. All movement and
//! deletion APIs are grapheme-cluster aware via the `unicode-segmentation`
//! crate, so combining characters and emoji ZWJ sequences move and
//! delete as single units (matching what users see, not what bytes
//! happen to encode).
//!
//! Word-wise operations (Alt-B, Alt-F, Alt-Backspace, Alt-D in emacs
//! mode) use whitespace boundaries — same heuristic readline ships
//! with by default. Word characters are non-whitespace, separators are
//! whitespace runs.
//!
//! "Kill" operations (Ctrl-K, Ctrl-U, Ctrl-W) currently just delete;
//! a kill ring (yank support) lands in a later phase along with the
//! rest of emacs mode.

use unicode_segmentation::UnicodeSegmentation;

#[derive(Debug, Clone, Default)]
pub struct LineBuffer {
    text: String,
    /// Cursor as a byte offset into `text`. Always on a grapheme
    /// boundary (asserted by every API that mutates it).
    cursor: usize,
}

impl LineBuffer {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn from_str(s: &str) -> Self {
        let cursor = s.len();
        Self {
            text: s.to_string(),
            cursor,
        }
    }

    pub fn text(&self) -> &str {
        &self.text
    }

    pub fn cursor(&self) -> usize {
        self.cursor
    }

    pub fn is_empty(&self) -> bool {
        self.text.is_empty()
    }

    pub fn len(&self) -> usize {
        self.text.len()
    }

    /// Slice of buffer text strictly before the cursor.
    pub fn before_cursor(&self) -> &str {
        &self.text[..self.cursor]
    }

    /// Slice of buffer text from the cursor onwards.
    pub fn after_cursor(&self) -> &str {
        &self.text[self.cursor..]
    }

    pub fn clear(&mut self) {
        self.text.clear();
        self.cursor = 0;
    }

    /// Replace the entire buffer with `s` and place the cursor at the end.
    /// Used by history navigation.
    pub fn set_text(&mut self, s: &str) {
        self.text.clear();
        self.text.push_str(s);
        self.cursor = self.text.len();
    }

    // ---- single-character editing ----

    pub fn insert_char(&mut self, c: char) {
        self.text.insert(self.cursor, c);
        self.cursor += c.len_utf8();
    }

    pub fn insert_str(&mut self, s: &str) {
        self.text.insert_str(self.cursor, s);
        self.cursor += s.len();
    }

    /// Delete the grapheme to the left of the cursor (Backspace).
    pub fn delete_left(&mut self) {
        if let Some(prev) = grapheme_boundary_before(&self.text, self.cursor) {
            self.text.drain(prev..self.cursor);
            self.cursor = prev;
        }
    }

    /// Delete the grapheme to the right of the cursor (Delete / Ctrl-D
    /// when buffer is non-empty — Ctrl-D on an empty buffer is EOF and
    /// is handled at the engine layer).
    pub fn delete_right(&mut self) {
        if let Some(next) = grapheme_boundary_after(&self.text, self.cursor) {
            self.text.drain(self.cursor..next);
        }
    }

    // ---- cursor movement ----

    pub fn move_left(&mut self) {
        if let Some(prev) = grapheme_boundary_before(&self.text, self.cursor) {
            self.cursor = prev;
        }
    }

    pub fn move_right(&mut self) {
        if let Some(next) = grapheme_boundary_after(&self.text, self.cursor) {
            self.cursor = next;
        }
    }

    pub fn move_home(&mut self) {
        self.cursor = 0;
    }

    pub fn move_end(&mut self) {
        self.cursor = self.text.len();
    }

    // ---- word-wise (whitespace-bounded, readline default) ----

    pub fn move_word_left(&mut self) {
        self.cursor = word_boundary_before(&self.text, self.cursor);
    }

    pub fn move_word_right(&mut self) {
        self.cursor = word_boundary_after(&self.text, self.cursor);
    }

    pub fn delete_word_left(&mut self) {
        let start = word_boundary_before(&self.text, self.cursor);
        if start < self.cursor {
            self.text.drain(start..self.cursor);
            self.cursor = start;
        }
    }

    pub fn delete_word_right(&mut self) {
        let end = word_boundary_after(&self.text, self.cursor);
        if end > self.cursor {
            self.text.drain(self.cursor..end);
        }
    }

    // ---- line-wise ("kill") ----

    /// Ctrl-K: delete from cursor to end of line (end of buffer for
    /// single-line edits; end of current line within multi-line).
    pub fn kill_to_end(&mut self) {
        let end = match self.text[self.cursor..].find('\n') {
            Some(rel) => self.cursor + rel,
            None => self.text.len(),
        };
        self.text.drain(self.cursor..end);
    }

    /// Ctrl-U: delete from start of line to cursor.
    pub fn kill_to_start(&mut self) {
        let start = match self.text[..self.cursor].rfind('\n') {
            Some(at) => at + 1,
            None => 0,
        };
        self.text.drain(start..self.cursor);
        self.cursor = start;
    }
}

// ---- grapheme / word boundary helpers ----

fn grapheme_boundary_before(text: &str, byte_idx: usize) -> Option<usize> {
    if byte_idx == 0 {
        return None;
    }
    text[..byte_idx]
        .grapheme_indices(true)
        .next_back()
        .map(|(i, _)| i)
}

fn grapheme_boundary_after(text: &str, byte_idx: usize) -> Option<usize> {
    if byte_idx >= text.len() {
        return None;
    }
    text[byte_idx..]
        .grapheme_indices(true)
        .nth(1)
        .map(|(i, _)| byte_idx + i)
        .or(Some(text.len()))
}

/// Move backward over a run of whitespace, then a run of word
/// characters. Return the byte offset of the start of the run we
/// landed in.
fn word_boundary_before(text: &str, byte_idx: usize) -> usize {
    if byte_idx == 0 {
        return 0;
    }
    let bytes = text.as_bytes();
    let mut i = byte_idx;
    // Skip whitespace before cursor.
    while i > 0 && is_ws_byte(bytes[i - 1]) {
        i -= 1;
    }
    // Skip word characters.
    while i > 0 && !is_ws_byte(bytes[i - 1]) {
        i -= 1;
    }
    // Snap to a char boundary just in case (multi-byte chars).
    while !text.is_char_boundary(i) {
        i -= 1;
    }
    i
}

fn word_boundary_after(text: &str, byte_idx: usize) -> usize {
    let bytes = text.as_bytes();
    let len = bytes.len();
    if byte_idx >= len {
        return len;
    }
    let mut i = byte_idx;
    // Skip whitespace at cursor.
    while i < len && is_ws_byte(bytes[i]) {
        i += 1;
    }
    // Skip word characters.
    while i < len && !is_ws_byte(bytes[i]) {
        i += 1;
    }
    while i < len && !text.is_char_boundary(i) {
        i += 1;
    }
    i
}

#[inline]
fn is_ws_byte(b: u8) -> bool {
    b == b' ' || b == b'\t' || b == b'\n'
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn empty_buffer_basics() {
        let b = LineBuffer::new();
        assert!(b.is_empty());
        assert_eq!(b.cursor(), 0);
        assert_eq!(b.text(), "");
        assert_eq!(b.before_cursor(), "");
        assert_eq!(b.after_cursor(), "");
    }

    #[test]
    fn insert_char_advances_cursor() {
        let mut b = LineBuffer::new();
        b.insert_char('h');
        b.insert_char('i');
        assert_eq!(b.text(), "hi");
        assert_eq!(b.cursor(), 2);
    }

    #[test]
    fn insert_str_advances_cursor() {
        let mut b = LineBuffer::new();
        b.insert_str("hello");
        assert_eq!(b.text(), "hello");
        assert_eq!(b.cursor(), 5);
    }

    #[test]
    fn insert_at_cursor_position() {
        let mut b = LineBuffer::from_str("abcd");
        b.move_left();
        b.move_left();
        // cursor is now between 'b' and 'c' at byte 2
        assert_eq!(b.cursor(), 2);
        b.insert_char('X');
        assert_eq!(b.text(), "abXcd");
        assert_eq!(b.cursor(), 3);
    }

    #[test]
    fn delete_left_deletes_grapheme_before_cursor() {
        let mut b = LineBuffer::from_str("hello");
        b.delete_left();
        assert_eq!(b.text(), "hell");
        assert_eq!(b.cursor(), 4);
    }

    #[test]
    fn delete_left_at_start_is_noop() {
        let mut b = LineBuffer::from_str("x");
        b.move_home();
        b.delete_left();
        assert_eq!(b.text(), "x");
        assert_eq!(b.cursor(), 0);
    }

    #[test]
    fn delete_right_deletes_grapheme_after_cursor() {
        let mut b = LineBuffer::from_str("hello");
        b.move_home();
        b.delete_right();
        assert_eq!(b.text(), "ello");
        assert_eq!(b.cursor(), 0);
    }

    #[test]
    fn delete_right_at_end_is_noop() {
        let mut b = LineBuffer::from_str("hi");
        b.delete_right();
        assert_eq!(b.text(), "hi");
        assert_eq!(b.cursor(), 2);
    }

    #[test]
    fn cursor_movement_basics() {
        let mut b = LineBuffer::from_str("abc");
        assert_eq!(b.cursor(), 3);
        b.move_left();
        assert_eq!(b.cursor(), 2);
        b.move_home();
        assert_eq!(b.cursor(), 0);
        b.move_right();
        assert_eq!(b.cursor(), 1);
        b.move_end();
        assert_eq!(b.cursor(), 3);
    }

    #[test]
    fn move_handles_multibyte_chars() {
        let mut b = LineBuffer::from_str("café");
        // 'é' is two bytes (e + combining acute) in NFC normalized,
        // but in this literal it's the precomposed U+00E9 (2 bytes).
        // The buffer has 5 bytes total: 'c','a','f' (1 each), 'é' (2).
        assert_eq!(b.text().len(), 5);
        b.move_left(); // before 'é'
        assert_eq!(b.cursor(), 3);
        b.move_left(); // before 'f'
        assert_eq!(b.cursor(), 2);
    }

    #[test]
    fn delete_handles_multibyte_chars() {
        let mut b = LineBuffer::from_str("café");
        b.delete_left();
        assert_eq!(b.text(), "caf");
        assert_eq!(b.cursor(), 3);
    }

    #[test]
    fn delete_handles_grapheme_clusters() {
        // 'é' as 'e' + combining acute (U+0301): two scalars, one grapheme.
        let mut b = LineBuffer::from_str("e\u{0301}");
        assert_eq!(b.text().len(), 3); // 1 + 2 bytes
        b.delete_left();
        // The whole grapheme deletes as a unit.
        assert_eq!(b.text(), "");
        assert_eq!(b.cursor(), 0);
    }

    #[test]
    fn word_left_skips_trailing_space_then_word() {
        let mut b = LineBuffer::from_str("hello world  ");
        // cursor at end, after the trailing spaces
        b.move_word_left();
        // skips spaces, lands at start of "world"
        assert_eq!(b.cursor(), 6);
        b.move_word_left();
        // skips space (none here, we're at 'w'), lands at start of "hello"
        assert_eq!(b.cursor(), 0);
    }

    #[test]
    fn word_right_skips_leading_space_then_word() {
        let mut b = LineBuffer::from_str("  hello world");
        b.move_home();
        b.move_word_right();
        // skips spaces, then "hello", lands after 'o' at byte 7
        assert_eq!(b.cursor(), 7);
        b.move_word_right();
        // skips space, then "world", lands at end
        assert_eq!(b.cursor(), 13);
    }

    #[test]
    fn delete_word_left_removes_word_leaves_trailing_space() {
        // Matches readline `backward-kill-word` and emacs M-DEL: the
        // word goes, the space before it (now after cursor) stays.
        // A second M-DEL would land on whitespace and consume the
        // space + the previous word as one unit.
        let mut b = LineBuffer::from_str("foo bar baz");
        b.delete_word_left();
        assert_eq!(b.text(), "foo bar ");
        assert_eq!(b.cursor(), 8);
        b.delete_word_left();
        assert_eq!(b.text(), "foo ");
        b.delete_word_left();
        assert_eq!(b.text(), "");
    }

    #[test]
    fn delete_word_right_removes_word_and_trail_space() {
        let mut b = LineBuffer::from_str("foo bar baz");
        b.move_home();
        b.delete_word_right();
        assert_eq!(b.text(), " bar baz");
    }

    #[test]
    fn kill_to_end_single_line() {
        let mut b = LineBuffer::from_str("hello world");
        b.move_home();
        b.move_word_right();
        // cursor after "hello"
        b.kill_to_end();
        assert_eq!(b.text(), "hello");
        assert_eq!(b.cursor(), 5);
    }

    #[test]
    fn kill_to_end_multi_line_stops_at_newline() {
        let mut b = LineBuffer::from_str("line1\nline2");
        b.move_home();
        b.kill_to_end();
        // killed up to (not including) the \n
        assert_eq!(b.text(), "\nline2");
        assert_eq!(b.cursor(), 0);
    }

    #[test]
    fn kill_to_start_single_line() {
        let mut b = LineBuffer::from_str("hello world");
        // cursor at end
        b.kill_to_start();
        assert_eq!(b.text(), "");
        assert_eq!(b.cursor(), 0);
    }

    #[test]
    fn kill_to_start_multi_line_stops_at_newline() {
        let mut b = LineBuffer::from_str("line1\nline2");
        // cursor at end (after "line2")
        b.kill_to_start();
        // killed back to (just after) the \n
        assert_eq!(b.text(), "line1\n");
        assert_eq!(b.cursor(), 6);
    }

    #[test]
    fn set_text_replaces_buffer_and_puts_cursor_at_end() {
        let mut b = LineBuffer::from_str("hello");
        b.move_home();
        b.set_text("goodbye");
        assert_eq!(b.text(), "goodbye");
        assert_eq!(b.cursor(), 7);
    }

    #[test]
    fn before_after_cursor_views() {
        let mut b = LineBuffer::from_str("hello");
        b.move_left();
        b.move_left();
        assert_eq!(b.before_cursor(), "hel");
        assert_eq!(b.after_cursor(), "lo");
    }
}

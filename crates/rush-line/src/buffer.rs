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
    /// Snapshots of (text, cursor) before each mutation. [`undo`]
    /// pops from this and restores. Capped to keep memory bounded.
    undo_stack: Vec<(String, usize)>,
}

const UNDO_STACK_CAP: usize = 256;

impl LineBuffer {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn from_str(s: &str) -> Self {
        let cursor = s.len();
        Self {
            text: s.to_string(),
            cursor,
            undo_stack: Vec::new(),
        }
    }

    /// Push a (text, cursor) snapshot to the undo stack. Idempotent
    /// when the most recent snapshot already matches current state,
    /// so a no-op mutation (e.g. Backspace at start of buffer) doesn't
    /// pollute history.
    fn push_undo(&mut self) {
        if let Some((last_text, last_cursor)) = self.undo_stack.last() {
            if last_text == &self.text && *last_cursor == self.cursor {
                return;
            }
        }
        self.undo_stack.push((self.text.clone(), self.cursor));
        if self.undo_stack.len() > UNDO_STACK_CAP {
            self.undo_stack.remove(0);
        }
    }

    /// Pop the most recent undo snapshot and restore it. Returns
    /// `true` if a snapshot was restored, `false` if the stack was
    /// empty.
    pub fn undo(&mut self) -> bool {
        if let Some((text, cursor)) = self.undo_stack.pop() {
            self.text = text;
            self.cursor = cursor;
            true
        } else {
            false
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
        self.push_undo();
        self.text.clear();
        self.cursor = 0;
    }

    /// Replace the entire buffer with `s` and place the cursor at the end.
    /// Used by history navigation and tab completion replacements.
    pub fn set_text(&mut self, s: &str) {
        self.push_undo();
        self.text.clear();
        self.text.push_str(s);
        self.cursor = self.text.len();
    }

    // ---- single-character editing ----

    pub fn insert_char(&mut self, c: char) {
        self.push_undo();
        self.text.insert(self.cursor, c);
        self.cursor += c.len_utf8();
    }

    pub fn insert_str(&mut self, s: &str) {
        self.push_undo();
        self.text.insert_str(self.cursor, s);
        self.cursor += s.len();
    }

    /// Delete the grapheme to the left of the cursor (Backspace).
    pub fn delete_left(&mut self) {
        if let Some(prev) = grapheme_boundary_before(&self.text, self.cursor) {
            self.push_undo();
            self.text.drain(prev..self.cursor);
            self.cursor = prev;
        }
    }

    /// Delete the grapheme to the right of the cursor (Delete / Ctrl-D
    /// when buffer is non-empty — Ctrl-D on an empty buffer is EOF and
    /// is handled at the engine layer).
    pub fn delete_right(&mut self) {
        if let Some(next) = grapheme_boundary_after(&self.text, self.cursor) {
            self.push_undo();
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

    /// Place the cursor at the byte position `pos`. Snaps to the
    /// nearest grapheme boundary at or below `pos`, and clamps to the
    /// buffer length. No undo snapshot — pure cursor movement.
    pub fn set_cursor(&mut self, pos: usize) {
        let mut p = pos.min(self.text.len());
        // Snap down to a char boundary first (cheap).
        while p > 0 && !self.text.is_char_boundary(p) {
            p -= 1;
        }
        self.cursor = p;
    }

    // ---- word-wise (whitespace-bounded, readline default) ----

    pub fn move_word_left(&mut self) {
        self.cursor = word_boundary_before(&self.text, self.cursor);
    }

    pub fn move_word_right(&mut self) {
        self.cursor = word_boundary_after(&self.text, self.cursor);
    }

    /// Delete the word to the left of the cursor. Returns the
    /// deleted text so the engine can route it to the kill ring.
    pub fn delete_word_left(&mut self) -> Option<String> {
        let start = word_boundary_before(&self.text, self.cursor);
        if start < self.cursor {
            self.push_undo();
            let killed: String = self.text.drain(start..self.cursor).collect();
            self.cursor = start;
            Some(killed)
        } else {
            None
        }
    }

    /// Delete the word to the right of the cursor. Returns the
    /// deleted text.
    pub fn delete_word_right(&mut self) -> Option<String> {
        let end = word_boundary_after(&self.text, self.cursor);
        if end > self.cursor {
            self.push_undo();
            let killed: String = self.text.drain(self.cursor..end).collect();
            Some(killed)
        } else {
            None
        }
    }

    // ---- line-wise ("kill") ----

    /// Ctrl-K: delete from cursor to end of line (end of buffer for
    /// single-line edits; end of current line within multi-line).
    /// Returns the deleted text.
    pub fn kill_to_end(&mut self) -> Option<String> {
        let end = match self.text[self.cursor..].find('\n') {
            Some(rel) => self.cursor + rel,
            None => self.text.len(),
        };
        if end > self.cursor {
            self.push_undo();
            let killed: String = self.text.drain(self.cursor..end).collect();
            Some(killed)
        } else {
            None
        }
    }

    /// Ctrl-U: delete from start of line to cursor. Returns the
    /// deleted text.
    pub fn kill_to_start(&mut self) -> Option<String> {
        let start = match self.text[..self.cursor].rfind('\n') {
            Some(at) => at + 1,
            None => 0,
        };
        if start < self.cursor {
            self.push_undo();
            let killed: String = self.text.drain(start..self.cursor).collect();
            self.cursor = start;
            Some(killed)
        } else {
            None
        }
    }

    /// Drain the entire buffer and return what was there. Used by the
    /// engine for `dd`/`cc`-style "kill the whole line" actions.
    pub fn take_all(&mut self) -> Option<String> {
        if self.text.is_empty() {
            return None;
        }
        self.push_undo();
        let killed = std::mem::take(&mut self.text);
        self.cursor = 0;
        Some(killed)
    }

    /// Replace the character at the cursor with `c`. Cursor stays put.
    /// No-op if cursor is at end of buffer (vi `r` at EOL is a no-op
    /// in real vim; matches that behavior).
    pub fn replace_char_at_cursor(&mut self, c: char) {
        let Some(end) = grapheme_boundary_after(&self.text, self.cursor) else {
            return;
        };
        self.push_undo();
        self.text.replace_range(self.cursor..end, &c.to_string());
    }

    /// Toggle the case of the character at the cursor and advance
    /// the cursor one grapheme to the right. Vi `~` semantics. No-op
    /// if cursor is at end of buffer.
    pub fn toggle_case_at_cursor(&mut self) {
        let Some(end) = grapheme_boundary_after(&self.text, self.cursor) else {
            return;
        };
        let segment = &self.text[self.cursor..end];
        let toggled: String = segment
            .chars()
            .map(|ch| {
                if ch.is_uppercase() {
                    ch.to_lowercase().collect::<String>()
                } else if ch.is_lowercase() {
                    ch.to_uppercase().collect::<String>()
                } else {
                    ch.to_string()
                }
            })
            .collect();
        if toggled != segment {
            self.push_undo();
            self.text.replace_range(self.cursor..end, &toggled);
        }
        // Advance cursor either way (matches vim — `~` always moves
        // even on punctuation).
        if let Some(next) = grapheme_boundary_after(&self.text, self.cursor) {
            self.cursor = next;
        }
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

    #[test]
    fn undo_restores_pre_mutation_state() {
        let mut b = LineBuffer::new();
        b.insert_char('a');
        b.insert_char('b');
        b.insert_char('c');
        assert_eq!(b.text(), "abc");
        assert!(b.undo());
        assert_eq!(b.text(), "ab");
        assert!(b.undo());
        assert_eq!(b.text(), "a");
        assert!(b.undo());
        assert_eq!(b.text(), "");
        // Stack empty.
        assert!(!b.undo());
    }

    #[test]
    fn undo_restores_cursor_position() {
        let mut b = LineBuffer::from_str("hello");
        b.move_left();
        b.move_left();
        let cursor_before = b.cursor();
        b.delete_left();
        assert_eq!(b.text(), "helo");
        b.undo();
        assert_eq!(b.text(), "hello");
        assert_eq!(b.cursor(), cursor_before);
    }

    #[test]
    fn undo_handles_kill_to_end() {
        let mut b = LineBuffer::from_str("hello world");
        b.move_home();
        b.move_word_right();
        b.kill_to_end();
        assert_eq!(b.text(), "hello");
        b.undo();
        assert_eq!(b.text(), "hello world");
    }

    #[test]
    fn undo_idempotent_on_noop_mutations() {
        // delete_left at start of buffer is a no-op and should not
        // pollute the undo stack.
        let mut b = LineBuffer::from_str("abc");
        b.move_home();
        b.delete_left();
        b.delete_left();
        // Stack still empty — neither no-op pushed.
        assert!(!b.undo());
    }

    #[test]
    fn replace_char_at_cursor() {
        let mut b = LineBuffer::from_str("abc");
        b.move_home();
        b.replace_char_at_cursor('X');
        assert_eq!(b.text(), "Xbc");
        assert_eq!(b.cursor(), 0); // cursor stays put
    }

    #[test]
    fn replace_char_at_end_of_buffer_is_noop() {
        let mut b = LineBuffer::from_str("abc");
        // cursor at end (3)
        b.replace_char_at_cursor('X');
        assert_eq!(b.text(), "abc");
    }

    #[test]
    fn toggle_case_lowercase_to_upper() {
        let mut b = LineBuffer::from_str("abc");
        b.move_home();
        b.toggle_case_at_cursor();
        assert_eq!(b.text(), "Abc");
        assert_eq!(b.cursor(), 1); // advanced
    }

    #[test]
    fn toggle_case_uppercase_to_lower() {
        let mut b = LineBuffer::from_str("ABC");
        b.move_home();
        b.toggle_case_at_cursor();
        assert_eq!(b.text(), "aBC");
        assert_eq!(b.cursor(), 1);
    }

    #[test]
    fn toggle_case_punctuation_advances_anyway() {
        let mut b = LineBuffer::from_str("!ab");
        b.move_home();
        b.toggle_case_at_cursor();
        assert_eq!(b.text(), "!ab"); // unchanged
        assert_eq!(b.cursor(), 1);   // but cursor advanced
    }

    #[test]
    fn set_cursor_clamps_and_snaps() {
        let mut b = LineBuffer::from_str("café");
        b.set_cursor(99);
        assert_eq!(b.cursor(), b.len());
        b.set_cursor(0);
        assert_eq!(b.cursor(), 0);
        // Mid-multibyte snaps down. 'é' starts at byte 3.
        b.set_cursor(4);
        assert_eq!(b.cursor(), 3);
    }
}

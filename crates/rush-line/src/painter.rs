//! Cursor-relative painter. Readline-model.
//!
//! ## State
//!
//! Three fields. That's the whole model.
//!
//! - `rows_above_cursor` — how many rows of our paint area sit above the
//!   terminal cursor's current position. After a paint, this is the row
//!   offset (0-based) from the top of what we drew to the editor's
//!   logical cursor position.
//! - `last_emit_rows` — how many rows the previous paint emitted in total.
//!   Bounds the clear walk; never inspected for absolute coordinates.
//! - `terminal_size` — cached for wrap/width math only. Refreshed on
//!   `handle_resize`.
//!
//! What's *not* in the state: any absolute terminal row, any sense of
//! "where the prompt is". That information lives in the cursor itself,
//! which is the terminal's responsibility, and which we never query in
//! the hot path.
//!
//! ## Repaint protocol
//!
//! The owner of the painter (the read_line engine, in a later phase)
//! drives a repaint with this sequence:
//!
//! 1. [`Painter::walk_to_paint_top`] — relative `MoveUp` + `\r` to the
//!    top-left of whatever we drew last time. If `rows_above_cursor` is
//!    0 (first paint of a session, or after `invalidate`), no-op.
//!
//! 2. [`Painter::clear_paint_area`] — walks down `last_emit_rows` rows
//!    emitting `\x1b[2K` on each, then walks back up to the top. Per-row
//!    clear can't reach outside our paint area; that was rush#270's
//!    failure mode with `Clear(FromCursorDown)` (`\x1b[0J`), which the
//!    old painter used and which destructively erased rows we didn't
//!    own when our absolute row went stale.
//!
//! 3. Emit content via [`Painter::out`]. The caller writes the prompt,
//!    buffer, hint, menu, whatever — sequentially. The terminal scrolls
//!    naturally if total content runs past screen bottom; cursor and
//!    paint area scroll together so relative tracking stays valid.
//!
//! 4. [`Painter::finalize`] — record `rows_emitted` and
//!    `cursor_row_within_paint` for next time. The caller derives both
//!    from the same wrap-aware line counting it uses to lay out the
//!    content (we don't query the terminal for them).
//!
//! Resize / invalidation: [`Painter::invalidate`] zeroes both row
//! trackers. The next paint just emits at the current cursor; any
//! visible remnant of the old paint scrolls off naturally as the user
//! continues. Bash readline behaves the same way on SIGWINCH.

use std::io::{Result, Write};

use crossterm::{
    cursor::MoveUp,
    style::Print,
    terminal::{self, Clear, ClearType},
    QueueableCommand,
};

/// Cursor-relative painter primitive. Generic over `Write` so tests can
/// substitute a `Vec<u8>` capture buffer for a real terminal stream.
pub struct Painter<W: Write> {
    out: W,
    rows_above_cursor: u16,
    last_emit_rows: u16,
    terminal_size: (u16, u16),
}

impl<W: Write> Painter<W> {
    /// Build a painter that writes through `out`. Queries `terminal::size()`
    /// once for the initial size; falls back to 80x24 if the query fails
    /// (e.g. when `out` is a capture buffer in a non-tty test).
    pub fn new(out: W) -> Self {
        Self {
            out,
            rows_above_cursor: 0,
            last_emit_rows: 0,
            terminal_size: terminal::size().unwrap_or((80, 24)),
        }
    }

    /// Build a painter with an explicit terminal size. For tests; the size
    /// is otherwise queried via `terminal::size()` at construction and on
    /// [`Painter::handle_resize`].
    pub fn with_size(out: W, size: (u16, u16)) -> Self {
        Self {
            out,
            rows_above_cursor: 0,
            last_emit_rows: 0,
            terminal_size: size,
        }
    }

    /// Direct write access for the caller's emit step. Use this between
    /// [`Painter::clear_paint_area`] and [`Painter::finalize`].
    pub fn out(&mut self) -> &mut W {
        &mut self.out
    }

    pub fn screen_width(&self) -> u16 {
        self.terminal_size.0
    }

    pub fn screen_height(&self) -> u16 {
        self.terminal_size.1
    }

    /// Update the cached terminal size. Call from a SIGWINCH / Resize
    /// event handler.
    ///
    /// Resize also implies the previous paint geometry is unreliable
    /// (terminal reflowed everything), so we [`Painter::invalidate`].
    pub fn handle_resize(&mut self, width: u16, height: u16) {
        self.terminal_size = (width, height);
        self.invalidate();
    }

    /// Forget any tracked paint area. The next paint emits fresh at the
    /// current cursor position, walks no rows up, and clears no rows.
    /// Use this on resize, on session start (after `initialize_prompt`),
    /// or any time you can't trust the previous paint's geometry.
    pub fn invalidate(&mut self) {
        self.rows_above_cursor = 0;
        self.last_emit_rows = 0;
    }

    /// Walk the cursor relatively up to the top-left of the previous
    /// paint area. After this, the cursor is at the row our last paint
    /// started on, column 0. No-op if no previous paint is tracked.
    pub fn walk_to_paint_top(&mut self) -> Result<()> {
        if self.rows_above_cursor > 0 {
            self.out.queue(MoveUp(self.rows_above_cursor))?;
        }
        self.out.queue(Print("\r"))?;
        Ok(())
    }

    /// Erase the rows belonging to the previous paint area. Caller is
    /// expected to be at the top of that area (i.e. just after
    /// [`Painter::walk_to_paint_top`]); cursor ends back there.
    ///
    /// Per-row `\x1b[2K`. Walking down between rows uses CRLF (`\r\n`),
    /// which scrolls at screen bottom — but if the previous paint fit
    /// on screen (the assumption for this primitive), the walk stays
    /// inside the paint area and never reaches the bottom edge.
    pub fn clear_paint_area(&mut self) -> Result<()> {
        for i in 0..self.last_emit_rows {
            self.out.queue(Clear(ClearType::CurrentLine))?;
            if i + 1 < self.last_emit_rows {
                self.out.queue(Print("\r\n"))?;
            }
        }
        if self.last_emit_rows > 1 {
            self.out.queue(MoveUp(self.last_emit_rows - 1))?;
        }
        self.out.queue(Print("\r"))?;
        Ok(())
    }

    /// Convenience: walk to top and clear in one call.
    pub fn prepare_for_emit(&mut self) -> Result<()> {
        self.walk_to_paint_top()?;
        self.clear_paint_area()?;
        Ok(())
    }

    /// Record the just-completed paint's geometry.
    ///
    /// - `rows_emitted` is the total number of rows the paint occupies.
    /// - `cursor_row_within_paint` is the 0-based row offset, from the
    ///   top of the paint, of the editor's logical cursor position.
    ///
    /// Both are derived by the caller from the same wrap-aware line
    /// counting used to lay the content out. The painter never queries
    /// the terminal to discover them.
    pub fn finalize(&mut self, rows_emitted: u16, cursor_row_within_paint: u16) {
        self.last_emit_rows = rows_emitted;
        self.rows_above_cursor = cursor_row_within_paint;
    }

    /// Flush queued output to the underlying writer. Call after `finalize`
    /// (or whenever you want the bytes to actually land on screen).
    pub fn flush(&mut self) -> Result<()> {
        self.out.flush()
    }

    /// Recover the underlying writer. For tests that want to inspect the
    /// captured byte stream after a sequence of paint operations.
    pub fn into_inner(self) -> W {
        self.out
    }

    /// Borrow the captured-bytes view. For tests.
    pub fn buffer(&self) -> &W {
        &self.out
    }

    // --- accessors used by the read_line engine in later phases ---

    pub fn rows_above_cursor(&self) -> u16 {
        self.rows_above_cursor
    }

    pub fn last_emit_rows(&self) -> u16 {
        self.last_emit_rows
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Helper: run a closure against a painter wrapping a `Vec<u8>` and
    /// return the captured byte stream as a `String`.
    fn capture(size: (u16, u16), f: impl FnOnce(&mut Painter<Vec<u8>>)) -> String {
        let mut painter = Painter::with_size(Vec::new(), size);
        f(&mut painter);
        let _ = painter.flush();
        String::from_utf8(painter.into_inner()).expect("painter emitted non-utf8")
    }

    #[test]
    fn fresh_painter_has_no_tracked_paint() {
        let p = Painter::with_size(Vec::<u8>::new(), (80, 24));
        assert_eq!(p.rows_above_cursor(), 0);
        assert_eq!(p.last_emit_rows(), 0);
    }

    #[test]
    fn invalidate_zeros_state() {
        let mut p = Painter::with_size(Vec::<u8>::new(), (80, 24));
        p.finalize(5, 2);
        assert_eq!(p.last_emit_rows(), 5);
        assert_eq!(p.rows_above_cursor(), 2);
        p.invalidate();
        assert_eq!(p.last_emit_rows(), 0);
        assert_eq!(p.rows_above_cursor(), 0);
    }

    #[test]
    fn handle_resize_updates_size_and_invalidates() {
        let mut p = Painter::with_size(Vec::<u8>::new(), (80, 24));
        p.finalize(3, 1);
        p.handle_resize(120, 40);
        assert_eq!(p.screen_width(), 120);
        assert_eq!(p.screen_height(), 40);
        assert_eq!(p.last_emit_rows(), 0);
        assert_eq!(p.rows_above_cursor(), 0);
    }

    #[test]
    fn walk_to_paint_top_with_no_state_emits_only_cr() {
        let out = capture((80, 24), |p| {
            p.walk_to_paint_top().unwrap();
        });
        // No previous paint → no MoveUp queued, only the trailing \r.
        assert_eq!(out, "\r");
    }

    #[test]
    fn walk_to_paint_top_emits_relative_move_up() {
        let out = capture((80, 24), |p| {
            p.finalize(/*rows*/ 4, /*cursor_in_paint*/ 3);
            p.walk_to_paint_top().unwrap();
        });
        // 3 rows above cursor → MoveUp(3) = "\x1b[3A", then "\r".
        assert_eq!(out, "\x1b[3A\r");
    }

    #[test]
    fn walk_to_paint_top_skips_move_when_cursor_is_at_top_already() {
        // Single-row paint with cursor on that row → nothing to walk up.
        let out = capture((80, 24), |p| {
            p.finalize(1, 0);
            p.walk_to_paint_top().unwrap();
        });
        assert_eq!(out, "\r");
    }

    #[test]
    fn clear_paint_area_emits_per_row_el_with_crlf_between() {
        let out = capture((80, 24), |p| {
            p.finalize(3, 0);
            p.clear_paint_area().unwrap();
        });
        // Three rows: EL, CRLF, EL, CRLF, EL, then walk back up 2 rows + \r.
        // EL = "\x1b[2K", CUU(2) = "\x1b[2A".
        assert_eq!(out, "\x1b[2K\r\n\x1b[2K\r\n\x1b[2K\x1b[2A\r");
    }

    #[test]
    fn clear_paint_area_with_zero_rows_is_a_noop_save_for_cr() {
        let out = capture((80, 24), |p| {
            p.clear_paint_area().unwrap();
        });
        // No previous paint → no clears, no walk-back. Just the trailing \r.
        assert_eq!(out, "\r");
    }

    #[test]
    fn clear_paint_area_with_one_row_emits_single_el_no_walk_back() {
        let out = capture((80, 24), |p| {
            p.finalize(1, 0);
            p.clear_paint_area().unwrap();
        });
        // One row → one EL, no inter-row CRLF, no walk-back, just trailing \r.
        assert_eq!(out, "\x1b[2K\r");
    }

    #[test]
    fn prepare_for_emit_chains_walk_then_clear() {
        let out = capture((80, 24), |p| {
            p.finalize(3, 2);
            p.prepare_for_emit().unwrap();
        });
        // Walk up 2 rows + \r, then clear 3 rows + walk back 2 + \r.
        let expected = "\x1b[2A\r\x1b[2K\r\n\x1b[2K\r\n\x1b[2K\x1b[2A\r";
        assert_eq!(out, expected);
    }

    #[test]
    fn caller_writes_through_out_directly() {
        let out = capture((80, 24), |p| {
            p.out().write_all(b"hello").unwrap();
        });
        assert_eq!(out, "hello");
    }

    #[test]
    fn finalize_records_geometry_for_next_paint() {
        let mut p = Painter::with_size(Vec::<u8>::new(), (80, 24));
        p.finalize(7, 3);
        assert_eq!(p.last_emit_rows(), 7);
        assert_eq!(p.rows_above_cursor(), 3);
    }

    #[test]
    fn full_repaint_cycle_byte_stream_is_what_we_expect() {
        // Simulate two consecutive paints of a 3-row prompt with the editor
        // cursor on the last row (rows_above_cursor = 2). Verify the second
        // paint's prologue is exactly what we'd want a real terminal to see:
        // walk up 2 rows, clear 3 rows, walk back 2, then user content.
        let out = capture((80, 24), |p| {
            // First paint.
            p.out().write_all(b"line0\r\nline1\r\nline2").unwrap();
            p.finalize(3, 2);
            // Second paint.
            p.prepare_for_emit().unwrap();
            p.out().write_all(b"LINE0\r\nLINE1\r\nLINE2").unwrap();
            p.finalize(3, 2);
        });
        let expected = concat!(
            // First emit.
            "line0\r\nline1\r\nline2",
            // Walk up 2 (cursor was on last row, 2 rows above) + \r.
            "\x1b[2A\r",
            // Clear 3 rows: EL, CRLF, EL, CRLF, EL, walk back 2, \r.
            "\x1b[2K\r\n\x1b[2K\r\n\x1b[2K\x1b[2A\r",
            // Second emit.
            "LINE0\r\nLINE1\r\nLINE2",
        );
        assert_eq!(out, expected);
    }
}

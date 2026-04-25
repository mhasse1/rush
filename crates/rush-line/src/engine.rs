//! `LineEditor` — the read_line engine.
//!
//! Owns a [`Painter`], a [`LineBuffer`], and a [`KeyMap`]. The
//! [`LineEditor::read_line`] entry point drives one editing session:
//! enable raw mode, paint the prompt, dispatch key/resize events into
//! buffer mutations, repaint after each, and return a [`Signal`] when
//! the user submits, cancels, or sends EOF.
//!
//! ## Repaint flow
//!
//! Each repaint, in order:
//!
//! 1. [`Painter::prepare_for_emit`] — relative walk-up to the top of
//!    the previous paint, per-row clear of those rows, walk back to
//!    the top.
//! 2. Emit content sequentially: prompt text, then `before_cursor`,
//!    then `SavePosition`, then `after_cursor`, then hint. Embedded
//!    `\n` are coerced to `\r\n` for raw-mode line endings.
//! 3. `RestorePosition` to land cursor at the editor's logical
//!    position.
//! 4. [`Painter::finalize`] with `(rows_used, cursor_row)` derived
//!    from [`crate::layout::measure`] over the same emitted strings.
//!
//! No absolute terminal rows are tracked anywhere. Resize handling is
//! `Painter::handle_resize` which invalidates paint geometry; the next
//! paint emits fresh from wherever the cursor is.

use std::borrow::Cow;
use std::io::{self, BufWriter, Stderr, Write};

use crossterm::{
    cursor::{self, SetCursorStyle},
    event::{self, Event, KeyEventKind},
    style::Print,
    terminal::{self, Clear, ClearType},
    QueueableCommand,
};

use crate::buffer::LineBuffer;
use crate::history::History;
use crate::keymap::{Action, EmacsKeyMap, KeyMap};
use crate::layout::measure;
use crate::painter::Painter;

/// Whatever produces the prompt string for the current input session.
/// Implementors return the entire prompt (including any embedded `\n`
/// for multi-line prompts) on each call. The engine doesn't cache —
/// `read_line` is allowed to render the prompt once at the start of
/// the session and once per repaint. Implementations should be cheap
/// to call repeatedly, or do their own caching.
pub trait Prompt {
    fn render(&self) -> String;
}

impl<F: Fn() -> String> Prompt for F {
    fn render(&self) -> String {
        (self)()
    }
}

/// Why `read_line` returned.
#[derive(Debug)]
pub enum Signal {
    /// The user pressed Enter; payload is the submitted line.
    Success(String),
    /// The user pressed Ctrl-C.
    CtrlC,
    /// The user pressed Ctrl-D on an empty buffer.
    CtrlD,
}

pub struct LineEditor {
    painter: Painter<BufWriter<Stderr>>,
    buffer: LineBuffer,
    keymap: Box<dyn KeyMap>,
    history: Option<Box<dyn History>>,
    /// Snapshot of the in-progress edit taken on first `HistoryPrev`,
    /// so a subsequent `HistoryNext` past the newest entry can put
    /// the user's typing back. None when not currently navigating
    /// history.
    edit_stash: Option<String>,
}

impl LineEditor {
    pub fn new() -> Self {
        let out = BufWriter::new(io::stderr());
        Self {
            painter: Painter::new(out),
            buffer: LineBuffer::new(),
            keymap: Box::new(EmacsKeyMap),
            history: None,
            edit_stash: None,
        }
    }

    pub fn with_keymap<K: KeyMap + 'static>(mut self, keymap: K) -> Self {
        self.keymap = Box::new(keymap);
        self
    }

    /// Attach a history backend. Up/Down arrows will recall entries;
    /// submitted lines are added to it. Pass any `History`, typically
    /// [`crate::FileBackedHistory`].
    pub fn with_history<H: History + 'static>(mut self, history: H) -> Self {
        self.history = Some(Box::new(history));
        self
    }

    /// Borrow the history backend for direct manipulation (e.g.
    /// `sync()` from the host between submissions). `None` if no
    /// history was attached.
    pub fn history_mut(&mut self) -> Option<&mut (dyn History + 'static)> {
        self.history.as_deref_mut()
    }

    /// Read one line of input. Enables raw mode for the duration and
    /// restores it on exit (success or error). On Enter, returns
    /// [`Signal::Success`] with the buffer contents and clears the
    /// buffer for the next call.
    pub fn read_line(&mut self, prompt: &dyn Prompt) -> io::Result<Signal> {
        terminal::enable_raw_mode()?;
        let result = self.read_line_inner(prompt);
        let _ = terminal::disable_raw_mode();
        result
    }

    fn read_line_inner(&mut self, prompt: &dyn Prompt) -> io::Result<Signal> {
        // Each call starts a fresh editing session. The cursor is
        // wherever the host left it (after a previous command's stdout
        // ran, etc.); we don't query it. The painter starts with no
        // paint area tracked, so the first repaint won't try to walk
        // up into anything that isn't ours.
        self.painter.invalidate();
        self.buffer.clear();
        self.edit_stash = None;
        if let Some(history) = self.history.as_deref_mut() {
            history.reset_cursor();
        }

        // Refresh terminal size in case it changed between read_lines.
        if let Ok((w, h)) = terminal::size() {
            self.painter.handle_resize(w, h);
            // handle_resize calls invalidate, so we're still in a
            // fresh-paint state.
        }

        // Initial paint.
        self.repaint(prompt)?;

        loop {
            let evt = event::read()?;
            match evt {
                Event::Key(key_event) => {
                    // Crossterm on Windows emits Press *and* Release;
                    // we only act on Press to avoid double-processing.
                    if key_event.kind != KeyEventKind::Press
                        && key_event.kind != KeyEventKind::Repeat
                    {
                        continue;
                    }
                    let actions = self.keymap.translate(key_event);
                    if actions.is_empty() {
                        continue;
                    }
                    let mut needs_repaint = false;
                    for action in actions {
                        match self.apply_action(action)? {
                            ActionResult::Continue => needs_repaint = true,
                            ActionResult::Submit => {
                                self.move_below_paint()?;
                                let line = std::mem::take(&mut self.buffer)
                                    .text()
                                    .to_string();
                                if let Some(history) = self.history.as_deref_mut() {
                                    history.add(&line);
                                }
                                return Ok(Signal::Success(line));
                            }
                            ActionResult::Cancel => {
                                self.move_below_paint()?;
                                return Ok(Signal::CtrlC);
                            }
                            ActionResult::EndOfFile => {
                                self.move_below_paint()?;
                                return Ok(Signal::CtrlD);
                            }
                        }
                    }
                    if needs_repaint {
                        self.repaint(prompt)?;
                    }
                }
                Event::Resize(w, h) => {
                    self.painter.handle_resize(w, h);
                    self.repaint(prompt)?;
                }
                _ => {} // mouse / paste / focus events: ignored for now
            }
        }
    }

    /// Apply an `Action` to the buffer (or signal a control flow
    /// change). Mostly pure buffer-state transformation; mode-signal
    /// actions (`EnterInsertMode`/`EnterNormalMode`) and `Clear` do
    /// touch the terminal directly.
    fn apply_action(&mut self, action: Action) -> io::Result<ActionResult> {
        // Any text-modifying action while the user is on a recalled
        // history entry detaches them from history navigation: the
        // current buffer is now their edit, not the recalled entry.
        // The history cursor stays where it is so further Up/Down
        // still works, but the "stashed live edit" is replaced by
        // the current divergent buffer.
        let modifies_text = matches!(
            action,
            Action::InsertChar(_)
                | Action::DeleteLeft
                | Action::DeleteRight
                | Action::DeleteWordLeft
                | Action::DeleteWordRight
                | Action::KillToEnd
                | Action::KillToStart
                | Action::DeleteLine
        );
        if modifies_text {
            self.edit_stash = None;
        }

        let result = match action {
            Action::InsertChar(c) => {
                self.buffer.insert_char(c);
                ActionResult::Continue
            }
            Action::DeleteLeft => {
                self.buffer.delete_left();
                ActionResult::Continue
            }
            Action::DeleteRight => {
                self.buffer.delete_right();
                ActionResult::Continue
            }
            Action::DeleteWordLeft => {
                self.buffer.delete_word_left();
                ActionResult::Continue
            }
            Action::DeleteWordRight => {
                self.buffer.delete_word_right();
                ActionResult::Continue
            }
            Action::KillToEnd => {
                self.buffer.kill_to_end();
                ActionResult::Continue
            }
            Action::KillToStart => {
                self.buffer.kill_to_start();
                ActionResult::Continue
            }
            Action::DeleteLine => {
                self.buffer.clear();
                ActionResult::Continue
            }
            Action::MoveLeft => {
                self.buffer.move_left();
                ActionResult::Continue
            }
            Action::MoveRight => {
                self.buffer.move_right();
                ActionResult::Continue
            }
            Action::MoveWordLeft => {
                self.buffer.move_word_left();
                ActionResult::Continue
            }
            Action::MoveWordRight => {
                self.buffer.move_word_right();
                ActionResult::Continue
            }
            Action::MoveHome => {
                self.buffer.move_home();
                ActionResult::Continue
            }
            Action::MoveEnd => {
                self.buffer.move_end();
                ActionResult::Continue
            }
            Action::HistoryPrev => {
                if let Some(history) = self.history.as_deref_mut() {
                    if history.at_present() {
                        self.edit_stash = Some(self.buffer.text().to_string());
                    }
                    if let Some(entry) = history.backward() {
                        self.buffer.set_text(entry);
                    }
                }
                ActionResult::Continue
            }
            Action::HistoryNext => {
                if let Some(history) = self.history.as_deref_mut() {
                    if let Some(entry) = history.forward() {
                        self.buffer.set_text(entry);
                    } else if let Some(stash) = self.edit_stash.take() {
                        self.buffer.set_text(&stash);
                    }
                }
                ActionResult::Continue
            }
            Action::Submit => ActionResult::Submit,
            Action::Cancel => ActionResult::Cancel,
            Action::EndOfInput => {
                if self.buffer.is_empty() {
                    ActionResult::EndOfFile
                } else {
                    self.buffer.delete_right();
                    ActionResult::Continue
                }
            }
            Action::Clear => {
                // Ctrl-L: clear screen, keep buffer state, redraw at top.
                self.painter
                    .out()
                    .queue(Clear(ClearType::All))?
                    .queue(cursor::MoveTo(0, 0))?
                    .flush()?;
                self.painter.invalidate();
                ActionResult::Continue
            }
            Action::EnterInsertMode => {
                self.set_cursor_style(SetCursorStyle::SteadyBar)?;
                ActionResult::Continue
            }
            Action::EnterNormalMode => {
                self.set_cursor_style(SetCursorStyle::SteadyBlock)?;
                ActionResult::Continue
            }
        };
        Ok(result)
    }

    /// Emit a cursor-style escape and flush. Used by vi mode to
    /// distinguish Insert (bar) from Normal (block).
    fn set_cursor_style(&mut self, style: SetCursorStyle) -> io::Result<()> {
        self.painter.out().queue(style)?;
        self.painter.flush()?;
        Ok(())
    }

    fn repaint(&mut self, prompt: &dyn Prompt) -> io::Result<()> {
        let prompt_text = prompt.render();
        let before = self.buffer.before_cursor();
        let after = self.buffer.after_cursor();
        let width = self.painter.screen_width();

        // Layout: cursor lands at end of (prompt + before); total
        // emit covers (prompt + before + after).
        let pre_cursor = format!("{prompt_text}{before}");
        let pre_m = measure(&pre_cursor, width);
        let full_text = format!("{prompt_text}{before}{after}");
        let full_m = measure(&full_text, width);

        // Emit.
        self.painter.prepare_for_emit()?;
        let prompt_crlf = coerce_crlf(&prompt_text);
        let before_crlf = coerce_crlf(before);
        let after_crlf = coerce_crlf(after);
        {
            let out = self.painter.out();
            out.queue(Print(prompt_crlf.as_ref()))?;
            out.queue(Print(before_crlf.as_ref()))?;
            out.queue(cursor::SavePosition)?;
            out.queue(Print(after_crlf.as_ref()))?;
            out.queue(cursor::RestorePosition)?;
        }
        self.painter.finalize(full_m.rows_used, pre_m.cursor_row);
        self.painter.flush()?;
        Ok(())
    }

    /// Walk the cursor below the paint area and emit a fresh CRLF.
    /// Called on Submit / Cancel / EOF so that whatever the host does
    /// next (run a command, exit) starts on a clean row.
    fn move_below_paint(&mut self) -> io::Result<()> {
        let above = self.painter.rows_above_cursor();
        let total = self.painter.last_emit_rows();
        let rows_below_cursor = total.saturating_sub(above.saturating_add(1));
        {
            let out = self.painter.out();
            if rows_below_cursor > 0 {
                out.queue(cursor::MoveDown(rows_below_cursor))?;
            }
            out.queue(Print("\r\n"))?;
            out.flush()?;
        }
        // The host now owns the terminal until the next `read_line`.
        self.painter.invalidate();
        Ok(())
    }
}

impl Default for LineEditor {
    fn default() -> Self {
        Self::new()
    }
}

enum ActionResult {
    Continue,
    Submit,
    Cancel,
    EndOfFile,
}

/// Convert solitary `\n` into `\r\n` so output lands correctly in raw
/// mode (where the kernel's `ONLCR` is disabled). Existing `\r\n`
/// pairs are left as-is.
fn coerce_crlf(s: &str) -> Cow<'_, str> {
    if !s.contains('\n') {
        return Cow::Borrowed(s);
    }
    let mut out = String::with_capacity(s.len() + 8);
    let mut prev = '\0';
    for c in s.chars() {
        if c == '\n' && prev != '\r' {
            out.push('\r');
        }
        out.push(c);
        prev = c;
    }
    Cow::Owned(out)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn coerce_crlf_passes_through_when_no_newline() {
        assert!(matches!(coerce_crlf("hello"), Cow::Borrowed(_)));
    }

    #[test]
    fn coerce_crlf_inserts_cr_before_solitary_lf() {
        assert_eq!(coerce_crlf("a\nb"), "a\r\nb");
    }

    #[test]
    fn coerce_crlf_leaves_existing_crlf_alone() {
        assert_eq!(coerce_crlf("a\r\nb"), "a\r\nb");
    }

    #[test]
    fn coerce_crlf_handles_leading_newline() {
        assert_eq!(coerce_crlf("\nabc"), "\r\nabc");
    }

    #[test]
    fn coerce_crlf_handles_trailing_newline() {
        assert_eq!(coerce_crlf("abc\n"), "abc\r\n");
    }

    #[test]
    fn coerce_crlf_handles_multiple_newlines() {
        assert_eq!(coerce_crlf("a\nb\nc\n"), "a\r\nb\r\nc\r\n");
    }
}

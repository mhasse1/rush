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
    event::{self, DisableBracketedPaste, EnableBracketedPaste, Event, KeyEventKind},
    execute,
    style::{Color, Print, ResetColor, SetForegroundColor},
    terminal::{self, Clear, ClearType},
    QueueableCommand,
};

use crate::buffer::LineBuffer;
use crate::completion::{longest_common_prefix, Completer, Span, Suggestion};
use crate::hint::longest_history_match;
use crate::history::History;
use crate::keymap::{Action, EmacsKeyMap, KeyMap};
use crate::layout::measure;
use crate::painter::Painter;
use crate::validator::{AlwaysComplete, ValidationResult, Validator};

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
    completer: Option<Box<dyn Completer>>,
    validator: Box<dyn Validator>,
    /// Whether to render an autosuggestion hint after the cursor.
    /// Defaults to `true` if a history is attached. The host can
    /// turn it off via [`LineEditor::with_hint`].
    hint_enabled: bool,
    /// Last computed hint suffix, populated each repaint. Used by
    /// MoveRight/MoveEnd at end-of-buffer to "accept" the hint by
    /// appending it to the buffer.
    last_hint: String,
    /// Snapshot of the in-progress edit taken on first `HistoryPrev`,
    /// so a subsequent `HistoryNext` past the newest entry can put
    /// the user's typing back. None when not currently navigating
    /// history.
    edit_stash: Option<String>,
    /// Active completion cycle, if the user is pressing Tab repeatedly
    /// after an ambiguous match. Cleared by any non-Complete action.
    completion_cycle: Option<CompletionCycle>,
}

/// State tracked across consecutive Tab presses. After we extend the
/// buffer to the longest common prefix and find the user is still on
/// an ambiguous match, the next Tab uses this to step through the
/// suggestions one at a time.
struct CompletionCycle {
    /// The full list of suggestions returned by the completer at the
    /// moment the cycle started.
    suggestions: Vec<Suggestion>,
    /// Index into `suggestions` of the *previously applied* suggestion.
    /// `None` before the first cycle step.
    index: Option<usize>,
    /// The buffer text before any cycle replacement was applied. We
    /// restore this minus span before each step's replacement so we
    /// don't accumulate replacements.
    base_text: String,
    /// The cursor position in `base_text` at cycle start.
    base_cursor: usize,
    /// The replacement span common to all suggestions.
    span: Span,
}

impl LineEditor {
    pub fn new() -> Self {
        let out = BufWriter::new(io::stderr());
        Self {
            painter: Painter::new(out),
            buffer: LineBuffer::new(),
            keymap: Box::new(EmacsKeyMap),
            history: None,
            completer: None,
            validator: Box::new(AlwaysComplete),
            hint_enabled: true,
            last_hint: String::new(),
            edit_stash: None,
            completion_cycle: None,
        }
    }

    /// Enable or disable the autosuggestion hint. Default is on; the
    /// hint requires a history backend and falls back to no-op without
    /// one.
    pub fn with_hint(mut self, enabled: bool) -> Self {
        self.hint_enabled = enabled;
        self
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

    /// Attach a completer. Tab will invoke it.
    pub fn with_completer<C: Completer + 'static>(mut self, completer: C) -> Self {
        self.completer = Some(Box::new(completer));
        self
    }

    /// Attach a validator. On Enter, if it reports `Incomplete`, the
    /// engine inserts a `\n` instead of submitting (multi-line
    /// continuation for unclosed quotes / braces / trailing `\`).
    /// Default is [`AlwaysComplete`] — every Enter submits.
    pub fn with_validator<V: Validator + 'static>(mut self, validator: V) -> Self {
        self.validator = Box::new(validator);
        self
    }

    /// Read one line of input. Enables raw mode for the duration and
    /// restores it on exit (success or error). On Enter, returns
    /// [`Signal::Success`] with the buffer contents and clears the
    /// buffer for the next call.
    pub fn read_line(&mut self, prompt: &dyn Prompt) -> io::Result<Signal> {
        terminal::enable_raw_mode()?;
        // Bracketed paste tells the terminal to wrap pasted content
        // in `\x1b[200~...\x1b[201~`, so crossterm can deliver it as
        // `Event::Paste` instead of streaming each char (and each
        // embedded \n triggering Submit). Best-effort: not every
        // emulator supports it; ignore the error.
        let _ = execute!(io::stderr(), EnableBracketedPaste);
        let result = self.read_line_inner(prompt);
        let _ = execute!(io::stderr(), DisableBracketedPaste);
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
        self.completion_cycle = None;
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
                                // Ask the validator: is this buffer
                                // ready, or does it want a continuation?
                                if matches!(
                                    self.validator.validate(self.buffer.text()),
                                    ValidationResult::Incomplete
                                ) {
                                    self.buffer.insert_char('\n');
                                    needs_repaint = true;
                                    continue;
                                }
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
                Event::Paste(text) => {
                    // Insert the full pasted string at cursor position
                    // as one unit. Embedded \n stay as `\n` in the
                    // buffer (the validator decides whether the result
                    // is submittable on Enter; Paste itself never
                    // submits).
                    self.buffer.insert_str(&text);
                    self.edit_stash = None;
                    self.completion_cycle = None;
                    if let Some(history) = self.history.as_deref_mut() {
                        history.reset_cursor();
                    }
                    self.repaint(prompt)?;
                }
                _ => {} // mouse / focus events: ignored for now
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

        // Any action other than Complete itself ends an in-flight
        // Tab cycle. If the user pressed Tab → Tab → 'x', we don't
        // want a future Tab to resume the cycle from where it was.
        if !matches!(action, Action::Complete) {
            self.completion_cycle = None;
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
                if self.cursor_at_end() && !self.last_hint.is_empty() {
                    // Accept the autosuggestion hint: append it to
                    // the buffer. Cursor lands at the new end.
                    self.buffer.insert_str(&self.last_hint.clone());
                    self.last_hint.clear();
                } else {
                    self.buffer.move_right();
                }
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
                if self.cursor_at_end() && !self.last_hint.is_empty() {
                    self.buffer.insert_str(&self.last_hint.clone());
                    self.last_hint.clear();
                } else {
                    self.buffer.move_end();
                }
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
            Action::Complete => {
                self.handle_complete();
                ActionResult::Continue
            }
        };
        Ok(result)
    }

    /// Apply tab completion using the bash-style policy described in
    /// [`crate::completion`]. No-op if no completer is registered.
    fn handle_complete(&mut self) {
        // If there's already an active cycle, advance to the next
        // suggestion rather than re-querying the completer.
        if let Some(cycle) = self.completion_cycle.as_mut() {
            let n = cycle.suggestions.len();
            if n == 0 {
                return;
            }
            let next = match cycle.index {
                None => 0,
                Some(i) => (i + 1) % n,
            };
            cycle.index = Some(next);
            apply_replacement(
                &mut self.buffer,
                &cycle.base_text,
                cycle.span,
                &cycle.suggestions[next],
            );
            return;
        }

        let Some(completer) = self.completer.as_deref_mut() else {
            return;
        };
        let line = self.buffer.text().to_string();
        let pos = self.buffer.cursor();
        let suggestions = completer.complete(&line, pos);
        if suggestions.is_empty() {
            return;
        }

        // All suggestions in a single completer response should share
        // a span (rush's RushCompleter does this). Pick the first
        // suggestion's span as authoritative.
        let span = suggestions[0].span;
        // Sanity-bound the span to the buffer.
        let span = Span {
            start: span.start.min(line.len()),
            end: span.end.min(line.len()),
        };

        let already_typed = &line[span.start..span.end];

        if suggestions.len() == 1 {
            // Single match: apply it (with optional trailing space).
            apply_replacement(&mut self.buffer, &line, span, &suggestions[0]);
            return;
        }

        // Multiple matches: extend to longest common prefix if it's
        // longer than what's already typed.
        let lcp =
            longest_common_prefix(suggestions.iter().map(|s| s.value.as_str()));
        if lcp.len() > already_typed.len() && lcp.starts_with(already_typed) {
            let extended = Suggestion {
                value: lcp,
                span,
                append_whitespace: false,
            };
            apply_replacement(&mut self.buffer, &line, span, &extended);
            return;
        }

        // Already at the common prefix (or no useful extension).
        // Start a Tab-cycle: subsequent Tabs step through suggestions.
        self.completion_cycle = Some(CompletionCycle {
            suggestions,
            index: None,
            base_text: line,
            base_cursor: pos,
            span,
        });
        // First Tab when cycle starts: don't replace yet. The user
        // hits Tab again to take the first suggestion. (This matches
        // the bash UX where first Tab lists; we don't have a list
        // yet, so first Tab is a "ready to cycle" state.)
        // To make this less surprising, immediately apply the first
        // suggestion on this same Tab — feels closer to zsh's "always
        // pick something."
        let cycle = self.completion_cycle.as_mut().unwrap();
        cycle.index = Some(0);
        apply_replacement(
            &mut self.buffer,
            &cycle.base_text,
            cycle.span,
            &cycle.suggestions[0],
        );
        // base_cursor isn't used right now but kept in the struct for
        // future "restore on Esc-during-cycle" UX.
        let _ = cycle.base_cursor;
    }

    /// Emit a cursor-style escape and flush. Used by vi mode to
    /// distinguish Insert (bar) from Normal (block).
    fn set_cursor_style(&mut self, style: SetCursorStyle) -> io::Result<()> {
        self.painter.out().queue(style)?;
        self.painter.flush()?;
        Ok(())
    }

    fn cursor_at_end(&self) -> bool {
        self.buffer.cursor() == self.buffer.len()
    }

    fn repaint(&mut self, prompt: &dyn Prompt) -> io::Result<()> {
        let prompt_text = prompt.render();
        let before = self.buffer.before_cursor().to_string();
        let after = self.buffer.after_cursor().to_string();
        let width = self.painter.screen_width();

        // Compute the autosuggestion hint. Only meaningful when the
        // cursor is at the end of the buffer (otherwise the hint
        // would visually float in the middle of edited text). And
        // only when the user is editing fresh, not navigating
        // history — avoid hinting "ls -la /tmp" when they just
        // recalled "ls -la /tmp".
        let hint = if self.hint_enabled
            && after.is_empty()
            && self
                .history
                .as_deref()
                .map(|h| h.at_present())
                .unwrap_or(true)
        {
            self.history
                .as_deref()
                .map(|h| {
                    longest_history_match(&before, h.entries().iter().map(String::as_str))
                })
                .unwrap_or_default()
        } else {
            String::new()
        };
        self.last_hint = hint.clone();

        // Layout: cursor lands at end of (prompt + before); total
        // emit covers (prompt + before + after + hint).
        let pre_cursor = format!("{prompt_text}{before}");
        let pre_m = measure(&pre_cursor, width);
        let full_text = format!("{prompt_text}{before}{after}{hint}");
        let full_m = measure(&full_text, width);

        // Emit.
        self.painter.prepare_for_emit()?;
        let prompt_crlf = coerce_crlf(&prompt_text);
        let before_crlf = coerce_crlf(&before);
        let after_crlf = coerce_crlf(&after);
        let hint_crlf = coerce_crlf(&hint);
        {
            let out = self.painter.out();
            out.queue(Print(prompt_crlf.as_ref()))?;
            out.queue(Print(before_crlf.as_ref()))?;
            out.queue(cursor::SavePosition)?;
            out.queue(Print(after_crlf.as_ref()))?;
            if !hint.is_empty() {
                out.queue(SetForegroundColor(Color::DarkGrey))?;
                out.queue(Print(hint_crlf.as_ref()))?;
                out.queue(ResetColor)?;
            }
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

/// Replace the bytes in `span` of `base` with the suggestion's value
/// (plus a trailing space if requested), commit the result to
/// `buffer`, and put the cursor immediately after the replacement.
fn apply_replacement(buffer: &mut LineBuffer, base: &str, span: Span, suggestion: &Suggestion) {
    let mut new_text = String::with_capacity(base.len() + suggestion.value.len() + 1);
    new_text.push_str(&base[..span.start]);
    new_text.push_str(&suggestion.value);
    let cursor_after = new_text.len();
    if suggestion.append_whitespace {
        new_text.push(' ');
    }
    if span.end < base.len() {
        new_text.push_str(&base[span.end..]);
    }
    buffer.set_text(&new_text);
    // Place cursor immediately after the inserted value (or after the
    // trailing space if we added one).
    let cursor_pos = if suggestion.append_whitespace {
        cursor_after + 1
    } else {
        cursor_after
    };
    // set_text put cursor at end-of-buffer; walk it back.
    while buffer.cursor() > cursor_pos {
        buffer.move_left();
    }
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

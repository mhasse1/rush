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
    event::{
        DisableBracketedPaste, EnableBracketedPaste, Event, KeyCode, KeyEvent,
        KeyEventKind, KeyModifiers,
    },
    execute,
    style::{Color, Print, ResetColor, SetForegroundColor},
    terminal::{self, Clear, ClearType},
    QueueableCommand,
};
// `event::read()` only runs on Windows now; Unix uses our `unix_input`.
#[cfg(windows)]
use crossterm::event;

use crate::buffer::LineBuffer;
use crate::completion::{longest_common_prefix, Completer, Span, Suggestion};
use crate::highlighter::Highlighter;
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
    /// The keymap fired [`crate::Action::HostCommand`]. The host
    /// runs whatever external process the name refers to, then can
    /// re-enter `read_line` (optionally pre-loading the buffer with
    /// [`LineEditor::set_initial_text`]).
    HostCommand(String),
}

pub struct LineEditor {
    painter: Painter<BufWriter<Stderr>>,
    buffer: LineBuffer,
    keymap: Box<dyn KeyMap>,
    history: Option<Box<dyn History>>,
    completer: Option<Box<dyn Completer>>,
    validator: Box<dyn Validator>,
    highlighter: Option<Box<dyn Highlighter>>,
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
    /// Active reverse-incremental history search (Ctrl-R). When
    /// `Some`, the regular keymap is bypassed; key events go to the
    /// search handler.
    search: Option<SearchState>,
    /// Most-recently-killed text. Yank inserts this at the cursor.
    /// Single slot, not a ring — bash readline has a fuller kill
    /// ring with M-y to cycle, but for shell daily use a single
    /// slot covers 95% of cases; expand later if asked.
    kill_ring: Option<String>,
    /// If `Some`, the next `read_line` call pre-loads the buffer
    /// with this text instead of starting empty. Set by the host via
    /// [`LineEditor::set_initial_text`] after a `Signal::HostCommand`
    /// detour (e.g. fzf history search returns a selected line).
    pending_initial_text: Option<String>,
    /// Most recent committed edit-command sequence. Vi `.` replays
    /// this. Empty when nothing's been recorded yet.
    last_edit: Vec<Action>,
    /// Edit-recording-in-progress. `Some(vec)` while a multi-action
    /// edit (insert mode session, operator+motion+chars+Esc, etc.) is
    /// being captured; committed to `last_edit` when it completes.
    recording: Option<Vec<Action>>,
    /// Mirrors the keymap's vi-Insert mode flag for the engine, set
    /// by `EnterInsertMode` actions. Used to decide when to commit
    /// a recording (we commit when control returns to Normal — i.e.
    /// `EnterNormalMode` arrives, or a self-contained Normal-mode
    /// edit like `dd` finishes a single action vec).
    in_insert_mode: bool,
}

/// State for an in-progress reverse-incremental history search.
struct SearchState {
    /// What the user has typed so far.
    pattern: String,
    /// Currently matched history-entries index (newest matched-back).
    /// `None` means no match (the prompt shows `failing-i-search`).
    match_index: Option<usize>,
    /// Buffer state to restore on cancel.
    original_buffer: String,
    original_cursor: usize,
}

/// State tracked while a completion menu is open. Replaces the
/// pre-menu in-place cycle: after the user has Tab'd into ambiguity,
/// the menu shows all suggestions below the buffer, highlighting the
/// current selection. Tab/Shift-Tab navigate, Enter commits, Esc
/// cancels (restores original buffer).
struct CompletionCycle {
    /// All suggestions from the completer at the moment the menu opened.
    suggestions: Vec<Suggestion>,
    /// Currently highlighted suggestion (always `Some` while a menu
    /// is open — the field stays `Option` only because we share the
    /// type with the no-menu single-match shortcut path which never
    /// constructs this struct).
    index: usize,
    /// Buffer text before the menu opened. Esc restores this verbatim.
    base_text: String,
    /// Cursor byte offset in `base_text` at menu-open time. Used by
    /// Esc to restore. Currently unused; kept in the struct so the
    /// engine has the full original state on hand.
    #[allow(dead_code)]
    base_cursor: usize,
    /// Replacement span common to all suggestions.
    span: Span,
}

impl LineEditor {
    pub fn new() -> Self {
        let out = BufWriter::new(io::stderr());
        Self {
            painter: Painter::new(out),
            buffer: LineBuffer::new(),
            keymap: Box::new(EmacsKeyMap::new()),
            history: None,
            completer: None,
            validator: Box::new(AlwaysComplete),
            highlighter: None,
            hint_enabled: true,
            last_hint: String::new(),
            edit_stash: None,
            completion_cycle: None,
            search: None,
            kill_ring: None,
            pending_initial_text: None,
            last_edit: Vec::new(),
            recording: None,
            in_insert_mode: false,
        }
    }

    /// Pre-load the buffer for the next `read_line` call. Used by
    /// hosts after a `Signal::HostCommand` detour to seed the editor
    /// with text from an external source (e.g. fzf-selected history).
    pub fn set_initial_text(&mut self, text: &str) {
        self.pending_initial_text = Some(text.to_string());
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

    /// Attach a syntax highlighter. The buffer (split at the cursor)
    /// is piped through it on each repaint, with the result emitted
    /// verbatim — including any ANSI escapes for color/style.
    pub fn with_highlighter<H: Highlighter + 'static>(mut self, highlighter: H) -> Self {
        self.highlighter = Some(Box::new(highlighter));
        self
    }

    /// Read one line of input. Enables raw mode for the duration and
    /// restores it on exit (success or error). On Enter, returns
    /// [`Signal::Success`] with the buffer contents and clears the
    /// buffer for the next call.
    pub fn read_line(&mut self, prompt: &dyn Prompt) -> io::Result<Signal> {
        // Unix: take raw mode + signal handlers via RawTty (RAII), and
        // run the read loop against our own decoder. This avoids
        // crossterm's mio-based event reader, which busy-loops on a
        // destroyed pty's `EPOLLHUP` (#282).
        //
        // Windows: keep crossterm. The orphan-pty failure mode is Unix
        // -specific (no EPOLLHUP, ConPTY closes the handle cleanly),
        // and the decoder's escape-sequence assumptions don't match
        // Windows' VK_*-encoded events.
        crate::trace!("read_line", "enter");
        #[cfg(unix)]
        let mut input = crate::unix_input::UnixInput::enter()?;
        #[cfg(windows)]
        terminal::enable_raw_mode()?;

        // Bracketed paste tells the terminal to wrap pasted content
        // in `\x1b[200~...\x1b[201~` so we can deliver it as
        // `Event::Paste` instead of streaming each char (and each
        // embedded \n triggering Submit). Best-effort: not every
        // emulator supports it; ignore the error.
        let _ = execute!(io::stderr(), EnableBracketedPaste);
        let result = self.read_line_inner(
            prompt,
            #[cfg(unix)]
            &mut input,
        );
        let _ = execute!(io::stderr(), DisableBracketedPaste);
        #[cfg(windows)]
        let _ = terminal::disable_raw_mode();
        // Unix: `input` drops here, restoring termios + handlers.
        crate::trace!("read_line", "exit ok={}", result.is_ok());
        result
    }

    fn read_line_inner(
        &mut self,
        prompt: &dyn Prompt,
        #[cfg(unix)] input: &mut crate::unix_input::UnixInput,
    ) -> io::Result<Signal> {
        // Each call starts a fresh editing session. The cursor is
        // wherever the host left it (after a previous command's stdout
        // ran, etc.); we don't query it. The painter starts with no
        // paint area tracked, so the first repaint won't try to walk
        // up into anything that isn't ours.
        self.painter.invalidate();
        self.buffer.clear();
        if let Some(seed) = self.pending_initial_text.take() {
            self.buffer.set_text(&seed);
            // set_text places cursor at end; that's the desired
            // landing spot for fzf-recalled lines.
        }
        self.edit_stash = None;
        self.completion_cycle = None;
        self.search = None;
        if let Some(history) = self.history.as_deref_mut() {
            history.reset_cursor();
        }

        // Refresh terminal size in case it changed between read_lines.
        if let Ok((w, h)) = terminal::size() {
            self.painter.handle_resize(w, h);
            // handle_resize calls invalidate, so we're still in a
            // fresh-paint state.
        }

        // Reset keymap to its session-start state. Vi keymaps return
        // [EnterInsertMode] so the cursor shape matches the mode the
        // user expects on a fresh prompt.
        let init_actions = self.keymap.reset();
        for action in init_actions {
            let _ = self.apply_action(action)?;
        }

        // Initial paint.
        self.repaint(prompt)?;

        loop {
            #[cfg(unix)]
            let evt = input.next_event()?;
            #[cfg(windows)]
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
                    // If we're in a Ctrl-R search, route the event
                    // through the search handler instead of the
                    // regular keymap.
                    if self.search.is_some() {
                        match self.handle_search_key(key_event) {
                            SearchResult::Continue => {
                                self.repaint(prompt)?;
                                continue;
                            }
                            SearchResult::Accept => {
                                // Exit search; the buffer is already
                                // set to the matched entry. Fall
                                // through to the normal keymap with
                                // the same key event so e.g. Right
                                // accepts and moves cursor right.
                                self.search = None;
                            }
                            SearchResult::Cancel => {
                                self.search = None;
                                self.repaint(prompt)?;
                                continue;
                            }
                            SearchResult::Submit => {
                                self.search = None;
                                if matches!(
                                    self.validator.validate(self.buffer.text()),
                                    ValidationResult::Incomplete
                                ) {
                                    self.buffer.insert_char('\n');
                                    self.repaint(prompt)?;
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
                        }
                    }
                    let mut actions = self.keymap.translate(key_event);
                    if actions.is_empty() {
                        continue;
                    }
                    // Vi `.`: substitute the recorded last-edit
                    // sequence in place of Action::Repeat. We don't
                    // expand recursively (the recording filter never
                    // keeps Repeat itself, so this is one-shot).
                    if actions.iter().any(|a| matches!(a, Action::Repeat)) {
                        actions = self.last_edit.clone();
                        if actions.is_empty() {
                            continue; // nothing to repeat
                        }
                    }
                    // Recording: start when the vec contains any
                    // text-mod or mode-entry action. Append every
                    // subsequent vec while we're in Insert. Commit
                    // when we drop back to Normal.
                    let triggers_recording = self.recording.is_none()
                        && actions.iter().any(starts_edit_recording);
                    if triggers_recording {
                        self.recording = Some(Vec::new());
                    }
                    if let Some(rec) = self.recording.as_mut() {
                        rec.extend(actions.iter().cloned());
                    }
                    let mut needs_repaint = false;
                    for action in actions {
                        // HostCommand short-circuits — surface to the
                        // host immediately. We move the cursor below
                        // our paint area first so whatever the host
                        // runs (fzf, etc.) starts on a clean row.
                        if let Action::HostCommand(name) = action {
                            self.move_below_paint()?;
                            return Ok(Signal::HostCommand(name));
                        }
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
                    // After the action vec finishes, commit any
                    // in-progress recording if we're back in Normal
                    // (or were never in Insert — single-action edits
                    // like `dd` or `x`).
                    if !self.in_insert_mode {
                        if let Some(rec) = self.recording.take() {
                            if !rec.is_empty() {
                                self.last_edit = rec;
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

        // Any action other than menu navigation ends an in-flight
        // completion menu. Buffer keeps the current preview (the user
        // implicitly accepted by typing); Esc-cancel is handled in
        // the EnterNormalMode arm below.
        if !matches!(action, Action::Complete | Action::CompletePrev) {
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
                if let Some(killed) = self.buffer.delete_word_left() {
                    self.kill_ring = Some(killed);
                }
                ActionResult::Continue
            }
            Action::DeleteWordRight => {
                if let Some(killed) = self.buffer.delete_word_right() {
                    self.kill_ring = Some(killed);
                }
                ActionResult::Continue
            }
            Action::KillToEnd => {
                if let Some(killed) = self.buffer.kill_to_end() {
                    self.kill_ring = Some(killed);
                }
                ActionResult::Continue
            }
            Action::KillToStart => {
                if let Some(killed) = self.buffer.kill_to_start() {
                    self.kill_ring = Some(killed);
                }
                ActionResult::Continue
            }
            Action::DeleteLine => {
                if let Some(killed) = self.buffer.take_all() {
                    self.kill_ring = Some(killed);
                }
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
                self.in_insert_mode = true;
                self.set_cursor_style(SetCursorStyle::SteadyBar)?;
                ActionResult::Continue
            }
            Action::EnterNormalMode => {
                // Esc while a completion menu is open: cancel the
                // menu (restore original buffer), do NOT change mode.
                // This matches zsh's menu-select behavior — Esc
                // dismisses the menu without leaving Insert.
                if self.cancel_completion_menu() {
                    return Ok(ActionResult::Continue);
                }
                self.in_insert_mode = false;
                self.set_cursor_style(SetCursorStyle::SteadyBlock)?;
                ActionResult::Continue
            }
            Action::Complete => {
                self.handle_complete();
                ActionResult::Continue
            }
            Action::CompletePrev => {
                self.handle_complete_prev();
                ActionResult::Continue
            }
            Action::SearchHistory => {
                self.enter_search();
                ActionResult::Continue
            }
            Action::Undo => {
                self.buffer.undo();
                ActionResult::Continue
            }
            Action::Yank => {
                if let Some(text) = self.kill_ring.clone() {
                    self.buffer.insert_str(&text);
                }
                ActionResult::Continue
            }
            Action::FindCharForward(c) => {
                self.find_char(c, FindDir::Forward, false);
                ActionResult::Continue
            }
            Action::FindCharBackward(c) => {
                self.find_char(c, FindDir::Backward, false);
                ActionResult::Continue
            }
            Action::TillCharForward(c) => {
                self.find_char(c, FindDir::Forward, true);
                ActionResult::Continue
            }
            Action::TillCharBackward(c) => {
                self.find_char(c, FindDir::Backward, true);
                ActionResult::Continue
            }
            Action::ReplaceChar(c) => {
                self.buffer.replace_char_at_cursor(c);
                ActionResult::Continue
            }
            Action::ToggleCase => {
                self.buffer.toggle_case_at_cursor();
                ActionResult::Continue
            }
            Action::YankLine => {
                let text = self.buffer.text();
                if !text.is_empty() {
                    self.kill_ring = Some(text.to_string());
                }
                ActionResult::Continue
            }
            Action::YankWordRight => {
                let cursor = self.buffer.cursor();
                let text = self.buffer.text();
                let end = word_boundary_after_external(text, cursor);
                if end > cursor {
                    self.kill_ring = Some(text[cursor..end].to_string());
                }
                ActionResult::Continue
            }
            Action::YankWordLeft => {
                let cursor = self.buffer.cursor();
                let text = self.buffer.text();
                let start = word_boundary_before_external(text, cursor);
                if start < cursor {
                    self.kill_ring = Some(text[start..cursor].to_string());
                }
                ActionResult::Continue
            }
            Action::YankToEnd => {
                let cursor = self.buffer.cursor();
                let text = self.buffer.text();
                if cursor < text.len() {
                    self.kill_ring = Some(text[cursor..].to_string());
                }
                ActionResult::Continue
            }
            Action::YankToStart => {
                let cursor = self.buffer.cursor();
                let text = self.buffer.text();
                if cursor > 0 {
                    self.kill_ring = Some(text[..cursor].to_string());
                }
                ActionResult::Continue
            }
            // HostCommand is short-circuited in read_line_inner before
            // we get here, but match exhaustiveness still wants an arm.
            Action::HostCommand(_) => ActionResult::Continue,
            // Repeat is expanded to last_edit at the start of the
            // action loop and never reaches apply_action.
            Action::Repeat => ActionResult::Continue,
        };
        Ok(result)
    }

    fn enter_search(&mut self) {
        if self.history.is_none() {
            return;
        }
        self.search = Some(SearchState {
            pattern: String::new(),
            match_index: None,
            original_buffer: self.buffer.text().to_string(),
            original_cursor: self.buffer.cursor(),
        });
    }

    /// Drive the reverse-incremental search state machine for one
    /// key event. Returns whether to continue searching, accept the
    /// match into the buffer, cancel back to the original buffer,
    /// or submit immediately (Enter while in search).
    fn handle_search_key(&mut self, key: KeyEvent) -> SearchResult {
        let KeyEvent { code, modifiers, .. } = key;
        let mods = modifiers - KeyModifiers::SHIFT;

        match (code, mods) {
            // Cancel: restore original buffer.
            (KeyCode::Esc, KeyModifiers::NONE)
            | (KeyCode::Char('c'), KeyModifiers::CONTROL)
            | (KeyCode::Char('g'), KeyModifiers::CONTROL) => {
                if let Some(state) = self.search.as_ref() {
                    let original = state.original_buffer.clone();
                    let cursor = state.original_cursor;
                    self.buffer.set_text(&original);
                    while self.buffer.cursor() > cursor {
                        self.buffer.move_left();
                    }
                }
                SearchResult::Cancel
            }
            // Submit immediately.
            (KeyCode::Enter, KeyModifiers::NONE) => SearchResult::Submit,
            // Cycle to next older match.
            (KeyCode::Char('r'), KeyModifiers::CONTROL) => {
                self.search_step_older();
                SearchResult::Continue
            }
            // Backspace: trim pattern, re-search from the most recent.
            (KeyCode::Backspace, KeyModifiers::NONE) => {
                if let Some(state) = self.search.as_mut() {
                    state.pattern.pop();
                    if state.pattern.is_empty() {
                        state.match_index = None;
                        let original = state.original_buffer.clone();
                        self.buffer.set_text(&original);
                    } else {
                        self.search_from_newest();
                    }
                }
                SearchResult::Continue
            }
            // Printable char: extend the pattern.
            (KeyCode::Char(c), KeyModifiers::NONE | KeyModifiers::SHIFT) => {
                if let Some(state) = self.search.as_mut() {
                    state.pattern.push(c);
                }
                self.search_from_newest();
                SearchResult::Continue
            }
            // Anything else (arrow keys, Ctrl-A/E, etc.): accept
            // the current match into the buffer and let the regular
            // keymap process the same key event.
            _ => SearchResult::Accept,
        }
    }

    /// Search from the newest entry backward for the first one
    /// containing the current pattern. Updates the buffer to the
    /// matched entry (or restores the original if no match).
    fn search_from_newest(&mut self) {
        let Some(state) = self.search.as_mut() else { return; };
        let Some(history) = self.history.as_deref() else { return; };
        let entries = history.entries();
        let mut found: Option<usize> = None;
        for (i, entry) in entries.iter().enumerate().rev() {
            if entry.contains(&state.pattern) {
                found = Some(i);
                break;
            }
        }
        state.match_index = found;
        if let Some(i) = found {
            let entry = entries[i].clone();
            self.buffer.set_text(&entry);
        } else {
            // No match — leave the buffer showing whatever the
            // previous match was (so the user can see what they had).
            // bash's prompt prefix changes to "failing-i-search" to
            // signal this; we render the same way at paint time.
        }
    }

    /// Step to the next older match (Ctrl-R while already in search).
    fn search_step_older(&mut self) {
        let Some(state) = self.search.as_mut() else { return; };
        let Some(history) = self.history.as_deref() else { return; };
        let entries = history.entries();
        let start = state.match_index.unwrap_or(entries.len());
        let mut found: Option<usize> = None;
        for i in (0..start).rev() {
            if entries[i].contains(&state.pattern) {
                found = Some(i);
                break;
            }
        }
        if let Some(i) = found {
            state.match_index = Some(i);
            let entry = entries[i].clone();
            self.buffer.set_text(&entry);
        }
        // No older match: leave state as-is.
    }

    /// Apply tab completion. Single match → apply. Multiple → extend
    /// to longest common prefix if useful, otherwise open a menu
    /// below the buffer; Tab/Shift-Tab navigate, Enter commits, Esc
    /// restores the original buffer.
    fn handle_complete(&mut self) {
        // Menu already open: cycle to next suggestion.
        if let Some(cycle) = self.completion_cycle.as_mut() {
            let n = cycle.suggestions.len();
            if n == 0 {
                return;
            }
            cycle.index = (cycle.index + 1) % n;
            let idx = cycle.index;
            apply_replacement(
                &mut self.buffer,
                &cycle.base_text,
                cycle.span,
                &cycle.suggestions[idx],
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

        let span = suggestions[0].span;
        let span = Span {
            start: span.start.min(line.len()),
            end: span.end.min(line.len()),
        };
        let already_typed = &line[span.start..span.end];

        if suggestions.len() == 1 {
            // Single match: apply directly, no menu.
            apply_replacement(&mut self.buffer, &line, span, &suggestions[0]);
            return;
        }

        // Multiple matches: extend to longest common prefix if it's
        // longer than what's already typed. No menu shown in this case
        // — the user keeps typing or hits Tab again.
        let lcp = longest_common_prefix(suggestions.iter().map(|s| s.value.as_str()));
        if lcp.len() > already_typed.len() && lcp.starts_with(already_typed) {
            let extended = Suggestion {
                value: lcp,
                span,
                append_whitespace: false,
            };
            apply_replacement(&mut self.buffer, &line, span, &extended);
            return;
        }

        // Already at the common prefix. Open the menu and apply the
        // first suggestion as a preview.
        apply_replacement(&mut self.buffer, &line, span, &suggestions[0]);
        self.completion_cycle = Some(CompletionCycle {
            suggestions,
            index: 0,
            base_text: line,
            base_cursor: pos,
            span,
        });
    }

    /// Cycle completion menu backward (Shift-Tab).
    fn handle_complete_prev(&mut self) {
        if let Some(cycle) = self.completion_cycle.as_mut() {
            let n = cycle.suggestions.len();
            if n == 0 {
                return;
            }
            cycle.index = if cycle.index == 0 { n - 1 } else { cycle.index - 1 };
            let idx = cycle.index;
            apply_replacement(
                &mut self.buffer,
                &cycle.base_text,
                cycle.span,
                &cycle.suggestions[idx],
            );
        }
    }

    /// Esc while a completion menu is open: restore the original
    /// buffer and close the menu. Returns `true` if a menu was open.
    fn cancel_completion_menu(&mut self) -> bool {
        if let Some(cycle) = self.completion_cycle.take() {
            self.buffer.set_text(&cycle.base_text);
            // base_text was the buffer before menu opened. set_text
            // landed cursor at end; pop the undo snapshot so this
            // restoration isn't `u`-able.
            let _ = self.buffer.undo();
            true
        } else {
            false
        }
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

    /// Implement vi `f`/`F`/`t`/`T` motions. Searches the buffer in
    /// the given direction starting just past the current cursor for
    /// `target` and moves the cursor to it (or one grapheme before
    /// it, if `till`). No-op if not found. Search is bounded to the
    /// current visual line: a `\n` between cursor and target stops
    /// the search, matching vim's "f only operates on the current
    /// line" semantics.
    fn find_char(&mut self, target: char, dir: FindDir, till: bool) {
        let text = self.buffer.text().to_string();
        let cursor = self.buffer.cursor();
        let new_cursor = match dir {
            FindDir::Forward => {
                let search_start = (cursor + target.len_utf8()).min(text.len());
                let mut hit: Option<usize> = None;
                let bytes = text.as_bytes();
                let mut i = search_start;
                while i < bytes.len() {
                    if bytes[i] == b'\n' {
                        break;
                    }
                    if text.is_char_boundary(i) {
                        if let Some(c) = text[i..].chars().next() {
                            if c == target {
                                hit = Some(i);
                                break;
                            }
                        }
                    }
                    i += 1;
                }
                hit.map(|h| {
                    if till {
                        prev_char_boundary(&text, h).unwrap_or(h)
                    } else {
                        h
                    }
                })
            }
            FindDir::Backward => {
                let mut hit: Option<usize> = None;
                let mut i = cursor;
                while i > 0 {
                    i -= 1;
                    if !text.is_char_boundary(i) {
                        continue;
                    }
                    let c = text[i..].chars().next();
                    if c == Some('\n') {
                        break;
                    }
                    if c == Some(target) {
                        hit = Some(i);
                        break;
                    }
                }
                hit.map(|h| {
                    if till {
                        next_char_boundary(&text, h).unwrap_or(h)
                    } else {
                        h
                    }
                })
            }
        };
        if let Some(pos) = new_cursor {
            self.buffer.set_cursor(pos);
        }
    }

    fn repaint(&mut self, prompt: &dyn Prompt) -> io::Result<()> {
        // While searching history, replace the host prompt with the
        // bash-style search indicator. `failing-i-search` signals
        // there's no match for the current pattern.
        let prompt_text = if let Some(state) = &self.search {
            let label = if state.match_index.is_none() && !state.pattern.is_empty() {
                "failing-i-search"
            } else {
                "reverse-i-search"
            };
            format!("({label})`{}': ", state.pattern)
        } else {
            prompt.render()
        };
        let before = self.buffer.before_cursor().to_string();
        let after = self.buffer.after_cursor().to_string();
        let width = self.painter.screen_width();

        // Compute the autosuggestion hint. Only meaningful when the
        // cursor is at the end of the buffer (otherwise the hint
        // would visually float in the middle of edited text). And
        // only when the user is editing fresh, not navigating
        // history — avoid hinting "ls -la /tmp" when they just
        // recalled "ls -la /tmp". Disabled in Ctrl-R search mode
        // since the buffer there is the matched entry, not user
        // input.
        let hint = if self.hint_enabled
            && after.is_empty()
            && self.search.is_none()
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
        // emit covers (prompt + before + after + hint + menu).
        let pre_cursor = format!("{prompt_text}{before}");
        let pre_m = measure(&pre_cursor, width);
        // Menu rows live below the buffer/hint. We render them in the
        // emit stream so the painter's last_emit_rows includes them
        // and the next clear walk wipes them.
        let menu_text = if let Some(cycle) = &self.completion_cycle {
            render_completion_menu(cycle, width)
        } else {
            String::new()
        };
        let full_text = format!("{prompt_text}{before}{after}{hint}{menu_text}");
        let full_m = measure(&full_text, width);

        // Emit.
        self.painter.prepare_for_emit()?;
        let prompt_crlf = coerce_crlf(&prompt_text);
        // Apply the syntax highlighter to before_cursor / after_cursor
        // when one is attached and we're not in Ctrl-R search mode
        // (the search-mode "buffer" is the matched history entry, not
        // user input — the highlighter would decorate the recall, not
        // the typing).
        let highlight_active = self.highlighter.is_some() && self.search.is_none();
        let before_to_print = if highlight_active {
            coerce_crlf(&self.highlighter.as_deref().unwrap().highlight(&before))
                .into_owned()
        } else {
            coerce_crlf(&before).into_owned()
        };
        let after_to_print = if highlight_active {
            coerce_crlf(&self.highlighter.as_deref().unwrap().highlight(&after))
                .into_owned()
        } else {
            coerce_crlf(&after).into_owned()
        };
        let hint_crlf = coerce_crlf(&hint);
        {
            let out = self.painter.out();
            out.queue(Print(prompt_crlf.as_ref()))?;
            out.queue(Print(&before_to_print))?;
            out.queue(cursor::SavePosition)?;
            out.queue(Print(&after_to_print))?;
            if !hint.is_empty() {
                out.queue(SetForegroundColor(Color::DarkGrey))?;
                out.queue(Print(hint_crlf.as_ref()))?;
                out.queue(ResetColor)?;
            }
            if !menu_text.is_empty() {
                let menu_crlf = coerce_crlf(&menu_text);
                out.queue(Print(menu_crlf.as_ref()))?;
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

enum SearchResult {
    /// Stay in search mode; repaint and read the next event.
    Continue,
    /// Accept the current match into the buffer; exit search and
    /// re-process the key through the normal keymap.
    Accept,
    /// Restore the original buffer; exit search.
    Cancel,
    /// Submit immediately (Enter pressed in search mode).
    Submit,
}

#[derive(Debug, Clone, Copy)]
enum FindDir {
    Forward,
    Backward,
}

fn prev_char_boundary(text: &str, byte_idx: usize) -> Option<usize> {
    if byte_idx == 0 {
        return None;
    }
    let mut i = byte_idx - 1;
    while i > 0 && !text.is_char_boundary(i) {
        i -= 1;
    }
    Some(i)
}

fn next_char_boundary(text: &str, byte_idx: usize) -> Option<usize> {
    if byte_idx >= text.len() {
        return None;
    }
    let mut i = byte_idx + 1;
    while i < text.len() && !text.is_char_boundary(i) {
        i += 1;
    }
    Some(i)
}

/// Same whitespace-bounded word logic as in the buffer module, but
/// reachable from the engine for yank-without-delete. The engine
/// could call buffer methods that returned ranges instead, but the
/// duplication here is small and avoids changing the LineBuffer API.
fn word_boundary_after_external(text: &str, byte_idx: usize) -> usize {
    let bytes = text.as_bytes();
    let len = bytes.len();
    if byte_idx >= len {
        return len;
    }
    let is_ws = |b: u8| b == b' ' || b == b'\t' || b == b'\n';
    let mut i = byte_idx;
    while i < len && is_ws(bytes[i]) {
        i += 1;
    }
    while i < len && !is_ws(bytes[i]) {
        i += 1;
    }
    while i < len && !text.is_char_boundary(i) {
        i += 1;
    }
    i
}

/// Render the completion menu as a string of `\n`-separated rows
/// to append to the paint emit. Each suggestion takes one row; the
/// currently selected one is reverse-video so the user can see which
/// will be committed on Enter or by typing past it. Capped at the
/// terminal width so wrapping doesn't multiply menu rows.
fn render_completion_menu(cycle: &CompletionCycle, width: u16) -> String {
    if cycle.suggestions.is_empty() {
        return String::new();
    }
    let max_cells = (width as usize).saturating_sub(2).max(1);
    let mut out = String::new();
    for (i, suggestion) in cycle.suggestions.iter().enumerate() {
        out.push('\n');
        let value = &suggestion.value;
        let truncated: String = value.chars().take(max_cells).collect();
        if i == cycle.index {
            // SGR 7 = reverse video. Reset at end of row.
            out.push_str("\x1b[7m");
            out.push_str(&truncated);
            out.push_str("\x1b[0m");
        } else {
            out.push_str(&truncated);
        }
    }
    out
}

/// Action types that start a vi `.` recording when seen at the
/// start of a fresh edit. Movement-only actions (MoveLeft, etc.) and
/// non-buffer actions (Submit, Cancel, history nav, search, complete,
/// HostCommand, Repeat itself) don't trigger recording — `.` repeats
/// the last *edit*, not the last keystroke.
fn starts_edit_recording(action: &Action) -> bool {
    matches!(
        action,
        Action::InsertChar(_)
            | Action::DeleteLeft
            | Action::DeleteRight
            | Action::DeleteWordLeft
            | Action::DeleteWordRight
            | Action::KillToEnd
            | Action::KillToStart
            | Action::DeleteLine
            | Action::ReplaceChar(_)
            | Action::ToggleCase
            | Action::EnterInsertMode
            | Action::Yank
    )
}

fn word_boundary_before_external(text: &str, byte_idx: usize) -> usize {
    if byte_idx == 0 {
        return 0;
    }
    let bytes = text.as_bytes();
    let is_ws = |b: u8| b == b' ' || b == b'\t' || b == b'\n';
    let mut i = byte_idx;
    while i > 0 && is_ws(bytes[i - 1]) {
        i -= 1;
    }
    while i > 0 && !is_ws(bytes[i - 1]) {
        i -= 1;
    }
    while i > 0 && !text.is_char_boundary(i) {
        i -= 1;
    }
    i
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

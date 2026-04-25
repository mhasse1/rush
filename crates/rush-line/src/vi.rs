//! Vi keymap. Insert mode + Normal mode, with the operator-pending
//! state machine for compound commands (`dd`, `dw`, `cw`, `c$`, etc.).
//!
//! ## What's in
//!
//! Insert mode is essentially emacs (so editing-while-typing feels
//! the same): printable chars insert, Backspace/Delete work, arrow
//! keys move, Up/Down navigate history. `Esc` enters Normal mode.
//!
//! Normal mode covers the bread-and-butter set:
//!
//! | Key       | Effect                                             |
//! |-----------|----------------------------------------------------|
//! | `h`/`l`   | move left / right                                  |
//! | `0`/`$`   | start / end of line                                |
//! | `w`/`b`   | next / previous word                               |
//! | `i`       | enter insert mode                                  |
//! | `I`       | start of line, then insert                         |
//! | `a`       | move right, then insert                            |
//! | `A`       | end of line, then insert                           |
//! | `x`/`X`   | delete char under / before cursor                  |
//! | `D`       | delete to end of line                              |
//! | `C`       | delete to end + insert                             |
//! | `S`       | delete entire line + insert                        |
//! | `s`       | delete char under cursor + insert                  |
//! | `dd`      | delete entire line                                 |
//! | `cc`      | delete entire line + insert                        |
//! | `d{w,b,$,0}` | delete word forward/back / to end / to start    |
//! | `c{w,b,$,0}` | same as `d` motions, then enter insert          |
//! | `k`/`j`   | history previous / next                            |
//! | `Enter`   | submit                                             |
//! | `Ctrl-C`  | cancel                                             |
//!
//! ## What's not (yet)
//!
//! No yank / paste (`y`, `p`, `P`) — that needs a kill ring, separate
//! phase. No undo (`u`) — needs an undo stack. No counts (`3w`, `5dd`).
//! No `f`/`F`/`t`/`T` find-char. No search (`/`, `?`, `n`, `N`). No
//! `r` replace, no `~` toggle case. No `g` prefix (`gg`). No `o`/`O`
//! open-line (single-line buffers don't have a great answer for it).
//!
//! All of the above are additive — the keymap state machine has room
//! for them, and they don't change the core paint or buffer model.

use crossterm::event::{KeyCode, KeyEvent, KeyModifiers};

use crate::keymap::{Action, KeyMap};

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ViMode {
    Insert,
    Normal,
}

/// Operator pending state. Set when the user types `d`, `c`, etc. in
/// Normal mode; the next keystroke is interpreted as a motion or
/// (if it matches the operator) a "whole line" trigger like `dd`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum Pending {
    None,
    Delete,
    Change,
    /// `y` operator — awaiting motion or `y` for whole-line yank.
    Yank,
    /// `f` motion — awaiting target character.
    FindForward,
    /// `F` motion — awaiting target character (search backward).
    FindBackward,
    /// `t` motion — awaiting target character (till, exclusive).
    TillForward,
    /// `T` motion — awaiting target character (till, exclusive,
    /// search backward).
    TillBackward,
    /// `r` — awaiting replacement character.
    Replace,
}

#[derive(Debug)]
pub struct ViKeyMap {
    mode: ViMode,
    pending: Pending,
    /// Count accumulator for vi commands like `3w`, `5dd`. Digits
    /// accumulate; the next non-digit action gets repeated this
    /// many times. `0` alone (without an in-progress count) is a
    /// motion (MoveHome), not a digit.
    count: Option<u32>,
    /// When true, Ctrl-R, `/`, and `?` fire
    /// `Action::HostCommand("__fzf_history__")` instead of the
    /// built-in reverse-i-search. Set by the host when fzf is on
    /// PATH.
    fzf_enabled: bool,
}

impl Default for ViKeyMap {
    fn default() -> Self {
        Self::new()
    }
}

impl ViKeyMap {
    pub fn new() -> Self {
        Self {
            mode: ViMode::Insert,
            pending: Pending::None,
            count: None,
            fzf_enabled: false,
        }
    }

    /// Construct starting in Normal mode (less common; usually you
    /// want a fresh prompt to start in Insert).
    pub fn starting_normal() -> Self {
        Self {
            mode: ViMode::Normal,
            pending: Pending::None,
            count: None,
            fzf_enabled: false,
        }
    }

    /// Route Ctrl-R / `/` / `?` to `Action::HostCommand("__fzf_history__")`
    /// instead of the built-in reverse-i-search. Set by the host
    /// when fzf is available.
    pub fn with_fzf(mut self, enabled: bool) -> Self {
        self.fzf_enabled = enabled;
        self
    }

    pub fn mode(&self) -> ViMode {
        self.mode
    }
}

impl KeyMap for ViKeyMap {
    fn translate(&mut self, event: KeyEvent) -> Vec<Action> {
        match self.mode {
            ViMode::Insert => self.translate_insert(event),
            ViMode::Normal => self.translate_normal(event),
        }
    }

    fn reset(&mut self) -> Vec<Action> {
        // Each fresh prompt starts in Insert with all pending state
        // cleared. The returned action makes the engine update the
        // cursor shape (bar) so it visibly matches.
        self.mode = ViMode::Insert;
        self.pending = Pending::None;
        self.count = None;
        vec![Action::EnterInsertMode]
    }
}

impl ViKeyMap {
    fn translate_insert(&mut self, event: KeyEvent) -> Vec<Action> {
        let KeyEvent { code, modifiers, .. } = event;
        let mods = modifiers - KeyModifiers::SHIFT;

        let one = |a: Action| vec![a];
        match (code, mods) {
            // Esc → Normal mode
            (KeyCode::Esc, KeyModifiers::NONE) => {
                self.mode = ViMode::Normal;
                self.pending = Pending::None;
                vec![Action::EnterNormalMode]
            }
            // Same as emacs: insert chars, basic editing, history, submit.
            (KeyCode::Char(c), KeyModifiers::NONE) => one(Action::InsertChar(c)),
            (KeyCode::Backspace, KeyModifiers::NONE) => one(Action::DeleteLeft),
            (KeyCode::Char('h'), KeyModifiers::CONTROL) => one(Action::DeleteLeft),
            (KeyCode::Delete, KeyModifiers::NONE) => one(Action::DeleteRight),
            (KeyCode::Char('w'), KeyModifiers::CONTROL) => one(Action::DeleteWordLeft),
            (KeyCode::Char('u'), KeyModifiers::CONTROL) => one(Action::KillToStart),
            (KeyCode::Char('k'), KeyModifiers::CONTROL) => one(Action::KillToEnd),
            (KeyCode::Left, KeyModifiers::NONE) => one(Action::MoveLeft),
            (KeyCode::Right, KeyModifiers::NONE) => one(Action::MoveRight),
            (KeyCode::Home, KeyModifiers::NONE) => one(Action::MoveHome),
            (KeyCode::End, KeyModifiers::NONE) => one(Action::MoveEnd),
            (KeyCode::Up, KeyModifiers::NONE) => one(Action::HistoryPrev),
            (KeyCode::Down, KeyModifiers::NONE) => one(Action::HistoryNext),
            (KeyCode::Enter, KeyModifiers::NONE) => one(Action::Submit),
            (KeyCode::Char('c'), KeyModifiers::CONTROL) => one(Action::Cancel),
            (KeyCode::Char('d'), KeyModifiers::CONTROL) => one(Action::EndOfInput),
            (KeyCode::Char('l'), KeyModifiers::CONTROL) => one(Action::Clear),
            (KeyCode::Tab, KeyModifiers::NONE) => one(Action::Complete),
            (KeyCode::BackTab, KeyModifiers::NONE)
            | (KeyCode::BackTab, KeyModifiers::SHIFT)
            | (KeyCode::Tab, KeyModifiers::SHIFT) => one(Action::CompletePrev),
            (KeyCode::Char('r'), KeyModifiers::CONTROL)
            // Alt-/ and Alt-? cover terminals that fold `Esc /` and
            // `Esc ?` into a single Alt-modified event before crossterm
            // sees them (xterm with metaSendsEsc, some tmux configs).
            // The vi-Normal handler already covers the two-keystroke
            // case where Esc and / arrive as separate events.
            | (KeyCode::Char('/'), KeyModifiers::ALT)
            | (KeyCode::Char('?'), KeyModifiers::ALT) => {
                if self.fzf_enabled {
                    one(Action::HostCommand("__fzf_history__".to_string()))
                } else {
                    one(Action::SearchHistory)
                }
            }
            _ => Vec::new(),
        }
    }

    fn translate_normal(&mut self, event: KeyEvent) -> Vec<Action> {
        let KeyEvent { code, modifiers, .. } = event;
        let mods = modifiers - KeyModifiers::SHIFT;

        // Count accumulator. A bare `0` is a motion (MoveHome) and
        // doesn't consume a count; a `0` after another digit extends
        // the count. Digit input never emits an action — we wait for
        // the next non-digit key, then repeat the resulting action
        // vec `count` times.
        if let KeyCode::Char(c) = code {
            if mods == KeyModifiers::NONE && c.is_ascii_digit() {
                let d = c.to_digit(10).unwrap();
                let starting_count = self.count.is_some() || d != 0;
                if starting_count {
                    self.count = Some(
                        self.count
                            .unwrap_or(0)
                            .saturating_mul(10)
                            .saturating_add(d),
                    );
                    return Vec::new();
                }
            }
        }

        // Operator-pending: previous keystroke was `d` or `c`. The
        // current keystroke is interpreted as a motion or a same-key
        // trigger (`dd`, `cc`).
        let actions = if self.pending != Pending::None {
            let pending = self.pending;
            self.pending = Pending::None;
            self.translate_operator_motion(pending, code, mods)
        } else {
            self.translate_normal_action(code, mods)
        };

        // Only consume the count when we actually emit actions. The
        // operator key (`d`, `c`) returns an empty vec while it sets
        // `pending`; we want the count preserved across the operator
        // and applied by the motion that follows.
        let count = if actions.is_empty() {
            1
        } else {
            self.count.take().unwrap_or(1)
        };
        repeat_actions(actions, count)
    }

    /// One-keystroke Normal-mode dispatch (no count, no pending).
    fn translate_normal_action(&mut self, code: KeyCode, mods: KeyModifiers) -> Vec<Action> {
        let one = |a: Action| vec![a];
        match (code, mods) {
            // ---- mode transitions ----
            (KeyCode::Char('i'), KeyModifiers::NONE) => {
                self.mode = ViMode::Insert;
                vec![Action::EnterInsertMode]
            }
            (KeyCode::Char('I'), KeyModifiers::NONE) => {
                self.mode = ViMode::Insert;
                vec![Action::MoveHome, Action::EnterInsertMode]
            }
            (KeyCode::Char('a'), KeyModifiers::NONE) => {
                self.mode = ViMode::Insert;
                vec![Action::MoveRight, Action::EnterInsertMode]
            }
            (KeyCode::Char('A'), KeyModifiers::NONE) => {
                self.mode = ViMode::Insert;
                vec![Action::MoveEnd, Action::EnterInsertMode]
            }

            // ---- single-key edits ----
            (KeyCode::Char('u'), KeyModifiers::NONE) => one(Action::Undo),
            (KeyCode::Char('x'), KeyModifiers::NONE) => one(Action::DeleteRight),
            (KeyCode::Char('X'), KeyModifiers::NONE) => one(Action::DeleteLeft),
            (KeyCode::Char('p'), KeyModifiers::NONE) => one(Action::Yank),
            // P (paste before cursor) collapses to Yank for our
            // single-line shell buffer — there's no "above the
            // current line" to distinguish from "before the cursor."
            (KeyCode::Char('P'), KeyModifiers::NONE) => one(Action::Yank),
            (KeyCode::Char('D'), KeyModifiers::NONE) => one(Action::KillToEnd),
            (KeyCode::Char('C'), KeyModifiers::NONE) => {
                self.mode = ViMode::Insert;
                vec![Action::KillToEnd, Action::EnterInsertMode]
            }
            (KeyCode::Char('S'), KeyModifiers::NONE) => {
                self.mode = ViMode::Insert;
                vec![Action::DeleteLine, Action::EnterInsertMode]
            }
            (KeyCode::Char('s'), KeyModifiers::NONE) => {
                self.mode = ViMode::Insert;
                vec![Action::DeleteRight, Action::EnterInsertMode]
            }

            // ---- operator-pending entries ----
            (KeyCode::Char('d'), KeyModifiers::NONE) => {
                self.pending = Pending::Delete;
                Vec::new()
            }
            (KeyCode::Char('c'), KeyModifiers::NONE) => {
                self.pending = Pending::Change;
                Vec::new()
            }
            (KeyCode::Char('y'), KeyModifiers::NONE) => {
                self.pending = Pending::Yank;
                Vec::new()
            }

            // ---- find-char (await target) ----
            (KeyCode::Char('f'), KeyModifiers::NONE) => {
                self.pending = Pending::FindForward;
                Vec::new()
            }
            (KeyCode::Char('F'), KeyModifiers::NONE) => {
                self.pending = Pending::FindBackward;
                Vec::new()
            }
            (KeyCode::Char('t'), KeyModifiers::NONE) => {
                self.pending = Pending::TillForward;
                Vec::new()
            }
            (KeyCode::Char('T'), KeyModifiers::NONE) => {
                self.pending = Pending::TillBackward;
                Vec::new()
            }

            // ---- replace single char (await replacement) ----
            (KeyCode::Char('r'), KeyModifiers::NONE) => {
                self.pending = Pending::Replace;
                Vec::new()
            }

            // ---- toggle case (one-shot) ----
            (KeyCode::Char('~'), KeyModifiers::NONE) => one(Action::ToggleCase),

            // ---- repeat last edit ----
            (KeyCode::Char('.'), KeyModifiers::NONE) => one(Action::Repeat),

            // ---- motions ----
            (KeyCode::Char('h'), KeyModifiers::NONE) | (KeyCode::Left, KeyModifiers::NONE) => {
                one(Action::MoveLeft)
            }
            (KeyCode::Char('l'), KeyModifiers::NONE) | (KeyCode::Right, KeyModifiers::NONE) => {
                one(Action::MoveRight)
            }
            (KeyCode::Char('0'), KeyModifiers::NONE) | (KeyCode::Home, KeyModifiers::NONE) => {
                one(Action::MoveHome)
            }
            (KeyCode::Char('$'), KeyModifiers::NONE) | (KeyCode::End, KeyModifiers::NONE) => {
                one(Action::MoveEnd)
            }
            (KeyCode::Char('w'), KeyModifiers::NONE) => one(Action::MoveWordRight),
            (KeyCode::Char('b'), KeyModifiers::NONE) => one(Action::MoveWordLeft),

            // ---- history ----
            (KeyCode::Char('k'), KeyModifiers::NONE) | (KeyCode::Up, KeyModifiers::NONE) => {
                one(Action::HistoryPrev)
            }
            (KeyCode::Char('j'), KeyModifiers::NONE) | (KeyCode::Down, KeyModifiers::NONE) => {
                one(Action::HistoryNext)
            }

            // ---- submission / signals ----
            (KeyCode::Enter, KeyModifiers::NONE) => one(Action::Submit),
            (KeyCode::Char('c'), KeyModifiers::CONTROL) => one(Action::Cancel),
            (KeyCode::Char('l'), KeyModifiers::CONTROL) => one(Action::Clear),

            // ---- history search ----
            // Ctrl-R, /, and ? all enter history search. With fzf
            // enabled they fire HostCommand("__fzf_history__"); the
            // host runs fzf and pre-loads the editor with the
            // selected line. Without fzf, our built-in reverse-i-
            // search handles them.
            (KeyCode::Char('r'), KeyModifiers::CONTROL)
            | (KeyCode::Char('/'), KeyModifiers::NONE)
            | (KeyCode::Char('?'), KeyModifiers::NONE) => {
                if self.fzf_enabled {
                    one(Action::HostCommand("__fzf_history__".to_string()))
                } else {
                    one(Action::SearchHistory)
                }
            }

            // Unknown — Esc on Esc is a no-op (already in Normal).
            _ => Vec::new(),
        }
    }

    fn translate_operator_motion(
        &mut self,
        pending: Pending,
        code: KeyCode,
        mods: KeyModifiers,
    ) -> Vec<Action> {
        // Find-char / till-char / replace pendings: the next key is
        // the target character, regardless of modifier (uppercase
        // letters arrive as Char + SHIFT).
        match pending {
            Pending::FindForward => {
                if let KeyCode::Char(c) = code {
                    return vec![Action::FindCharForward(c)];
                }
                return Vec::new();
            }
            Pending::FindBackward => {
                if let KeyCode::Char(c) = code {
                    return vec![Action::FindCharBackward(c)];
                }
                return Vec::new();
            }
            Pending::TillForward => {
                if let KeyCode::Char(c) = code {
                    return vec![Action::TillCharForward(c)];
                }
                return Vec::new();
            }
            Pending::TillBackward => {
                if let KeyCode::Char(c) = code {
                    return vec![Action::TillCharBackward(c)];
                }
                return Vec::new();
            }
            Pending::Replace => {
                if let KeyCode::Char(c) = code {
                    return vec![Action::ReplaceChar(c)];
                }
                return Vec::new();
            }
            _ => {}
        }

        // Same-key double press → operate on whole line: dd, cc, yy.
        let same_key = match (pending, code) {
            (Pending::Delete, KeyCode::Char('d')) => true,
            (Pending::Change, KeyCode::Char('c')) => true,
            (Pending::Yank, KeyCode::Char('y')) => true,
            _ => false,
        };
        if same_key {
            return match pending {
                Pending::Delete => vec![Action::DeleteLine],
                Pending::Change => {
                    self.mode = ViMode::Insert;
                    vec![Action::DeleteLine, Action::EnterInsertMode]
                }
                Pending::Yank => vec![Action::YankLine],
                _ => Vec::new(),
            };
        }

        // Map a motion key onto the operator's edit/yank action.
        let motion_kind = match (code, mods) {
            (KeyCode::Char('w'), KeyModifiers::NONE) => Some(MotionKind::WordRight),
            (KeyCode::Char('b'), KeyModifiers::NONE) => Some(MotionKind::WordLeft),
            (KeyCode::Char('$'), KeyModifiers::NONE) | (KeyCode::End, KeyModifiers::NONE) => {
                Some(MotionKind::ToEnd)
            }
            (KeyCode::Char('0'), KeyModifiers::NONE) | (KeyCode::Home, KeyModifiers::NONE) => {
                Some(MotionKind::ToStart)
            }
            (KeyCode::Esc, KeyModifiers::NONE) => return Vec::new(),
            _ => None,
        };

        let Some(motion) = motion_kind else {
            return Vec::new();
        };

        match pending {
            Pending::Delete => vec![motion.delete_action()],
            Pending::Change => {
                self.mode = ViMode::Insert;
                vec![motion.delete_action(), Action::EnterInsertMode]
            }
            Pending::Yank => vec![motion.yank_action()],
            _ => Vec::new(),
        }
    }
}

/// One of vi's range-defining motions, translated to either a
/// delete or a yank action depending on which operator preceded it.
#[derive(Debug, Clone, Copy)]
enum MotionKind {
    WordRight,
    WordLeft,
    ToEnd,
    ToStart,
}

impl MotionKind {
    fn delete_action(self) -> Action {
        match self {
            MotionKind::WordRight => Action::DeleteWordRight,
            MotionKind::WordLeft => Action::DeleteWordLeft,
            MotionKind::ToEnd => Action::KillToEnd,
            MotionKind::ToStart => Action::KillToStart,
        }
    }
    fn yank_action(self) -> Action {
        match self {
            MotionKind::WordRight => Action::YankWordRight,
            MotionKind::WordLeft => Action::YankWordLeft,
            MotionKind::ToEnd => Action::YankToEnd,
            MotionKind::ToStart => Action::YankToStart,
        }
    }
}

/// Repeat `actions` `count` times. `count == 1` (the default when no
/// vi count prefix was typed) returns `actions` unchanged. Higher
/// counts allocate a fresh vec; capped at 1000 to bound how badly a
/// typo like `9999w` can hurt us.
fn repeat_actions(actions: Vec<Action>, count: u32) -> Vec<Action> {
    if count <= 1 || actions.is_empty() {
        return actions;
    }
    let n = count.min(1000) as usize;
    let mut out = Vec::with_capacity(actions.len() * n);
    for _ in 0..n {
        out.extend(actions.iter().cloned());
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    fn ev(code: KeyCode) -> KeyEvent {
        KeyEvent::new(code, KeyModifiers::NONE)
    }

    #[test]
    fn starts_in_insert_mode() {
        let m = ViKeyMap::new();
        assert_eq!(m.mode(), ViMode::Insert);
    }

    #[test]
    fn esc_enters_normal_mode() {
        let mut m = ViKeyMap::new();
        let actions = m.translate(ev(KeyCode::Esc));
        assert_eq!(actions, vec![Action::EnterNormalMode]);
        assert_eq!(m.mode(), ViMode::Normal);
    }

    #[test]
    fn insert_chars_in_insert_mode() {
        let mut m = ViKeyMap::new();
        assert_eq!(m.translate(ev(KeyCode::Char('a'))), vec![Action::InsertChar('a')]);
    }

    #[test]
    fn normal_mode_motions() {
        let mut m = ViKeyMap::starting_normal();
        assert_eq!(m.translate(ev(KeyCode::Char('h'))), vec![Action::MoveLeft]);
        assert_eq!(m.translate(ev(KeyCode::Char('l'))), vec![Action::MoveRight]);
        assert_eq!(m.translate(ev(KeyCode::Char('w'))), vec![Action::MoveWordRight]);
        assert_eq!(m.translate(ev(KeyCode::Char('b'))), vec![Action::MoveWordLeft]);
        assert_eq!(m.translate(ev(KeyCode::Char('0'))), vec![Action::MoveHome]);
        assert_eq!(m.translate(ev(KeyCode::Char('$'))), vec![Action::MoveEnd]);
    }

    #[test]
    fn i_enters_insert_in_place() {
        let mut m = ViKeyMap::starting_normal();
        assert_eq!(m.translate(ev(KeyCode::Char('i'))), vec![Action::EnterInsertMode]);
        assert_eq!(m.mode(), ViMode::Insert);
    }

    #[test]
    fn capital_i_homes_then_enters_insert() {
        let mut m = ViKeyMap::starting_normal();
        assert_eq!(
            m.translate(ev(KeyCode::Char('I'))),
            vec![Action::MoveHome, Action::EnterInsertMode]
        );
    }

    #[test]
    fn a_moves_right_then_enters_insert() {
        let mut m = ViKeyMap::starting_normal();
        assert_eq!(
            m.translate(ev(KeyCode::Char('a'))),
            vec![Action::MoveRight, Action::EnterInsertMode]
        );
    }

    #[test]
    fn capital_a_moves_to_end_then_enters_insert() {
        let mut m = ViKeyMap::starting_normal();
        assert_eq!(
            m.translate(ev(KeyCode::Char('A'))),
            vec![Action::MoveEnd, Action::EnterInsertMode]
        );
    }

    #[test]
    fn x_deletes_under_cursor_X_deletes_before() {
        let mut m = ViKeyMap::starting_normal();
        assert_eq!(m.translate(ev(KeyCode::Char('x'))), vec![Action::DeleteRight]);
        assert_eq!(m.translate(ev(KeyCode::Char('X'))), vec![Action::DeleteLeft]);
    }

    #[test]
    fn capital_d_kills_to_end() {
        let mut m = ViKeyMap::starting_normal();
        assert_eq!(m.translate(ev(KeyCode::Char('D'))), vec![Action::KillToEnd]);
    }

    #[test]
    fn capital_c_kills_to_end_then_inserts() {
        let mut m = ViKeyMap::starting_normal();
        assert_eq!(
            m.translate(ev(KeyCode::Char('C'))),
            vec![Action::KillToEnd, Action::EnterInsertMode]
        );
        assert_eq!(m.mode(), ViMode::Insert);
    }

    #[test]
    fn capital_s_substitutes_whole_line() {
        let mut m = ViKeyMap::starting_normal();
        assert_eq!(
            m.translate(ev(KeyCode::Char('S'))),
            vec![Action::DeleteLine, Action::EnterInsertMode]
        );
    }

    #[test]
    fn dd_deletes_line() {
        let mut m = ViKeyMap::starting_normal();
        assert_eq!(m.translate(ev(KeyCode::Char('d'))), Vec::<Action>::new());
        assert_eq!(m.translate(ev(KeyCode::Char('d'))), vec![Action::DeleteLine]);
    }

    #[test]
    fn cc_deletes_line_and_enters_insert() {
        let mut m = ViKeyMap::starting_normal();
        m.translate(ev(KeyCode::Char('c')));
        assert_eq!(
            m.translate(ev(KeyCode::Char('c'))),
            vec![Action::DeleteLine, Action::EnterInsertMode]
        );
        assert_eq!(m.mode(), ViMode::Insert);
    }

    #[test]
    fn dw_deletes_word_forward() {
        let mut m = ViKeyMap::starting_normal();
        m.translate(ev(KeyCode::Char('d')));
        assert_eq!(m.translate(ev(KeyCode::Char('w'))), vec![Action::DeleteWordRight]);
    }

    #[test]
    fn db_deletes_word_back() {
        let mut m = ViKeyMap::starting_normal();
        m.translate(ev(KeyCode::Char('d')));
        assert_eq!(m.translate(ev(KeyCode::Char('b'))), vec![Action::DeleteWordLeft]);
    }

    #[test]
    fn cw_deletes_word_forward_and_enters_insert() {
        let mut m = ViKeyMap::starting_normal();
        m.translate(ev(KeyCode::Char('c')));
        assert_eq!(
            m.translate(ev(KeyCode::Char('w'))),
            vec![Action::DeleteWordRight, Action::EnterInsertMode]
        );
    }

    #[test]
    fn d_dollar_kills_to_end() {
        let mut m = ViKeyMap::starting_normal();
        m.translate(ev(KeyCode::Char('d')));
        assert_eq!(m.translate(ev(KeyCode::Char('$'))), vec![Action::KillToEnd]);
    }

    #[test]
    fn c_zero_kills_to_start_and_enters_insert() {
        let mut m = ViKeyMap::starting_normal();
        m.translate(ev(KeyCode::Char('c')));
        assert_eq!(
            m.translate(ev(KeyCode::Char('0'))),
            vec![Action::KillToStart, Action::EnterInsertMode]
        );
    }

    #[test]
    fn esc_in_operator_pending_cancels_without_action() {
        let mut m = ViKeyMap::starting_normal();
        m.translate(ev(KeyCode::Char('d')));
        assert_eq!(m.translate(ev(KeyCode::Esc)), Vec::<Action>::new());
        // After cancel, we're back in plain Normal — pending cleared.
        assert_eq!(m.translate(ev(KeyCode::Char('h'))), vec![Action::MoveLeft]);
    }

    #[test]
    fn k_and_j_navigate_history_in_normal() {
        let mut m = ViKeyMap::starting_normal();
        assert_eq!(m.translate(ev(KeyCode::Char('k'))), vec![Action::HistoryPrev]);
        assert_eq!(m.translate(ev(KeyCode::Char('j'))), vec![Action::HistoryNext]);
    }

    #[test]
    fn enter_submits_in_normal_too() {
        let mut m = ViKeyMap::starting_normal();
        assert_eq!(m.translate(ev(KeyCode::Enter)), vec![Action::Submit]);
    }

    #[test]
    fn count_repeats_simple_motion() {
        let mut m = ViKeyMap::starting_normal();
        // "3w" → MoveWordRight x 3
        assert_eq!(m.translate(ev(KeyCode::Char('3'))), Vec::<Action>::new());
        assert_eq!(
            m.translate(ev(KeyCode::Char('w'))),
            vec![Action::MoveWordRight, Action::MoveWordRight, Action::MoveWordRight]
        );
    }

    #[test]
    fn count_resets_after_use() {
        let mut m = ViKeyMap::starting_normal();
        m.translate(ev(KeyCode::Char('3')));
        m.translate(ev(KeyCode::Char('w'))); // uses count
        // Next motion is uncounted.
        assert_eq!(m.translate(ev(KeyCode::Char('w'))), vec![Action::MoveWordRight]);
    }

    #[test]
    fn count_with_dd_repeats_delete_line() {
        let mut m = ViKeyMap::starting_normal();
        // "5dd"
        m.translate(ev(KeyCode::Char('5')));
        m.translate(ev(KeyCode::Char('d')));
        let actions = m.translate(ev(KeyCode::Char('d')));
        assert_eq!(actions.len(), 5);
        assert!(actions.iter().all(|a| *a == Action::DeleteLine));
    }

    #[test]
    fn count_with_dw_repeats_delete_word_right() {
        let mut m = ViKeyMap::starting_normal();
        m.translate(ev(KeyCode::Char('2')));
        m.translate(ev(KeyCode::Char('d')));
        let actions = m.translate(ev(KeyCode::Char('w')));
        assert_eq!(actions, vec![Action::DeleteWordRight, Action::DeleteWordRight]);
    }

    #[test]
    fn multidigit_count() {
        let mut m = ViKeyMap::starting_normal();
        // "12l" → MoveRight x 12
        m.translate(ev(KeyCode::Char('1')));
        m.translate(ev(KeyCode::Char('2')));
        let actions = m.translate(ev(KeyCode::Char('l')));
        assert_eq!(actions.len(), 12);
        assert!(actions.iter().all(|a| *a == Action::MoveRight));
    }

    #[test]
    fn bare_zero_is_motion_not_count() {
        let mut m = ViKeyMap::starting_normal();
        // "0" with no in-progress count is MoveHome.
        assert_eq!(m.translate(ev(KeyCode::Char('0'))), vec![Action::MoveHome]);
    }

    #[test]
    fn zero_after_digit_extends_count() {
        let mut m = ViKeyMap::starting_normal();
        // "10w" → MoveWordRight x 10 (the 0 is part of the count, not MoveHome).
        m.translate(ev(KeyCode::Char('1')));
        m.translate(ev(KeyCode::Char('0')));
        let actions = m.translate(ev(KeyCode::Char('w')));
        assert_eq!(actions.len(), 10);
        assert!(actions.iter().all(|a| *a == Action::MoveWordRight));
    }

    #[test]
    fn yank_word_compound() {
        let mut m = ViKeyMap::starting_normal();
        m.translate(ev(KeyCode::Char('y')));
        assert_eq!(m.translate(ev(KeyCode::Char('w'))), vec![Action::YankWordRight]);
    }

    #[test]
    fn yank_yy_yanks_whole_line() {
        let mut m = ViKeyMap::starting_normal();
        m.translate(ev(KeyCode::Char('y')));
        assert_eq!(m.translate(ev(KeyCode::Char('y'))), vec![Action::YankLine]);
    }

    #[test]
    fn find_char_forward() {
        let mut m = ViKeyMap::starting_normal();
        // f x → FindCharForward('x')
        assert_eq!(m.translate(ev(KeyCode::Char('f'))), Vec::<Action>::new());
        assert_eq!(
            m.translate(ev(KeyCode::Char('x'))),
            vec![Action::FindCharForward('x')]
        );
    }

    #[test]
    fn till_char_backward() {
        let mut m = ViKeyMap::starting_normal();
        m.translate(ev(KeyCode::Char('T')));
        assert_eq!(
            m.translate(ev(KeyCode::Char('a'))),
            vec![Action::TillCharBackward('a')]
        );
    }

    #[test]
    fn replace_char() {
        let mut m = ViKeyMap::starting_normal();
        m.translate(ev(KeyCode::Char('r')));
        assert_eq!(
            m.translate(ev(KeyCode::Char('Z'))),
            vec![Action::ReplaceChar('Z')]
        );
    }

    #[test]
    fn tilde_toggles_case() {
        let mut m = ViKeyMap::starting_normal();
        assert_eq!(m.translate(ev(KeyCode::Char('~'))), vec![Action::ToggleCase]);
    }

    #[test]
    fn count_capped_at_thousand() {
        let mut m = ViKeyMap::starting_normal();
        // "9999l" — capped to 1000 to avoid silly memory blowup.
        m.translate(ev(KeyCode::Char('9')));
        m.translate(ev(KeyCode::Char('9')));
        m.translate(ev(KeyCode::Char('9')));
        m.translate(ev(KeyCode::Char('9')));
        let actions = m.translate(ev(KeyCode::Char('l')));
        assert_eq!(actions.len(), 1000);
    }
}

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
}

#[derive(Debug)]
pub struct ViKeyMap {
    mode: ViMode,
    pending: Pending,
}

impl Default for ViKeyMap {
    fn default() -> Self {
        Self::new()
    }
}

impl ViKeyMap {
    pub fn new() -> Self {
        Self { mode: ViMode::Insert, pending: Pending::None }
    }

    /// Construct starting in Normal mode (less common; usually you
    /// want a fresh prompt to start in Insert).
    pub fn starting_normal() -> Self {
        Self { mode: ViMode::Normal, pending: Pending::None }
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
            _ => Vec::new(),
        }
    }

    fn translate_normal(&mut self, event: KeyEvent) -> Vec<Action> {
        let KeyEvent { code, modifiers, .. } = event;
        let mods = modifiers - KeyModifiers::SHIFT;

        // Operator-pending: previous keystroke was `d` or `c`. The
        // current keystroke is interpreted as a motion or a same-key
        // trigger (`dd`, `cc`).
        if self.pending != Pending::None {
            let pending = self.pending;
            self.pending = Pending::None;
            return self.translate_operator_motion(pending, code, mods);
        }

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
            (KeyCode::Char('x'), KeyModifiers::NONE) => one(Action::DeleteRight),
            (KeyCode::Char('X'), KeyModifiers::NONE) => one(Action::DeleteLeft),
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
        // Same-key double press → operate on whole line: dd, cc.
        let same_key = match (pending, code) {
            (Pending::Delete, KeyCode::Char('d')) => true,
            (Pending::Change, KeyCode::Char('c')) => true,
            _ => false,
        };
        if same_key {
            let mut actions = vec![Action::DeleteLine];
            if pending == Pending::Change {
                self.mode = ViMode::Insert;
                actions.push(Action::EnterInsertMode);
            }
            return actions;
        }

        // Map the motion to an edit. Word motions become word-deletes,
        // line-end motions become kill-to-end / kill-to-start.
        let motion_action = match (code, mods) {
            (KeyCode::Char('w'), KeyModifiers::NONE) => Some(Action::DeleteWordRight),
            (KeyCode::Char('b'), KeyModifiers::NONE) => Some(Action::DeleteWordLeft),
            (KeyCode::Char('$'), KeyModifiers::NONE) | (KeyCode::End, KeyModifiers::NONE) => {
                Some(Action::KillToEnd)
            }
            (KeyCode::Char('0'), KeyModifiers::NONE) | (KeyCode::Home, KeyModifiers::NONE) => {
                Some(Action::KillToStart)
            }
            // Esc cancels operator-pending — return to plain Normal.
            (KeyCode::Esc, KeyModifiers::NONE) => return Vec::new(),
            _ => None,
        };

        let Some(motion_action) = motion_action else {
            return Vec::new();
        };

        let mut actions = vec![motion_action];
        if pending == Pending::Change {
            self.mode = ViMode::Insert;
            actions.push(Action::EnterInsertMode);
        }
        actions
    }
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
}

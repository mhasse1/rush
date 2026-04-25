//! Key-event → `Action` translation.
//!
//! [`Action`] is the small alphabet of things the engine can do in
//! response to user input: edit the buffer, move the cursor, navigate
//! history, submit, cancel. Keymaps own the policy of "which keys map
//! to which actions"; the engine owns the policy of "how to apply
//! each action."
//!
//! Keymaps are pluggable via the [`KeyMap`] trait. We ship
//! [`EmacsKeyMap`] as the default; vi mode lands in a later phase.

use crossterm::event::{KeyCode, KeyEvent, KeyModifiers};

/// Things the engine knows how to do. Each variant is one indivisible
/// edit/navigation/mode operation. Vi compound commands (e.g. `a` =
/// "move right then enter insert") return multiple actions from a
/// single keystroke — see [`KeyMap::translate`].
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Action {
    // Insertion
    InsertChar(char),

    // Deletion
    DeleteLeft,
    DeleteRight,
    DeleteWordLeft,
    DeleteWordRight,
    KillToEnd,
    KillToStart,
    /// Delete the entire buffer (vi `dd` / `cc`).
    DeleteLine,

    // Cursor movement
    MoveLeft,
    MoveRight,
    MoveWordLeft,
    MoveWordRight,
    MoveHome,
    MoveEnd,

    // History
    HistoryPrev,
    HistoryNext,

    // Submission / signals
    Submit,
    Cancel,         // Ctrl-C
    EndOfInput,     // Ctrl-D on empty buffer; engine decides

    // Display
    Clear,          // Ctrl-L

    // Mode signals (vi). The engine updates the cursor shape; the
    // keymap is responsible for tracking which mode it's in.
    EnterInsertMode,
    EnterNormalMode,

    /// Tab completion. Engine asks the registered completer for
    /// suggestions and applies the bash-style "complete to common
    /// prefix; subsequent Tabs cycle" policy. No-op if no completer
    /// is registered.
    Complete,
}

pub trait KeyMap {
    /// Translate a key event into a sequence of `Action`s. Returns
    /// an empty vec if the keymap has no binding for this event, or
    /// if the keystroke advanced an internal state machine without
    /// completing a command (e.g. vi's `d` waiting for a motion).
    fn translate(&mut self, event: KeyEvent) -> Vec<Action>;
}

/// Default emacs-style bindings.
///
/// | Key                       | Action            |
/// |---------------------------|-------------------|
/// | printable char            | `InsertChar`      |
/// | Backspace, Ctrl-H         | `DeleteLeft`      |
/// | Delete, Ctrl-D (non-empty)| `DeleteRight`     |
/// | Ctrl-D (empty buffer)     | `EndOfInput`      |
/// | Alt-Backspace, Ctrl-W     | `DeleteWordLeft`  |
/// | Alt-D                     | `DeleteWordRight` |
/// | Ctrl-K                    | `KillToEnd`       |
/// | Ctrl-U                    | `KillToStart`     |
/// | Left, Ctrl-B              | `MoveLeft`        |
/// | Right, Ctrl-F             | `MoveRight`       |
/// | Alt-B                     | `MoveWordLeft`    |
/// | Alt-F                     | `MoveWordRight`   |
/// | Home, Ctrl-A              | `MoveHome`        |
/// | End, Ctrl-E               | `MoveEnd`         |
/// | Up, Ctrl-P                | `HistoryPrev`     |
/// | Down, Ctrl-N              | `HistoryNext`     |
/// | Enter, Ctrl-J, Ctrl-M     | `Submit`          |
/// | Ctrl-C                    | `Cancel`          |
/// | Ctrl-L                    | `Clear`           |
///
/// `Ctrl-D` on an empty buffer is `EndOfInput`; on a non-empty buffer
/// it falls through to `DeleteRight`. The keymap can't see the buffer
/// so it always returns `EndOfInput` for Ctrl-D — the engine decides
/// which one applies based on buffer state.
#[derive(Debug, Default, Clone, Copy)]
pub struct EmacsKeyMap;

impl KeyMap for EmacsKeyMap {
    fn translate(&mut self, event: KeyEvent) -> Vec<Action> {
        let KeyEvent { code, modifiers, .. } = event;

        // Strip SHIFT for printable keys — uppercase letters arrive
        // as KeyCode::Char('A') with KeyModifiers::SHIFT, and we want
        // the SHIFT to be invisible to our match arms below. Keep CTRL
        // and ALT bits as-is.
        let mods = modifiers - KeyModifiers::SHIFT;

        let one = |a: Action| vec![a];
        match (code, mods) {
            // ---- printable insertion ----
            (KeyCode::Char(c), KeyModifiers::NONE) => one(Action::InsertChar(c)),

            // ---- deletion ----
            (KeyCode::Backspace, KeyModifiers::NONE) => one(Action::DeleteLeft),
            (KeyCode::Char('h'), KeyModifiers::CONTROL) => one(Action::DeleteLeft),
            (KeyCode::Delete, KeyModifiers::NONE) => one(Action::DeleteRight),
            (KeyCode::Char('d'), KeyModifiers::CONTROL) => one(Action::EndOfInput),
            (KeyCode::Backspace, KeyModifiers::ALT) => one(Action::DeleteWordLeft),
            (KeyCode::Char('w'), KeyModifiers::CONTROL) => one(Action::DeleteWordLeft),
            (KeyCode::Char('d'), KeyModifiers::ALT) => one(Action::DeleteWordRight),
            (KeyCode::Char('k'), KeyModifiers::CONTROL) => one(Action::KillToEnd),
            (KeyCode::Char('u'), KeyModifiers::CONTROL) => one(Action::KillToStart),

            // ---- movement ----
            (KeyCode::Left, KeyModifiers::NONE) => one(Action::MoveLeft),
            (KeyCode::Char('b'), KeyModifiers::CONTROL) => one(Action::MoveLeft),
            (KeyCode::Right, KeyModifiers::NONE) => one(Action::MoveRight),
            (KeyCode::Char('f'), KeyModifiers::CONTROL) => one(Action::MoveRight),
            (KeyCode::Char('b'), KeyModifiers::ALT) => one(Action::MoveWordLeft),
            (KeyCode::Left, KeyModifiers::ALT) => one(Action::MoveWordLeft),
            (KeyCode::Char('f'), KeyModifiers::ALT) => one(Action::MoveWordRight),
            (KeyCode::Right, KeyModifiers::ALT) => one(Action::MoveWordRight),
            (KeyCode::Home, KeyModifiers::NONE) => one(Action::MoveHome),
            (KeyCode::Char('a'), KeyModifiers::CONTROL) => one(Action::MoveHome),
            (KeyCode::End, KeyModifiers::NONE) => one(Action::MoveEnd),
            (KeyCode::Char('e'), KeyModifiers::CONTROL) => one(Action::MoveEnd),

            // ---- history ----
            (KeyCode::Up, KeyModifiers::NONE) => one(Action::HistoryPrev),
            (KeyCode::Char('p'), KeyModifiers::CONTROL) => one(Action::HistoryPrev),
            (KeyCode::Down, KeyModifiers::NONE) => one(Action::HistoryNext),
            (KeyCode::Char('n'), KeyModifiers::CONTROL) => one(Action::HistoryNext),

            // ---- submission / signals ----
            (KeyCode::Enter, KeyModifiers::NONE) => one(Action::Submit),
            (KeyCode::Char('j'), KeyModifiers::CONTROL) => one(Action::Submit),
            (KeyCode::Char('m'), KeyModifiers::CONTROL) => one(Action::Submit),
            (KeyCode::Char('c'), KeyModifiers::CONTROL) => one(Action::Cancel),

            // ---- display ----
            (KeyCode::Char('l'), KeyModifiers::CONTROL) => one(Action::Clear),

            // ---- completion ----
            (KeyCode::Tab, KeyModifiers::NONE) => one(Action::Complete),

            _ => Vec::new(),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn ev(code: KeyCode, modifiers: KeyModifiers) -> KeyEvent {
        KeyEvent::new(code, modifiers)
    }

    fn one(m: &mut dyn KeyMap, code: KeyCode, modifiers: KeyModifiers) -> Vec<Action> {
        m.translate(ev(code, modifiers))
    }

    #[test]
    fn printable_char_inserts() {
        let mut m = EmacsKeyMap;
        assert_eq!(
            one(&mut m, KeyCode::Char('a'), KeyModifiers::NONE),
            vec![Action::InsertChar('a')]
        );
    }

    #[test]
    fn shift_modifier_is_invisible_to_keymap() {
        let mut m = EmacsKeyMap;
        assert_eq!(
            one(&mut m, KeyCode::Char('A'), KeyModifiers::SHIFT),
            vec![Action::InsertChar('A')]
        );
    }

    #[test]
    fn enter_submits() {
        let mut m = EmacsKeyMap;
        assert_eq!(one(&mut m, KeyCode::Enter, KeyModifiers::NONE), vec![Action::Submit]);
    }

    #[test]
    fn ctrl_c_cancels() {
        let mut m = EmacsKeyMap;
        assert_eq!(
            one(&mut m, KeyCode::Char('c'), KeyModifiers::CONTROL),
            vec![Action::Cancel]
        );
    }

    #[test]
    fn ctrl_d_translates_to_end_of_input_keymap_does_not_inspect_buffer() {
        let mut m = EmacsKeyMap;
        assert_eq!(
            one(&mut m, KeyCode::Char('d'), KeyModifiers::CONTROL),
            vec![Action::EndOfInput]
        );
    }

    #[test]
    fn backspace_and_ctrl_h_both_delete_left() {
        let mut m = EmacsKeyMap;
        assert_eq!(
            one(&mut m, KeyCode::Backspace, KeyModifiers::NONE),
            vec![Action::DeleteLeft]
        );
        assert_eq!(
            one(&mut m, KeyCode::Char('h'), KeyModifiers::CONTROL),
            vec![Action::DeleteLeft]
        );
    }

    #[test]
    fn arrow_keys_and_emacs_motion_keys_agree() {
        let mut m = EmacsKeyMap;
        assert_eq!(one(&mut m, KeyCode::Left, KeyModifiers::NONE), vec![Action::MoveLeft]);
        assert_eq!(
            one(&mut m, KeyCode::Char('b'), KeyModifiers::CONTROL),
            vec![Action::MoveLeft]
        );
        assert_eq!(one(&mut m, KeyCode::Right, KeyModifiers::NONE), vec![Action::MoveRight]);
        assert_eq!(
            one(&mut m, KeyCode::Char('f'), KeyModifiers::CONTROL),
            vec![Action::MoveRight]
        );
    }

    #[test]
    fn alt_motion_is_word_wise() {
        let mut m = EmacsKeyMap;
        assert_eq!(
            one(&mut m, KeyCode::Char('b'), KeyModifiers::ALT),
            vec![Action::MoveWordLeft]
        );
        assert_eq!(
            one(&mut m, KeyCode::Char('f'), KeyModifiers::ALT),
            vec![Action::MoveWordRight]
        );
    }

    #[test]
    fn home_end_keys() {
        let mut m = EmacsKeyMap;
        assert_eq!(one(&mut m, KeyCode::Home, KeyModifiers::NONE), vec![Action::MoveHome]);
        assert_eq!(one(&mut m, KeyCode::End, KeyModifiers::NONE), vec![Action::MoveEnd]);
        assert_eq!(
            one(&mut m, KeyCode::Char('a'), KeyModifiers::CONTROL),
            vec![Action::MoveHome]
        );
        assert_eq!(
            one(&mut m, KeyCode::Char('e'), KeyModifiers::CONTROL),
            vec![Action::MoveEnd]
        );
    }

    #[test]
    fn history_keys() {
        let mut m = EmacsKeyMap;
        assert_eq!(
            one(&mut m, KeyCode::Up, KeyModifiers::NONE),
            vec![Action::HistoryPrev]
        );
        assert_eq!(
            one(&mut m, KeyCode::Down, KeyModifiers::NONE),
            vec![Action::HistoryNext]
        );
    }

    #[test]
    fn kill_keys() {
        let mut m = EmacsKeyMap;
        assert_eq!(
            one(&mut m, KeyCode::Char('k'), KeyModifiers::CONTROL),
            vec![Action::KillToEnd]
        );
        assert_eq!(
            one(&mut m, KeyCode::Char('u'), KeyModifiers::CONTROL),
            vec![Action::KillToStart]
        );
        assert_eq!(
            one(&mut m, KeyCode::Char('w'), KeyModifiers::CONTROL),
            vec![Action::DeleteWordLeft]
        );
    }

    #[test]
    fn unknown_keybinding_returns_empty_vec() {
        let mut m = EmacsKeyMap;
        assert_eq!(one(&mut m, KeyCode::F(1), KeyModifiers::NONE), Vec::<Action>::new());
    }
}

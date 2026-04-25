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
/// edit/navigation operation. Compound operations (e.g. "transpose
/// chars") would compose these in the engine.
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

    // Catch-all for events the keymap doesn't handle.
    // The engine ignores these; keymaps return `None` to mean
    // "this isn't a known binding."
}

pub trait KeyMap: Send {
    /// Translate a key event to an `Action`. Returns `None` if the
    /// keymap has no binding for this event — the engine ignores it.
    fn translate(&self, event: KeyEvent) -> Option<Action>;
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
    fn translate(&self, event: KeyEvent) -> Option<Action> {
        let KeyEvent { code, modifiers, .. } = event;

        // Strip SHIFT for printable keys — uppercase letters arrive
        // as KeyCode::Char('A') with KeyModifiers::SHIFT, and we want
        // the SHIFT to be invisible to our match arms below. Keep CTRL
        // and ALT bits as-is.
        let mods = modifiers - KeyModifiers::SHIFT;

        match (code, mods) {
            // ---- printable insertion ----
            (KeyCode::Char(c), KeyModifiers::NONE) => Some(Action::InsertChar(c)),

            // ---- deletion ----
            (KeyCode::Backspace, KeyModifiers::NONE) => Some(Action::DeleteLeft),
            (KeyCode::Char('h'), KeyModifiers::CONTROL) => Some(Action::DeleteLeft),
            (KeyCode::Delete, KeyModifiers::NONE) => Some(Action::DeleteRight),
            (KeyCode::Char('d'), KeyModifiers::CONTROL) => Some(Action::EndOfInput),
            (KeyCode::Backspace, KeyModifiers::ALT) => Some(Action::DeleteWordLeft),
            (KeyCode::Char('w'), KeyModifiers::CONTROL) => Some(Action::DeleteWordLeft),
            (KeyCode::Char('d'), KeyModifiers::ALT) => Some(Action::DeleteWordRight),
            (KeyCode::Char('k'), KeyModifiers::CONTROL) => Some(Action::KillToEnd),
            (KeyCode::Char('u'), KeyModifiers::CONTROL) => Some(Action::KillToStart),

            // ---- movement ----
            (KeyCode::Left, KeyModifiers::NONE) => Some(Action::MoveLeft),
            (KeyCode::Char('b'), KeyModifiers::CONTROL) => Some(Action::MoveLeft),
            (KeyCode::Right, KeyModifiers::NONE) => Some(Action::MoveRight),
            (KeyCode::Char('f'), KeyModifiers::CONTROL) => Some(Action::MoveRight),
            (KeyCode::Char('b'), KeyModifiers::ALT) => Some(Action::MoveWordLeft),
            (KeyCode::Left, KeyModifiers::ALT) => Some(Action::MoveWordLeft),
            (KeyCode::Char('f'), KeyModifiers::ALT) => Some(Action::MoveWordRight),
            (KeyCode::Right, KeyModifiers::ALT) => Some(Action::MoveWordRight),
            (KeyCode::Home, KeyModifiers::NONE) => Some(Action::MoveHome),
            (KeyCode::Char('a'), KeyModifiers::CONTROL) => Some(Action::MoveHome),
            (KeyCode::End, KeyModifiers::NONE) => Some(Action::MoveEnd),
            (KeyCode::Char('e'), KeyModifiers::CONTROL) => Some(Action::MoveEnd),

            // ---- history ----
            (KeyCode::Up, KeyModifiers::NONE) => Some(Action::HistoryPrev),
            (KeyCode::Char('p'), KeyModifiers::CONTROL) => Some(Action::HistoryPrev),
            (KeyCode::Down, KeyModifiers::NONE) => Some(Action::HistoryNext),
            (KeyCode::Char('n'), KeyModifiers::CONTROL) => Some(Action::HistoryNext),

            // ---- submission / signals ----
            (KeyCode::Enter, KeyModifiers::NONE) => Some(Action::Submit),
            (KeyCode::Char('j'), KeyModifiers::CONTROL) => Some(Action::Submit),
            (KeyCode::Char('m'), KeyModifiers::CONTROL) => Some(Action::Submit),
            (KeyCode::Char('c'), KeyModifiers::CONTROL) => Some(Action::Cancel),

            // ---- display ----
            (KeyCode::Char('l'), KeyModifiers::CONTROL) => Some(Action::Clear),

            _ => None,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn ev(code: KeyCode, modifiers: KeyModifiers) -> KeyEvent {
        KeyEvent::new(code, modifiers)
    }

    #[test]
    fn printable_char_inserts() {
        let m = EmacsKeyMap;
        assert_eq!(
            m.translate(ev(KeyCode::Char('a'), KeyModifiers::NONE)),
            Some(Action::InsertChar('a'))
        );
    }

    #[test]
    fn shift_modifier_is_invisible_to_keymap() {
        // Uppercase 'A' arrives with SHIFT; we treat it as plain insert.
        let m = EmacsKeyMap;
        assert_eq!(
            m.translate(ev(KeyCode::Char('A'), KeyModifiers::SHIFT)),
            Some(Action::InsertChar('A'))
        );
    }

    #[test]
    fn enter_submits() {
        let m = EmacsKeyMap;
        assert_eq!(
            m.translate(ev(KeyCode::Enter, KeyModifiers::NONE)),
            Some(Action::Submit)
        );
    }

    #[test]
    fn ctrl_c_cancels() {
        let m = EmacsKeyMap;
        assert_eq!(
            m.translate(ev(KeyCode::Char('c'), KeyModifiers::CONTROL)),
            Some(Action::Cancel)
        );
    }

    #[test]
    fn ctrl_d_translates_to_end_of_input_keymap_does_not_inspect_buffer() {
        // The keymap unconditionally maps Ctrl-D to EndOfInput; the engine
        // turns it into DeleteRight when the buffer is non-empty.
        let m = EmacsKeyMap;
        assert_eq!(
            m.translate(ev(KeyCode::Char('d'), KeyModifiers::CONTROL)),
            Some(Action::EndOfInput)
        );
    }

    #[test]
    fn backspace_and_ctrl_h_both_delete_left() {
        let m = EmacsKeyMap;
        assert_eq!(
            m.translate(ev(KeyCode::Backspace, KeyModifiers::NONE)),
            Some(Action::DeleteLeft)
        );
        assert_eq!(
            m.translate(ev(KeyCode::Char('h'), KeyModifiers::CONTROL)),
            Some(Action::DeleteLeft)
        );
    }

    #[test]
    fn arrow_keys_and_emacs_motion_keys_agree() {
        let m = EmacsKeyMap;
        assert_eq!(
            m.translate(ev(KeyCode::Left, KeyModifiers::NONE)),
            Some(Action::MoveLeft)
        );
        assert_eq!(
            m.translate(ev(KeyCode::Char('b'), KeyModifiers::CONTROL)),
            Some(Action::MoveLeft)
        );
        assert_eq!(
            m.translate(ev(KeyCode::Right, KeyModifiers::NONE)),
            Some(Action::MoveRight)
        );
        assert_eq!(
            m.translate(ev(KeyCode::Char('f'), KeyModifiers::CONTROL)),
            Some(Action::MoveRight)
        );
    }

    #[test]
    fn alt_motion_is_word_wise() {
        let m = EmacsKeyMap;
        assert_eq!(
            m.translate(ev(KeyCode::Char('b'), KeyModifiers::ALT)),
            Some(Action::MoveWordLeft)
        );
        assert_eq!(
            m.translate(ev(KeyCode::Char('f'), KeyModifiers::ALT)),
            Some(Action::MoveWordRight)
        );
    }

    #[test]
    fn home_end_keys() {
        let m = EmacsKeyMap;
        assert_eq!(
            m.translate(ev(KeyCode::Home, KeyModifiers::NONE)),
            Some(Action::MoveHome)
        );
        assert_eq!(
            m.translate(ev(KeyCode::End, KeyModifiers::NONE)),
            Some(Action::MoveEnd)
        );
        assert_eq!(
            m.translate(ev(KeyCode::Char('a'), KeyModifiers::CONTROL)),
            Some(Action::MoveHome)
        );
        assert_eq!(
            m.translate(ev(KeyCode::Char('e'), KeyModifiers::CONTROL)),
            Some(Action::MoveEnd)
        );
    }

    #[test]
    fn history_keys() {
        let m = EmacsKeyMap;
        assert_eq!(
            m.translate(ev(KeyCode::Up, KeyModifiers::NONE)),
            Some(Action::HistoryPrev)
        );
        assert_eq!(
            m.translate(ev(KeyCode::Down, KeyModifiers::NONE)),
            Some(Action::HistoryNext)
        );
    }

    #[test]
    fn kill_keys() {
        let m = EmacsKeyMap;
        assert_eq!(
            m.translate(ev(KeyCode::Char('k'), KeyModifiers::CONTROL)),
            Some(Action::KillToEnd)
        );
        assert_eq!(
            m.translate(ev(KeyCode::Char('u'), KeyModifiers::CONTROL)),
            Some(Action::KillToStart)
        );
        assert_eq!(
            m.translate(ev(KeyCode::Char('w'), KeyModifiers::CONTROL)),
            Some(Action::DeleteWordLeft)
        );
    }

    #[test]
    fn unknown_keybinding_returns_none() {
        let m = EmacsKeyMap;
        // F-keys aren't bound by default.
        assert_eq!(m.translate(ev(KeyCode::F(1), KeyModifiers::NONE)), None);
    }
}

//! `rush-line` — line editor for Rush.
//!
//! ## Philosophy
//!
//! Cursor-relative painting, modeled on GNU readline's `display.c`. There is
//! no concept of "the absolute terminal row where the prompt lives" anywhere
//! in this crate. The painter knows two things:
//!
//! - how many rows are above the terminal cursor inside our paint area
//! - how many rows the previous paint emitted in total
//!
//! Every redraw walks relative-up to the top of the previous paint, clears
//! the rows we own (and only those rows), emits fresh content sequentially,
//! then walks back to the editor cursor position. The terminal scrolls
//! naturally if the new content runs off the bottom — that's bash's behavior
//! and we don't try to second-guess it.
//!
//! Why? rushline (forked from reedline) tracked `prompt_start_row` as an
//! absolute terminal row, derived `MoveTo(_, row)` and `Clear(FromCursorDown)`
//! from it, and re-anchored via `cursor::position()` queries that turned out
//! to be unreliable under keystroke pressure. The drift hazards are
//! impossible to express away from that design without rewriting it. So
//! we did.
//!
//! ## Status
//!
//! Phase 1: painter primitive (this commit). The full line-editing engine
//! (buffer + edit operations, keymaps, history, completion, hint, read_line
//! loop) is built up in subsequent phases on top of this primitive.

pub mod buffer;
pub mod completion;
#[cfg(unix)]
pub mod decoder;
pub mod engine;
pub mod highlighter;
pub mod hint;
pub mod history;
pub mod keymap;
pub mod layout;
pub mod painter;
#[cfg(unix)]
pub mod tty;
pub mod validator;
pub mod vi;

pub use buffer::LineBuffer;
pub use completion::{Completer, Span, Suggestion};
pub use engine::{LineEditor, Prompt, Signal};
pub use highlighter::Highlighter;
pub use history::{FileBackedHistory, History};
pub use keymap::{Action, EmacsKeyMap, KeyMap};
pub use painter::Painter;
pub use validator::{AlwaysComplete, ValidationResult, Validator};
pub use vi::{ViKeyMap, ViMode};

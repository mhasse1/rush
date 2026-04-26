//! Adapter that turns the byte-level [`RawTty`] + [`Decoder`] pair into
//! a stream of crossterm-shaped `Event`s for the engine to consume.
//!
//! Phase D of the #282 root-cause fix. Replaces `crossterm::event::read`
//! on Unix while keeping the engine's match arms (`Event::Key`,
//! `Event::Resize`, `Event::Paste`) shape-compatible — so the rest of
//! `engine.rs` doesn't need a parallel dispatch path.
//!
//! Three concerns live here:
//!
//! 1. **Raw mode lifecycle.** [`UnixInput::enter`] takes the terminal
//!    via [`RawTty::enter`]; Drop restores. Single owner per session.
//!
//! 2. **Esc disambiguation.** When the decoder is mid-Esc-sequence and
//!    no follow-up byte arrives within ~50 ms, we flush the decoder so
//!    a lone Esc commits as a keypress. Any byte that arrives in the
//!    window goes through the decoder normally; Alt-x, arrow keys,
//!    function keys all stay intact.
//!
//! 3. **Translation.** Our [`crate::decoder::Event`] is shape-compatible
//!    with `crossterm::event::Event` but uses our own types. We
//!    translate at the boundary so engine.rs sees crossterm types and
//!    dispatch logic stays unchanged.

use std::collections::VecDeque;
use std::io;
use std::time::Duration;

use crossterm::event::{Event, KeyCode, KeyEvent, KeyEventKind, KeyEventState, KeyModifiers};

use crate::decoder::{
    Decoder, Event as MyEvent, KeyCode as MyKeyCode, KeyEvent as MyKeyEvent, KeyMods,
};
use crate::tty::{RawByte, RawTty};

/// Maximum wait between the leading Esc byte and a possible follow-up
/// before we commit Esc as a standalone keypress. 50 ms is the
/// readline / xterm convention; long enough that a paste of an Esc
/// sequence reliably makes it through, short enough that a deliberate
/// Esc tap feels instantaneous.
const ESC_TIMEOUT: Duration = Duration::from_millis(50);

pub struct UnixInput {
    tty: RawTty,
    decoder: Decoder,
    /// Already-decoded events waiting to be drained by the engine.
    queue: VecDeque<Event>,
}

impl UnixInput {
    /// Take the controlling tty into raw mode and initialize the
    /// decoder. Returns an error if stdin isn't a tty.
    pub fn enter() -> io::Result<Self> {
        Ok(Self {
            tty: RawTty::enter()?,
            decoder: Decoder::new(),
            queue: VecDeque::new(),
        })
    }

    /// Block until at least one event is available, then return it.
    /// Mirrors `crossterm::event::read()` for the engine's perspective.
    pub fn next_event(&mut self) -> io::Result<Event> {
        loop {
            if let Some(evt) = self.queue.pop_front() {
                return Ok(evt);
            }
            self.fill_queue()?;
        }
    }

    fn fill_queue(&mut self) -> io::Result<()> {
        // Esc disambiguation: if the decoder is mid-Esc and stdin has no
        // bytes ready within the timeout, flush so the lone Esc commits.
        if self.decoder.is_pending() && !self.tty.wait_input(ESC_TIMEOUT)? {
            for ev in self.decoder.flush() {
                push_translated(&mut self.queue, ev);
            }
            return Ok(());
        }

        // Otherwise read a byte (or signal-driven event) and feed.
        match self.tty.read_byte()? {
            RawByte::Byte(b) => {
                for ev in self.decoder.feed(b) {
                    push_translated(&mut self.queue, ev);
                }
            }
            RawByte::Resize => {
                // Look up new terminal size for the engine. crossterm's
                // terminal::size() is just a TIOCGWINSZ ioctl — no
                // event-loop coupling — so we keep using it here.
                if let Ok((w, h)) = crossterm::terminal::size() {
                    self.queue.push_back(Event::Resize(w, h));
                } else {
                    // Best-effort fallback so the engine still gets
                    // a Resize signal it can handle.
                    self.queue.push_back(Event::Resize(80, 24));
                }
            }
            RawByte::Eof => {
                return Err(io::Error::new(
                    io::ErrorKind::UnexpectedEof,
                    "stdin closed (controlling pty destroyed or signal received)",
                ));
            }
        }
        Ok(())
    }
}

fn push_translated(queue: &mut VecDeque<Event>, ev: MyEvent) {
    match ev {
        MyEvent::Key(k) => queue.push_back(Event::Key(translate_key(k))),
        MyEvent::Paste(s) => queue.push_back(Event::Paste(s)),
        MyEvent::Resize => {
            if let Ok((w, h)) = crossterm::terminal::size() {
                queue.push_back(Event::Resize(w, h));
            }
        }
    }
}

fn translate_key(k: MyKeyEvent) -> KeyEvent {
    let code = match k.code {
        MyKeyCode::Char(c) => KeyCode::Char(c),
        MyKeyCode::Backspace => KeyCode::Backspace,
        MyKeyCode::Enter => KeyCode::Enter,
        MyKeyCode::Tab => KeyCode::Tab,
        MyKeyCode::BackTab => KeyCode::BackTab,
        MyKeyCode::Esc => KeyCode::Esc,
        MyKeyCode::Up => KeyCode::Up,
        MyKeyCode::Down => KeyCode::Down,
        MyKeyCode::Left => KeyCode::Left,
        MyKeyCode::Right => KeyCode::Right,
        MyKeyCode::Home => KeyCode::Home,
        MyKeyCode::End => KeyCode::End,
        MyKeyCode::PageUp => KeyCode::PageUp,
        MyKeyCode::PageDown => KeyCode::PageDown,
        MyKeyCode::Insert => KeyCode::Insert,
        MyKeyCode::Delete => KeyCode::Delete,
        MyKeyCode::F(n) => KeyCode::F(n),
    };
    KeyEvent {
        code,
        modifiers: translate_mods(k.mods),
        kind: KeyEventKind::Press,
        state: KeyEventState::NONE,
    }
}

fn translate_mods(m: KeyMods) -> KeyModifiers {
    let mut out = KeyModifiers::empty();
    if m.ctrl {
        out |= KeyModifiers::CONTROL;
    }
    if m.alt {
        out |= KeyModifiers::ALT;
    }
    if m.shift {
        out |= KeyModifiers::SHIFT;
    }
    out
}

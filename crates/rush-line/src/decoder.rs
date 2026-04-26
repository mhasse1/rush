//! Byte-stream → key/event decoder.
//!
//! Phase C of the #282 root-cause fix. Consumes raw bytes from
//! [`crate::tty::RawTty::read_byte`] and produces structured key/paste
//! events. Replaces what crossterm's input layer was doing for us.
//!
//! Scope:
//!
//! - ASCII printable, Ctrl-letter, Backspace, Tab, Enter, Esc.
//! - Alt-x via Esc-prefix (caller times out lone Esc; see
//!   [`Decoder::is_pending`] / [`Decoder::flush`]).
//! - CSI sequences: arrows (`\x1b[A`-D), Home/End (`\x1b[H`/F),
//!   `\x1b[1~`-`\x1b[24~` for navigation/F-keys, modifier-encoded
//!   variants (`\x1b[1;5A` = Ctrl-Up), shift-tab (`\x1b[Z`).
//! - SS3 sequences: F1-F4 via `\x1bOP`-S.
//! - Bracketed paste: `\x1b[200~`...`\x1b[201~` collected as one
//!   `Event::Paste(String)`.
//! - UTF-8 multi-byte: 2/3/4-byte sequences assembled into one
//!   `KeyCode::Char(c)`.
//!
//! Out of scope: mouse events (we don't use them), focus events,
//! Kitty-protocol extensions, alternate-screen prompts.

use std::collections::VecDeque;

/// One decoded event from the byte stream.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Event {
    Key(KeyEvent),
    Paste(String),
    /// Surfaced by [`crate::tty::RawByte::Resize`] propagation; the
    /// decoder itself never emits this, but the event-stream API
    /// includes it so callers see one ordered stream.
    Resize,
}

/// Key event with code and modifiers. Mirrors crossterm's shape so the
/// engine can swap between paths with minimal diff.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct KeyEvent {
    pub code: KeyCode,
    pub mods: KeyMods,
}

impl KeyEvent {
    pub fn new(code: KeyCode, mods: KeyMods) -> Self {
        Self { code, mods }
    }
    pub fn plain(code: KeyCode) -> Self {
        Self { code, mods: KeyMods::NONE }
    }
}

/// Logical key. Char carries the decoded Unicode scalar (multi-byte
/// UTF-8 sequences are assembled before emit).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum KeyCode {
    Char(char),
    Backspace,
    Enter,
    Tab,
    BackTab,
    Esc,
    Up,
    Down,
    Left,
    Right,
    Home,
    End,
    PageUp,
    PageDown,
    Insert,
    Delete,
    F(u8),
}

/// Modifier set. Bitfield-shaped struct so we can compose flags
/// ergonomically without a derive_more dep.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct KeyMods {
    pub ctrl: bool,
    pub alt: bool,
    pub shift: bool,
}

impl KeyMods {
    pub const NONE: Self = Self { ctrl: false, alt: false, shift: false };
    pub const CTRL: Self = Self { ctrl: true, alt: false, shift: false };
    pub const ALT: Self = Self { ctrl: false, alt: true, shift: false };
    pub const SHIFT: Self = Self { ctrl: false, alt: false, shift: true };

    pub fn with_ctrl(mut self) -> Self { self.ctrl = true; self }
    pub fn with_alt(mut self) -> Self { self.alt = true; self }
    pub fn with_shift(mut self) -> Self { self.shift = true; self }

    /// Decode an xterm modifier number (`\x1b[1;Nx` → N).
    /// 1=none, 2=Shift, 3=Alt, 4=Alt+Shift, 5=Ctrl, 6=Ctrl+Shift,
    /// 7=Ctrl+Alt, 8=Ctrl+Alt+Shift. Anything else → no mods.
    fn from_xterm(n: u32) -> Self {
        let n = n.saturating_sub(1);
        Self {
            shift: (n & 0b001) != 0,
            alt: (n & 0b010) != 0,
            ctrl: (n & 0b100) != 0,
        }
    }
}

/// Internal decoder state.
enum State {
    Idle,
    /// Saw 0x1B; deciding lone-Esc vs. start-of-sequence.
    AfterEsc,
    /// Saw `\x1b[`; collecting CSI params + intermediates.
    InCsi { params: String },
    /// Saw `\x1bO`; expecting one final byte for SS3 (F1-F4).
    AfterEscO,
    /// Inside bracketed paste; accumulating bytes until `\x1b[201~`.
    /// We keep raw bytes (not String) so multi-byte UTF-8 sequences
    /// that straddle the close-marker watch window aren't lossily
    /// re-encoded mid-flight.
    InPaste { buf: Vec<u8>, tail: Vec<u8> },
    /// Mid-UTF-8 sequence: `expect` more continuation bytes; current
    /// `buf` holds the start byte plus any continuations seen so far.
    Utf8 { buf: Vec<u8>, expect: usize },
}

/// Stream decoder. One per session. Stateful — feed it bytes in order.
pub struct Decoder {
    state: State,
    /// Emitted but not yet drained. Internal so feed() can return a
    /// borrow-friendly Vec.
    out: VecDeque<Event>,
}

impl Default for Decoder {
    fn default() -> Self { Self::new() }
}

impl Decoder {
    pub fn new() -> Self {
        Self {
            state: State::Idle,
            out: VecDeque::new(),
        }
    }

    /// True when the decoder is mid-sequence and waiting for follow-up
    /// bytes. The caller (engine) should give the byte source a short
    /// timeout (~50ms) and call [`Decoder::flush`] if no byte arrives —
    /// that's how lone Esc is disambiguated from Alt-x / arrow / etc.
    pub fn is_pending(&self) -> bool {
        !matches!(self.state, State::Idle)
    }

    /// Force-flush partial state. Call when the byte source has been
    /// idle past the disambiguation timeout. After this, the decoder
    /// is back in Idle.
    ///
    /// Effects:
    /// - `AfterEsc` → emit lone Esc keypress.
    /// - `AfterEscO` → drop (rare; could only happen mid-flight).
    /// - `InCsi` → drop (incomplete sequence we can't interpret).
    /// - `InPaste` → emit accumulated buffer as Paste (paste was
    ///   interrupted; better to surface what we have than lose it).
    /// - `Utf8` → drop (incomplete UTF-8 byte sequence).
    pub fn flush(&mut self) -> Vec<Event> {
        match std::mem::replace(&mut self.state, State::Idle) {
            State::Idle => {}
            State::AfterEsc => {
                self.emit_key(KeyCode::Esc, KeyMods::NONE);
            }
            State::InPaste { mut buf, tail } => {
                // No close marker arrived. Concatenate any in-flight
                // tail bytes onto the body and surface what we have.
                buf.extend_from_slice(&tail);
                self.out
                    .push_back(Event::Paste(String::from_utf8_lossy(&buf).into_owned()));
            }
            _ => {}
        }
        self.drain()
    }

    /// Consume one byte. Returns any complete events emitted.
    pub fn feed(&mut self, b: u8) -> Vec<Event> {
        match std::mem::replace(&mut self.state, State::Idle) {
            State::Idle => self.feed_idle(b),
            State::AfterEsc => self.feed_after_esc(b),
            State::AfterEscO => self.feed_after_esc_o(b),
            State::InCsi { params } => self.feed_in_csi(params, b),
            State::InPaste { buf, tail } => self.feed_in_paste(buf, tail, b),
            State::Utf8 { buf, expect } => self.feed_utf8(buf, expect, b),
        }
        self.drain()
    }

    fn drain(&mut self) -> Vec<Event> {
        self.out.drain(..).collect()
    }

    fn emit_key(&mut self, code: KeyCode, mods: KeyMods) {
        self.out.push_back(Event::Key(KeyEvent::new(code, mods)));
    }

    fn feed_idle(&mut self, b: u8) {
        match b {
            0x1B => self.state = State::AfterEsc,
            0x7F => self.emit_key(KeyCode::Backspace, KeyMods::NONE),
            // Tab — note Ctrl-I is also 0x09, indistinguishable in a
            // raw stream. We surface it as Tab; consumers that want
            // Ctrl-I treat them equivalently.
            0x09 => self.emit_key(KeyCode::Tab, KeyMods::NONE),
            // Enter — both CR (0x0D) and LF (0x0A). Most TTYs send CR
            // in raw mode (we cleared ICRNL), but accept either.
            0x0D | 0x0A => self.emit_key(KeyCode::Enter, KeyMods::NONE),
            // Ctrl-letter range. Map 0x01-0x1A to Ctrl-{a..z}, then
            // pick out the special-case codes that conflict with the
            // navigation handling above. Ctrl-@/0x00 → null char.
            0x00 => self.emit_key(KeyCode::Char(' '), KeyMods::CTRL), // Ctrl-Space
            0x01..=0x1A => {
                let c = (b - 1 + b'a') as char;
                self.emit_key(KeyCode::Char(c), KeyMods::CTRL);
            }
            // 0x1B handled above. Others in 0x1C-0x1F are Ctrl-\, Ctrl-],
            // Ctrl-^, Ctrl-_ — emit as Ctrl-mod chars.
            0x1C..=0x1F => {
                let c = (b + b'@') as char; // 0x1C → '\\' is wrong; use actual mapping
                // Actually: Ctrl-\ = 0x1C, Ctrl-] = 0x1D, Ctrl-^ = 0x1E, Ctrl-_ = 0x1F
                let c = match b {
                    0x1C => '\\',
                    0x1D => ']',
                    0x1E => '^',
                    0x1F => '_',
                    _ => c, // unreachable
                };
                self.emit_key(KeyCode::Char(c), KeyMods::CTRL);
            }
            // ASCII printable.
            0x20..=0x7E => self.emit_key(KeyCode::Char(b as char), KeyMods::NONE),
            // UTF-8 lead byte. 0x80-0xBF here is invalid (continuation
            // without a start) — silently drop. 0xC0-0xF7 are valid
            // multi-byte starts.
            0xC0..=0xDF => self.state = State::Utf8 { buf: vec![b], expect: 1 },
            0xE0..=0xEF => self.state = State::Utf8 { buf: vec![b], expect: 2 },
            0xF0..=0xF7 => self.state = State::Utf8 { buf: vec![b], expect: 3 },
            _ => {} // 0x80-0xBF stray, 0xF8+ invalid — discard
        }
    }

    fn feed_after_esc(&mut self, b: u8) {
        match b {
            // ESC ESC: emit one Esc, restart sequence.
            0x1B => {
                self.emit_key(KeyCode::Esc, KeyMods::NONE);
                self.state = State::AfterEsc;
            }
            b'[' => {
                self.state = State::InCsi { params: String::new() };
            }
            b'O' => self.state = State::AfterEscO,
            // Alt-Backspace.
            0x7F => self.emit_key(KeyCode::Backspace, KeyMods::ALT),
            0x09 => self.emit_key(KeyCode::Tab, KeyMods::ALT),
            0x0D | 0x0A => self.emit_key(KeyCode::Enter, KeyMods::ALT),
            // Alt-Ctrl-letter.
            0x01..=0x1A => {
                let c = (b - 1 + b'a') as char;
                self.emit_key(
                    KeyCode::Char(c),
                    KeyMods::CTRL.with_alt(),
                );
            }
            // Alt-printable.
            0x20..=0x7E => self.emit_key(KeyCode::Char(b as char), KeyMods::ALT),
            // Anything else: drop the Esc and re-feed the byte at Idle
            // so we don't lose it.
            _ => {
                self.emit_key(KeyCode::Esc, KeyMods::NONE);
                self.feed_idle(b);
            }
        }
    }

    fn feed_after_esc_o(&mut self, b: u8) {
        let code = match b {
            b'P' => Some(KeyCode::F(1)),
            b'Q' => Some(KeyCode::F(2)),
            b'R' => Some(KeyCode::F(3)),
            b'S' => Some(KeyCode::F(4)),
            // Some terminals emit \x1bOH / \x1bOF for Home/End.
            b'H' => Some(KeyCode::Home),
            b'F' => Some(KeyCode::End),
            _ => None,
        };
        if let Some(c) = code {
            self.emit_key(c, KeyMods::NONE);
        }
        // Unknown final byte → drop. State is already Idle (we replaced
        // it at the top of feed()).
    }

    fn feed_in_csi(&mut self, mut params: String, b: u8) {
        // Parameter bytes: 0x30-0x3F (digits and `;<=>?`).
        // Final byte: 0x40-0x7E.
        // (We skip intermediates 0x20-0x2F — none of the sequences we
        // care about use them.)
        if (0x30..=0x3F).contains(&b) {
            params.push(b as char);
            self.state = State::InCsi { params };
            return;
        }
        if (0x40..=0x7E).contains(&b) {
            self.dispatch_csi(&params, b);
            // state stays Idle (set by feed() entry).
            return;
        }
        // Out-of-range byte mid-sequence — drop the partial sequence.
    }

    fn dispatch_csi(&mut self, params: &str, final_byte: u8) {
        // Parse params into a Vec<Option<u32>>, separated by `;`.
        let parts: Vec<Option<u32>> = if params.is_empty() {
            Vec::new()
        } else {
            params
                .split(';')
                .map(|s| s.parse::<u32>().ok())
                .collect()
        };

        // Bracketed paste markers come as `\x1b[200~` / `\x1b[201~`.
        if final_byte == b'~' {
            match parts.first().copied().flatten() {
                Some(200) => {
                    self.state = State::InPaste { buf: Vec::new(), tail: Vec::new() };
                    return;
                }
                // 201 outside paste — defensive ignore.
                Some(201) => return,
                _ => {}
            }
        }

        // Modifier from "1;N" form (e.g. \x1b[1;5A = Ctrl-Up).
        let mods = parts
            .get(1)
            .and_then(|p| p.as_ref().copied())
            .map(KeyMods::from_xterm)
            .unwrap_or(KeyMods::NONE);

        let code = match (final_byte, parts.first().copied().flatten()) {
            (b'A', _) => Some(KeyCode::Up),
            (b'B', _) => Some(KeyCode::Down),
            (b'C', _) => Some(KeyCode::Right),
            (b'D', _) => Some(KeyCode::Left),
            (b'H', _) => Some(KeyCode::Home),
            (b'F', _) => Some(KeyCode::End),
            (b'Z', _) => Some(KeyCode::BackTab),
            (b'~', Some(n)) => match n {
                1 | 7 => Some(KeyCode::Home),
                2 => Some(KeyCode::Insert),
                3 => Some(KeyCode::Delete),
                4 | 8 => Some(KeyCode::End),
                5 => Some(KeyCode::PageUp),
                6 => Some(KeyCode::PageDown),
                11 => Some(KeyCode::F(1)),
                12 => Some(KeyCode::F(2)),
                13 => Some(KeyCode::F(3)),
                14 => Some(KeyCode::F(4)),
                15 => Some(KeyCode::F(5)),
                17 => Some(KeyCode::F(6)),
                18 => Some(KeyCode::F(7)),
                19 => Some(KeyCode::F(8)),
                20 => Some(KeyCode::F(9)),
                21 => Some(KeyCode::F(10)),
                23 => Some(KeyCode::F(11)),
                24 => Some(KeyCode::F(12)),
                _ => None,
            },
            _ => None,
        };
        if let Some(c) = code {
            self.emit_key(c, mods);
        }
    }

    fn feed_in_paste(&mut self, mut buf: Vec<u8>, mut tail: Vec<u8>, b: u8) {
        // Watch for `\x1b[201~` (the 6-byte close marker). Keep a small
        // tail buffer; bytes that fall out of the tail are committed
        // to the body. Bytes stay raw until emit so multi-byte UTF-8
        // sequences straddling the watch window aren't lossily decoded
        // mid-flight.
        const CLOSE: &[u8] = b"\x1b[201~";
        tail.push(b);
        if tail.len() > CLOSE.len() {
            let drop_count = tail.len() - CLOSE.len();
            buf.extend(tail.drain(..drop_count));
        }
        if tail == CLOSE {
            self.out
                .push_back(Event::Paste(String::from_utf8_lossy(&buf).into_owned()));
            return;
        }
        self.state = State::InPaste { buf, tail };
    }

    fn feed_utf8(&mut self, mut buf: Vec<u8>, expect: usize, b: u8) {
        // Continuation bytes are 0x80-0xBF. Any other byte means the
        // sequence is malformed — drop it and re-feed the new byte at
        // Idle so we don't lose it.
        if !(0x80..=0xBF).contains(&b) {
            self.feed_idle(b);
            return;
        }
        buf.push(b);
        if buf.len() >= expect + 1 {
            // We have all the bytes — try to decode.
            if let Ok(s) = std::str::from_utf8(&buf) {
                if let Some(c) = s.chars().next() {
                    self.emit_key(KeyCode::Char(c), KeyMods::NONE);
                }
            }
            // state already Idle.
            return;
        }
        self.state = State::Utf8 { buf, expect };
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn feed_all(d: &mut Decoder, bytes: &[u8]) -> Vec<Event> {
        let mut out = Vec::new();
        for &b in bytes {
            out.extend(d.feed(b));
        }
        out
    }

    fn key(code: KeyCode, mods: KeyMods) -> Event {
        Event::Key(KeyEvent::new(code, mods))
    }

    #[test]
    fn ascii_printable() {
        let mut d = Decoder::new();
        let evs = feed_all(&mut d, b"abc");
        assert_eq!(evs, vec![
            key(KeyCode::Char('a'), KeyMods::NONE),
            key(KeyCode::Char('b'), KeyMods::NONE),
            key(KeyCode::Char('c'), KeyMods::NONE),
        ]);
    }

    #[test]
    fn enter_tab_backspace() {
        let mut d = Decoder::new();
        let evs = feed_all(&mut d, &[0x0D, 0x09, 0x7F]);
        assert_eq!(evs, vec![
            key(KeyCode::Enter, KeyMods::NONE),
            key(KeyCode::Tab, KeyMods::NONE),
            key(KeyCode::Backspace, KeyMods::NONE),
        ]);
    }

    #[test]
    fn ctrl_letter() {
        let mut d = Decoder::new();
        // Ctrl-A = 0x01, Ctrl-Z = 0x1A
        let evs = feed_all(&mut d, &[0x01, 0x1A]);
        assert_eq!(evs, vec![
            key(KeyCode::Char('a'), KeyMods::CTRL),
            key(KeyCode::Char('z'), KeyMods::CTRL),
        ]);
    }

    #[test]
    fn ctrl_special() {
        let mut d = Decoder::new();
        // Ctrl-\, Ctrl-], Ctrl-^, Ctrl-_
        let evs = feed_all(&mut d, &[0x1C, 0x1D, 0x1E, 0x1F]);
        assert_eq!(evs, vec![
            key(KeyCode::Char('\\'), KeyMods::CTRL),
            key(KeyCode::Char(']'), KeyMods::CTRL),
            key(KeyCode::Char('^'), KeyMods::CTRL),
            key(KeyCode::Char('_'), KeyMods::CTRL),
        ]);
    }

    #[test]
    fn lone_esc_via_flush() {
        let mut d = Decoder::new();
        let evs = d.feed(0x1B);
        assert!(evs.is_empty(), "Esc held mid-sequence");
        assert!(d.is_pending());
        let evs = d.flush();
        assert_eq!(evs, vec![key(KeyCode::Esc, KeyMods::NONE)]);
        assert!(!d.is_pending());
    }

    #[test]
    fn alt_letter() {
        let mut d = Decoder::new();
        let evs = feed_all(&mut d, b"\x1ba");
        assert_eq!(evs, vec![key(KeyCode::Char('a'), KeyMods::ALT)]);
    }

    #[test]
    fn esc_then_unknown_emits_esc_and_byte() {
        // ESC followed by something we can't fold into Alt-x or a
        // sequence: emit lone Esc, then process the byte normally.
        let mut d = Decoder::new();
        let evs = feed_all(&mut d, &[0x1B, 0x7F]);
        // 0x7F is Backspace; mapped as Alt-Backspace via after_esc path.
        assert_eq!(evs, vec![key(KeyCode::Backspace, KeyMods::ALT)]);
    }

    #[test]
    fn arrow_keys() {
        let mut d = Decoder::new();
        let evs = feed_all(&mut d, b"\x1b[A\x1b[B\x1b[C\x1b[D");
        assert_eq!(evs, vec![
            key(KeyCode::Up, KeyMods::NONE),
            key(KeyCode::Down, KeyMods::NONE),
            key(KeyCode::Right, KeyMods::NONE),
            key(KeyCode::Left, KeyMods::NONE),
        ]);
    }

    #[test]
    fn ctrl_arrow() {
        // \x1b[1;5A = Ctrl-Up
        let mut d = Decoder::new();
        let evs = feed_all(&mut d, b"\x1b[1;5A");
        assert_eq!(evs, vec![key(KeyCode::Up, KeyMods::CTRL)]);
    }

    #[test]
    fn home_end_navigation() {
        let mut d = Decoder::new();
        let evs = feed_all(&mut d, b"\x1b[H\x1b[F\x1b[1~\x1b[4~");
        assert_eq!(evs, vec![
            key(KeyCode::Home, KeyMods::NONE),
            key(KeyCode::End, KeyMods::NONE),
            key(KeyCode::Home, KeyMods::NONE),
            key(KeyCode::End, KeyMods::NONE),
        ]);
    }

    #[test]
    fn delete_pgup_pgdn_insert() {
        let mut d = Decoder::new();
        let evs = feed_all(&mut d, b"\x1b[3~\x1b[5~\x1b[6~\x1b[2~");
        assert_eq!(evs, vec![
            key(KeyCode::Delete, KeyMods::NONE),
            key(KeyCode::PageUp, KeyMods::NONE),
            key(KeyCode::PageDown, KeyMods::NONE),
            key(KeyCode::Insert, KeyMods::NONE),
        ]);
    }

    #[test]
    fn function_keys_ss3_and_csi() {
        let mut d = Decoder::new();
        let evs = feed_all(&mut d, b"\x1bOP\x1bOQ\x1bOR\x1bOS\x1b[15~\x1b[24~");
        assert_eq!(evs, vec![
            key(KeyCode::F(1), KeyMods::NONE),
            key(KeyCode::F(2), KeyMods::NONE),
            key(KeyCode::F(3), KeyMods::NONE),
            key(KeyCode::F(4), KeyMods::NONE),
            key(KeyCode::F(5), KeyMods::NONE),
            key(KeyCode::F(12), KeyMods::NONE),
        ]);
    }

    #[test]
    fn shift_tab() {
        let mut d = Decoder::new();
        let evs = feed_all(&mut d, b"\x1b[Z");
        assert_eq!(evs, vec![key(KeyCode::BackTab, KeyMods::NONE)]);
    }

    #[test]
    fn bracketed_paste() {
        let mut d = Decoder::new();
        let mut input = Vec::new();
        input.extend_from_slice(b"\x1b[200~");
        input.extend_from_slice(b"hello world");
        input.extend_from_slice(b"\x1b[201~");
        let evs = feed_all(&mut d, &input);
        assert_eq!(evs, vec![Event::Paste("hello world".to_string())]);
    }

    #[test]
    fn bracketed_paste_with_newlines_and_unicode() {
        let mut d = Decoder::new();
        let body = "line 1\nline 2\nrush ✓";
        let mut input = Vec::new();
        input.extend_from_slice(b"\x1b[200~");
        input.extend_from_slice(body.as_bytes());
        input.extend_from_slice(b"\x1b[201~");
        let evs = feed_all(&mut d, &input);
        assert_eq!(evs, vec![Event::Paste(body.to_string())]);
    }

    #[test]
    fn utf8_2_byte() {
        // U+00E9 'é' = 0xC3 0xA9
        let mut d = Decoder::new();
        let evs = feed_all(&mut d, &[0xC3, 0xA9]);
        assert_eq!(evs, vec![key(KeyCode::Char('é'), KeyMods::NONE)]);
    }

    #[test]
    fn utf8_3_byte() {
        // U+2713 '✓' = 0xE2 0x9C 0x93
        let mut d = Decoder::new();
        let evs = feed_all(&mut d, &[0xE2, 0x9C, 0x93]);
        assert_eq!(evs, vec![key(KeyCode::Char('✓'), KeyMods::NONE)]);
    }

    #[test]
    fn utf8_4_byte_emoji() {
        // U+1F600 '😀' = 0xF0 0x9F 0x98 0x80
        let mut d = Decoder::new();
        let evs = feed_all(&mut d, &[0xF0, 0x9F, 0x98, 0x80]);
        assert_eq!(evs, vec![key(KeyCode::Char('😀'), KeyMods::NONE)]);
    }

    #[test]
    fn utf8_split_across_feeds() {
        // Bytes arrive one at a time, split across feed() calls.
        let mut d = Decoder::new();
        let evs1 = d.feed(0xE2);
        assert!(evs1.is_empty());
        assert!(d.is_pending());
        let evs2 = d.feed(0x9C);
        assert!(evs2.is_empty());
        let evs3 = d.feed(0x93);
        assert_eq!(evs3, vec![key(KeyCode::Char('✓'), KeyMods::NONE)]);
        assert!(!d.is_pending());
    }

    #[test]
    fn utf8_invalid_continuation_recovers() {
        // Lead byte 0xC3 expects a continuation, but next byte 0x41 ('A')
        // isn't one. Drop the partial UTF-8 and process 'A' normally.
        let mut d = Decoder::new();
        let _ = d.feed(0xC3);
        let evs = d.feed(b'A');
        assert_eq!(evs, vec![key(KeyCode::Char('A'), KeyMods::NONE)]);
    }

    #[test]
    fn esc_esc_emits_esc_then_pending_again() {
        let mut d = Decoder::new();
        let evs1 = d.feed(0x1B);
        assert!(evs1.is_empty());
        let evs2 = d.feed(0x1B);
        assert_eq!(evs2, vec![key(KeyCode::Esc, KeyMods::NONE)]);
        assert!(d.is_pending());
        let evs3 = d.flush();
        assert_eq!(evs3, vec![key(KeyCode::Esc, KeyMods::NONE)]);
    }

    #[test]
    fn ctrl_space() {
        let mut d = Decoder::new();
        let evs = d.feed(0x00);
        assert_eq!(evs, vec![key(KeyCode::Char(' '), KeyMods::CTRL)]);
    }

    #[test]
    fn xterm_modifier_decoding() {
        // Reference table from the docstring.
        assert_eq!(KeyMods::from_xterm(1), KeyMods::NONE);
        assert_eq!(KeyMods::from_xterm(2), KeyMods::SHIFT);
        assert_eq!(KeyMods::from_xterm(3), KeyMods::ALT);
        assert_eq!(KeyMods::from_xterm(5), KeyMods::CTRL);
        assert_eq!(KeyMods::from_xterm(6), KeyMods::CTRL.with_shift());
        assert_eq!(KeyMods::from_xterm(7), KeyMods::CTRL.with_alt());
        assert_eq!(KeyMods::from_xterm(8), KeyMods::CTRL.with_alt().with_shift());
    }

    #[test]
    fn paste_interrupted_flush_still_emits_partial() {
        let mut d = Decoder::new();
        let _ = feed_all(&mut d, b"\x1b[200~partial");
        // No close marker arrived; caller times out and flushes.
        let evs = d.flush();
        assert_eq!(evs, vec![Event::Paste("partial".to_string())]);
    }
}

//! Smoke test for the termios raw-mode + byte-reader primitive.
//!
//! Phase A of the #282 root-cause fix: validates that we can put the
//! terminal into raw mode, read bytes one at a time without crossterm,
//! and restore termios on exit. No decoding yet — we just print each
//! byte's value so you can sanity-check that arrows, Enter, Esc, etc.
//! produce the expected sequences.
//!
//! Run with:
//!     cargo run --example raw_bytes -p rush-line
//!
//! Press `q` (lowercase) to exit cleanly. Ctrl-C also works since we
//! don't trap signals in this example.
//!
//! Sample expected output:
//!   - Arrow up:  27 (0x1b) -> 91 ([) -> 65 (A)
//!   - Enter:     13 (0x0d) on most terminals
//!   - Tab:       9
//!   - Backspace: 127 (0x7f)

#[cfg(unix)]
fn main() -> std::io::Result<()> {
    use rush_line::tty::RawTty;

    println!("raw_bytes demo. Press `q` (lowercase) to exit.");
    println!("Each line shows one byte read from stdin.\r");

    let mut tty = RawTty::enter()?;

    loop {
        match tty.read_byte()? {
            None => {
                // EOF — destroyed pty or stdin closed.
                eprint!("\r\n[eof]\r\n");
                break;
            }
            Some(b'q') => {
                eprint!("\r\n[bye]\r\n");
                break;
            }
            Some(b) => {
                let printable = if (0x20..=0x7E).contains(&b) {
                    format!(" '{}'", b as char)
                } else {
                    String::new()
                };
                eprint!("byte: {b:3} (0x{b:02x}){printable}\r\n");
            }
        }
    }

    drop(tty);
    Ok(())
}

#[cfg(not(unix))]
fn main() {
    eprintln!("raw_bytes demo is Unix-only");
}

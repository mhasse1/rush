//! Termios raw-mode lifecycle + blocking byte reader.
//!
//! Phase A of the #282 root-cause fix: replaces crossterm's mio-based
//! event reader (which busy-loops on a destroyed pty's `EPOLLHUP`) with
//! a direct `read(0, buf, 1)` syscall. Same model bash/readline and
//! zsh/ZLE use: blocking read, signals interrupt cleanly via `EINTR`,
//! `read()=0` surfaces as EOF.
//!
//! This module owns just the byte-level concerns: putting the terminal
//! into raw mode (via `tcgetattr`/`tcsetattr` on stdin) and reading one
//! byte at a time. Signal handling and decoding to key events come in
//! later phases.
//!
//! Unix-only — Windows uses crossterm's input path under `cfg(windows)`.

use std::io;
use std::mem::MaybeUninit;

/// RAII guard for raw-mode terminal state.
///
/// `enter()` reads stdin's current termios via `tcgetattr`, applies a
/// `cfmakeraw`-equivalent mask, and writes it back with `tcsetattr`.
/// Drop restores the original termios so the shell hands a sane terminal
/// back to whatever ran rush (or to the user, on `exit`).
///
/// One guard per process at a time. Constructing two would race on
/// stdin's termios; cheap discipline since rush only enters raw mode
/// from one place (the read_line loop).
pub struct RawTty {
    original: libc::termios,
    fd: libc::c_int,
}

impl RawTty {
    /// Snapshot stdin's current termios, then switch into raw mode.
    /// Errors if stdin isn't a tty (`ENOTTY`) or the ioctl fails for any
    /// other reason — caller can fall back or surface the error.
    pub fn enter() -> io::Result<Self> {
        let fd = libc::STDIN_FILENO;
        let original = unsafe {
            let mut t: MaybeUninit<libc::termios> = MaybeUninit::uninit();
            if libc::tcgetattr(fd, t.as_mut_ptr()) != 0 {
                return Err(io::Error::last_os_error());
            }
            t.assume_init()
        };

        // Build a raw-mode termios from the snapshot. Mirrors what
        // `cfmakeraw(3)` does — but we set it manually because cfmakeraw
        // isn't in libc on every platform we target. Mask:
        //   c_iflag: clear IGNBRK BRKINT PARMRK ISTRIP INLCR IGNCR ICRNL IXON
        //   c_oflag: clear OPOST
        //   c_lflag: clear ECHO ECHONL ICANON ISIG IEXTEN
        //   c_cflag: clear CSIZE PARENB; set CS8
        //   VMIN = 1 (read returns when at least one byte is available)
        //   VTIME = 0 (no inter-byte timer)
        let mut raw = original;
        raw.c_iflag &= !(libc::IGNBRK
            | libc::BRKINT
            | libc::PARMRK
            | libc::ISTRIP
            | libc::INLCR
            | libc::IGNCR
            | libc::ICRNL
            | libc::IXON);
        raw.c_oflag &= !libc::OPOST;
        raw.c_lflag &= !(libc::ECHO
            | libc::ECHONL
            | libc::ICANON
            | libc::ISIG
            | libc::IEXTEN);
        raw.c_cflag &= !(libc::CSIZE | libc::PARENB);
        raw.c_cflag |= libc::CS8;
        raw.c_cc[libc::VMIN] = 1;
        raw.c_cc[libc::VTIME] = 0;

        if unsafe { libc::tcsetattr(fd, libc::TCSANOW, &raw) } != 0 {
            return Err(io::Error::last_os_error());
        }

        Ok(Self { original, fd })
    }

    /// Blocking single-byte read from stdin.
    ///
    /// Returns:
    /// - `Ok(Some(byte))` on a successful read of one byte.
    /// - `Ok(None)` on EOF (`read()` returned 0). On a pty whose master
    ///   has been destroyed, the kernel surfaces this — which is
    ///   *exactly* the case crossterm's mio loop fails to propagate
    ///   (#282). Caller treats this as session end.
    /// - `Err(e)` on any other syscall error. `EINTR` is included here;
    ///   the next phase wraps this with a signal-aware variant that
    ///   converts `EINTR` to a typed event.
    pub fn read_byte(&mut self) -> io::Result<Option<u8>> {
        let mut buf = [0u8; 1];
        let n = unsafe {
            libc::read(self.fd, buf.as_mut_ptr() as *mut libc::c_void, 1)
        };
        if n < 0 {
            return Err(io::Error::last_os_error());
        }
        if n == 0 {
            return Ok(None);
        }
        Ok(Some(buf[0]))
    }
}

impl Drop for RawTty {
    fn drop(&mut self) {
        // Best-effort restore. If this fails (e.g., terminal already
        // gone), there's nothing reasonable to do — we're already in
        // teardown and the Drop signature can't propagate errors.
        unsafe {
            let _ = libc::tcsetattr(self.fd, libc::TCSANOW, &self.original);
        }
    }
}

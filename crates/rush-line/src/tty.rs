//! Termios raw-mode lifecycle + signal-aware blocking byte reader.
//!
//! Phases A+B of the #282 root-cause fix. Replaces crossterm's mio-based
//! event reader (which busy-loops on a destroyed pty's `EPOLLHUP`) with
//! a direct `read(0, buf, 1)` syscall — the model bash/readline and
//! zsh/ZLE use. Signals interrupt the read via `EINTR`; `read()=0`
//! surfaces as EOF. No userspace polling, no busy loop.
//!
//! Phase A added the termios guard + blocking read. Phase B (this
//! revision) adds signal awareness: while raw mode is active, we own
//! handlers for `SIGWINCH` (terminal resize) and `SIGHUP`/`SIGTERM`
//! (exit signals). Each handler just flips an atomic flag; the read
//! wrapper checks the flags whenever `read` returns `EINTR` and surfaces
//! a typed event (`Resize` / `Eof`) up to the caller. Original handlers
//! are restored on `Drop`.
//!
//! Unix-only — Windows uses crossterm's input path under `cfg(windows)`.

use std::io;
use std::mem::MaybeUninit;
use std::sync::atomic::{AtomicBool, Ordering};

/// `SIGWINCH` was delivered while we were waiting for input. Caller sees
/// this via [`RawTty::read_byte`] returning [`RawByte::Resize`].
static WINCH_PENDING: AtomicBool = AtomicBool::new(false);

/// `SIGHUP` / `SIGTERM` was delivered. Caller sees this via
/// [`RawTty::read_byte`] returning [`RawByte::Eof`] — we treat exit
/// signals as end-of-input so the existing Ctrl-D path in the engine
/// handles the wind-down naturally.
static EXIT_PENDING: AtomicBool = AtomicBool::new(false);

/// Outcome of one byte-read attempt. The signal-aware variants
/// (`Resize`, `Eof`) collapse `EINTR` + flag-check into a single ordered
/// stream the caller can handle without touching errno or signals.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum RawByte {
    /// One byte was read successfully.
    Byte(u8),
    /// `SIGWINCH` interrupted the read. Caller should re-query terminal
    /// size and emit a Resize event, then call `read_byte` again.
    Resize,
    /// End of input. Either `read()` returned 0 (controlling pty was
    /// destroyed, or stdin closed cleanly) or `SIGHUP`/`SIGTERM` was
    /// delivered. Either way, the session is over from the line
    /// editor's perspective.
    Eof,
}

extern "C" fn handle_winch(_sig: libc::c_int) {
    WINCH_PENDING.store(true, Ordering::Relaxed);
}

extern "C" fn handle_exit(_sig: libc::c_int) {
    EXIT_PENDING.store(true, Ordering::Relaxed);
}

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
    /// Originals captured before we replaced the handlers, restored on
    /// `Drop`. Full `sigaction` structs (not just handlers) so we
    /// preserve the original `sa_flags` / `sa_mask`.
    prev_winch: libc::sigaction,
    prev_hup: libc::sigaction,
    prev_term: libc::sigaction,
}

impl RawTty {
    /// Snapshot stdin's current termios, switch into raw mode, and take
    /// over the input-relevant signal handlers (`SIGWINCH`, `SIGHUP`,
    /// `SIGTERM`). Errors if stdin isn't a tty (`ENOTTY`) or the ioctl
    /// fails — caller can fall back or surface the error.
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

        // Clear any stale flags from a previous session (e.g. a prior
        // raw-mode block in the same process). Order: clear, then
        // install handlers — minimizes the window where a signal would
        // be lost.
        WINCH_PENDING.store(false, Ordering::Relaxed);
        EXIT_PENDING.store(false, Ordering::Relaxed);

        // Install our handlers. We use `sigaction` rather than `signal`
        // because Linux glibc's `signal()` sets `SA_RESTART` by default
        // — which is the opposite of what we want. With `SA_RESTART`,
        // a blocking `read()` is auto-restarted after the handler runs,
        // so we never see `EINTR` and never check `EXIT_PENDING`. The
        // process hangs through SIGHUP / SIGTERM; only the default
        // disposition (which we replaced) was killing the shell.
        //
        // `flags = 0` gives us: handler stays installed across deliveries
        // (no `SA_RESETHAND`), and `read()` returns `EINTR` (no
        // `SA_RESTART`). That's exactly the contract this module needs.
        let mut act: libc::sigaction = unsafe { std::mem::zeroed() };
        act.sa_sigaction = handle_winch as *const () as usize;
        act.sa_flags = 0;
        unsafe { libc::sigemptyset(&mut act.sa_mask); }
        let mut prev_winch: libc::sigaction = unsafe { std::mem::zeroed() };
        if unsafe { libc::sigaction(libc::SIGWINCH, &act, &mut prev_winch) } != 0 {
            return Err(io::Error::last_os_error());
        }

        act.sa_sigaction = handle_exit as *const () as usize;
        let mut prev_hup: libc::sigaction = unsafe { std::mem::zeroed() };
        if unsafe { libc::sigaction(libc::SIGHUP, &act, &mut prev_hup) } != 0 {
            return Err(io::Error::last_os_error());
        }

        let mut prev_term: libc::sigaction = unsafe { std::mem::zeroed() };
        if unsafe { libc::sigaction(libc::SIGTERM, &act, &mut prev_term) } != 0 {
            return Err(io::Error::last_os_error());
        }

        Ok(Self {
            original,
            fd,
            prev_winch,
            prev_hup,
            prev_term,
        })
    }

    /// Signal-aware blocking single-byte read.
    ///
    /// On a healthy tty, blocks until one byte arrives and returns
    /// `RawByte::Byte(b)`. On an EOF (`read()=0` — destroyed pty,
    /// stdin closed), returns `RawByte::Eof`. If `SIGWINCH` was
    /// delivered while waiting (or just before the call), returns
    /// `RawByte::Resize` so the caller can re-query terminal size.
    /// If `SIGHUP`/`SIGTERM` was delivered, returns `RawByte::Eof` —
    /// the engine treats exit signals as end-of-input.
    ///
    /// `EINTR` from the underlying `read` for any other reason is
    /// transparently retried; signal-pending flags are checked before
    /// each retry so we never busy-loop.
    pub fn read_byte(&mut self) -> io::Result<RawByte> {
        // Drain any flags set before we entered the call (e.g. a
        // SIGWINCH that landed during the previous keystroke's
        // processing) — surface them first.
        if EXIT_PENDING.swap(false, Ordering::Relaxed) {
            return Ok(RawByte::Eof);
        }
        if WINCH_PENDING.swap(false, Ordering::Relaxed) {
            return Ok(RawByte::Resize);
        }

        loop {
            let mut buf = [0u8; 1];
            let n = unsafe {
                libc::read(self.fd, buf.as_mut_ptr() as *mut libc::c_void, 1)
            };
            if n > 0 {
                return Ok(RawByte::Byte(buf[0]));
            }
            if n == 0 {
                return Ok(RawByte::Eof);
            }
            // n < 0 — error. Most relevant is EINTR (signal interrupted
            // before any byte arrived). Check our flags; if a relevant
            // signal fired, surface it. Otherwise retry the read.
            let err = io::Error::last_os_error();
            if err.raw_os_error() == Some(libc::EINTR) {
                if EXIT_PENDING.swap(false, Ordering::Relaxed) {
                    return Ok(RawByte::Eof);
                }
                if WINCH_PENDING.swap(false, Ordering::Relaxed) {
                    return Ok(RawByte::Resize);
                }
                // EINTR with no flag we care about (e.g. SIGCHLD from
                // a backgrounded job that exited) — retry.
                continue;
            }
            return Err(err);
        }
    }

    /// Wait up to `timeout` for stdin to have data ready. Returns `true`
    /// if stdin is readable before the timeout, `false` if the wait
    /// expired without input.
    ///
    /// Used by the engine to disambiguate a lone Esc from a multi-byte
    /// escape sequence (Alt-x, arrow key, etc.). After feeding `0x1B`
    /// to the decoder, the engine waits ~50ms here; on `false` it calls
    /// `Decoder::flush()` to commit the lone Esc.
    ///
    /// Uses `select(2)` on `STDIN_FILENO`. Returns Ok(false) on signal
    /// interruption (`EINTR`) — caller should re-check decoder state
    /// and signal flags before deciding what to do; that path falls
    /// through to "no input arrived in the window," which is what we
    /// want for Esc disambiguation.
    pub fn wait_input(&self, timeout: std::time::Duration) -> io::Result<bool> {
        let mut tv = libc::timeval {
            tv_sec: timeout.as_secs() as libc::time_t,
            tv_usec: (timeout.subsec_micros() as libc::suseconds_t),
        };
        let mut set: libc::fd_set = unsafe { std::mem::zeroed() };
        unsafe {
            libc::FD_ZERO(&mut set);
            libc::FD_SET(self.fd, &mut set);
        }
        let rc = unsafe {
            libc::select(
                self.fd + 1,
                &mut set,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
                &mut tv,
            )
        };
        if rc < 0 {
            let err = io::Error::last_os_error();
            if err.raw_os_error() == Some(libc::EINTR) {
                return Ok(false);
            }
            return Err(err);
        }
        Ok(rc > 0)
    }
}

impl Drop for RawTty {
    fn drop(&mut self) {
        // Best-effort restore: termios first, then signal handlers.
        // If anything fails (terminal already gone), Drop can't
        // propagate errors and we're in teardown anyway.
        unsafe {
            let _ = libc::tcsetattr(self.fd, libc::TCSANOW, &self.original);
            libc::sigaction(libc::SIGWINCH, &self.prev_winch, std::ptr::null_mut());
            libc::sigaction(libc::SIGHUP, &self.prev_hup, std::ptr::null_mut());
            libc::sigaction(libc::SIGTERM, &self.prev_term, std::ptr::null_mut());
        }
    }
}

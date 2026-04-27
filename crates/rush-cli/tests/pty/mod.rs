//! Pty test harness for rush-cli integration tests.
//!
//! Spawns rush-cli attached to a pty pair via `libc::forkpty`, which
//! atomically: opens master/slave, forks, calls `setsid` in the child,
//! attaches the slave as the child's controlling terminal, and dup2's
//! it to stdin/stdout/stderr. Parent gets the master fd.
//!
//! We use `forkpty` rather than the manual `posix_openpt + setsid +
//! TIOCSCTTY + dup2` dance because the manual sequence works on Linux
//! but hangs on macOS (#295) — TIOCSCTTY-on-inherited-fd has different
//! semantics across kernels. `forkpty` is the libc-provided "do this
//! correctly per kernel" entrypoint and works the same on Linux and
//! macOS (and the BSDs).
//!
//! Bypassing `std::process::Command` means we build the child's argv
//! and environment by hand and `execve` directly. The harness owns the
//! child as a raw `pid_t`; `try_wait` / `kill` go through `waitpid` /
//! `kill` directly.
//!
//! Unix-only — pty semantics on Windows differ enough to need a
//! separate harness (ConPTY).
//!
//! Issue refs: #289 (this harness), #282 (the input rewrite this is
//! intended to backfill), #292 (the paint regression this is intended
//! to verify the fix for), #295 (macOS support — addressed here).
#![cfg(unix)]
#![allow(dead_code)] // Different test files use different subsets.

use std::env;
use std::ffi::{CString, OsStr, OsString};
use std::io;
use std::os::fd::{AsRawFd, FromRawFd, OwnedFd, RawFd};
use std::os::unix::ffi::OsStrExt;
use std::path::PathBuf;
use std::time::{Duration, Instant};

/// Outcome of a child process exit, modeled on the bits of
/// `std::process::ExitStatus` we actually need.
#[derive(Debug, Clone, Copy)]
pub enum ExitOutcome {
    /// Normal exit with a status code.
    Code(i32),
    /// Killed by a signal.
    Signal(i32),
}

/// One rush-cli session attached to a pty.
pub struct PtySession {
    master: OwnedFd,
    pid: libc::pid_t,
    waited: bool,
    /// Tempdir backing RUSH_CONFIG_DIR for this session — kept alive
    /// so it doesn't drop until after the child does. We don't pull in
    /// a tempfile crate; manual cleanup in Drop.
    config_dir: PathBuf,
}

impl PtySession {
    /// Spawn rush-cli inside a pty of the given size. The slave is the
    /// child's controlling terminal and stdin/stdout/stderr; the parent
    /// holds the master fd for read/write. `RUSH_CONFIG_DIR` is set to
    /// a fresh tempdir so each session sees no init.rush, no history,
    /// and no theme override from the user's real config.
    pub fn spawn(cols: u16, rows: u16) -> io::Result<Self> {
        let bin = env!("CARGO_BIN_EXE_rush-cli");
        let config_dir = make_tmp_config_dir()?;

        // Build argv. Just the binary name; rush has no args here.
        let argv0 = CString::new(bin).expect("bin path NUL-free");
        let argv: [*const libc::c_char; 2] = [argv0.as_ptr(), std::ptr::null()];

        // Build envp: inherit parent's env, but override TERM /
        // RUSH_CONFIG_DIR / RUSH_TRACE and strip RUST_BACKTRACE so
        // panic noise doesn't pollute the test output.
        let envp_strings = build_envp(&config_dir);
        let envp_ptrs: Vec<*const libc::c_char> = envp_strings
            .iter()
            .map(|s| s.as_ptr())
            .chain(std::iter::once(std::ptr::null()))
            .collect();

        // forkpty: atomic master/slave + fork + setsid + ctty + dup2.
        // Returns 0 in child, child pid in parent, -1 on error.
        let mut master_fd: libc::c_int = -1;
        let ws = libc::winsize {
            ws_row: rows,
            ws_col: cols,
            ws_xpixel: 0,
            ws_ypixel: 0,
        };
        let pid = unsafe {
            libc::forkpty(
                &mut master_fd,
                std::ptr::null_mut(), // don't need slave name
                std::ptr::null(),     // default termios for slave
                &ws as *const _,
            )
        };
        if pid < 0 {
            return Err(io::Error::last_os_error());
        }
        if pid == 0 {
            // Child: replace this process with rush. execve only
            // returns on failure, in which case the child must exit
            // immediately — it shares pages with parent until that
            // happens. Use _exit (not exit) to skip atexit handlers.
            unsafe {
                libc::execve(argv0.as_ptr(), argv.as_ptr(), envp_ptrs.as_ptr());
                // execve returned ⇒ failure. errno tells us why; best
                // we can do is signal it via exit code 127 (POSIX
                // "command not found" convention).
                let _ = libc::write(2, b"forkpty child: execve failed\n".as_ptr() as *const _, 29);
                libc::_exit(127);
            }
        }

        let master = unsafe { OwnedFd::from_raw_fd(master_fd) };
        Ok(Self {
            master,
            pid,
            waited: false,
            config_dir,
        })
    }

    /// Write bytes verbatim to the master end. Anything written here
    /// reaches the child as if typed at the terminal.
    pub fn write(&mut self, bytes: &[u8]) -> io::Result<()> {
        let mut written = 0;
        while written < bytes.len() {
            let n = unsafe {
                libc::write(
                    self.master.as_raw_fd(),
                    bytes.as_ptr().add(written) as *const _,
                    bytes.len() - written,
                )
            };
            if n < 0 {
                return Err(io::Error::last_os_error());
            }
            if n == 0 {
                return Err(io::Error::new(io::ErrorKind::WriteZero, "pty closed"));
            }
            written += n as usize;
        }
        Ok(())
    }

    /// Read whatever is available on the master end before the deadline.
    /// Returns whatever was received (possibly empty).
    pub fn read_chunk(&mut self, deadline: Instant) -> io::Result<Vec<u8>> {
        let mut buf = [0u8; 4096];
        let mut out = Vec::new();
        loop {
            let now = Instant::now();
            if now >= deadline {
                break;
            }
            let mut pfd = libc::pollfd {
                fd: self.master.as_raw_fd(),
                events: libc::POLLIN,
                revents: 0,
            };
            let ms = (deadline - now).as_millis().min(50) as i32;
            let rc = unsafe { libc::poll(&mut pfd, 1, ms) };
            if rc < 0 {
                let err = io::Error::last_os_error();
                if err.kind() == io::ErrorKind::Interrupted {
                    continue;
                }
                return Err(err);
            }
            if rc == 0 {
                if !out.is_empty() {
                    break;
                }
                continue;
            }
            let n = unsafe {
                libc::read(
                    self.master.as_raw_fd(),
                    buf.as_mut_ptr() as *mut _,
                    buf.len(),
                )
            };
            if n < 0 {
                let err = io::Error::last_os_error();
                if err.kind() == io::ErrorKind::Interrupted {
                    continue;
                }
                // Read on a closed pty returns EIO on Linux when the
                // child has exited. Treat as EOF.
                if err.raw_os_error() == Some(libc::EIO) {
                    break;
                }
                return Err(err);
            }
            if n == 0 {
                break; // EOF
            }
            out.extend_from_slice(&buf[..n as usize]);
            if out.len() > 1 << 20 {
                break; // safety cap
            }
        }
        Ok(out)
    }

    /// Read until either `predicate(&accumulated)` returns true or the
    /// timeout expires. Returns the full accumulated buffer.
    pub fn read_until(
        &mut self,
        timeout: Duration,
        mut predicate: impl FnMut(&[u8]) -> bool,
    ) -> io::Result<Vec<u8>> {
        let deadline = Instant::now() + timeout;
        let mut accum = Vec::new();
        loop {
            let chunk = self.read_chunk(deadline)?;
            if !chunk.is_empty() {
                accum.extend_from_slice(&chunk);
                if predicate(&accum) {
                    return Ok(accum);
                }
            }
            if Instant::now() >= deadline {
                return Err(io::Error::new(
                    io::ErrorKind::TimedOut,
                    format!(
                        "read_until: predicate not satisfied within {timeout:?}; \
                         got {} bytes: {:?}",
                        accum.len(),
                        String::from_utf8_lossy(&accum)
                    ),
                ));
            }
        }
    }

    /// Wait for rush to print a prompt. Detection: at least one `» ` or
    /// `: ` (vi-normal indicator) appears in ANSI-stripped output.
    pub fn expect_prompt(&mut self, timeout: Duration) -> io::Result<Vec<u8>> {
        self.read_until(timeout, |bytes| {
            let s = strip_ansi(bytes);
            s.contains("» ") || s.contains(": ")
        })
    }

    /// Send a line followed by CR (which the line editor sees as Enter).
    pub fn send_line(&mut self, line: &str) -> io::Result<()> {
        self.write(line.as_bytes())?;
        self.write(b"\r")
    }

    /// Send raw bytes without any newline. For escape sequences,
    /// bracketed-paste markers, etc.
    pub fn send_raw(&mut self, bytes: &[u8]) -> io::Result<()> {
        self.write(bytes)
    }

    /// Send a Unix signal to the child.
    pub fn send_signal(&mut self, sig: libc::c_int) -> io::Result<()> {
        if unsafe { libc::kill(self.pid, sig) } < 0 {
            return Err(io::Error::last_os_error());
        }
        Ok(())
    }

    /// Resize the pty (delivers SIGWINCH to the child).
    pub fn resize(&mut self, cols: u16, rows: u16) -> io::Result<()> {
        let ws = libc::winsize {
            ws_row: rows,
            ws_col: cols,
            ws_xpixel: 0,
            ws_ypixel: 0,
        };
        let rc = unsafe { libc::ioctl(self.master.as_raw_fd(), libc::TIOCSWINSZ, &ws as *const _) };
        if rc < 0 {
            return Err(io::Error::last_os_error());
        }
        Ok(())
    }

    /// Wait for the child to exit, or kill it on deadline.
    pub fn expect_exit_within(&mut self, timeout: Duration) -> io::Result<ExitOutcome> {
        let deadline = Instant::now() + timeout;
        loop {
            if let Some(outcome) = self.try_wait()? {
                return Ok(outcome);
            }
            if Instant::now() >= deadline {
                let _ = unsafe { libc::kill(self.pid, libc::SIGKILL) };
                let _ = self.try_wait_blocking();
                return Err(io::Error::new(
                    io::ErrorKind::TimedOut,
                    format!("child did not exit within {timeout:?}"),
                ));
            }
            std::thread::sleep(Duration::from_millis(20));
        }
    }

    /// Non-blocking waitpid: returns Some(outcome) if child has exited,
    /// None if still running.
    fn try_wait(&mut self) -> io::Result<Option<ExitOutcome>> {
        if self.waited {
            return Ok(None);
        }
        let mut status: libc::c_int = 0;
        let rc = unsafe { libc::waitpid(self.pid, &mut status, libc::WNOHANG) };
        if rc == 0 {
            return Ok(None);
        }
        if rc < 0 {
            return Err(io::Error::last_os_error());
        }
        self.waited = true;
        Ok(Some(decode_status(status)))
    }

    fn try_wait_blocking(&mut self) -> io::Result<ExitOutcome> {
        if self.waited {
            // Already reaped; we don't have the prior status. Fake one.
            return Ok(ExitOutcome::Code(-1));
        }
        let mut status: libc::c_int = 0;
        let rc = unsafe { libc::waitpid(self.pid, &mut status, 0) };
        if rc < 0 {
            return Err(io::Error::last_os_error());
        }
        self.waited = true;
        Ok(decode_status(status))
    }
}

impl Drop for PtySession {
    fn drop(&mut self) {
        // Best-effort cleanup. SIGKILL → wait → remove tempdir.
        if !self.waited {
            let _ = unsafe { libc::kill(self.pid, libc::SIGKILL) };
            let _ = self.try_wait_blocking();
        }
        let _ = std::fs::remove_dir_all(&self.config_dir);
    }
}

fn decode_status(status: libc::c_int) -> ExitOutcome {
    // POSIX: WIFEXITED + WEXITSTATUS, WIFSIGNALED + WTERMSIG. The libc
    // crate provides macro wrappers as functions on most platforms but
    // not all; do the bit math directly so we don't fight feature-flag
    // surface.
    if libc_wif_exited(status) {
        ExitOutcome::Code(libc_wexit_status(status))
    } else if libc_wif_signaled(status) {
        ExitOutcome::Signal(libc_wterm_sig(status))
    } else {
        ExitOutcome::Code(-1)
    }
}

#[allow(non_snake_case)]
fn libc_wif_exited(status: libc::c_int) -> bool {
    (status & 0x7f) == 0
}
#[allow(non_snake_case)]
fn libc_wexit_status(status: libc::c_int) -> i32 {
    (status >> 8) & 0xff
}
#[allow(non_snake_case)]
fn libc_wif_signaled(status: libc::c_int) -> bool {
    let lo = status & 0x7f;
    lo != 0 && lo != 0x7f
}
#[allow(non_snake_case)]
fn libc_wterm_sig(status: libc::c_int) -> i32 {
    status & 0x7f
}

fn make_tmp_config_dir() -> io::Result<PathBuf> {
    let base = env::temp_dir();
    let pid = std::process::id();
    let nanos = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.subsec_nanos())
        .unwrap_or(0);
    let dir = base.join(format!("rush-pty-test-{pid}-{nanos}"));
    std::fs::create_dir_all(&dir)?;
    Ok(dir)
}

/// Build the child's environment: inherit parent's env, override the
/// shell-test essentials, strip RUST_BACKTRACE so panic prints don't
/// pollute test output. Returns CStrings (kept alive by the caller)
/// from which we'll take pointers for execve.
fn build_envp(config_dir: &PathBuf) -> Vec<CString> {
    let overrides: &[(&str, OsString)] = &[
        ("TERM", OsString::from("xterm-256color")),
        ("RUSH_CONFIG_DIR", config_dir.as_os_str().to_owned()),
        ("RUSH_TRACE", OsString::from("0")),
    ];
    let strip: &[&str] = &["RUST_BACKTRACE"];

    let mut entries: Vec<CString> = Vec::new();
    for (k, v) in env::vars_os() {
        let key_str = k.to_string_lossy();
        let key_ref: &str = &key_str;
        if strip.contains(&key_ref) {
            continue;
        }
        if overrides.iter().any(|(ok, _)| *ok == key_ref) {
            continue; // we'll re-add the override below
        }
        if let Some(c) = make_kv_cstring(&k, &v) {
            entries.push(c);
        }
    }
    for (k, v) in overrides {
        if let Some(c) = make_kv_cstring(OsStr::new(*k), v) {
            entries.push(c);
        }
    }
    entries
}

fn make_kv_cstring(key: &OsStr, value: &OsStr) -> Option<CString> {
    let mut bytes = Vec::with_capacity(key.len() + 1 + value.len());
    bytes.extend_from_slice(key.as_bytes());
    bytes.push(b'=');
    bytes.extend_from_slice(value.as_bytes());
    CString::new(bytes).ok()
}

/// Strip ANSI escape sequences from a byte slice, returning the visible
/// text. Handles CSI (`\x1b[...<final>`), OSC (`\x1b]...\x07` or `...ST`),
/// and single-char escapes (`\x1b<char>`).
pub fn strip_ansi(bytes: &[u8]) -> String {
    let s = String::from_utf8_lossy(bytes);
    let mut out = String::with_capacity(s.len());
    let mut chars = s.chars().peekable();
    while let Some(c) = chars.next() {
        if c != '\x1b' {
            out.push(c);
            continue;
        }
        match chars.peek().copied() {
            Some('[') => {
                chars.next();
                while let Some(&p) = chars.peek() {
                    chars.next();
                    if matches!(p, '@'..='~') {
                        break;
                    }
                }
            }
            Some(']') => {
                // OSC: terminate on BEL (0x07) or ST (\x1b\\)
                chars.next();
                while let Some(&p) = chars.peek() {
                    chars.next();
                    if p == '\x07' {
                        break;
                    }
                    if p == '\x1b' && chars.peek().copied() == Some('\\') {
                        chars.next();
                        break;
                    }
                }
            }
            Some(_) => {
                // Single-char escape (\x1b 7, \x1b 8, \x1b=, \x1b>, etc.)
                chars.next();
            }
            None => break,
        }
    }
    out
}

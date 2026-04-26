//! Pty test harness for rush-cli integration tests.
//!
//! Spawns rush-cli attached to a pty pair so tests can drive the shell
//! the way a real terminal would. The child process gets the slave end
//! as its controlling terminal (setsid + TIOCSCTTY); the test keeps the
//! master end for sending input + reading output.
//!
//! Unix-only — pty semantics on Windows differ enough to need a separate
//! harness (ConPTY), which we'll add when we care about Windows again.
//!
//! Issue refs: #289 (this harness), #282 (the input rewrite this is
//! intended to backfill), #292 (the paint regression this is intended
//! to verify the fix for).
#![cfg(unix)]
#![allow(dead_code)] // Different test files use different subsets.

use std::env;
use std::ffi::CString;
use std::io;
use std::os::fd::{AsRawFd, FromRawFd, OwnedFd, RawFd};
use std::os::unix::process::CommandExt;
use std::path::PathBuf;
use std::process::{Child, Command, ExitStatus};
use std::time::{Duration, Instant};

/// One rush-cli session attached to a pty.
pub struct PtySession {
    master: OwnedFd,
    child: Child,
    /// Tempdir that backs RUSH_CONFIG_DIR for this session — kept alive
    /// so it doesn't drop until after the child does. We don't use the
    /// `tempfile` crate to stay dep-free; manual cleanup in Drop.
    config_dir: PathBuf,
}

impl PtySession {
    /// Spawn rush-cli inside a pty of the given size. The slave is the
    /// child's stdin/stdout/stderr and controlling terminal; the master
    /// fd is held in the returned session for read/write.
    ///
    /// `RUSH_CONFIG_DIR` is set to a fresh tempdir so the session sees
    /// no init.rush, no history, and no theme override from the user's
    /// real config.
    pub fn spawn(cols: u16, rows: u16) -> io::Result<Self> {
        let bin = env!("CARGO_BIN_EXE_rush-cli");

        // Fresh, empty config dir so init.rush / history don't perturb.
        let config_dir = make_tmp_config_dir()?;

        let (master, slave) = unsafe { open_pty_pair()? };
        unsafe { set_winsize(master.as_raw_fd(), cols, rows)? };

        let slave_raw = slave.as_raw_fd();
        let mut cmd = Command::new(bin);
        cmd.env("TERM", "xterm-256color")
            .env("RUSH_CONFIG_DIR", &config_dir)
            .env("RUSH_TRACE", "0")
            .env_remove("RUST_BACKTRACE");

        unsafe {
            cmd.pre_exec(move || {
                // New session — drops controlling tty inherited from the
                // test runner. Required before TIOCSCTTY can succeed.
                if libc::setsid() < 0 {
                    return Err(io::Error::last_os_error());
                }
                // Make the slave end of the pty our controlling tty.
                // Linux signature is `ioctl(fd, TIOCSCTTY, 0)`; macOS is
                // `ioctl(fd, TIOCSCTTY)` (no third arg). Passing 0 is
                // safe on both — macOS ignores extra varargs.
                #[cfg(target_os = "linux")]
                {
                    if libc::ioctl(slave_raw, libc::TIOCSCTTY, 0) < 0 {
                        return Err(io::Error::last_os_error());
                    }
                }
                #[cfg(not(target_os = "linux"))]
                {
                    if libc::ioctl(slave_raw, libc::TIOCSCTTY as _, 0) < 0 {
                        return Err(io::Error::last_os_error());
                    }
                }
                // Splice slave into 0/1/2.
                for target in &[libc::STDIN_FILENO, libc::STDOUT_FILENO, libc::STDERR_FILENO] {
                    if libc::dup2(slave_raw, *target) < 0 {
                        return Err(io::Error::last_os_error());
                    }
                }
                // Close the original slave fd if it's not 0/1/2.
                if slave_raw > 2 {
                    libc::close(slave_raw);
                }
                Ok(())
            });
        }

        let child = cmd.spawn()?;
        // Parent doesn't need the slave end; closing it lets EOF
        // propagate to the master if the child exits abnormally.
        drop(slave);

        Ok(Self { master, child, config_dir })
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

    /// Read whatever is available on the master end, polling with the
    /// given deadline. Returns whatever was received before the deadline
    /// expired (possibly empty).
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
                // Timeout — nothing more available right now.
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
            // Safety cap so a runaway child can't wedge a test forever.
            if out.len() > 1 << 20 {
                break;
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

    /// Send a line followed by a CR (which the line editor sees as
    /// Enter). No automatic newline-coercion or echo handling.
    pub fn send_line(&mut self, line: &str) -> io::Result<()> {
        self.write(line.as_bytes())?;
        self.write(b"\r")
    }

    /// Send raw bytes without any newline. For sending escape sequences,
    /// bracketed-paste markers, signals-as-control-chars, etc.
    pub fn send_raw(&mut self, bytes: &[u8]) -> io::Result<()> {
        self.write(bytes)
    }

    /// Send a Unix signal to the child process.
    pub fn send_signal(&mut self, sig: libc::c_int) -> io::Result<()> {
        let pid = self.child.id() as libc::pid_t;
        if unsafe { libc::kill(pid, sig) } < 0 {
            return Err(io::Error::last_os_error());
        }
        Ok(())
    }

    /// Resize the pty (delivers SIGWINCH to the child).
    pub fn resize(&mut self, cols: u16, rows: u16) -> io::Result<()> {
        unsafe { set_winsize(self.master.as_raw_fd(), cols, rows) }
    }

    /// Wait for the child to exit, or kill it on deadline.
    pub fn expect_exit_within(&mut self, timeout: Duration) -> io::Result<ExitStatus> {
        let deadline = Instant::now() + timeout;
        loop {
            if let Some(status) = self.child.try_wait()? {
                return Ok(status);
            }
            if Instant::now() >= deadline {
                let _ = self.child.kill();
                let _ = self.child.wait();
                return Err(io::Error::new(
                    io::ErrorKind::TimedOut,
                    format!("child did not exit within {timeout:?}"),
                ));
            }
            std::thread::sleep(Duration::from_millis(20));
        }
    }
}

impl Drop for PtySession {
    fn drop(&mut self) {
        // Best-effort cleanup. SIGKILL → wait → remove tempdir.
        let _ = self.child.kill();
        let _ = self.child.wait();
        let _ = std::fs::remove_dir_all(&self.config_dir);
    }
}

unsafe fn open_pty_pair() -> io::Result<(OwnedFd, OwnedFd)> {
    let master = libc::posix_openpt(libc::O_RDWR | libc::O_NOCTTY);
    if master < 0 {
        return Err(io::Error::last_os_error());
    }
    if libc::grantpt(master) < 0 {
        let err = io::Error::last_os_error();
        libc::close(master);
        return Err(err);
    }
    if libc::unlockpt(master) < 0 {
        let err = io::Error::last_os_error();
        libc::close(master);
        return Err(err);
    }
    let mut buf = [0 as libc::c_char; 256];
    let rc = libc::ptsname_r(master, buf.as_mut_ptr(), buf.len());
    if rc != 0 {
        let err = io::Error::from_raw_os_error(rc);
        libc::close(master);
        return Err(err);
    }
    let slave = libc::open(buf.as_ptr(), libc::O_RDWR | libc::O_NOCTTY);
    if slave < 0 {
        let err = io::Error::last_os_error();
        libc::close(master);
        return Err(err);
    }
    Ok((OwnedFd::from_raw_fd(master), OwnedFd::from_raw_fd(slave)))
}

unsafe fn set_winsize(fd: RawFd, cols: u16, rows: u16) -> io::Result<()> {
    let ws = libc::winsize {
        ws_row: rows,
        ws_col: cols,
        ws_xpixel: 0,
        ws_ypixel: 0,
    };
    let rc = unsafe { libc::ioctl(fd, libc::TIOCSWINSZ, &ws as *const _) };
    if rc < 0 {
        return Err(io::Error::last_os_error());
    }
    Ok(())
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
                // Single-char escape (e.g. \x1b 7, \x1b 8, \x1b=, \x1b>).
                chars.next();
            }
            None => break,
        }
    }
    let _ = CString::default(); // keep CString import live for future use
    out
}

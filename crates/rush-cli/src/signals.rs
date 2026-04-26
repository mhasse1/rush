//! Signal handling for the Rush shell.
//! Delegates to platform abstraction layer.

use rush_core::platform;

/// Install signal handlers. Call once at startup.
pub fn install() {
    let p = platform::current();
    p.install_signal_handlers();
}

/// Spawn a background thread that detects controlling-terminal loss and
/// force-exits the process when it happens (#282).
///
/// Why this exists: when the parent terminal is closed cleanly the kernel
/// sends SIGHUP and our handler sets a flag — but the main read loop is
/// blocked inside crossterm's event reader, which busy-loops on a
/// destroyed pty's `EPOLLHUP` instead of propagating an error. The flag
/// never gets checked. When the parent is `kill -9`'d, SIGHUP isn't even
/// delivered. Either way, the result is an orphan rush at 99% CPU
/// indefinitely.
///
/// The watchdog polls `isatty(STDIN_FILENO)` every few seconds. On a
/// healthy controlling tty it returns 1; on a destroyed pty the
/// underlying TCGETS ioctl fails (EIO / ENOTTY) and isatty returns 0.
/// The watchdog calls `_exit(129)` (the conventional SIGHUP exit code)
/// on detection — async-signal-safe and bypasses the busy-looping main
/// thread. Skipped Drop cleanup is fine since the terminal is already
/// gone.
///
/// Caller must guarantee stdin is a tty at startup; otherwise the
/// watchdog would terminate scripts driven by `rush < file` immediately.
#[cfg(unix)]
pub fn install_orphan_watchdog() {
    std::thread::Builder::new()
        .name("rush-tty-watchdog".to_string())
        .spawn(|| loop {
            std::thread::sleep(std::time::Duration::from_secs(5));
            unsafe {
                if libc::isatty(libc::STDIN_FILENO) != 1 {
                    libc::_exit(129);
                }
            }
        })
        .ok();
}

#[cfg(not(unix))]
pub fn install_orphan_watchdog() {}

/// Update COLUMNS and LINES env vars from terminal size.
pub fn update_terminal_size() {
    let p = platform::current();
    if let Some(size) = p.terminal_size() {
        unsafe {
            std::env::set_var("COLUMNS", size.cols.to_string());
            std::env::set_var("LINES", size.rows.to_string());
        }
    }
}

/// Check if we should exit (SIGHUP/SIGTERM received).
pub fn should_exit() -> bool {
    platform::current().should_exit()
}

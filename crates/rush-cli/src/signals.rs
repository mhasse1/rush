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
/// History: the original 5-second poll was a band-aid for the orphan-
/// pty busy-loop caused by crossterm's mio-based event reader (it
/// spun on a destroyed pty's `EPOLLHUP` instead of propagating an
/// error). That root cause is now fixed in rush-line — phases A–D
/// replaced the input path with direct blocking `read(2)`, signal-
/// driven `EINTR`, and natural `read()=0` EOF detection. SIGHUP /
/// SIGTERM exits via the typed `RawByte::Eof` path, and a destroyed
/// pty surfaces immediately the next time the read loop ticks.
///
/// The watchdog stays as defense-in-depth for genuinely degenerate
/// cases — e.g. an external command running in the foreground (rush
/// is *not* in the read loop, the child holds the tty) when the
/// parent terminal vanishes via `kill -9`. The relaxed 30-second poll
/// is still sub-minute exit latency at vanishing CPU cost.
///
/// Polls `isatty(STDIN_FILENO)`. On a destroyed pty the underlying
/// TCGETS ioctl fails (EIO / ENOTTY) and isatty returns 0. The
/// watchdog calls `_exit(129)` (the conventional SIGHUP exit code)
/// on detection — async-signal-safe and bypasses any busy-looping
/// main thread.
///
/// Caller must guarantee stdin is a tty at startup; otherwise the
/// watchdog would terminate scripts driven by `rush < file` immediately.
#[cfg(unix)]
pub fn install_orphan_watchdog() {
    std::thread::Builder::new()
        .name("rush-tty-watchdog".to_string())
        .spawn(|| loop {
            std::thread::sleep(std::time::Duration::from_secs(30));
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

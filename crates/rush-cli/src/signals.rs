//! Signal handling for the Rush shell.
//! Delegates to platform abstraction layer.

use rush_core::platform;

/// Install signal handlers. Call once at startup.
pub fn install() {
    let p = platform::current();
    p.install_signal_handlers();
}

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

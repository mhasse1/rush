//! Signal handling for the Rush shell.
//! SIGHUP/SIGTERM: save history and exit gracefully.
//! SIGWINCH: update COLUMNS/LINES.
//! SIGTSTP: swallowed (no job control).

use std::sync::atomic::{AtomicBool, Ordering};

/// Global flag: set when SIGHUP or SIGTERM received.
pub static SHOULD_EXIT: AtomicBool = AtomicBool::new(false);

/// Install signal handlers. Call once at startup.
pub fn install() {
    #[cfg(unix)]
    {
        // SIGHUP — terminal closed
        unsafe {
            libc::signal(libc::SIGHUP, sighup_handler as libc::sighandler_t);
        }

        // SIGTERM — system shutdown
        unsafe {
            libc::signal(libc::SIGTERM, sigterm_handler as libc::sighandler_t);
        }

        // SIGTSTP — Ctrl+Z: swallow
        unsafe {
            libc::signal(libc::SIGTSTP, libc::SIG_IGN);
        }

        // SIGPIPE — broken pipe: ignore (don't crash)
        unsafe {
            libc::signal(libc::SIGPIPE, libc::SIG_IGN);
        }
    }
}

/// Update COLUMNS and LINES env vars from terminal size.
pub fn update_terminal_size() {
    #[cfg(unix)]
    {
        unsafe {
            let mut ws: libc::winsize = std::mem::zeroed();
            if libc::ioctl(libc::STDOUT_FILENO, libc::TIOCGWINSZ, &mut ws) == 0 {
                if ws.ws_col > 0 {
                    std::env::set_var("COLUMNS", ws.ws_col.to_string());
                }
                if ws.ws_row > 0 {
                    std::env::set_var("LINES", ws.ws_row.to_string());
                }
            }
        }
    }
}

/// Check if we should exit (SIGHUP/SIGTERM received).
pub fn should_exit() -> bool {
    SHOULD_EXIT.load(Ordering::Relaxed)
}

#[cfg(unix)]
extern "C" fn sighup_handler(_sig: libc::c_int) {
    SHOULD_EXIT.store(true, Ordering::Relaxed);
}

#[cfg(unix)]
extern "C" fn sigterm_handler(_sig: libc::c_int) {
    SHOULD_EXIT.store(true, Ordering::Relaxed);
}

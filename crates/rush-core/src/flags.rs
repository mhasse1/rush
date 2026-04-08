//! POSIX shell flags: set -e, set -f, set -x, etc.
//! Global state shared across the shell session.

use std::sync::atomic::{AtomicBool, Ordering};

// Global flags — atomic for safety
static ERREXIT: AtomicBool = AtomicBool::new(false);     // set -e
static NOGLOB: AtomicBool = AtomicBool::new(false);      // set -f
static XTRACE: AtomicBool = AtomicBool::new(false);      // set -x
static NOCLOBBER: AtomicBool = AtomicBool::new(false);    // set -C
static VERBOSE: AtomicBool = AtomicBool::new(false);      // set -v

/// set -e: exit on non-zero pipeline status
pub fn errexit() -> bool { ERREXIT.load(Ordering::Relaxed) }
pub fn set_errexit(val: bool) { ERREXIT.store(val, Ordering::Relaxed); }

/// set -f: disable pathname expansion (noglob)
pub fn noglob() -> bool { NOGLOB.load(Ordering::Relaxed) }
pub fn set_noglob(val: bool) { NOGLOB.store(val, Ordering::Relaxed); }

/// set -x: print commands before execution (xtrace)
pub fn xtrace() -> bool { XTRACE.load(Ordering::Relaxed) }
pub fn set_xtrace(val: bool) { XTRACE.store(val, Ordering::Relaxed); }

/// set -C: prevent > from overwriting (noclobber)
pub fn noclobber() -> bool { NOCLOBBER.load(Ordering::Relaxed) }
pub fn set_noclobber(val: bool) { NOCLOBBER.store(val, Ordering::Relaxed); }

/// set -v: print input lines as read (verbose)
pub fn verbose() -> bool { VERBOSE.load(Ordering::Relaxed) }
pub fn set_verbose(val: bool) { VERBOSE.store(val, Ordering::Relaxed); }

/// Handle POSIX `set` flag arguments. Returns true if handled.
pub fn handle_set_flag(flag: &str) -> bool {
    match flag {
        "-e" => { set_errexit(true); true }
        "+e" => { set_errexit(false); true }
        "-f" => { set_noglob(true); true }
        "+f" => { set_noglob(false); true }
        "-x" => { set_xtrace(true); true }
        "+x" => { set_xtrace(false); true }
        "-C" => { set_noclobber(true); true }
        "+C" => { set_noclobber(false); true }
        "-v" => { set_verbose(true); true }
        "+v" => { set_verbose(false); true }
        _ => false,
    }
}

/// Get current flags as a string (for $- special parameter).
pub fn current_flags() -> String {
    let mut s = String::new();
    if errexit() { s.push('e'); }
    if noglob() { s.push('f'); }
    if xtrace() { s.push('x'); }
    if noclobber() { s.push('C'); }
    if verbose() { s.push('v'); }
    s
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn set_and_check_errexit() {
        set_errexit(false);
        assert!(!errexit());
        set_errexit(true);
        assert!(errexit());
        set_errexit(false);
    }

    #[test]
    fn set_and_check_noglob() {
        set_noglob(false);
        assert!(!noglob());
        set_noglob(true);
        assert!(noglob());
        set_noglob(false);
    }

    #[test]
    fn handle_flags() {
        assert!(handle_set_flag("-e"));
        assert!(errexit());
        assert!(handle_set_flag("+e"));
        assert!(!errexit());
        assert!(!handle_set_flag("--unknown"));
    }

    #[test]
    fn current_flags_string() {
        set_errexit(false);
        set_noglob(false);
        set_xtrace(false);
        assert_eq!(current_flags(), "");
        set_errexit(true);
        set_xtrace(true);
        assert_eq!(current_flags(), "ex");
        set_errexit(false);
        set_xtrace(false);
    }
}

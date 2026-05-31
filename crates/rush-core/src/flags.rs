//! POSIX shell flags: set -e, set -f, set -x, etc.
//! Global state shared across the shell session.

use std::sync::atomic::{AtomicBool, Ordering};

// Global flags — atomic for safety
static ERREXIT: AtomicBool = AtomicBool::new(false);     // set -e
static NOGLOB: AtomicBool = AtomicBool::new(false);      // set -f
static XTRACE: AtomicBool = AtomicBool::new(false);      // set -x
static NOCLOBBER: AtomicBool = AtomicBool::new(false);    // set -C
static VERBOSE: AtomicBool = AtomicBool::new(false);      // set -v
static ALLEXPORT: AtomicBool = AtomicBool::new(false);   // set -a
static NOTIFY: AtomicBool = AtomicBool::new(false);      // set -b
static NOUNSET: AtomicBool = AtomicBool::new(false);     // set -u
static NOEXEC: AtomicBool = AtomicBool::new(false);      // set -n
static MONITOR: AtomicBool = AtomicBool::new(false);     // set -m
static AUTOCD: AtomicBool = AtomicBool::new(false);      // set -o autocd (#277)

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

/// set -a: export all assigned variables
pub fn allexport() -> bool { ALLEXPORT.load(Ordering::Relaxed) }
pub fn set_allexport(val: bool) { ALLEXPORT.store(val, Ordering::Relaxed); }

/// set -b: report background job completion immediately
pub fn notify() -> bool { NOTIFY.load(Ordering::Relaxed) }
pub fn set_notify(val: bool) { NOTIFY.store(val, Ordering::Relaxed); }

/// set -u: treat unset variables as error
pub fn nounset() -> bool { NOUNSET.load(Ordering::Relaxed) }
pub fn set_nounset(val: bool) { NOUNSET.store(val, Ordering::Relaxed); }

/// set -n: read commands but don't execute (syntax check)
pub fn noexec() -> bool { NOEXEC.load(Ordering::Relaxed) }
pub fn set_noexec(val: bool) { NOEXEC.store(val, Ordering::Relaxed); }

/// set -m: enable job control (monitor mode)
pub fn monitor() -> bool { MONITOR.load(Ordering::Relaxed) }
pub fn set_monitor(val: bool) { MONITOR.store(val, Ordering::Relaxed); }

/// set -o autocd: bare directory path acts as `cd <path>` (#277).
pub fn autocd() -> bool { AUTOCD.load(Ordering::Relaxed) }
pub fn set_autocd(val: bool) { AUTOCD.store(val, Ordering::Relaxed); }

/// Handle POSIX `set` flag arguments. Returns true if handled.
pub fn handle_set_flag(flag: &str) -> bool {
    // `set -o NAME` / `set +o NAME` — normalize to a single token so the
    // match below can pattern-match the option name. Currently autocd is
    // the only named option; add more as they appear.
    if let Some(name) = flag.strip_prefix("-o ") {
        return match name.trim() {
            "autocd" => { set_autocd(true); true }
            _ => false,
        };
    }
    if let Some(name) = flag.strip_prefix("+o ") {
        return match name.trim() {
            "autocd" => { set_autocd(false); true }
            _ => false,
        };
    }
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
        "-a" => { set_allexport(true); true }
        "+a" => { set_allexport(false); true }
        "-b" => { set_notify(true); true }
        "+b" => { set_notify(false); true }
        "-u" => { set_nounset(true); true }
        "+u" => { set_nounset(false); true }
        "-n" => { set_noexec(true); true }
        "+n" => { set_noexec(false); true }
        "-m" => { set_monitor(true); true }
        "+m" => { set_monitor(false); true }
        // `-o NAME` / `+o NAME` long-form options. Caller splits on
        // whitespace, so `-o` may arrive alone; handle that in the
        // dispatch layer where the next token is in reach. Here we only
        // recognise the explicit form `set -o autocd` users sometimes
        // write as one shell-word.
        "-oautocd" | "-o:autocd" => { set_autocd(true); true }
        "+oautocd" | "+o:autocd" => { set_autocd(false); true }
        _ => false,
    }
}

/// Get current flags as a string (for $- special parameter).
pub fn current_flags() -> String {
    let mut s = String::new();
    if allexport() { s.push('a'); }
    if notify() { s.push('b'); }
    if noclobber() { s.push('C'); }
    if errexit() { s.push('e'); }
    if noglob() { s.push('f'); }
    if monitor() { s.push('m'); }
    if noexec() { s.push('n'); }
    if nounset() { s.push('u'); }
    if verbose() { s.push('v'); }
    if xtrace() { s.push('x'); }
    s
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn set_and_check_errexit() {
        let _g = crate::test_locks::FLAGS_LOCK.lock().unwrap_or_else(|e| e.into_inner());
        set_errexit(false);
        assert!(!errexit());
        set_errexit(true);
        assert!(errexit());
        set_errexit(false);
    }

    #[test]
    fn set_and_check_noglob() {
        let _g = crate::test_locks::FLAGS_LOCK.lock().unwrap_or_else(|e| e.into_inner());
        set_noglob(false);
        assert!(!noglob());
        set_noglob(true);
        assert!(noglob());
        set_noglob(false);
    }

    #[test]
    fn handle_flags() {
        let _g = crate::test_locks::FLAGS_LOCK.lock().unwrap_or_else(|e| e.into_inner());
        assert!(handle_set_flag("-e"));
        assert!(errexit());
        assert!(handle_set_flag("+e"));
        assert!(!errexit());
        assert!(!handle_set_flag("--unknown"));
    }

    #[test]
    fn current_flags_string() {
        let _g = crate::test_locks::FLAGS_LOCK.lock().unwrap_or_else(|e| e.into_inner());
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

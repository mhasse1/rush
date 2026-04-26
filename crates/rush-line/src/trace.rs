//! Diagnostic tracing for the rush-line input path.
//!
//! Off by default. Set `RUSH_TRACE=1` (or any non-empty value) to enable
//! — log lines append to `/tmp/rush-trace.log`. Each line is fsynced so
//! a system freeze or kill -9 doesn't lose the trail leading up to the
//! event.
//!
//! Format:
//!
//! ```text
//! 1234567890.123456 [tid=NNNN] [tag] message
//! ```
//!
//! `tid` is the OS thread ID (so the watchdog thread, the main loop,
//! and signal-handler-driven entries are distinguishable). The
//! timestamp is monotonic seconds since process start, microsecond
//! resolution.

use std::fs::{File, OpenOptions};
use std::io::Write;
use std::sync::Mutex;
use std::sync::OnceLock;
use std::time::Instant;

static FILE: OnceLock<Option<Mutex<File>>> = OnceLock::new();
static START: OnceLock<Instant> = OnceLock::new();

/// One-time initializer. Cheap to call repeatedly; only the first call
/// does any work. After this returns, `enabled()` reports the final
/// state and `log()` will (or won't) emit accordingly.
pub fn init() {
    FILE.get_or_init(|| {
        if std::env::var_os("RUSH_TRACE").map_or(true, |v| v.is_empty()) {
            return None;
        }
        let path = std::env::var("RUSH_TRACE_FILE")
            .unwrap_or_else(|_| "/tmp/rush-trace.log".to_string());
        OpenOptions::new()
            .create(true)
            .append(true)
            .open(&path)
            .ok()
            .map(Mutex::new)
    });
    START.get_or_init(Instant::now);
}

/// True if tracing is on for this process.
pub fn enabled() -> bool {
    FILE.get().and_then(|x| x.as_ref()).is_some()
}

/// Emit one line. No-op if tracing is off. Each call fsyncs after
/// write — guarantees the line is on disk before we return, so
/// post-freeze inspection always sees the last action.
pub fn log(tag: &str, msg: &str) {
    let Some(Some(mutex)) = FILE.get() else {
        return;
    };
    let now = START.get().copied().unwrap_or_else(Instant::now);
    let elapsed = now.elapsed().as_secs_f64();
    let tid = thread_id();
    let line = format!("{elapsed:.6} [tid={tid}] [{tag}] {msg}\n");
    if let Ok(mut f) = mutex.lock() {
        let _ = f.write_all(line.as_bytes());
        let _ = f.sync_data();
    }
}

#[cfg(target_os = "linux")]
fn thread_id() -> u32 {
    unsafe { libc::syscall(libc::SYS_gettid) as u32 }
}

#[cfg(not(target_os = "linux"))]
fn thread_id() -> u32 {
    // Fallback — debug-format Thread::id, parse the digits. Good
    // enough for tracing.
    let id = format!("{:?}", std::thread::current().id());
    id.chars().filter(|c| c.is_ascii_digit()).collect::<String>()
        .parse()
        .unwrap_or(0)
}

/// Convenience: trace a tagged message. Cheap-ish even when off (one
/// `OnceLock::get` + Option check) but prefer wrapping call sites in
/// `if trace::enabled() { ... }` if the message itself is expensive
/// to build.
#[macro_export]
macro_rules! trace {
    ($tag:expr, $($arg:tt)*) => {
        $crate::trace::log($tag, &format!($($arg)*))
    };
}

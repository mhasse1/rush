//! Platform abstraction layer.
//!
//! Defines traits for OS-specific operations. Unix backend built;
//! Windows backend to be added later. Tests validate the interface.

// ── Process Execution ───────────────────────────────────────────────

/// Result of spawning a child process.
pub struct SpawnResult {
    pub pid: u32,
    pub pgid: u32,
}

/// Result of waiting for a child process.
#[derive(Debug, Clone)]
pub enum WaitResult {
    Exited(i32),        // normal exit with code
    Signaled(i32),      // killed by signal N
    Stopped(i32),       // stopped by signal N
}

impl WaitResult {
    pub fn exit_code(&self) -> i32 {
        match self {
            WaitResult::Exited(c) => *c,
            WaitResult::Signaled(s) => 128 + s,
            WaitResult::Stopped(s) => 128 + s,
        }
    }

    pub fn success(&self) -> bool {
        matches!(self, WaitResult::Exited(0))
    }
}

// ── Signal Management ───────────────────────────────────────────────

/// Signal numbers (platform-independent names).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum Sig {
    Hup,
    Int,
    Quit,
    Kill,
    Term,
    Tstp,
    Cont,
    Ttin,
    Ttou,
    Pipe,
    Winch,
}

/// Signal disposition.
#[derive(Debug, Clone, Copy, PartialEq)]
pub enum SigAction {
    Default,
    Ignore,
    // Handler — not directly representable cross-platform
}

// ── Terminal ────────────────────────────────────────────────────────

/// Terminal size.
#[derive(Debug, Clone, Copy)]
pub struct TermSize {
    pub cols: u16,
    pub rows: u16,
}

// ── Platform trait ──────────────────────────────────────────────────

/// Platform-specific operations. One implementation per OS.
pub trait Platform {
    // ── Signals ─────────────────────────────────────────────────────

    /// Install shell signal handlers (SIGHUP, SIGTERM, etc.).
    fn install_signal_handlers(&self);

    /// Set signal disposition for the current process.
    fn set_signal(&self, sig: Sig, action: SigAction);

    /// Check if an exit-requested signal was received.
    fn should_exit(&self) -> bool;

    // ── Process Groups ──────────────────────────────────────────────

    /// Set up a child process for foreground execution.
    /// Called between fork and exec (via pre_exec on Unix).
    /// Sets process group, resets signal dispositions to default.
    fn setup_foreground_child(&self);

    /// Set up a child process for background execution.
    /// Sets process group, ignores SIGINT/SIGQUIT (per POSIX).
    fn setup_background_child(&self);

    /// Give terminal control to a process group.
    fn set_foreground_pgid(&self, pgid: u32);

    /// Reclaim terminal control for the shell's process group.
    fn reclaim_terminal(&self);

    /// Get the shell's own process group ID.
    fn shell_pgid(&self) -> u32;

    // ── Job Waiting ─────────────────────────────────────────────────

    /// Wait for a specific PID (blocking).
    fn wait_pid(&self, pid: u32) -> WaitResult;

    /// Check if a PID has changed state (non-blocking).
    fn try_wait_pid(&self, pid: u32) -> Option<WaitResult>;

    /// Send a signal to a process group (negative PID).
    fn kill_pg(&self, pgid: u32, sig: Sig);

    // ── Terminal Info ────────────────────────────────────────────────

    /// Get terminal size (columns, rows).
    fn terminal_size(&self) -> Option<TermSize>;

    /// Get current local time as HH:MM string.
    fn local_time_hhmm(&self) -> String;

    /// Get hostname (short, no domain).
    fn hostname(&self) -> String;

    /// Get current username.
    fn username(&self) -> String;

    /// Check if running over SSH.
    fn is_ssh(&self) -> bool;

    /// Check if running as root/admin.
    fn is_root(&self) -> bool;
}

// ── Unix Implementation ─────────────────────────────────────────────

#[cfg(unix)]
mod unix;

#[cfg(unix)]
pub use unix::UnixPlatform;

/// Get the platform implementation for the current OS.
pub fn current() -> Box<dyn Platform> {
    #[cfg(unix)]
    { Box::new(UnixPlatform::new()) }

    #[cfg(not(unix))]
    { Box::new(WindowsPlatform::new()) }
}

// ── Windows / non-Unix backend ──────────────────────────────────────

#[cfg(not(unix))]
pub struct WindowsPlatform;

#[cfg(not(unix))]
impl WindowsPlatform {
    pub fn new() -> Self { Self }
}

#[cfg(not(unix))]
impl Platform for WindowsPlatform {
    fn install_signal_handlers(&self) {
        // Windows uses SetConsoleCtrlHandler — handled by Rust's ctrlc crate
        // or std::process signal handling. No-op here; reedline handles Ctrl+C.
    }

    fn set_signal(&self, _sig: Sig, _action: SigAction) {
        // No POSIX signals on Windows. Ctrl+C is handled by the console subsystem.
    }

    fn should_exit(&self) -> bool { false }

    fn setup_foreground_child(&self) {
        // Windows doesn't have process groups in the POSIX sense.
        // CREATE_NEW_PROCESS_GROUP is handled by std::process::Command.
    }

    fn setup_background_child(&self) {
        // No POSIX job control on Windows.
    }

    fn set_foreground_pgid(&self, _pgid: u32) {
        // No terminal process group control on Windows.
    }

    fn reclaim_terminal(&self) {
        // No-op on Windows — console is always owned by the process.
    }

    fn shell_pgid(&self) -> u32 { std::process::id() }

    fn wait_pid(&self, pid: u32) -> WaitResult {
        // Use std::process::Child::wait() instead — this is a fallback.
        // On Windows, PIDs aren't directly waitable without a handle.
        let _ = pid;
        WaitResult::Exited(0)
    }

    fn try_wait_pid(&self, _pid: u32) -> Option<WaitResult> { None }

    fn kill_pg(&self, _pgid: u32, _sig: Sig) {
        // On Windows, use TerminateProcess via std::process::Child::kill().
        // Process group killing requires job objects (future enhancement).
    }

    fn terminal_size(&self) -> Option<TermSize> {
        // Try COLUMNS/LINES env vars (set by some terminals)
        let cols = std::env::var("COLUMNS").ok()
            .and_then(|s| s.parse::<u16>().ok())
            .unwrap_or(120);
        let rows = std::env::var("LINES").ok()
            .and_then(|s| s.parse::<u16>().ok())
            .unwrap_or(30);
        Some(TermSize { cols, rows })
    }

    fn local_time_hhmm(&self) -> String {
        // Use system time — no libc dependency needed
        use std::time::{SystemTime, UNIX_EPOCH};
        let secs = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map(|d| d.as_secs())
            .unwrap_or(0);
        // UTC offset not easily available without a crate, but on Windows
        // we can shell out or use env. For now, use UTC with timezone offset.
        // This is approximate — proper fix would use chrono or windows-sys.
        let hours = (secs / 3600) % 24;
        let minutes = (secs / 60) % 60;
        format!("{hours:02}:{minutes:02}")
    }

    fn hostname(&self) -> String {
        std::env::var("COMPUTERNAME")
            .or_else(|_| std::env::var("HOSTNAME"))
            .unwrap_or_else(|_| "unknown".into())
            .to_lowercase()
    }

    fn username(&self) -> String {
        std::env::var("USERNAME")
            .or_else(|_| std::env::var("USER"))
            .unwrap_or_else(|_| "unknown".into())
    }

    fn is_ssh(&self) -> bool {
        std::env::var("SSH_CONNECTION").is_ok() || std::env::var("SSH_CLIENT").is_ok()
    }

    fn is_root(&self) -> bool {
        // On Windows, check if running as Administrator
        // Simplified: check for well-known admin env patterns
        false // proper check needs windows-sys or whoami crate
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn wait_result_exit_code() {
        assert_eq!(WaitResult::Exited(0).exit_code(), 0);
        assert_eq!(WaitResult::Exited(1).exit_code(), 1);
        assert_eq!(WaitResult::Signaled(2).exit_code(), 130); // 128+SIGINT
        assert_eq!(WaitResult::Signaled(9).exit_code(), 137); // 128+SIGKILL
        assert_eq!(WaitResult::Stopped(20).exit_code(), 148); // 128+SIGTSTP
    }

    #[test]
    fn wait_result_success() {
        assert!(WaitResult::Exited(0).success());
        assert!(!WaitResult::Exited(1).success());
        assert!(!WaitResult::Signaled(9).success());
    }

    #[test]
    fn platform_can_be_constructed() {
        let p = current();
        // Should not panic
        let _ = p.hostname();
        let _ = p.username();
    }

    #[test]
    fn platform_time() {
        let p = current();
        let time = p.local_time_hhmm();
        assert_eq!(time.len(), 5); // HH:MM
        assert_eq!(&time[2..3], ":");
    }

    #[test]
    fn platform_hostname_not_empty() {
        let p = current();
        assert!(!p.hostname().is_empty());
    }

    #[test]
    fn platform_username_not_empty() {
        let p = current();
        assert!(!p.username().is_empty());
    }

    #[test]
    fn platform_shell_pgid() {
        let p = current();
        assert!(p.shell_pgid() > 0);
    }
}

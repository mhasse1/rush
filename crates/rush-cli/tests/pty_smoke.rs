//! Smoke test for the pty harness (#289). Verifies the most basic
//! end-to-end path: rush starts in a real pty, prints a prompt, runs
//! a command, and exits cleanly when the controlling terminal sends
//! SIGHUP (the headline #282 regression we never want to ship again).
//!
//! Gated to Linux only for now: the harness compiles on macOS but the
//! tests hang there, almost certainly due to TIOCSCTTY / ptsname
//! semantics that differ from Linux. Tracked separately so #292 isn't
//! blocked on macOS-specific pty plumbing.
#![cfg(target_os = "linux")]

mod pty;

use pty::{strip_ansi, PtySession};
use std::time::Duration;

#[test]
fn rush_starts_runs_echo_and_exits_on_sighup() {
    let mut s = PtySession::spawn(80, 24).expect("spawn rush in pty");

    s.expect_prompt(Duration::from_secs(5))
        .expect("first prompt within 5s");

    s.send_line("echo hello-from-pty").expect("send echo");

    let raw = s
        .read_until(Duration::from_secs(5), |bytes| {
            // Wait until "hello-from-pty" appears in stripped output AND
            // a second prompt has rendered after it. The first prompt is
            // already accumulated by the time we get here.
            let text = strip_ansi(bytes);
            text.contains("hello-from-pty") && text.matches("» ").count() >= 2
        })
        .expect("echo output + next prompt");

    let text = strip_ansi(&raw);
    assert!(
        text.contains("hello-from-pty"),
        "expected echo output; got:\n{text}"
    );

    s.send_signal(libc::SIGHUP).expect("send SIGHUP");
    let status = s
        .expect_exit_within(Duration::from_secs(2))
        .expect("rush should exit within 2s of SIGHUP");

    // Either signal-killed (status.signal() == Some(SIGHUP)) or clean
    // exit via the EXIT_PENDING flag — both acceptable. We only care
    // that the process terminated.
    use std::os::unix::process::ExitStatusExt;
    let exited_cleanly = status.code().is_some() || status.signal().is_some();
    assert!(exited_cleanly, "child neither exited nor was signalled");
}

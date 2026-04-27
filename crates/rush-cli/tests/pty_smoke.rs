//! Smoke test for the pty harness (#289). Verifies the most basic
//! end-to-end path: rush starts in a real pty, prints a prompt, runs
//! a command, and exits cleanly when the controlling terminal sends
//! SIGHUP (the headline #282 regression we never want to ship again).
//!
//! Gated to Linux only: forkpty + cfg-shim landed for macOS (#295)
//! and the harness compiles cleanly there, but Unit Tests still hang
//! on macos-aarch64 (CI run 24971101349: 2h50m before manual cancel).
//! Root cause is *not* TIOCSCTTY semantics as initially suspected;
//! something else in the rush↔pty interaction on macOS is wedging.
//! Tracked under #295. Linux runs in <0.5s.
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
    let _outcome = s
        .expect_exit_within(Duration::from_secs(2))
        .expect("rush should exit within 2s of SIGHUP");
    // Either ExitOutcome::Code(_) or ExitOutcome::Signal(_) — both
    // acceptable. The expect_exit_within itself is the assertion.
}

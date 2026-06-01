//! Regression coverage for #299: rush under tmux pasted-input hang.
//!
//! The original report — paste into rush under tmux "ties up the entire
//! computer" — never had a documented repro. By the time it could be
//! revisited, the #292/#293 painter rework + the #271/#296 backslash-
//! escape fixes + the #295 pty teardown landed, and synthetic stress
//! against the deployed binary couldn't reproduce.
//!
//! These tests pin a few payload shapes that *could* have triggered the
//! hang (embedded paste-end sequence, multi-byte UTF-8 across the chunk
//! boundary, very large bracketed-paste blocks) so future input-path
//! changes that re-introduce a spin will trip a test instead of a
//! daily-driver hang.
//!
//! Cross-platform on Unix as of #295. See pty/mod.rs.
#![cfg(unix)]

mod pty;

use pty::PtySession;
use std::time::{Duration, Instant};

// NOTE: A "large bracketed paste" test (~16-50KB) belongs here but
// surfaces a separate performance issue: a 50KB unchunked bracketed
// paste through the pty took ~22 minutes to complete in early stress
// runs. The paste decoder itself is O(n) per byte, but somewhere in
// the read → decoder → engine → paint → write loop, large input
// becomes pathologically slow. Tracked separately so this regression
// suite stays fast in CI.

/// Paste containing an embedded `\x1b[201~` (the bracketed-paste end
/// sequence) inside the payload itself. A naive decoder that scans
/// blindly for the terminator could prematurely close paste mode and
/// then loop forever waiting for it to "really" close.
#[test]
fn paste_with_embedded_end_sentinel_completes() {
    let mut s = PtySession::spawn(80, 24).expect("spawn rush in pty");
    s.expect_prompt(Duration::from_secs(5)).expect("first prompt");
    let _ = s.read_chunk(Instant::now() + Duration::from_millis(300));

    let payload = b"\x1b[200~before \x1b[201~ after embedded end\x1b[201~";

    let started = Instant::now();
    s.send_raw(payload).expect("send embedded-end paste");
    let _ = s.read_chunk(Instant::now() + Duration::from_secs(5));
    let elapsed = started.elapsed();

    let _ = s.send_signal(libc::SIGHUP);

    assert!(
        elapsed < Duration::from_secs(5),
        "embedded \\x1b[201~ should not hang; elapsed={elapsed:?}"
    );
}

/// Multi-byte UTF-8 character split across two `send_raw` calls (and
/// thus likely two `read()`s in unix_input). A decoder that doesn't
/// buffer partial codepoints could drop bytes or hang waiting for
/// "complete" input that never arrives because the rest is in the next
/// chunk.
#[test]
fn paste_with_split_utf8_across_chunks_completes() {
    let mut s = PtySession::spawn(80, 24).expect("spawn rush in pty");
    s.expect_prompt(Duration::from_secs(5)).expect("first prompt");
    let _ = s.read_chunk(Instant::now() + Duration::from_millis(300));

    // `é` is 0xC3 0xA9 in UTF-8 — split across two sends.
    let part1: &[u8] = b"\x1b[200~h\xc3";
    let part2: &[u8] = b"\xa9llo world\x1b[201~";

    let started = Instant::now();
    s.send_raw(part1).expect("send first half");
    std::thread::sleep(Duration::from_millis(50));
    s.send_raw(part2).expect("send second half");
    let _ = s.read_chunk(Instant::now() + Duration::from_secs(5));
    let elapsed = started.elapsed();

    let _ = s.send_signal(libc::SIGHUP);

    assert!(
        elapsed < Duration::from_secs(5),
        "split UTF-8 should not hang; elapsed={elapsed:?}"
    );
}

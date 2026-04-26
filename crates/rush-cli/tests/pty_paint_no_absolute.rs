//! Regression test for #292: the painter's repaint loop must not emit
//! absolute cursor save/restore sequences. Tmux (and other emulators
//! with non-trivial scroll-region semantics) handle them in ways that
//! desynchronize the painter's relative-cursor model — see #292 for the
//! reproducible symptom.
//!
//! `cursor::SavePosition` / `cursor::RestorePosition` lower to either
//! DECSC (`\x1b 7`) / DECRC (`\x1b 8`) on most terminals — both
//! absolute. The painter has all the information it needs
//! (`pre_m.cursor_row`, `pre_m.cursor_col`, `full_m.cursor_row`) to
//! walk back relatively; absolute save/restore is redundant and
//! tmux-fragile.
//!
//! This test fails on main (today) — engine.rs:1161/1172 still emits
//! the save/restore pair. After the Phase 4 fix it will pass.
//!
//! Linux-only for the same reason as pty_smoke.rs (see header there).
#![cfg(target_os = "linux")]

mod pty;

use pty::PtySession;
use std::time::{Duration, Instant};

#[test]
fn paste_then_edit_emits_no_absolute_cursor_save_restore() {
    let mut s = PtySession::spawn(80, 24).expect("spawn rush in pty");
    s.expect_prompt(Duration::from_secs(5)).expect("first prompt");
    // Drain whatever else is buffered after the prompt so we capture
    // only the paste-induced repaint.
    let _ = s.read_chunk(Instant::now() + Duration::from_millis(300));

    // Bracketed-paste: long line wrapping past the 80-col edge AND
    // containing an embedded newline. Worst-case for the painter.
    let paste = b"\x1b[200~temperature.gpu, power.draw [W], utilization.gpu \
[%], clocks.current.graphics [MHz], pstate, clocks_event_reasons.active\n\
42, 7.65 W, 4 %, 494 MHz, P8, 0x0000000000000000\x1b[201~";
    s.send_raw(paste).expect("send paste");

    // Wait for the buffer text to render (sentinel that proves rush
    // both received the paste AND repainted). Rush highlighting on a
    // multi-row buffer takes ~2 seconds on this machine; give it 8.
    let post_paste = s
        .read_until(Duration::from_secs(8), |bytes| {
            bytes.windows(b"clocks_event_reasons.active".len())
                .any(|w| w == b"clocks_event_reasons.active")
        })
        .expect("paste content rendered within 8s");

    // Send a few backspaces to exercise the post-paste repaint path
    // (the "eats lines" symptom path in tmux). Each backspace triggers
    // a fresh repaint that re-emits the save/restore pair if the bug
    // is present.
    s.send_raw(b"\x7f\x7f\x7f").expect("backspaces");
    let post_bs = s
        .read_chunk(Instant::now() + Duration::from_secs(2))
        .expect("post-backspace drain");

    // Cleanup before assertions so a panic doesn't leak the child.
    let _ = s.send_signal(libc::SIGHUP);

    let mut all = Vec::with_capacity(post_paste.len() + post_bs.len());
    all.extend_from_slice(&post_paste);
    all.extend_from_slice(&post_bs);

    // Sanity: we got non-trivial output. Without this, an empty buffer
    // would trivially satisfy every "no bad sequence" check.
    assert!(
        all.len() > 200,
        "expected substantial repaint output; got {} bytes",
        all.len()
    );

    // Hard rule: no DECSC, DECRC, CSI SCOSC, CSI SCORC anywhere in the
    // captured stream. The painter has no business using these. Note
    // the byte literals — `b"\x1b 7"` would be three bytes (ESC, SPACE,
    // '7'); we want two (ESC, '7'), so we spell each as a slice.
    let bad: &[(&[u8], &str)] = &[
        (&[0x1b, b'7'], "DECSC (ESC 7) — absolute save"),
        (&[0x1b, b'8'], "DECRC (ESC 8) — absolute restore"),
        (b"\x1b[s", "CSI SCOSC (\\x1b[s) — absolute save"),
        (b"\x1b[u", "CSI SCORC (\\x1b[u) — absolute restore"),
    ];
    for (needle, desc) in bad {
        let count = all.windows(needle.len()).filter(|w| w == needle).count();
        assert_eq!(
            count, 0,
            "painter emitted {desc} ({count} occurrences) during \
             paste/backspace flow — see #292 / #293. Output ({} bytes): {:?}",
            all.len(),
            String::from_utf8_lossy(&all),
        );
    }
}

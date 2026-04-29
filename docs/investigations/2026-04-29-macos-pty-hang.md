# macOS pty harness hang — investigation brief (#295)

This is a self-contained brief for a Claude Code session running on
**rocinante** (mark's macOS dev box) to investigate issue [#295](https://github.com/mhasse1/rush/issues/295)
hands-on.

If you're picking this up cold, **start at the next section** — it
tells you exactly what to run and how to interpret the result. The
rest of the file is historical context (TL;DR, prior findings, code
pointers) for when you need to dig deeper.

## ▶ Next session: pull, run, decide

You're inheriting a sequence of fixes that build on each other. Each
round of investigation lands a fix; you re-run and either confirm
success or pinpoint the next wedge.

**1. Pull and run.**

```sh
cd ~/src/mcp/rush       # adjust if your checkout lives elsewhere
git pull origin main
RUSH_TRACE=1 ./scripts/macos-pty-investigate.sh
```

This temporarily flips `pty_smoke.rs` + `pty_paint_no_absolute.rs`
from `cfg(target_os = "linux")` to `cfg(unix)`, builds, runs the
smoke test with a 60-second wall timeout, and writes
`.macos-pty-log.txt` at the repo root. The cfg flip is reverted via
`trap` on exit (success or failure) — your working tree stays clean.

The harness has a bounded reap (commit `01399ba`), so even if rush
wedges, the test process panics with a real `TimedOut` message in
~12 seconds. No more multi-hour hangs.

**2. Read the result.**

Three possible outcomes:

| Outcome                                          | What it means                                                                 |
|--------------------------------------------------|-------------------------------------------------------------------------------|
| ✅ Test passes (`test result: ok. 1 passed; ...`) | TIOCNOTTY (commit `3e0b95d`) fixed it. **Go to step 3a.**                     |
| ❌ Test fails with `TimedOut` after `SIGHUP`      | The wedge moved. **Go to step 3b.**                                            |
| ❌ Test fails for any other reason                | Something regressed independently. Triage from the panic message; see the historical Findings sections below for context on what's known good. |

**3a. If the test passed — promote macOS to full coverage.**

Flip the cfg gates back to `cfg(unix)` permanently. Three files:

- `crates/rush-cli/tests/pty_smoke.rs` — change `#![cfg(target_os = "linux")]`
  to `#![cfg(unix)]` and drop the "Gated to Linux only" comment block
  in the file header.
- `crates/rush-cli/tests/pty_paint_no_absolute.rs` — same change, same
  cleanup of the linux-only note.
- `crates/rush-cli/tests/pty/mod.rs` — already `#![cfg(unix)]` at the
  top; **leave the inner `#[cfg(target_os = "linux")]` on
  `forkpty_compat` alone** — it's a function-definition gate that
  must stay or you'll hit E0428.

Run `cargo test -p rush-cli --tests` locally to confirm both pty
tests still pass. Then commit + push:

```sh
git add crates/rush-cli/tests/pty_smoke.rs \
        crates/rush-cli/tests/pty_paint_no_absolute.rs
git commit -m "pty: re-enable on macOS; #295 fixed by TIOCNOTTY at exit"
git push
```

CI on macos-aarch64 will now run the pty tests and should be green.
Close [#295](https://github.com/mhasse1/rush/issues/295) with a
comment pointing at the trace + the TIOCNOTTY commit.

**3b. If the test still wedges — chase the wedge one more level.**

Open `/tmp/rush-trace.log` and find the **last line emitted**. Match
it against this table:

| Last trace line                                        | Wedge location                                          | Fix to try                                                                                                                                                                              |
|--------------------------------------------------------|---------------------------------------------------------|-----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `repl returned — main exiting`                         | Between that and `TIOCNOTTY done` — the ioctl itself    | TIOCNOTTY blocked. Try moving it earlier — to `crates/rush-cli/src/repl_v2.rs`'s `Err` arm (right after `read_line Err — breaking loop` trace), wrapping in `#[cfg(unix)] unsafe { ... }`. |
| `TIOCNOTTY done — falling off main`                    | Past TIOCNOTTY, in libc atexit / Rust runtime epilogue   | TIOCNOTTY didn't help. Fallback: accept the kernel-side wedge, keep `cfg(target_os = "linux")`, document the orphan-child trade-off. The bounded reap already protects CI from hangs.    |
| Any earlier line (e.g. `tcsetattr done`, etc.)         | A regression from this round's changes                   | Revert `3e0b95d` locally, re-run, compare. The trace points themselves shouldn't break anything; if they do, that's the bug.                                                            |

For 3b's first row, the patch shape is:

```rust
// in repl_v2.rs, inside the Err arm:
Err(e) => {
    rush_line::trace!("repl_v2", "read_line Err — breaking loop: {e}");
    #[cfg(unix)]
    unsafe {
        libc::ioctl(libc::STDIN_FILENO, libc::TIOCNOTTY);
    }
    rush_line::trace!("repl_v2", "TIOCNOTTY done in Err branch");
    eprintln!("rush: input error: {e}");
    break;
}
```

…and remove the TIOCNOTTY block from `main.rs` so we don't call it
twice. Then re-run.

For 3b's second row, the relevant cfg flip is just on the two test
files — same as 3a's commit message but instead say
"document macOS orphan-child trade-off; bounded reap is durable".
Update this doc's Findings section to reflect that we accept it.

**4. Append your findings.**

Whatever you find, append a fresh `## Findings (YYYY-MM-DD)` section
at the bottom of this file with:

- What the trace's last line was.
- Whether the test passed, timed out, or failed otherwise.
- What you changed (cfg flip / TIOCNOTTY relocation / etc).
- Recommended next step if you didn't fully close it out.

Then commit + push:

```sh
git add docs/investigations/2026-04-29-macos-pty-hang.md \
        .macos-pty-log.txt \
        # plus any test or src files you touched
git commit -m "investigate #295 — <one-line summary>"
git push
```

The Linux-side investigator will pick it up on `git pull`.

---

## TL;DR

The Rust pty test harness (`crates/rush-cli/tests/pty/`) compiles cleanly
and passes on Linux. On macOS, the test process hangs indefinitely —
even though every read inside the harness has a 5-second deadline.
Two CI runs (24965360454, 24971101349) sat in the `Unit Tests` step for
3+ hours each before manual cancel.

We've already ruled out:

- **Compile error.** Originally `ptsname_r` (Linux-only); fixed in `848f255`.
  Then `forkpty` signature mismatch (`*const` vs `*mut` termios/winsize);
  fixed in `da84106` via cfg on function definitions.
- **TIOCSCTTY-on-inherited-fd semantics.** Original harness did the
  manual posix_openpt + setsid + TIOCSCTTY + dup2 dance. We rewrote on
  `libc::forkpty` (commit `edc5aa3`), which is the libc-provided
  "atomically do this correctly per kernel" entry point. Same hang.

So the bug is **not** in how we set up the pty pair — it's somewhere
in the rush↔pty interaction itself. Likely candidates:

1. **Rush's prompt rendering blocks on macOS pty.** Setbg / theme
   detection / OSC 11 background-query / `tcsetattr` round-trip might
   wait for a terminal response that the harness's master end isn't
   replying to. Linux pty defaults differ enough from macOS pty
   defaults that this would be invisible on Linux.

2. **`isatty(STDIN_FILENO)` returns 0 in the forkpty child** for some
   reason on macOS, sending rush down the non-interactive branch
   that reads stdin to EOF and never paints a prompt.

3. **Drop / waitpid hang.** Master fd close + SIGKILL + waitpid path
   may be wedged on macOS in a way Linux isn't. The smoke test has
   5s deadlines on all reads, so a >10s hang implies cleanup, not
   the test logic.

## Repository state

Repo: `~/src/mcp/rush` (or wherever your checkout lives — `cd $(git -C ~/src/mcp/rush rev-parse --show-toplevel)` to confirm).

Pull the latest main first: `git pull origin main`. The brief assumes
HEAD ≥ `da84106`.

Key files:

- `crates/rush-cli/tests/pty/mod.rs` — the harness (forkpty-based).
- `crates/rush-cli/tests/pty_smoke.rs` — the simplest test (echo,
  SIGHUP, exit). Currently `#![cfg(target_os = "linux")]`.
- `crates/rush-cli/tests/pty_paint_no_absolute.rs` — the #292
  regression test. Also linux-gated.
- `crates/rush-line/src/tty.rs` — RawTty (termios + signal handlers).
  Look here for `tcsetattr`, `cfmakeraw`-equivalent mask, and the
  `sigaction` setup.
- `crates/rush-line/src/engine.rs` — `read_line` driver. The
  `EnableBracketedPaste` write at line ~284 is one suspect.
- `crates/rush-line/src/unix_input.rs` — UnixInput::next_event.
- `crates/rush-cli/src/repl_v2.rs` — calls `editor.read_line(...)`.
- `scripts/macos-pty-investigate.sh` — automated repro: flips the
  cfg, builds, runs smoke with 60s wall timeout, captures
  ps/lsof/sample on hang, restores cfg via trap.

## What's already verified

- Linux: `cargo test -p rush-cli --test pty_smoke` passes in ~0.5s.
- Linux: `cargo test -p rush-cli --test pty_paint_no_absolute` passes
  in ~4s (this is the #292 regression test that originally found the
  `\x1b 7` / `\x1b 8` painter leak).
- Linux: full `cargo test --workspace` is green at HEAD.
- macOS: `cargo build -p rush-cli --tests` compiles (after `da84106`).

## What we need

A clear answer to: **where does it hang?** Specifically:

- Does the test fail fast (~5s) with an `expect_prompt` timeout? Then
  the harness isn't seeing rush's prompt — meaning rush either isn't
  printing one, or printed something the prompt detector isn't
  recognizing on macOS.
- Does the test hang >10s? Then something in our Drop/waitpid path
  on the harness side is stuck. Likely `try_wait_blocking` with
  SIGKILL'd child not getting reaped.
- Does the test pass? Then the hang was specific to GitHub's
  macos-aarch64 runner environment and we can flip the cfg to
  `cfg(unix)` permanently.

## Suggested investigation steps

These are suggestions, not a script. Adapt based on what you find.

### 1. Run the automated repro

```sh
cd ~/src/mcp/rush       # or wherever
git pull origin main
./scripts/macos-pty-investigate.sh
```

Read `.macos-pty-log.txt` — that gives you the basic shape (which
test, fast fail vs hang, and ps/lsof/sample if it hung).

### 2. If it hangs, instrument

The script's auto-captured `sample` output should show what syscall
the rush child is blocked on. Common shapes to recognize:

- `kevent` / `read` / `select` → blocked on input (expected — rush is
  in the read loop). If we *also* see no output coming from rush,
  then rush is stuck before painting the prompt.
- `tcsetattr` / `tcgetattr` → termios round-trip might be hanging.
  Investigate `RawTty::enter` in `crates/rush-line/src/tty.rs`.
- `__psynch_cvwait` / `pthread_cond_wait` → some thread internal
  to rush is blocked. The orphan watchdog (`signals.rs`) shouldn't
  be doing this; if it is, that's a smoking gun.

If `sample` doesn't fire because permissions, try `dtrace -p <pid>
-n 'syscall:::entry { @[probefunc] = count(); }'` for ~5s then
Ctrl-C — gives you a syscall histogram.

### 3. Hypothesis-driven probes

If sample/dtruss don't immediately reveal it, narrow the scope:

```sh
# Does rush even start painting a prompt under a forkpty pty?
# Run a stripped-down repro with raw bytes captured.
cargo test -p rush-cli --test pty_smoke -- --nocapture --test-threads=1 2>&1 | head -100
```

If `expect_prompt` timed out, dump what bytes (if any) the master
side did receive before the timeout. The harness's
`read_until` error message includes the partial buffer:
`"got N bytes: \"...\""`. Empty buffer ⇒ rush didn't write
anything. Non-empty ⇒ prompt detection (`» ` substring match)
failed; show the actual bytes.

You may want to add an `eprintln!` to the harness's `expect_prompt`
that dumps the first 200 bytes received whether or not the predicate
matched. Easy temporary hack:

```rust
pub fn expect_prompt(&mut self, timeout: Duration) -> io::Result<Vec<u8>> {
    let r = self.read_until(timeout, |bytes| {
        let s = strip_ansi(bytes);
        s.contains("» ") || s.contains(": ")
    });
    eprintln!("DEBUG expect_prompt: result={:?}", r.as_ref().map(|b| String::from_utf8_lossy(&b[..b.len().min(200)]).to_string()));
    r
}
```

### 4. Compare against `script(1)` / Python's pty.fork

If you suspect rush itself hangs in *any* macOS pty (not just our
harness), reproduce outside cargo:

```sh
script -q /tmp/rush-pty-out.log target/debug/rush-cli
# In rush, type: echo hi
# Ctrl-D to exit script, then look at the log.
cat /tmp/rush-pty-out.log
```

If `script` works but our harness doesn't, the bug is in our harness
setup. If `script` *also* hangs, the bug is in rush's pty handling.

### 5. Check isatty in the child

Add a temporary printf in rush-cli's main.rs right before the repl
runs:

```rust
eprintln!("DEBUG isatty(0)={} isatty(1)={} isatty(2)={}",
    unsafe { libc::isatty(0) },
    unsafe { libc::isatty(1) },
    unsafe { libc::isatty(2) });
```

If any of those return 0 under our forkpty harness on macOS, that's
why rush isn't entering the interactive read loop.

## Reporting back

When you have a finding (or are stuck), **commit the investigation
state and push**:

```sh
git add docs/investigations/2026-04-29-macos-pty-hang.md \
        .macos-pty-log.txt \
        # any other files you modified for the investigation
git commit -m "investigate #295 — <one-line finding>"
git push
```

Append your findings to **this file** under a `## Findings` section
at the end. That keeps everything in one place for the next person
who picks up the thread.

## Permission to:

- ✅ Modify any test file (changes are reversible via git)
- ✅ Add temporary `eprintln!` / `dbg!` to rush source for the
  duration of the investigation
- ✅ Run any `cargo test`, `cargo build`, `dtrace`, `sample`, `lsof`
- ✅ Run `git checkout`, `git diff`, `git stash`, `git restore` freely
- ❌ Do NOT push changes to rush-line/rush-core that aren't reverted
  before commit unless the investigation reveals a real bug worth
  fixing. The goal is to find where it hangs, not refactor.
- ❌ Do NOT modify `/usr/local/bin/rush` or `~/.config/rush/`. Cargo
  builds and runs `target/debug/rush-cli` — that's the only rush
  binary the harness invokes.

## Findings

Investigated 2026-04-29 on rocinante (Darwin 25.4.0 arm64, macOS 26.4.1)
at HEAD `5227966`. See `.macos-pty-log.txt` for raw output.

### Where it hangs

The harness reaches `pty_smoke.rs:46` — the `expect_exit_within(2s)`
call **after** `send_signal(SIGHUP)`. So the prompt-render hypothesis
in TL;DR is **wrong**: rush starts, paints the prompt, runs `echo
hello-from-pty`, and paints a second prompt all correctly on a
forkpty-attached macOS pty. The smoke test's pre-SIGHUP assertions all
pass (`expect_prompt`, `read_until` predicate including `» ` ×2 and
`hello-from-pty`).

After SIGHUP, the harness sees no exit within 2s, sends SIGKILL, then
calls `try_wait_blocking` (waitpid with options=0). That waitpid blocks
indefinitely. Sample:

```
pty_smoke::rush_starts_runs_echo_and_exits_on_sighup ... pty_smoke.rs:46
  PtySession::expect_exit_within ... mod.rs:290
    PtySession::try_wait_blocking ... mod.rs:324
      __wait4  (libsystem_kernel.dylib)
```

`ps -o stat` of the rush-cli child shows `?NEs` — `E` = "exit-in-progress"
per macOS `ps(1)`. The process has already started kernel-side teardown
(empty `lsof`, `(rush-cli)` parens, `sample` rejects with "cannot examine
process for unknown reasons"). On macOS, signals do not get delivered to
processes already in kernel exit teardown, so the SIGKILL the harness sent
2s in is silently ignored, and waitpid waits for an exit that has stalled.

### What this rules in / out

| Hypothesis (from brief)              | Verdict                                    |
|--------------------------------------|--------------------------------------------|
| Prompt rendering blocks (OSC 11 etc) | **Out.** Prompt + echo + 2nd prompt all OK. |
| `isatty` returns 0 in forkpty child  | **Out.** Same: rush is in the interactive read loop. |
| Drop / waitpid hang                  | **In, but a symptom.** The blocking waitpid never returns *because* the SIGKILL'd child is already mid-exit. |
| TIOCSCTTY semantics                  | **Out** (already ruled out, confirmed). |

The new finding is hypothesis 4: **SIGHUP itself wedges rush-cli's
userspace teardown on macOS**, putting the process in kernel state E
where neither SIGKILL nor any other signal can expedite it.

### Isolation: SIGKILL-only variant passes

Probe: temporarily change `pty_smoke.rs:44` from
`send_signal(SIGHUP)` to `send_signal(SIGKILL)`, leave everything else
identical. Test passes in **1.04s**:

```
running 1 test
test rush_starts_runs_echo_and_exits_on_sighup ... ok
test result: ok. 1 passed; ...; finished in 1.04s
```

So:
- The forkpty harness is correct on macOS.
- waitpid on a properly SIGKILL'd child reaps immediately.
- The bug is squarely in rush-cli's **SIGHUP exit path** on macOS — i.e.,
  in something that runs between the SIGHUP handler firing and the
  process completing exit.

### Where the SIGHUP teardown likely wedges

Code path on SIGHUP (per `crates/rush-line/src/tty.rs` and `engine.rs`):

1. `handle_exit` SA — sets `EXIT_PENDING.store(true, Relaxed)`.
2. `RawTty::read_byte` returns `RawByte::Eof`.
3. `engine.rs::read_line` writes `DisableBracketedPaste` to stderr
   (line 297) — note this is during teardown with the harness *not*
   reading the master.
4. `UnixInput` / `RawTty` drop: `tcsetattr` + 3× `sigaction` restore.
5. `read_line` returns to `repl_v2`; main loop sees `should_exit` and breaks.
6. `main` returns; Rust runtime calls `lang_start_internal::exit()` → atexit /
   thread destruction → `_exit`.

Likely culprits, in priority order:

1. **PTY back-pressure on a write during teardown.** Once
   `expect_exit_within` enters its poll loop, nothing reads the master.
   If anything in steps 3–5 writes to stdout/stderr — DisableBracketedPaste,
   theme/colour reset on Drop somewhere, a goodbye newline, etc. — and the
   slave's output buffer fills, the write blocks forever. macOS pty buffers
   are small; even a few hundred bytes during teardown could trip this.
2. **The orphan-watchdog thread (`crates/rush-cli/src/signals.rs:39`)**
   doing something during exit. It's a sleep-loop daemon; on a healthy
   `_exit` it would be reaped by the kernel. But if Rust's runtime exit
   is doing something other than `_exit` (atexit handlers, TLS dtors, etc.)
   that races with the watchdog's syscalls, that could wedge. Linux's
   `nptl` and macOS's `pthread` have different exit semantics for
   sleeping threads.
3. **A blocking `tcsetattr` on a half-broken pty in `RawTty::Drop`** —
   less likely since termios ioctls are usually instant, but possible
   if the slave is in a state where TCSADRAIN-equivalent semantics fire
   under macOS. The Drop uses TCSANOW which shouldn't drain, but worth
   verifying.

### Secondary bug: harness `try_wait_blocking` after SIGKILL

`crates/rush-cli/tests/pty/mod.rs:289-290` (and Drop) calls
`try_wait_blocking` *after* sending SIGKILL. On macOS this can block
indefinitely when the child is already mid-exit. The harness should:
- Use a bounded WNOHANG poll loop with a deadline (say 1s extra), and
  if the child still hasn't exited, return without reaping (let init
  inherit the orphan after the test process itself exits).
- Or, document that `expect_exit_within` is best-effort on macOS.

The same blocking waitpid sits in `Drop` for `PtySession`. A
PtySession Drop on a wedged child will hang the entire test process
on test teardown — exactly what happened in CI runs 24965360454 and
24971101349 (3+ hours each). Even without fixing rush's SIGHUP, fixing
this Drop would let CI fail fast with a real assertion message.

### Investigation-script bug (separate, but reported here)

`scripts/macos-pty-investigate.sh` cannot run a successful build on
macOS as committed. Its `sed -i '' 's/cfg(target_os = "linux")/cfg(unix)/'`
matches the harness's internal `#[cfg(target_os = "linux")]` on
`forkpty_compat` (`tests/pty/mod.rs:386`), turning it into
`#[cfg(unix)]`, which then collides with the macOS variant at
`tests/pty/mod.rs:399` (`#[cfg(not(target_os = "linux"))]`) → E0428
duplicate-definition, build aborts. Two fixes possible:
- Remove `tests/pty/mod.rs` from the script's `FILES` array (it
  doesn't need a flip — it already has `#![cfg(unix)]` at line 30 and
  internal cfg gates on `forkpty_compat`).
- Or use a tighter sed pattern that only matches the inner attribute
  `#![cfg(target_os = "linux")]` (with the leading `#!` and bracketing).

### Recommended next steps

In priority order:

1. **Fix the harness's blocking waitpid** in `expect_exit_within` / `Drop`
   so a hung CI run fails fast with a real assertion instead of timing
   out at the 6h GitHub-actions cap. Use bounded WNOHANG. Low risk.
2. **Instrument rush-cli's SIGHUP teardown** with `eprintln!`s at:
   - SIGHUP handler entry,
   - return-from-`read_line` post-Drop,
   - just before `repl_v2`'s loop break,
   - just before `main` returns,
   then re-run on macOS to find the last log line printed before the
   process enters state E. Strong candidates per priority list above.
3. **Fix the investigate-script sed pattern.** Cheap and unblocks
   future investigators.
4. Once SIGHUP teardown is fixed, flip the test cfg gates from
   `cfg(target_os = "linux")` to `cfg(unix)` in pty_smoke.rs and
   pty_paint_no_absolute.rs and remove the Linux-only header notes.

### Follow-up — done from the Linux side

Picked up after the rocinante session reported back. All three #1-#3
items shipped before the next macOS run.

1. **Bounded waitpid in `expect_exit_within` / `Drop`.** `try_wait_blocking`
   was the wedge — replaced with `reap_with_deadline(2s)`, a WNOHANG
   poll loop. After timeout, the test process gives up and returns;
   the orphaned child is inherited by init when this process itself
   exits. CI runs should now fail in <10 s with a real `TimedOut`
   message instead of hanging 3+ hours. Linux runs unchanged.

2. **Investigation script `sed` scope tightened.** Dropped
   `tests/pty/mod.rs` from the `FILES` array entirely — its top-level
   gate is already `#![cfg(unix)]`, and the inner `#[cfg(target_os =
   "linux")]` on `forkpty_compat` (line 406) must stay as-is to avoid
   E0428 on the parallel non-Linux definition.

3. **SIGHUP teardown instrumented via `crate::trace!`.** New trace
   points at:
   - `tty.rs::RawTty::drop` — entry, post-tcsetattr, post-sigaction
     restores, exit. Each is fsync'd by trace.rs so a kernel-state-E
     wedge doesn't lose the trail.
   - `engine.rs::read_line` — `inner returned ok=… err_kind=…`,
     `DisableBracketedPaste sent`, exit.
   - `repl_v2::run` — `read_line Err — breaking loop`, `loop break —
     running exit trap`, `run() returning`.
   - `main.rs::main` — `repl returned — main exiting`.

   Signal handlers themselves are NOT instrumented (signal-handler
   safety: Mutex/format/write_all aren't async-signal-safe). The
   handler just sets `EXIT_PENDING`; the trace fires when the read
   loop checks the flag and returns `RawByte::Eof`.

### Follow-up — for the next macOS session

Next investigator (probably another rocinante session) should:

1. Pull main and re-run `./scripts/macos-pty-investigate.sh` with
   `RUSH_TRACE=1` exported. The harness now gives up at 12 s with a
   real error; full teardown trace will be in `/tmp/rush-trace.log`.
2. The last trace line in `/tmp/rush-trace.log` names the wedge
   point. Likely candidates per priority:
   - **Stuck before `RawTty::drop start`** → wedge is in something
     that runs *before* read_line's drop chain. Probably the
     `DisableBracketedPaste` write at engine.rs (the master isn't
     being read; pty buffer fills). If so, the fix is to either
     skip Disable on EOF teardown or drain async.
   - **Stuck between `RawTty::drop start` and `tcsetattr done`** →
     `tcsetattr` blocking. Try TCSAFLUSH or TCSANOW with explicit
     buffer drain.
   - **Stuck after `repl_v2 run() returning`** → main()'s atexit /
     TLS dtors / orphan-watchdog thread. Most likely culprit: the
     watchdog thread's `nanosleep` racing with thread-pool teardown.
3. Add findings under a fresh `## Findings (2026-04-NN)` section.

## Findings (2026-04-29 follow-up run)

Re-ran on rocinante (same box, Darwin 25.4.0 arm64) at HEAD `01399ba`
with `RUSH_TRACE=1 ./scripts/macos-pty-investigate.sh`. Bounded reap
worked: harness panicked with the real `TimedOut` message in **8s**
total (vs. 3+h before). See `.macos-pty-log.txt` and the trace below.

### Trace from `/tmp/rush-trace.log`

```
0.000420 [tid=1] [main]      rush start argv=[".../target/debug/rush-cli"]
0.061713 [tid=1] [read_line] enter
0.157677 [tid=1] [fill_queue] byte 0x65   ← 'e'
0.218716 [tid=1] [fill_queue] byte 0x63   ← 'c'
0.272729 [tid=1] [fill_queue] byte 0x68   ← 'h'
   …'echo hello-from-pty\n' echoed and processed…
0.324692 [tid=1] [fill_queue] EOF                                       ← SIGHUP arrived
0.329868 [tid=1] [read_line] inner returned ok=false err_kind=Some(UnexpectedEof)
0.334706 [tid=1] [read_line] DisableBracketedPaste sent
0.338687 [tid=1] [read_line] exit ok=false
0.343687 [tid=1] [tty]       RawTty::drop start
0.348720 [tid=1] [tty]       RawTty::drop tcsetattr done
0.353672 [tid=1] [tty]       RawTty::drop sigaction restores done
0.358704 [tid=1] [tty]       RawTty::drop end
0.363728 [tid=1] [repl_v2]   read_line Err — breaking loop: stdin closed (controlling pty destroyed or signal received)
0.368737 [tid=1] [repl_v2]   loop break — running exit trap if any
0.373751 [tid=1] [repl_v2]   run() returning
0.377714 [tid=1] [main]      repl returned — main exiting
```

Userspace teardown is **clean and fast**: 53 ms from EOF (0.325) to the
last instrumented line (0.378). Every checkpoint fires in order, on the
main thread, with no gap. The watchdog thread (`signals.rs:39`) is
asleep on a 30 s `thread::sleep` and never participates — confirmed by
`tid=1` throughout (the watchdog would have a different tid if it
emitted, which it doesn't anyway).

### Where the wedge actually is

**After `fn main` returns.** The trace's last line is emitted on
`main.rs:256`, immediately before `fn main` falls off its closing brace.
What runs next is the Rust runtime's exit epilogue — drop of `main`'s
locals, `lang_start` cleanup, then libc `exit(3)` → atexit handlers →
`__cxa_finalize` → `_exit`. None of that is rush code; none of it is
instrumentable from inside `fn main`.

Combine with the prior session's `ps -o stat = ?NEs` + empty `lsof` +
`(rush-cli)` parens: by the time the harness samples the wedged child,
the kernel has already begun process teardown — closed all fds and
revoked the slave pty. The "stuck" phase is the **kernel's
controlling-pty disconnect path**, triggered by the session leader
(rush-cli, made one by `forkpty` via `setsid` + `TIOCSCTTY`) calling
`_exit`. That path is what holds the process in state `E`, and it's
the same reason the prior-run SIGKILL was silently dropped.

So none of the three priority candidates from the previous "for the
next session" list match:

| Priority candidate                                | Verdict                                |
|---------------------------------------------------|----------------------------------------|
| Stuck before `RawTty::drop start` (Disable write) | **Out** — `DisableBracketedPaste sent` fires at 0.335. |
| Stuck between drop start and `tcsetattr done`     | **Out** — `tcsetattr done` fires at 0.349. |
| Stuck after `run() returning` (atexit/watchdog)   | **Closer, but not it** — `main exiting` fires at 0.378. The watchdog hasn't woken. The wedge is past *all* of rush's user code. |

### Recommended next fix

The wedge is in the kernel's session-leader exit path interacting with
the slave pty. Two non-mutually-exclusive directions:

1. **Have rush-cli relinquish its controlling terminal before `main`
   returns.** Add `unsafe { libc::ioctl(STDIN_FILENO, libc::TIOCNOTTY); }`
   in the EOF/SIGHUP teardown branch (or just before `main` returns).
   This converts the kernel-side hangup-on-session-leader-exit path
   into a "process exits with no controlling tty" path, which on
   macOS doesn't go through the same teardown wedge.

2. **Accept the kernel-side wedge as a macOS pty quirk** and document
   that the harness's bounded reap is the durable fix. The orphan
   child is harmless: the cleanup trap's `pkill -9 -f 'target/debug/rush-cli'`
   reaps it (verified — `pgrep` after this run returned no debug
   rush-cli children). CI runs would let init reap it after the test
   process exits.

Recommend trying (1) first — it's a one-line change in `repl_v2.rs`'s
exit-trap branch or `main.rs` immediately before the final trace line.
If it works, the test passes outright on macOS; if it doesn't change
behavior, fall back to (2) and flip the cfg gates to `cfg(unix)`
permanently with a note explaining the orphan-child trade-off.

### Minor follow-up: `unsafe_op_in_unsafe_fn` warning

Build emits a single warning at `crates/rush-cli/tests/pty/mod.rs:424`
inside `forkpty_compat` (which is itself `unsafe fn`). Wrap the
`libc::forkpty` call in an explicit `unsafe { … }` to clear it. Cosmetic.

### Follow-up — done from the Linux side (round 2)

Picked up after the rocinante 2026-04-29 follow-up trace landed (commit
`2cf26e6`).

1. **TIOCNOTTY on Unix exit.** Added `libc::ioctl(STDIN_FILENO,
   TIOCNOTTY)` immediately before `fn main` falls off, after the
   `repl returned — main exiting` trace point. Disowns the controlling
   tty before the kernel's session-leader-exit teardown path runs. New
   trace point `TIOCNOTTY done — falling off main` after the ioctl
   confirms it returned.

   Safe in both modes:
   - **Under forkpty / login shell** (rush IS the session leader):
     TIOCNOTTY sends SIGHUP/SIGCONT to the foreground process group
     and disowns. The original SIGHUP handler has already been
     restored by `RawTty::Drop` (`sigaction restores done` line in
     trace), so SIGHUP at this point hits SIG_DFL → terminates. That's
     what we want anyway — we're already on the way out.
   - **Under interactive rush from bash/zsh** (rush is NOT the session
     leader): TIOCNOTTY just makes rush disown the terminal as its
     own ctty. Bash retains its session leadership and its ctty. No
     SIGHUP is sent. Safe.

   ENOTTY is ignored (return value not checked) — happens if stdin
   isn't a terminal, which means the wedge couldn't have happened
   anyway.

2. **`unsafe_op_in_unsafe_fn` warning.** Wrapped the `libc::forkpty`
   calls in explicit `unsafe { … }` blocks inside the cfg-gated
   `forkpty_compat` definitions. Cosmetic; build is now warning-clean
   for these test files.

### Follow-up — for the next macOS session (round 2)

Next investigator should:

1. Pull main, re-run `RUSH_TRACE=1 ./scripts/macos-pty-investigate.sh`.
2. **If the test passes**: great. Flip the cfg gates from
   `cfg(target_os = "linux")` to `cfg(unix)` in `pty_smoke.rs` and
   `pty_paint_no_absolute.rs`, drop the Linux-only header notes, and
   close #295. CI will then run pty tests on macos-aarch64 too.
3. **If the test still fails with TimedOut on SIGHUP**: the kernel
   wedge is happening *before* `fn main` returns — i.e. somewhere
   between `repl_v2 run() returning` and our new `TIOCNOTTY done`
   trace line. Most likely a libc TLS destructor or another atexit
   handler running on Rust's main-thread shutdown. In that case:
   - Try moving TIOCNOTTY *earlier* — to `repl_v2.rs` right after
     the read_line `Err` break, before `eprintln!`. That fires at
     0.364 in the previous trace, well before any teardown.
   - If still wedged, accept option (2) from the previous Findings
     section: bounded reap is durable, orphan child is cleaned up
     by the script's pkill, flip cfg gates with a comment explaining.

## Findings (2026-04-29 round 2 run)

Re-ran on rocinante at HEAD `b3e71ca` (with the round-2 TIOCNOTTY fix
from `3e0b95d` already in tree) via `RUSH_TRACE=1
./scripts/macos-pty-investigate.sh`.

### Build regression in 3e0b95d

First run aborted at the build step:

```
error[E0308]: mismatched types
 --> crates/rush-cli/src/main.rs:275:41
  |
275 |         libc::ioctl(libc::STDIN_FILENO, libc::TIOCNOTTY);
  |                                          ^^^^^^^^^^^^^^^ expected `u64`, found `u32`
```

`libc::TIOCNOTTY` is platform-typed: `c_uint` on Apple, `Ioctl`
(`c_ulong`) on Linux, `c_ulong` on FreeBSD, `c_int` on Solarish/AIX.
`libc::ioctl`'s second arg is always `c_ulong` on macOS. Linux happens
to type-match because `Ioctl` is `c_ulong` there, so `3e0b95d` built on
Linux but not macOS — exactly the inverse of what we needed.

Fix: cast at the call site — `libc::TIOCNOTTY as libc::c_ulong`.
Numeric `as`-cast widens/narrows uniformly across all platforms libc
supports, so this builds clean on Linux, macOS, BSD, and Solarish
without further cfg gates.

### Test result with build fixed

`TimedOut` after SIGHUP, again — `8s` total (vs. `≥3h` before bounded
reap). Trace from `/tmp/rush-trace.log`:

```
0.000410 [tid=1] [main]      rush start argv=[".../target/debug/rush-cli"]
0.063593 [tid=1] [read_line] enter
   …'echo hello-from-pty\n' echoed and processed (bytes 0x65 0x63 0x68…)…
0.335076 [tid=1] [fill_queue] EOF                           ← SIGHUP delivered
0.339031 [tid=1] [read_line] inner returned ok=false err_kind=Some(UnexpectedEof)
0.344065 [tid=1] [read_line] DisableBracketedPaste sent
0.349032 [tid=1] [read_line] exit ok=false
0.354074 [tid=1] [tty]       RawTty::drop start
0.359044 [tid=1] [tty]       RawTty::drop tcsetattr done
0.364093 [tid=1] [tty]       RawTty::drop sigaction restores done
0.368998 [tid=1] [tty]       RawTty::drop end
0.373032 [tid=1] [repl_v2]   read_line Err — breaking loop: stdin closed (controlling pty destroyed or signal received)
0.377100 [tid=1] [repl_v2]   loop break — running exit trap if any
0.382067 [tid=1] [repl_v2]   run() returning
0.386057 [tid=1] [main]      repl returned — main exiting
0.391059 [tid=1] [main]      TIOCNOTTY done — falling off main      ← LAST LINE
```

`TIOCNOTTY done — falling off main` **does** emit (0.391s). The ioctl
returned. So the wedge is **past `fn main`'s closing brace**, in the
Rust runtime exit epilogue → libc `exit(3)` → atexit handlers →
`__cxa_finalize` → `_exit`. TIOCNOTTY at end of main was not the
fix — it dropped into the same kernel teardown wedge.

This matches row 2 of §3b's decision table exactly: "`TIOCNOTTY done
— falling off main` → past TIOCNOTTY, in libc atexit / Rust runtime
epilogue → fallback: accept the kernel-side wedge."

### Why TIOCNOTTY didn't help

Hypothesis: the kernel-side wedge isn't about session-leader status.
It's about the slave pty's pending output buffer. When the kernel
closes the slave fd during process teardown, it does an implicit
`tty_revoke` / `ttyflush_input` that waits for any buffered output to
drain (or for the master to be readable). The harness's
`expect_exit_within(2s)` is in a poll-waitpid loop *not draining the
master*, so the buffer doesn't drain, the kernel close-path waits
forever, and the process sits in state `?NEs`.

TIOCNOTTY only changes the rush process's relationship to the tty
session — it doesn't drain the slave's output buffer. So even with
TIOCNOTTY, the close-time revoke still wedges.

A real fix would need to either (a) drain stdout/stderr explicitly
before exit (Rust's runtime doesn't expose a reliable way to do this
for the static singletons), or (b) have the harness drain the master
in a background thread during `expect_exit_within`. Both are
significantly more invasive than the current bounded-reap shield.

### Verdict: accept the macOS pty quirk

Per §3b row 2's prescription:

- **Keep `cfg(target_os = "linux")`** on `pty_smoke.rs` and
  `pty_paint_no_absolute.rs`. macOS pty tests stay disabled in CI.
- **Bounded reap is the durable fix.** CI fails fast (~12s) with a
  real `TimedOut` if the pty path ever wedges. No more 3+h hangs.
- **Orphan child is cleaned up.** The script's trap calls
  `pkill -9 -f 'target/debug/rush-cli'`; in CI, init reaps after
  the test process exits. Verified: `pgrep` after this run returned
  no debug rush-cli children.
- **TIOCNOTTY stays in `main.rs`** as a defensive measure. It's
  harmless on Linux and on non-pty stdin (ENOTTY ignored), and on
  macOS pty exits it executes cleanly even though it doesn't fix
  the kernel-revoke wedge.

The c_ulong cast fix should ship on its own — it's a real cross-
platform build bug independent of the macOS hang.

#295 should be **closed as wontfix** (or "accepted limitation") with
a comment linking to this Findings section. The bug is not in rush; it
is a macOS kernel pty-revoke-on-session-leader-exit interaction that
has been investigated to a clean stopping point.

### Recommended next step

None for #295 itself. If macOS CI coverage of the pty paths becomes
important, the work shifts to the harness:

- Spawn a background thread in `PtySession` that `read`s and discards
  master output during `expect_exit_within`. This drains the slave
  buffer, allowing the kernel-side revoke to complete.
- Or: switch the test from "expect rush to exit on SIGHUP within 2s"
  to "send SIGHUP, then drain the master while polling waitpid". Same
  observable behaviour, but the pty stays drained.

Either is a one-evening project; not in scope for this round.

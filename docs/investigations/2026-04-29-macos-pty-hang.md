# macOS pty harness hang — investigation brief (#295)

This is a self-contained brief for a Claude Code session running on
**rocinante** (mark's macOS dev box) to investigate issue [#295](https://github.com/mhasse1/rush/issues/295)
hands-on. Read the whole file before starting. Then dig in.

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

(append here as you investigate)

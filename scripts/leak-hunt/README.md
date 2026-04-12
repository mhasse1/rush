# leak-hunt

Diagnostic harness for tracking down a memory leak observed in the Rust
rush binary on macOS and Linux during long interactive sessions.

## What this is

Three small scripts:

- **`monitor.sh`** — samples a target PID's RSS / VSZ / CPU / threads
  every N seconds, writes one CSV row per sample. Exits when the target
  dies. Linux and macOS both supported.
- **`driver.sh`** — emits an infinite (or capped) stream of varied rush
  commands to stdout: variables, arithmetic, function defs, pipelines,
  shell substitution, File ops, error paths, large strings, etc.
  Inline `# category:NAME` comments tag what each block exercises so you
  can correlate growth to which workload caused it.
- **`run-llm.sh`** — orchestrator. Spawns `rush --llm` reading from a
  fifo, kicks off the driver writing to it, starts the monitor sampling
  RSS to CSV. Cleans up everything on exit.

## Usage on trinity (or any Linux box)

```bash
# from the rush repo:
cd scripts/leak-hunt
RUSH_BIN=/usr/local/bin/rush ./run-llm.sh 7200 5    # 2 hours, 5s sample
```

Output lands in `./leak-results/<host>-<ts>/`:

- `rss.csv` — the data you want
- `rush.stdout.log` — rush's wire-protocol responses (usually huge, ignore)
- `rush.stderr.log` — anything rush wrote to stderr
- `cmd.fifo` — temporary; cleaned up on exit

## Reading the CSV

Quick visual: `awk -F, 'NR>1 {print $4}' rss.csv` shows raw RSS in KB.
Plot it; linear growth = constant per-iteration leak; stepped growth
= leak triggered by specific operations. Idle baseline = no leak in
that path.

For attribution: kill the driver, restart it with a single category
enabled (edit `emit_batch` to comment out everything except one
category), run for an hour, see if growth correlates.

## Limitations

- The driver runs commands through `--llm` mode (subprocess pipe). If the
  leak is **interactive REPL only** (reedline-related), this won't catch
  it. We'll need a `--repl` driver via `expect(1)` or similar for that
  surface separately.
- Monitor.sh samples at fixed intervals. A leak that happens in a
  millisecond burst between samples won't show timing-wise but will
  show in cumulative RSS.

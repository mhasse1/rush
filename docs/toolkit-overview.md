# Toolkit Overview

A small family of CLI binaries for structured shell pipelines and LLM
integration. Each ships as a standalone binary on `$PATH` — usable from
zsh, bash, fish, PowerShell, or scripts. No shell language to learn;
compose with standard Unix tools.

## Binaries

### `ai` — query an LLM
- `ai "question"` — single-shot Q&A, streams to stdout as tokens arrive.
- `cat file | ai "summarize"` — piped input becomes part of the prompt.
- `ai --agent "task"` — agentic loop (tool-using; not for simple Q&A).
- Providers: `anthropic` (default), `openai`, `gemini`, `ollama`.
- Flags: `-p PROVIDER`, `-m MODEL` (e.g. `-m claude-sonnet-4-5`).
- Env: `ANTHROPIC_API_KEY` / `OPENAI_API_KEY` / `GEMINI_API_KEY`.
  (Ollama runs locally and needs no key.)

### `objectify` — text → JSON records
- Reads columnar stdin: first non-empty line = headers (whitespace-split
  column names), subsequent non-empty lines = records.
- Emits a JSON array of objects on stdout.
- **Last column captures all remaining text** — handles `ps aux`'s
  `COMMAND` field, paths with spaces, etc.
- Values that parse as int or float become JSON numbers; everything
  else is a string.
- Compose with `jq` for filter / select / sort / aggregate.
- Not a CSV/TSV parser — use `jq -R` or `mlr` if your input already has
  structured separators.

### `mcp-local` — local MCP server
- Speaks JSON-RPC 2.0 over stdio.
- Exposes `shell_execute` / `shell_read_file` tools to the launching
  agent. Each tool call runs in a persistent shell session (cwd, env,
  shell state persist across calls).
- Same wire protocol as `rush --mcp`; toolkit-flavored handshake.

### `mcp-ssh` — remote MCP gateway
- Speaks JSON-RPC 2.0 over stdio.
- Every tool call carries a `host` parameter; the gateway opens (and
  reuses, within a session) a per-host SSH connection and runs the
  command there.
- The remote shell is whatever the user has configured on that host.
- If the toolkit is installed on the remote, `objectify` / `ai` / `jq`
  are available there too — useful for keeping structured pipelines on
  the far side of the SSH boundary.

## Idiomatic chains

The structured-pipeline idiom is `<text-emitting command> | objectify | jq <query>`:

```sh
# Processes using > 5% CPU, as structured records
ps aux | objectify | jq '.[] | select(."%CPU" > 5) | {USER, PID, COMMAND}'

# Disk usage sorted by % used
df -h | objectify | jq 'sort_by(.Use)'

# Container names
docker ps | objectify | jq '.[] | .NAMES'

# Recent commits as records
git log --oneline -n 50 | objectify
```

LLM-in-the-pipeline patterns:

```sh
# Summarize a file
cat README.md | ai "summarize in three bullets"

# Explain query results
ps aux | objectify | jq '.[] | select(."%CPU" > 5)' \
  | ai "what are these processes doing?"

# Generate from structured input
git log --oneline -n 20 | objectify | jq '.[].sha' \
  | ai "draft a release note from these commit subjects"
```

## When NOT to use the toolkit

- **Already-structured input** (CSV / TSV / JSON) — reach for `jq -R`,
  `csvkit`, or `mlr` directly. `objectify` is for column-aligned
  human-readable tables.
- **Very large streams** — the toolkit binaries buffer the full
  pipeline stage before emitting. Not ideal for multi-GB inputs;
  pipeline-friendly Unix tools (`awk`, `grep`, `cut`) keep streaming.
- **Latency-sensitive LLM use** — `ai` streams to stdout as tokens
  arrive (no head-of-line block waiting for the full response), but
  it's still a network round-trip; embed it in scripts where
  ~500 ms-2 s of provider latency is fine, not in the inner loop of
  a tight shell function.

## Source

All four ship from the [rush repo](https://github.com/mhasse1/rush)
under the toolkit pivot. The MCP servers (`mcp-local`, `mcp-ssh`) speak
the same wire protocol as the legacy `rush --mcp` / `rush --mcp-ssh`
entry points — registering either one in your `~/.claude/mcp.json`
works.

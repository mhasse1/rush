#!/usr/bin/env bash
# driver.sh — emit a long, varied stream of rush commands to stdout
# for piping into `rush --llm` (or `rush --mcp` after JSON-wrapping).
#
# Goal: exercise as many rush surfaces as possible in a tight loop so a
# leak in any of them becomes visible in monitor.sh's RSS samples within
# a reasonable wall-clock window.
#
# Usage:
#   driver.sh                  # infinite stream to stdout
#   driver.sh <count>          # emit <count> command-batches then exit
#   driver.sh <count> <delay>  # add a sleep between batches (seconds)
#
# Output: one command per line, suitable for `rush --llm`.
#
# Each batch is N command-categories chosen for breadth. Categories are
# tagged in inline `# category:NAME` comments so you can correlate
# CSV growth to which workload was running.

set -euo pipefail

COUNT="${1:-0}"          # 0 = infinite
DELAY="${2:-0}"

emit_batch() {
    local i=$1

    # category: variables and arithmetic — env table growth
    echo "x_${i} = ${i}"
    echo "y_${i} = x_${i} * 2 + 1"
    echo "puts y_${i}"

    # category: string interpolation — string allocator pressure
    echo "msg_${i} = \"iteration #{${i}} value #{y_${i}}\""
    echo "puts msg_${i}"

    # category: function definition — function table growth
    echo "def fn_${i}(n)"
    echo "  return n * n"
    echo "end"
    echo "puts fn_${i}(${i})"

    # category: array + pipeline ops
    echo "[1, 2, 3, 4, 5] | sum"
    echo "[1, 2, 3, 4, 5] | where | first 3 | as json"

    # category: hash literal + as json (regression for F7)
    echo "{name: \"item${i}\", count: ${i}, ok: true} | as json"

    # category: shell command via subst — process spawn churn
    echo "tag_${i} = \$(echo subst-${i})"
    echo "puts tag_${i}"

    # category: shell command direct
    echo "echo direct-${i}"

    # category: pipeline through shell
    echo "ls / | first 3 | as json"

    # category: File ops — small reads; if File.read leaks, we'll see it
    echo "File.exist?(\"/etc/hostname\")"

    # category: Dir ops
    echo "Dir.list(\".\", :files) | count"

    # category: control flow
    echo "if ${i} % 2 == 0; puts \"even-${i}\"; else; puts \"odd-${i}\"; end"

    # category: loop + block
    echo "3.times { |k| puts \"loop-${i}-#{k}\" }"

    # category: error path — stdlib failure (regression for #200)
    echo "File.read(\"/nope-${i}\")"

    # category: command failure — exit code propagation
    echo "false"
    echo "puts \$?.code"

    # category: large-ish string allocation — scaled up to provoke growth
    echo "big_${i} = \"x\" * 8192"
    echo "puts big_${i}.length"
}

i=0
while true; do
    emit_batch "$i"
    i=$((i + 1))
    if [[ "$COUNT" -gt 0 && "$i" -ge "$COUNT" ]]; then
        break
    fi
    if [[ "$DELAY" != "0" ]]; then
        sleep "$DELAY"
    fi
done

#!/usr/bin/env bash
# driver-stable.sh — like driver.sh but reuses the same variable/function
# names every iteration instead of generating unique names. If the linear
# growth in --llm mode goes away with this driver, we've confirmed the
# leak is F1's persistent Environment accumulating new bindings.
#
# Same category mix as driver.sh; only the naming strategy changes.

set -euo pipefail

COUNT="${1:-0}"
DELAY="${2:-0}"

emit_batch() {
    local i=$1

    # All variable names are constant across iterations — so the env's
    # HashMap has a bounded set of keys, values get replaced in place.
    echo "x = ${i}"
    echo "y = x * 2 + 1"
    echo "puts y"

    echo "msg = \"iteration #{${i}} value #{y}\""
    echo "puts msg"

    # Same function name each iteration — replaced, not accumulated.
    echo "def fn(n)"
    echo "  return n * n"
    echo "end"
    echo "puts fn(${i})"

    echo "[1, 2, 3, 4, 5] | sum"
    echo "[1, 2, 3, 4, 5] | where | first 3 | as json"
    echo "{name: \"item\", count: ${i}, ok: true} | as json"

    echo "tag = \$(echo subst-${i})"
    echo "puts tag"

    echo "echo direct-${i}"
    echo "ls / | first 3 | as json"
    echo "File.exist?(\"/etc/hostname\")"
    echo "Dir.list(\".\", :files) | count"
    echo "if ${i} % 2 == 0; puts \"even-${i}\"; else; puts \"odd-${i}\"; end"
    echo "3.times { |k| puts \"loop-${i}-#{k}\" }"
    echo "File.read(\"/nope-${i}\")"
    echo "false"
    echo "puts \$?.code"

    # Same name; content is large but replaces each iteration.
    echo "big = \"x\" * 8192"
    echo "puts big.length"
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

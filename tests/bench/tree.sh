#!/bin/bash
# tests/bench/tree.sh: bench record integrity (tools/bench.forge `tree` and the dirty-tree refusal of `run`) in
# a scratch git repo; no timing. Usage: tests/bench/tree.sh BENCH (the built tool, e.g. build/bench).
B=$(cd "$(dirname "$1")" && pwd)/$(basename "$1")
T=$(mktemp -d /tmp/forge-benchtree.XXXXXX)
fail=0
ck() { if [ "$2" = "$3" ]; then echo "PASS bench-tree-$1"; else echo "FAIL bench-tree-$1 (got: $3)"; fail=1; fi; }
cd $T && git init -q && git config user.name t && git config user.email t@t && mkdir bench && echo a > x.txt && printf '# h\n' > bench/history.tsv && git add -A && git commit -qm init
S=$(git rev-parse HEAD)
ck clean "$S clean rc=0" "$($B tree --root $T) rc=$?"
echo r >> bench/history.tsv; echo u > untracked.txt
ck own-output-and-untracked "$S clean rc=0" "$($B tree --root $T) rc=$?"
echo b >> x.txt
ck dirty "$S dirty: x.txt rc=1" "$($B tree --root $T) rc=$?"
h0=$(cat bench/history.tsv)
o=$($B run --root $T 2>&1); r=$?
case "$o" in "error: refusing to record a bench run on uncommitted changes (x.txt)"*) o=refused;; esac
ck run-refuses-dirty "refused rc=2 history unchanged" "$o rc=$r history $([ "$(cat bench/history.tsv)" = "$h0" ] && echo unchanged || echo changed)"
cd / && rm -rf $T
exit $fail

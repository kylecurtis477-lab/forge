#!/bin/sh
# Runs tests/*.forge with the self-hosted compiler and diffs against .expected; checks error tests exit 1 with "error: line".
cd "$(dirname "$0")"; fail=0
for t in tests/*.forge; do
  ./forge "$t" -o build/t_bin && build/t_bin a b | diff -q - "${t%.forge}.expected" >/dev/null && echo "PASS $t" || { echo "FAIL $t"; fail=1; }
done
for t in tests/errors/*.forge; do
  m=$(build/forgec "$t" build/t_err.s 2>&1) && { echo "FAIL $t (no error)"; fail=1; } || { echo "$m" | grep -q "error: line" && echo "PASS $t: $m" || { echo "FAIL $t"; fail=1; }; }
done
exit $fail

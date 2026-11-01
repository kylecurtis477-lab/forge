#!/bin/sh
# tests/*.forge: stdout must match .expected
# tests/runtime/*.forge: stdout+stderr+exit code must match .expected (line-numbered runtime errors)
# tests/errors/*.forge: compiler must fail with "error: line N: ..."
cd "$(dirname "$0")"; fail=0; mkdir -p build
for t in tests/*.forge; do
  ./forge "$t" -o build/t_bin && build/t_bin a b | diff -q - "${t%.forge}.expected" >/dev/null && echo "PASS $t" || { echo "FAIL $t"; fail=1; }
done
for t in tests/runtime/*.forge; do
  ./forge "$t" -o build/t_bin && (build/t_bin 2>&1; echo "exit $?") | diff -q - "${t%.forge}.expected" >/dev/null && echo "PASS $t" || { echo "FAIL $t"; fail=1; }
done
for t in tests/errors/*.forge; do
  m=$(build/forgec "$t" build/t_err.s 2>&1) && { echo "FAIL $t (no error)"; fail=1; } || { echo "$m" | grep -q "error: line" && echo "PASS $t: $m" || { echo "FAIL $t"; fail=1; }; }
done
exit $fail

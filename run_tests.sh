#!/bin/sh
# tests/*.forge: stdout must match .expected
# tests/runtime/*.forge: stdout+stderr+exit code must match .expected (line-numbered runtime errors)
# tests/errors/*.forge: compiler must fail with "error: line N: ..."
# tests/fs/*.forge: freestanding (--freestanding / no-runtime pragma) programs linked with plain ld, no runtime
# tests/typeerrors/*.forge: static type errors; full diagnostic (line, col, source, caret) must match .expected
cd "$(dirname "$0")"; fail=0; mkdir -p build
for t in tests/*.forge; do
  case "$t" in tests/mod_*) continue;; esac
  ./forge "$t" -o build/t_bin && build/t_bin a b | diff -q - "${t%.forge}.expected" >/dev/null && echo "PASS $t" || { echo "FAIL $t"; fail=1; }
done
for t in tests/runtime/*.forge; do
  ./forge "$t" -o build/t_bin && (build/t_bin 2>&1; echo "exit $?") | diff -q - "${t%.forge}.expected" >/dev/null && echo "PASS $t" || { echo "FAIL $t"; fail=1; }
done
for t in tests/errors/*.forge; do
  m=$(build/forgec "$t" build/t_err.s 2>&1) && { echo "FAIL $t (no error)"; fail=1; } || { echo "$m" | grep -q "error: line" && echo "PASS $t: $m" || { echo "FAIL $t"; fail=1; }; }
done
for t in tests/typeerrors/*.forge; do
  build/forgec "$t" build/t_err.s > build/t_err.txt 2>&1 && { echo "FAIL $t (no error)"; fail=1; } || { diff -q build/t_err.txt "${t%.forge}.expected" >/dev/null && echo "PASS $t: $(head -1 build/t_err.txt)" || { echo "FAIL $t"; fail=1; }; }
done
for t in tests/fs/*.forge; do
  build/forgec "$t" build/t_fs.s && as -o build/t_fs.o build/t_fs.s && ld -o build/t_fs build/t_fs.o && build/t_fs | diff -q - "${t%.forge}.expected" >/dev/null && echo "PASS $t" || { echo "FAIL $t"; fail=1; }
done
exit $fail

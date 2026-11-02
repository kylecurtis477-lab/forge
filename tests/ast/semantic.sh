#!/bin/sh
# Pretty-printed tests/*.forge must compile and produce the same .expected output.
cd "$(dirname "$0")/../.."; fail=0; n=0
for t in tests/*.forge; do
  case "$t" in tests/mod_*) continue;; esac
  n=$((n+1)); b=$(basename "$t")
  o="$(dirname "$t")/.pp_$b"; build/forgec "$t" "$o" --emit=pretty && ./forge "$o" -o build/pp_bin 2>/dev/null && build/pp_bin a b | diff -q - "${t%.forge}.expected" >/dev/null || { echo "FAIL ast-pretty-semantic $t"; fail=1; }
  rm -f "$o"
done
[ $fail = 0 ] && echo "PASS ast-pretty-semantic ($n programs)"
exit $fail

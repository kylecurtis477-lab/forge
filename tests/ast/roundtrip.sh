#!/bin/sh
# AST round trip: source -> AST(json) -> pretty-printer -> source' -> AST' ; AST == AST' (line numbers ignored).
# Also: pretty(source') == source' (the pretty-printer is a fixpoint).  Usage: roundtrip.sh files...
cd "$(dirname "$0")/../.."; fail=0; n=0
for f in "$@"; do
  n=$((n+1))
  build/forgec "$f" build/rt_a.json --emit=ast-json > build/rt_err.txt 2>&1 || { echo "FAIL ast-roundtrip $f (parse: $(head -1 build/rt_err.txt))"; fail=1; continue; }
  build/forgec "$f" build/rt_p.forge --emit=pretty || { echo "FAIL ast-roundtrip $f (pretty)"; fail=1; continue; }
  build/forgec build/rt_p.forge build/rt_b.json --emit=ast-json > build/rt_err.txt 2>&1 || { echo "FAIL ast-roundtrip $f (reparse: $(head -1 build/rt_err.txt))"; fail=1; continue; }
  build/forgec build/rt_p.forge build/rt_p2.forge --emit=pretty
  sed -e 's/,"l":[0-9]*//g' -e 's/"file":"[^"]*",//' build/rt_a.json > build/rt_a2.json
  sed -e 's/,"l":[0-9]*//g' -e 's/"file":"[^"]*",//' build/rt_b.json > build/rt_b2.json
  cmp -s build/rt_a2.json build/rt_b2.json || { echo "FAIL ast-roundtrip $f (AST differs)"; fail=1; continue; }
  cmp -s build/rt_p.forge build/rt_p2.forge || { echo "FAIL ast-roundtrip $f (pretty not a fixpoint)"; fail=1; continue; }
done
[ $fail = 0 ] && echo "PASS ast-roundtrip ($n files)"
exit $fail

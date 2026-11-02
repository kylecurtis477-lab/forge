#!/bin/sh
# Grammar conformance (docs/GRAMMAR.md): valid/* must parse, invalid/* must be rejected
# with a parse diagnostic (E0100-E0105, never a crash), and the grammar fuzzer must pass.
cd "$(dirname "$0")/../.."; fail=0; nv=0; ni=0
for t in tests/grammar/valid/*.forge; do
  nv=$((nv+1)); build/forgec "$t" build/g.json --emit=ast-json > build/g_err.txt 2>&1 || { echo "FAIL grammar-valid $t: $(head -1 build/g_err.txt)"; fail=1; }
done
for t in tests/grammar/invalid/*.forge; do
  ni=$((ni+1)); build/forgec "$t" build/g.json --emit=ast-json --diagnostics=json > build/g_err.txt 2>&1; r=$?
  if [ $r = 1 ] && grep -q '"code":"E010[0-5]"' build/g_err.txt; then :; else echo "FAIL grammar-invalid $t (exit $r: $(head -c 200 build/g_err.txt))"; fail=1; fi
done
[ $fail = 0 ] && echo "PASS grammar-conformance ($nv valid, $ni invalid)"
python3 tools/grammar_fuzz.py --seed 1 --n 160 || fail=1
exit $fail

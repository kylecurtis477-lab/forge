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
# the Forge fuzzer (tools/grammar_fuzz.py stays as the reference; tests/grammar-fuzz checks they match)
mkdir -p build/grammar-fuzz
if build/forgec tools/grammar_fuzz.forge build/grammar-fuzz/gf.s && build/fas -o build/grammar-fuzz/gf.o build/grammar-fuzz/gf.s && build/fld -o build/grammar-fuzz/grammar_fuzz build/grammar-fuzz/gf.o; then
  build/grammar-fuzz/grammar_fuzz --seed 1 --n 160 || fail=1
else echo "FAIL grammar-fuzz build"; fail=1; fi
exit $fail

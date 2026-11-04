#!/bin/bash
# Phase 4.3 unsafe gating: ok.forge builds and runs; each bad_*.forge is rejected with its "# expect:" text;
# the --unsafe-report counts of the freestanding programs must not grow past baseline.txt.
cd "$(dirname "$0")"
FC=$PWD/../../build/forgec
T=$(mktemp -d)
fail=0
if $FC ok.forge $T/ok.s && ../../build/fas -o $T/ok.o $T/ok.s && ../../build/fld -o $T/ok $T/ok.o && [ "$($T/ok)" = "unsafe ok" ]; then
    echo "PASS unsafe-ok"
else
    echo "FAIL unsafe-ok"; fail=1
fi
for f in bad_*.forge; do
    want=$(sed -n 's/^# expect: //p' $f)
    out=$($FC $f $T/x.s 2>&1)
    if [ $? -ne 0 ] && echo "$out" | grep -qF -- "$want"; then
        echo "PASS unsafe-${f%.forge}"
    else
        echo "FAIL unsafe-${f%.forge}: $out"; fail=1
    fi
done
# baseline: "<dir> <file> <functions> <blocks> <ops> <asm>"
while read d f bf bb bo ba; do
    [ -z "$d" ] || [ "${d:0:1}" = "#" ] && continue
    s=$(cd ../../$d && $FC $f $T/r.s --unsafe-report 2>&1 | sed -n 's/^unsafe: \([0-9]*\) functions, \([0-9]*\) blocks, \([0-9]*\) ops, \([0-9]*\) top-level.*/\1 \2 \3 \4/p')
    set -- $s
    if [ -n "$s" ] && [ $1 -le $bf ] && [ $2 -le $bb ] && [ $3 -le $bo ] && [ $4 -le $ba ]; then
        echo "PASS unsafe-baseline-$f ($s)"
    else
        echo "FAIL unsafe-baseline-$f: now '$s', baseline '$bf $bb $bo $ba'"; fail=1
    fi
done < baseline.txt
# provenance (v1.3): integer-to-pointer only inside @unsafe functions (--provenance-lint)
out=$($FC prov_block.forge $T/p.s --provenance-lint 2>&1)
if [ $? -ne 0 ] && echo "$out" | grep -qF "provenance lint failed"; then
    echo "PASS unsafe-prov-block"
else
    echo "FAIL unsafe-prov-block: $out"; fail=1
fi
while read d f bf bb bo ba; do
    [ -z "$d" ] || [ "${d:0:1}" = "#" ] && continue
    s=$(cd ../../$d && $FC $f $T/r.s --provenance-lint 2>&1 | grep '^provenance:')
    if echo "$s" | grep -q ', 0 outside @unsafe'; then
        echo "PASS unsafe-prov-$f ($s)"
    else
        echo "FAIL unsafe-prov-$f: $s"; fail=1
    fi
done < baseline.txt
rm -rf $T
exit $fail

#!/bin/bash
# Phase 4.4 ownership: ok.forge builds and runs; each bad_*.forge is rejected with its "# expect:" text.
cd "$(dirname "$0")"
FC=$PWD/../../build/forgec
T=$(mktemp -d)
fail=0
if $FC ok.forge $T/ok.s && ../../build/fas -o $T/ok.o $T/ok.s && ../../build/fld -o $T/ok $T/ok.o && [ "$($T/ok)" = "own ok" ]; then
    echo "PASS own-ok"
else
    echo "FAIL own-ok"; fail=1
fi
if $FC field_layout.forge $T/field.s && ../../build/fas -o $T/field.o $T/field.s && ../../build/fld -o $T/field $T/field.o && $T/field | diff -q - field_layout.expected >/dev/null; then
    echo "PASS own-field-layout"
else
    echo "FAIL own-field-layout"; fail=1
fi
for f in bad_*.forge; do
    want=$(sed -n 's/^# expect: //p' $f)
    out=$($FC $f $T/x.s 2>&1)
    if [ $? -ne 0 ] && echo "$out" | grep -qF -- "$want"; then
        echo "PASS own-${f%.forge}"
    else
        echo "FAIL own-${f%.forge}: $out"; fail=1
    fi
done
rm -rf $T
exit $fail

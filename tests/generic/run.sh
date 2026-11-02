#!/bin/bash
# Phase 4.5b generics: ok.forge instantiates generic defs/structs (explicit and inferred);
# std.forge exercises std/vec.fsi + std/map.fsi; oob.forge must trap;
# each bad_*.forge is rejected with its "# expect:" text.
cd "$(dirname "$0")"
FC=$PWD/../../build/forgec
T=$(mktemp -d)
fail=0
if $FC ok.forge $T/ok.s $FORGEC_FLAGS && as -o $T/ok.o $T/ok.s && ld -o $T/ok $T/ok.o && [ "$($T/ok)" = "$(cat ok.expected)" ]; then
    echo "PASS generic-ok"
else
    echo "FAIL generic-ok: $($T/ok)"; fail=1
fi
if $FC std.forge $T/std.s $FORGEC_FLAGS && as -o $T/std.o $T/std.s && ld -o $T/std $T/std.o && [ "$($T/std)" = "$(cat std.expected)" ]; then
    echo "PASS generic-std-vec-map"
else
    echo "FAIL generic-std-vec-map: $($T/std)"; fail=1
fi
if $FC oob.forge $T/oob.s $FORGEC_FLAGS && as -o $T/oob.o $T/oob.s && ld -o $T/oob $T/oob.o && ! $T/oob >/dev/null 2>&1; then
    echo "PASS generic-vec-oob-trap"
else
    echo "FAIL generic-vec-oob-trap"; fail=1
fi
for f in bad_*.forge; do
    want=$(sed -n 's/^# expect: //p' $f)
    out=$($FC $f $T/x.s 2>&1)
    if [ $? -ne 0 ] && echo "$out" | grep -qF -- "$want"; then
        echo "PASS generic-${f%.forge}"
    else
        echo "FAIL generic-${f%.forge}: $out"; fail=1
    fi
done
rm -rf $T
exit $fail

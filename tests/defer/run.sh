#!/bin/bash
# Phase 4.5 defer: ok.forge prints ok.expected (LIFO order, every return path, rax kept);
# each bad_*.forge is rejected with its "# expect:" text.
cd "$(dirname "$0")"
FC=$PWD/../../build/forgec
T=$(mktemp -d)
fail=0
if $FC ok.forge $T/ok.s $FORGEC_FLAGS && as -o $T/ok.o $T/ok.s && ld -o $T/ok $T/ok.o && [ "$($T/ok)" = "$(cat ok.expected)" ]; then
    echo "PASS defer-ok"
else
    echo "FAIL defer-ok: $($T/ok)"; fail=1
fi
for f in bad_*.forge; do
    want=$(sed -n 's/^# expect: //p' $f)
    out=$($FC $f $T/x.s 2>&1)
    if [ $? -ne 0 ] && echo "$out" | grep -qF -- "$want"; then
        echo "PASS defer-${f%.forge}"
    else
        echo "FAIL defer-${f%.forge}: $out"; fail=1
    fi
done
rm -rf $T
exit $fail

#!/bin/bash
# Phase 4.6 typed atomics: ok.forge (safe mode) prints ok.expected; asm.forge's codegen uses the
# TSO mapping (release store = mov, seq_cst store = xchg, seq_cst fence = mfence, RMW = lock);
# each bad_*.forge is rejected with its "# expect:" text.
cd "$(dirname "$0")"
FC=$PWD/../../build/forgec
T=$(mktemp -d)
fail=0
if $FC ok.forge $T/ok.s $FORGEC_FLAGS && as -o $T/ok.o $T/ok.s && ld -o $T/ok $T/ok.o && [ "$($T/ok)" = "$(cat ok.expected)" ]; then
    echo "PASS atomic-ok"
else
    echo "FAIL atomic-ok: $($T/ok)"; fail=1
fi
if $FC ok.forge $T/a.s $FORGEC_FLAGS && grep -q 'xchgw' $T/a.s && grep -q 'mfence' $T/a.s && grep -q 'lock xaddb' $T/a.s && grep -q 'lock cmpxchgl' $T/a.s && [ "$(grep -c mfence $T/a.s)" = 1 ]; then
    echo "PASS atomic-codegen"
else
    echo "FAIL atomic-codegen"; fail=1
fi
for f in bad_*.forge; do
    want=$(sed -n 's/^# expect: //p' $f)
    out=$($FC $f $T/x.s 2>&1)
    if [ $? -ne 0 ] && echo "$out" | grep -qF -- "$want"; then
        echo "PASS atomic-${f%.forge}"
    else
        echo "FAIL atomic-${f%.forge}: $out"; fail=1
    fi
done
rm -rf $T
exit $fail

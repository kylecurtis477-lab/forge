#!/bin/bash
# Phase 4.1: slices + bounds checks — traps with precise diagnostics, compile-time errors, elision count, overhead
cd "$(dirname "$0")/../.." || exit 1
T=$(mktemp -d); fail=0
ok() { echo "PASS $1"; }; no() { echo "FAIL $1"; fail=1; }
trap_case() { # name, expected diagnostic
    ./forge tests/bounds/$1.forge -o $T/$1 >/dev/null 2>&1 || { no bounds-$1; return; }
    $T/$1 > $T/$1.out 2>&1; rc=$?
    [ $rc = 134 ] && grep -qF "$2" $T/$1.out && ok bounds-$1 || { no bounds-$1; cat $T/$1.out; }
}
trap_case oob_slice "bounds check failed: tests/bounds/oob_slice.forge:5: index 3 out of range for length 3"
trap_case oob_array "bounds check failed: tests/bounds/oob_array.forge:7: index 18446744073709551615 out of range for length 5"
trap_case oob_sub "bounds check failed: tests/bounds/oob_sub.forge:7: index 6 out of range for length 5"
# the in-range call before the trap still ran
grep -qx 0 $T/oob_slice.out && ok bounds-before-trap || no bounds-before-trap
cf_case() { # name, expected error
    ./forge tests/bounds/$1.forge -o $T/$1 > $T/$1.err 2>&1 && { no bounds-$1; return; }
    grep -qF "$2" $T/$1.err && ok bounds-$1 || { no bounds-$1; cat $T/$1.err; }
}
cf_case cf_const "index 5 is out of bounds for an array of length 5"
cf_case cf_mismatch "slice type mismatch"
# without a forge_bounds_fail handler the check is a bare ud2 (SIGILL)
printf '# forge: no-runtime\n@export\ndef _start():\n    a: u8[2]\n    i: u64 = 2\n    a[i] = 0\n' > $T/bare.forge
build/forgec $T/bare.forge $T/bare.s && grep -q ud2 $T/bare.s && ok bounds-bare-ud2 || no bounds-bare-ud2
# elision: loops over range(len(x)) / range(K<=N) drop the check; assigning the loop var keeps it
build/forgec tests/bounds/elide.forge $T/e.s --bounds-stats 2> $T/e.st
grep -qx "bounds: 8 sites, 5 checked, 3 elided" $T/e.st && ok bounds-elide-count || { no bounds-elide-count; cat $T/e.st; }
./forge tests/bounds/elide.forge -o $T/e && [ "$($T/e)" = 197 ] && ok bounds-elide-run || no bounds-elide-run
# --no-bounds and the pragma drop every check
build/forgec tests/bounds/oob_sub.forge $T/n.s --no-bounds --bounds-stats 2> $T/n.st
grep -q " 0 checked" $T/n.st && ok bounds-off-flag || no bounds-off-flag
sed '1a # forge: no-bounds-checks' tests/bounds/oob_array.forge > tests/bounds/.p.forge
build/forgec tests/bounds/.p.forge $T/p.s --bounds-stats 2> $T/p.st; rm -f tests/bounds/.p.forge
grep -q " 0 checked" $T/p.st && ok bounds-off-pragma || no bounds-off-pragma
# overhead: checked vs --no-bounds, best of 5 each, must be <= 5% slower
./forge tests/bounds/bench.forge -o $T/bc && FORGEC_FLAGS=--no-bounds ./forge tests/bounds/bench.forge -o $T/bn
[ "$($T/bc)" = "$($T/bn)" ] && ok bounds-bench-same || no bounds-bench-same
best() { b=999999; for k in 1 2 3 4 5; do s=$(date +%s%N); $1 >/dev/null; d=$(( ($(date +%s%N)-s)/1000 )); [ $d -lt $b ] && b=$d; done; echo $b; }
c=$(best $T/bc); n=$(best $T/bn)
build/forgec tests/bounds/bench.forge $T/b.s --bounds-stats 2> $T/b.st
echo "bounds bench: checked ${c}us, unchecked ${n}us, $(cat $T/b.st)"
[ $((c * 100)) -le $((n * 105)) ] && ok bounds-bench-overhead || no bounds-bench-overhead
rm -rf $T; exit $fail

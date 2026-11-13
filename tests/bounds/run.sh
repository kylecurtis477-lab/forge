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
# -O0 turns elision off: every site keeps its check (also for in-range constant indices), same output
build/forgec tests/bounds/elide.forge $T/e0.s -O0 --bounds-stats 2> $T/e0.st
grep -qx "bounds: 8 sites, 8 checked, 0 elided" $T/e0.st && ok bounds-elide-off-O0 || { no bounds-elide-off-O0; cat $T/e0.st; }
build/fas -o $T/e0.o $T/e0.s && build/fld -o $T/e0 $T/e0.o && [ "$($T/e0)" = 197 ] && ok bounds-elide-off-O0-run || no bounds-elide-off-O0-run
# --no-bounds and the pragma drop every check
build/forgec tests/bounds/oob_sub.forge $T/n.s --no-bounds --bounds-stats 2> $T/n.st
grep -q " 0 checked" $T/n.st && ok bounds-off-flag || no bounds-off-flag
sed '1a # forge: no-bounds-checks' tests/bounds/oob_array.forge > tests/bounds/.p.forge
build/forgec tests/bounds/.p.forge $T/p.s --bounds-stats 2> $T/p.st; rm -f tests/bounds/.p.forge
grep -q " 0 checked" $T/p.st && ok bounds-off-pragma || no bounds-off-pragma
# overhead: checked vs --no-bounds, must be <= 5% slower. 21 pairs; a pair is 3 interleaved checked/unchecked
# rounds (the order alternates every round so drift and warm-up favour neither) and its ratio is best checked /
# best unchecked of those 3, in basis points; the test takes the median of the 21 pair ratios. Comparing the best
# checked run with the best unchecked run of the whole series failed twice under gate load with byte-identical
# code: inv138 O2 (best of 7, 6.7%; three re-runs gave 5.7%, -3.3% and 4.3%) and inv156 O2 (best of 21, 6.95%:
# one lucky fast unchecked run, 286 ms against 287-334 ms in the ten gates before). Single-run pairs were too
# noisy on the box (pair ratios 0.63-2.66, medians of 21 from 0.997 to 1.067 with identical code); best of 3 per
# pair keeps the pair ratios near 0.9-1.17 and the median of 21 at 1.009-1.023 (measured 2026-11-13).
./forge tests/bounds/bench.forge -o $T/bc && FORGEC_FLAGS=--no-bounds ./forge tests/bounds/bench.forge -o $T/bn
[ "$($T/bc)" = "$($T/bn)" ] && ok bounds-bench-same || no bounds-bench-same
tm() { s=$(date +%s%N); $1 >/dev/null; echo $(( ($(date +%s%N)-s)/1000 )); }
: > $T/ratios
for k in $(seq 1 21); do
  c=999999999; n=999999999
  for j in 1 2 3; do
    if [ $(((k + j) % 2)) = 1 ]; then d=$(tm $T/bc); [ $d -lt $c ] && c=$d; d=$(tm $T/bn); [ $d -lt $n ] && n=$d
    else d=$(tm $T/bn); [ $d -lt $n ] && n=$d; d=$(tm $T/bc); [ $d -lt $c ] && c=$d; fi
  done
  [ $n -gt 0 ] || n=1
  echo $((c * 10000 / n)) >> $T/ratios
done
med=$(sort -n $T/ratios | sed -n 11p); lo=$(sort -n $T/ratios | head -1); hi=$(sort -n $T/ratios | tail -1)
build/forgec tests/bounds/bench.forge $T/b.s --bounds-stats 2> $T/b.st
r4() { echo "$(($1 / 10000)).$(printf %04d $(($1 % 10000)))"; }
echo "bounds bench: median checked/unchecked $(r4 $med) over 21 pairs of best-of-3 (min $(r4 $lo), max $(r4 $hi)," \
  "limit 1.0500), last pair checked ${c}us unchecked ${n}us, $(cat $T/b.st)"
[ $med -le 10500 ] && ok bounds-bench-overhead || no bounds-bench-overhead
rm -rf $T; exit $fail

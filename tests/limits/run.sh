#!/bin/sh
# Compiler table limits must end in a diagnostic, never an internal index error.
# fs mode: a function may have 4096 locals (fsl_* tables, FSLMAX); the 4097th, counting own-field slots, is
# "too many locals in one function" at the offending line. Generated programs, nothing checked in but this.
cd "$(dirname "$0")"
FC=$PWD/../../build/forgec; FAS=$PWD/../../build/fas; FLD=$PWD/../../build/fld
T=$(mktemp -d); fail=0
gen() {  # gen N FILE: f() with N scalar locals v0..v(N-1), returns v(N-1) % 256
    { echo "# forge: no-runtime"; echo "include \"$PWD/../fs/linux.fsi\""; echo "def f() -> u64:"
      i=0; while [ $i -lt $1 ]; do echo "    v$i = $i"; i=$((i+1)); done
      echo "    return v$(($1-1)) % 256"; echo "def main() -> u64:"; echo "    return f()"; } > $2
}
gen 4096 $T/ok.forge
if $FC $T/ok.forge $T/ok.s && $FAS -o $T/ok.o $T/ok.s && $FLD -o $T/ok $T/ok.o; then
    $T/ok; rc=$?
    [ $rc -eq $((4095 % 256)) ] && echo "PASS limits-locals-4096 (compiles, exit $rc)" || { echo "FAIL limits-locals-4096 (exit $rc)"; fail=1; }
else
    echo "FAIL limits-locals-4096 (does not build)"; fail=1
fi
gen 4097 $T/over.forge
out=$($FC $T/over.forge $T/over.s 2>&1); rc=$?
if [ $rc -ne 0 ] && echo "$out" | grep -qF "error: line 4100: too many locals in one function (limit 4096"; then
    echo "PASS limits-locals-4097"
else
    echo "FAIL limits-locals-4097 (rc $rc): $out"; fail=1
fi
# own-field slots count too: 4095 scalars, then a struct local with two own fields needs three entries
{ echo "# forge: no-runtime"; echo "struct Node:"; echo "    id: u64"; echo "struct Pair:"; echo "    a: own[Node]"; echo "    b: own[Node]"
  echo "def f() -> u64:"; i=0; while [ $i -lt 4094 ]; do echo "    v$i = $i"; i=$((i+1)); done
  echo "    p: Pair"; echo "    return 0"; } > $T/own.forge
out=$($FC $T/own.forge $T/own.s 2>&1); rc=$?
if [ $rc -ne 0 ] && echo "$out" | grep -qF "too many locals in one function (limit 4096" && ! echo "$out" | grep -q "out of range"; then
    echo "PASS limits-locals-own-slots"
else
    echo "FAIL limits-locals-own-slots (rc $rc): $out"; fail=1
fi
rm -rf $T
exit $fail

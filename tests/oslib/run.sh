#!/bin/sh
# tests/oslib: the hosted os library's directory and process calls (tests/oslib/oslib.forge) at the current
# FORGEC_FLAGS optimisation level; every check line must say "ok".
cd "$(dirname "$0")/../.." || exit 1
T=$(mktemp -d); trap 'rm -rf $T' EXIT
build/forgec tests/oslib/oslib.forge $T/o.s $FORGEC_FLAGS && build/fas -o $T/o.o $T/o.s && build/fld --base 0x100000000000 -o $T/oslib $T/o.o || { echo "FAIL oslib build"; exit 1; }
mkdir $T/d
$T/oslib $T/d >$T/out 2>&1; r=$?
fail=0
while read -r s n; do
  if [ "$s" = ok ]; then echo "PASS oslib $n"; else echo "FAIL oslib $s $n"; fail=1; fi
done < $T/out
[ $r = 0 ] || { echo "FAIL oslib exit $r"; fail=1; }
[ $(grep -c '^ok ' $T/out) = 21 ] || { echo "FAIL oslib count"; fail=1; }
exit $fail

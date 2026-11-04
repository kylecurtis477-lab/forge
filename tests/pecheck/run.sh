#!/bin/sh
# tests/pecheck: tests/fld/pecheck.forge must agree with tests/fld/pecheck.py (kept as the reference):
# identical stdout and exit code on the real fld PE image, and the same exit code (1) on corrupted images
# (Python fails with an AssertionError traceback, Forge with a one-line `pecheck: ...` message).
cd "$(dirname "$0")/../.." || exit 1
T=$(mktemp -d); trap 'rm -rf $T' EXIT
fail=0
build/forgec tests/fld/pecheck.forge $T/p.s && build/fas -o $T/p.o $T/p.s && build/fld --base 0x100000000000 -o $T/pecheck $T/p.o || { echo "FAIL pecheck build"; exit 1; }
[ -f build/efi/loader.o ] || { echo "SKIP pecheck (no build/efi/loader.o)"; exit 0; }
command -v python3 >/dev/null || { echo "SKIP pecheck (needs python3 for the reference)"; exit 0; }
build/fld --format pe --subsystem 10 --image-base 0x10000000 -e efi_main -o $T/l.efi build/efi/loader.o || { echo "FAIL pecheck pe link"; exit 1; }
# patch NAME OFFSET-EXPR BYTE: copy l.efi with one byte replaced (offsets relative to the PE header for 'pe+')
patch() {
  cp $T/l.efi $T/$1.efi
  pe=$(od -An -tu4 -j60 -N4 $T/l.efi | tr -d ' ')
  printf "$3" | dd of=$T/$1.efi bs=1 seek=$(($2)) conv=notrunc 2>/dev/null
}
patch nomz 0 'X'
patch nope "pe" 'Q'
patch mach "pe+4" '\001'
patch subsys "pe+24+68" '\003'
patch nx "pe+24+71" '\000'
patch salign "pe+24+33" '\002'
for n in l nomz nope mach subsys nx salign; do
  python3 tests/fld/pecheck.py $T/$n.efi >$T/py.out 2>/dev/null; pr=$?
  $T/pecheck $T/$n.efi >$T/fo.out 2>/dev/null; fr=$?
  if [ $pr = $fr ] && cmp -s $T/py.out $T/fo.out; then echo "PASS pecheck $n (rc=$fr)"; else echo "FAIL pecheck $n (py rc=$pr, forge rc=$fr)"; fail=1; fi
done
$T/pecheck $T/missing.efi 2>$T/e; r=$?
[ $r = 1 ] && grep -q "pecheck: cannot read" $T/e && echo "PASS pecheck missing-input" || { echo "FAIL pecheck missing-input"; fail=1; }
exit $fail

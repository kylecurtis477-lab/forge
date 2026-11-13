#!/bin/sh
# tests/user-build: examples/user/build.forge (built to build/user-build; what examples/kernel/build.sh runs)
# must do exactly what examples/user/build.sh (kept as the reference) does: same stdout, stderr and exit code,
# and the same artifact set with identical bytes and modes (sealed programs, seal.pub, the seal / ABI negatives,
# the Linux-ABI test programs). The signing keys are random per build tree, so the Forge run's OUTDIR is seeded
# with build/user's keys (the script only makes a key when it is missing or empty); a fresh-keys case checks the
# generated keys are 32 bytes with the same file set and modes. BusyBox is skipped (BB_SRC=/nonexist) except in
# the bb-branch case, where a fake tarball must fail the SHA-256 pin identically (rc 3) in both.
# Needs build/ (forgec, fas, fld, forgeseal): run after build.sh.
cd "$(dirname "$0")/../.." || exit 1
T=$(mktemp -d); trap 'rm -rf $T' EXIT
fail=0
cp -a build/user $T/orig
./forge examples/user/build.forge -o build/user-build >/dev/null || { echo "FAIL user-build build"; exit 1; }
./forge tools/busybox/build.forge -o build/busybox-build >/dev/null || { echo "FAIL user-build busybox-build"; exit 1; }
man() { (cd $1 && stat -c '%a %n' * && sha256sum *); }
# chk name flags bbsrc nfiles (118 since screen step 5 added gfxapp and gfxdeny: .s .o .elf .raw each)
chk() {
  BB_SRC="$3" FORGEC_FLAGS="$2" sh examples/user/build.sh >$T/s.out 2>$T/s.err; rs=$?
  rm -rf $T/sh $T/fg; cp -a build/user $T/sh; mkdir $T/fg; cp -p build/user/seal.key build/user/seal2.key $T/fg/
  (cd / && BB_SRC="$3" FORGEC_FLAGS="$2" "$OLDPWD/build/user-build" $T/fg >$T/f.out 2>$T/f.err); rf=$?
  sed "s#$T/fg#OUT#g" $T/f.out >$T/f.o2; sed "s#$PWD/build/user#OUT#g" $T/s.out >$T/s.o2
  man $T/sh >$T/m1; man $T/fg >$T/m2
  if [ $rs = $rf ] && cmp -s $T/s.o2 $T/f.o2 && cmp -s $T/s.err $T/f.err && cmp -s $T/m1 $T/m2 \
     && [ "$(ls $T/fg | wc -l)" = $4 ]; then echo "PASS user-build/$1"; else echo "FAIL user-build/$1 (rc $rs/$rf)"; diff $T/m1 $T/m2 | head -5; fail=1; fi
}
rm -rf build/user/busybox.elf
chk default "" /nonexist 118
chk O0 "-O0" /nonexist 118
mkdir $T/bb; echo fake > $T/bb/busybox-1.38.0.tar.bz2
chk bb-branch "" $T/bb 118
# fresh keys: generated, 32 bytes, and the same names and modes as the shell build
rm -rf $T/fk; (cd / && BB_SRC=/nonexist "$OLDPWD/build/user-build" $T/fk >/dev/null 2>&1); rf=$?
if [ $rf = 0 ] && [ "$(stat -c %s $T/fk/seal.key $T/fk/seal2.key)" = "32
32" ] && [ "$(cd $T/fk && stat -c '%a %n' *)" = "$(cd $T/sh && stat -c '%a %n' *)" ] && ! cmp -s $T/fk/seal.key build/user/seal.key
then echo "PASS user-build/fresh-keys"; else echo "FAIL user-build/fresh-keys"; fail=1; fi
rm -rf build/user && cp -a $T/orig build/user
exit $fail

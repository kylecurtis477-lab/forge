#!/bin/sh
# tests/busybox-build: tools/busybox/build.forge (built to build/busybox-build; what examples/user/build.forge
# runs) must do exactly what tools/busybox/build.sh (kept as the reference) does: same stdout, stderr and exit
# code and a byte-identical sealed busybox.elf with the same mode. Cases: a cold build (fresh $BB_WORK: GNU make
# and musl built from source), a warm one (make and musl reused), and the refusal paths: tarball missing (rc 2),
# SHA-256 pin mismatch (rc 3) and a bad upstream signature (rc 4). SOURCE_DATE_EPOCH is fixed for both so
# BusyBox's build timestamp does not differ. Needs the source tarballs in $BB_SRC (default /workspace/bb) and
# build/user/seal.key; without the tarballs it reports SKIP.
cd "$(dirname "$0")/../.." || exit 1
S=${BB_SRC:-/workspace/bb}
[ -f $S/busybox-1.38.0.tar.bz2 ] && [ -f $S/musl-1.2.5.tar.gz ] || { echo "SKIP busybox-build (no source tarballs in $S)"; exit 0; }
T=$(mktemp -d); trap 'rm -rf $T' EXIT
fail=0
export SOURCE_DATE_EPOCH=1700000000
./forge tools/busybox/build.forge -o build/busybox-build >/dev/null || { echo "FAIL busybox-build build"; exit 1; }
K=$PWD/build/user/seal.key
# chk name src work(fresh|keep) want-rc
chk() {
  [ "$3" = fresh ] && rm -rf $T/w1 $T/w2
  BB_SRC=$2 BB_WORK=$T/w1 sh tools/busybox/build.sh $T/sh/bb.elf $K >$T/s.out 2>$T/s.err; rs=$?
  (cd / && BB_SRC=$2 BB_WORK=$T/w2 "$OLDPWD/build/busybox-build" $T/fg/bb.elf $K >$T/f.out 2>$T/f.err); rf=$?
  sed "s#$T/w1#W#g" $T/s.out >$T/s.o; sed "s#$T/w2#W#g" $T/f.out >$T/f.o
  sed "s#$T/w1#W#g" $T/s.err >$T/s.e; sed "s#$T/w2#W#g" $T/f.err >$T/f.e
  if [ $rs = $4 ] && [ $rf = $4 ] && cmp -s $T/s.o $T/f.o && cmp -s $T/s.e $T/f.e && \
     { [ $4 != 0 ] || { cmp -s $T/sh/bb.elf $T/fg/bb.elf && [ "$(stat -c %a $T/sh/bb.elf)" = "$(stat -c %a $T/fg/bb.elf)" ]; }; }
  then echo "PASS busybox-build/$1"; else echo "FAIL busybox-build/$1 (rc $rs/$rf, want $4)"; diff $T/s.o $T/f.o | head -3; fail=1; fi
  rm -f $T/sh/bb.elf $T/fg/bb.elf
}
mkdir -p $T/sh $T/fg
chk cold $S fresh 0
chk warm $S keep 0
mkdir $T/none; chk missing $T/none fresh 2
mkdir $T/bad; echo fake >$T/bad/busybox-1.38.0.tar.bz2; chk pin-mismatch $T/bad fresh 3
mkdir $T/sig; ln -s $S/busybox-1.38.0.tar.bz2 $S/musl-1.2.5.tar.gz $T/sig/; echo junk >$T/sig/busybox-1.38.0.tar.bz2.sig
[ -d $S/gnupg ] && cp -a $S/gnupg $T/sig/gnupg || mkdir -m 700 $T/sig/gnupg
chk bad-signature $T/sig fresh 4
exit $fail

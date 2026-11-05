#!/bin/sh
# tests/modules-build: examples/modules/build.forge (built to build/modules-build; what examples/kernel/build.sh
# runs) must do exactly what examples/modules/build.sh (kept as the reference) does: same stdout, stderr and
# exit code, and the same artifact set with identical bytes and modes (sealed .ko files, .s, .raw copies, the
# unsealed / tampered / userimg negatives), both at the default flags and with FORGEC_FLAGS=-O0.
# Needs build/user (seal.key, hello.elf): run after examples/kernel/build.sh.
cd "$(dirname "$0")/../.." || exit 1
T=$(mktemp -d); trap 'rm -rf $T' EXIT
fail=0
cp -a build/modules $T/orig
./forge examples/modules/build.forge -o build/modules-build >/dev/null || { echo "FAIL modules-build build"; exit 1; }
chk() { # name flags
  FORGEC_FLAGS="$2" sh examples/modules/build.sh >$T/s.out 2>$T/s.err; rs=$?
  rm -rf $T/sh $T/fg; cp -a build/modules $T/sh
  (cd / && FORGEC_FLAGS="$2" "$OLDPWD/build/modules-build" $T/fg >$T/f.out 2>$T/f.err); rf=$?
  (cd $T/sh && stat -c '%a %n' * && sha256sum *) >$T/m1; (cd $T/fg && stat -c '%a %n' * && sha256sum *) >$T/m2
  if [ $rs = 0 ] && [ $rs = $rf ] && cmp -s $T/s.out $T/f.out && cmp -s $T/s.err $T/f.err && cmp -s $T/m1 $T/m2 \
     && [ "$(ls $T/fg | wc -l)" = 25 ]; then echo "PASS modules-build/$1"; else echo "FAIL modules-build/$1 (rc $rs/$rf)"; diff $T/m1 $T/m2 | head -5; fail=1; fi
}
chk default ""
chk O0 "-O0"
# put back exactly what was there (the suite's own build, at its flags)
rm -rf build/modules && cp -a $T/orig build/modules
exit $fail

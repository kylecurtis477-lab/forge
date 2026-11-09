#!/bin/sh
# tests/efi-build: examples/efi/build.forge (built to build/efi-build) must do exactly what examples/efi/build.sh
# (kept as the reference) does: same stdout, stderr and exit code, and the same build/efi artifacts (loader,
# ESP image, GPT disk, mkgpt, big.bin, notes.txt) with identical bytes and modes. Both runs pin the mtools
# timestamps (SOURCE_DATE_EPOCH) and the GPT GUIDs (FORGE_GUID_SEED); mformat's random volume serial is the one
# allowed difference: exactly the 4 serial bytes of the boot sector and its backup (offsets 67 and 3139), in
# esp.fat and at the ESP's offset in disk.img. The missing-input case hides examples/efi/scripts/hello.sh:
# mcopy must fail with the same message and exit code in both, leaving the same partial build/efi.
# Needs build/ (forgec, fas, fld and the kernel/user/modules artifacts): run after build.sh.
cd "$(dirname "$0")/../.." || exit 1
T=$(mktemp -d); trap 'rm -rf $T' EXIT
fail=0
./forge examples/efi/build.forge -o build/efi-build >/dev/null || { echo "FAIL efi-build build"; exit 1; }
export SOURCE_DATE_EPOCH=1700000000 FORGE_GUID_SEED=7
[ -d build/efi ] && cp -a build/efi $T/orig
man() { (cd $1 && stat -c '%a %n' * && sha256sum $(ls | grep -v -e '^esp.fat$' -e '^disk.img$')); }
# serial-masked compare of a b: identical except exactly the 8 serial bytes at base+68..71, base+3140..3143
sercmp() {
  cmp -l $1 $2 >$T/cl
  [ "$(stat -c %s $1)" = "$(stat -c %s $2)" ] || return 1
  n=$(wc -l <$T/cl); [ $n = 0 ] && return 0; [ $n -le 8 ] || return 1
  base=$(( $(head -1 $T/cl | awk '{print $1}') - 68 ))
  awk -v b=$base '{o=$1-b; if (!((o>=68&&o<=71)||(o>=3140&&o<=3143))) bad=1} END {exit bad}' $T/cl
}
chk() {
  rm -rf build/efi; FORGEC_FLAGS="$2" sh examples/efi/build.sh >$T/s.out 2>$T/s.err; rs=$?
  rm -rf $T/sh; cp -a build/efi $T/sh
  rm -rf build/efi; (cd / && FORGEC_FLAGS="$2" "$OLDPWD/build/efi-build" >$T/f.out 2>$T/f.err); rf=$?
  rm -rf $T/fg; cp -a build/efi $T/fg
  man $T/sh >$T/m1 2>&1; man $T/fg >$T/m2 2>&1
  ok=1
  [ -f $T/sh/esp.fat ] && { sercmp $T/sh/esp.fat $T/fg/esp.fat || ok=0; }
  [ -f $T/sh/disk.img ] && { sercmp $T/sh/disk.img $T/fg/disk.img || ok=0; }
  if [ $ok = 1 ] && [ $rs = $rf ] && cmp -s $T/s.out $T/f.out && cmp -s $T/s.err $T/f.err && cmp -s $T/m1 $T/m2 \
     && [ "$(ls $T/fg | wc -l)" = $3 ]; then echo "PASS efi-build/$1"; else echo "FAIL efi-build/$1 (rc $rs/$rf)"; diff $T/m1 $T/m2 | head -5; diff $T/s.err $T/f.err | head -5; fail=1; fi
}
chk default "" 10
chk O0 "-O0" 10
mv examples/efi/scripts/hello.sh $T/hello.sh
chk missing-input "" 6
mv $T/hello.sh examples/efi/scripts/hello.sh
rm -rf build/efi; [ -d $T/orig ] && cp -a $T/orig build/efi
exit $fail

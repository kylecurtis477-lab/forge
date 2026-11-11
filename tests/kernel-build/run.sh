#!/bin/sh
# tests/kernel-build: examples/kernel/build.forge (built to build/kernel-build) must do exactly what
# examples/kernel/build.sh (kept as the reference) does: same stdout, stderr and exit code, and the same
# build/kernel artifacts (kernel.s/.o/.elf, kernel-next.s/.o/.elf, iso/boot/kernel.elf, iso/boot/grub/grub.cfg,
# forge.iso) with identical bytes and modes. With SOURCE_DATE_EPOCH set, tools/repro-mkrescue pins
# grub-mkrescue's ISO UUID/timestamps and the El Torito FAT serial, so forge.iso is byte-identical across
# clean builds. kernel-build/iso-reproducible runs the wrapper twice on the same iso/ tree and compares
# sha256, also against the ISO from the script build. xorriso's extract tree is read-only; isot and the
# EXIT trap chmod -R u+w before rm -rf so cleanup is silent.
# Cases: default, O0, and missing-input (grub.cfg hidden: the same cp error and exit code, the same partial tree).
# Needs build/ (forgec, fas, fld, forge): run after build.sh.
cd "$(dirname "$0")/../.." || exit 1
T=$(mktemp -d); trap 'chmod -R u+w "$T" 2>/dev/null; rm -rf "$T"' EXIT
fail=0
./forge examples/kernel/build.forge -o build/kernel-build >/dev/null || { echo "FAIL kernel-build build"; exit 1; }
export SOURCE_DATE_EPOCH=1700000000
[ -d build/kernel ] && cp -a build/kernel $T/orig
man() { (cd $1 && find . -type f | sort | xargs stat -c '%a %n' && find . -type f | sort | xargs sha256sum); }
isot() {
  [ -f $1/forge.iso ] || { echo none; return; }
  # xorriso extracts read-only; chmod before rm or the next call (and the EXIT trap) spam Permission denied
  chmod -R u+w "$T/x" 2>/dev/null; rm -rf "$T/x"
  xorriso -osirrox on -indev $1/forge.iso -extract / $T/x >/dev/null 2>&1
  (cd $T/x && find . -type f | sort && sha256sum boot/kernel.elf boot/grub/grub.cfg .disk/*.uuid)
}
chk() {
  rm -rf build/kernel; FORGEC_FLAGS="$2" sh examples/kernel/build.sh >$T/s.out 2>$T/s.err; rs=$?
  rm -rf $T/sh; cp -a build/kernel $T/sh
  rm -rf build/kernel; (cd / && FORGEC_FLAGS="$2" "$OLDPWD/build/kernel-build" >$T/f.out 2>$T/f.err); rf=$?
  rm -rf $T/fg; cp -a build/kernel $T/fg
  man $T/sh >$T/m1; man $T/fg >$T/m2; isot $T/sh >$T/i1; isot $T/fg >$T/i2
  if [ $rs = $rf ] && cmp -s $T/s.out $T/f.out && cmp -s $T/s.err $T/f.err && cmp -s $T/m1 $T/m2 && cmp -s $T/i1 $T/i2 \
     && [ "$(wc -l <$T/m2)" = $3 ]; then echo "PASS kernel-build/$1"; else echo "FAIL kernel-build/$1 (rc $rs/$rf)"; diff $T/m1 $T/m2 | head -5; diff $T/s.err $T/f.err | head -5; diff $T/i1 $T/i2 | head -3; fail=1; fi
}
chk default "" 18
# two packaging passes of the same iso/ tree, plus a match against the script's forge.iso
if [ -d $T/sh/iso ] && [ -f $T/sh/forge.iso ]; then
  tools/repro-mkrescue -o $T/isoA.iso $T/sh/iso
  tools/repro-mkrescue -o $T/isoB.iso $T/sh/iso
  if cmp -s $T/isoA.iso $T/isoB.iso && cmp -s $T/isoA.iso $T/sh/forge.iso; then
    h=$(sha256sum < $T/isoA.iso | awk '{print $1}')
    echo "PASS kernel-build/iso-reproducible (forge.iso $h)"
  else
    echo "FAIL kernel-build/iso-reproducible"
    cmp -l $T/isoA.iso $T/isoB.iso 2>/dev/null | head -5
    fail=1
  fi
else
  echo "FAIL kernel-build/iso-reproducible (missing iso from default build)"
  fail=1
fi
chk O0 "-O0" 18
mv examples/kernel/grub.cfg $T/grub.cfg
chk missing-input "" 14
mv $T/grub.cfg examples/kernel/grub.cfg
rm -rf build/kernel; [ -d $T/orig ] && cp -a $T/orig build/kernel
exit $fail

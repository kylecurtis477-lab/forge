#!/bin/sh
# tests/kernel-build: examples/kernel/build.forge (built to build/kernel-build) must do exactly what
# examples/kernel/build.sh (kept as the reference) does: same stdout, stderr and exit code, and the same
# build/kernel artifacts (kernel.s/.o/.elf, kernel-next.s/.o/.elf, iso/boot/kernel.elf, iso/boot/grub/grub.cfg)
# with identical bytes and modes. forge.iso is not byte-reproducible even from the script (grub-mkrescue embeds a
# fresh UUID in its core images and the .disk/<time>.uuid name), so the ISOs are compared by their file trees
# (xorriso extract): same paths apart from .disk/*.uuid, identical boot/kernel.elf and boot/grub/grub.cfg.
# Cases: default, O0, and missing-input (grub.cfg hidden: the same cp error and exit code, the same partial tree).
# Needs build/ (forgec, fas, fld, forge): run after build.sh.
cd "$(dirname "$0")/../.." || exit 1
T=$(mktemp -d); trap 'chmod -R u+w "$T" 2>/dev/null; rm -rf "$T"' EXIT
fail=0
./forge examples/kernel/build.forge -o build/kernel-build >/dev/null || { echo "FAIL kernel-build build"; exit 1; }
[ -d build/kernel ] && cp -a build/kernel $T/orig
man() { (cd $1 && find . -type f ! -name forge.iso | sort | xargs stat -c '%a %n' && find . -type f ! -name forge.iso | sort | xargs sha256sum); }
isot() {
  [ -f $1/forge.iso ] || { echo none; return; }
  rm -rf $T/x; xorriso -osirrox on -indev $1/forge.iso -extract / $T/x >/dev/null 2>&1
  (cd $T/x && find . -type f ! -path './.disk/*' | sort && sha256sum boot/kernel.elf boot/grub/grub.cfg)
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
chk default "" 16
chk O0 "-O0" 16
mv examples/kernel/grub.cfg $T/grub.cfg
chk missing-input "" 14
mv $T/grub.cfg examples/kernel/grub.cfg
rm -rf build/kernel; [ -d $T/orig ] && cp -a $T/orig build/kernel
exit $fail

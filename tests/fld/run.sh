#!/bin/bash
# Phase 7.2 tools/fld: images linked by fld must load the same bytes at the same addresses as GNU ld's
# (user programs, the compiler, the tests corpus, and the kernel against examples/kernel/linker.ld +
# tools/krel.forge, the reference layout); the PE32+ loader is checked structurally (it boots in tests/os);
# link errors are diagnosed. GNU ld is only the reference here: no build step uses it past the seed.
cd "$(dirname "$0")/../.."
T=$(mktemp -d); trap 'rm -rf $T' EXIT
fail=0
FLD=build/fld
same() { # name ref.elf fld.elf
  if python3 tests/fld/memimg.py "$2" "$3" | grep -q '^SAME'; then echo "PASS fld $1"; else echo "FAIL fld $1"; fail=1; fi
}
command -v ld >/dev/null || { echo "SKIP fld reference comparisons (no GNU ld)"; exit 0; }
# hosted: the compiler itself and every tests/*.forge program
build/fas -o $T/c.o build/stage3.s && ld -Ttext-segment=0x100000000000 -o $T/c.ld $T/c.o && $FLD --base 0x100000000000 -o $T/c.fld $T/c.o && same compiler $T/c.ld $T/c.fld
$T/c.fld selfhost/forgec.forge $T/c.s && cmp -s $T/c.s build/stage3.s && echo "PASS fld compiler-runs" || { echo "FAIL fld compiler-runs"; fail=1; }
bad=0; n=0
for t in tests/*.forge; do
  build/forgec "$t" $T/t.s $FORGEC_FLAGS >/dev/null 2>&1 || continue
  build/fas -o $T/t.o $T/t.s && ld -o $T/t.ld $T/t.o && $FLD -o $T/t.fld $T/t.o || { echo "  link: $t"; bad=1; continue; }
  python3 tests/fld/memimg.py $T/t.ld $T/t.fld | grep -q '^SAME' || { echo "  differs: $t"; bad=1; }
  n=$((n+1))
done
[ $bad = 0 ] && echo "PASS fld tests-corpus ($n programs)" || { echo "FAIL fld tests-corpus"; fail=1; }
# freestanding user programs (ring 3)
for o in build/user/*.o; do
  [ -f "$o" ] || continue; p=$(basename ${o%.o})
  ld -static -nostdlib -e _start -z noexecstack -z separate-code -o $T/u.ld $o && $FLD -o $T/u.fld $o && same user/$p $T/u.ld $T/u.fld
done
# kernel: GNU ld with the reference linker script + krel (tools/krel.forge; tests/krel checks it against krel.py)
# two-pass table vs fld --format kernel
build/forgec tools/krel.forge $T/krel.fs.s && build/fas -o $T/krel.fs.o $T/krel.fs.s && build/fld -o $T/krel $T/krel.fs.o || { echo "FAIL fld krel-build"; fail=1; }
for k in kernel kernel-next; do
  o=build/kernel/$k.o; [ -f $o ] || continue
  ld -n -q -T examples/kernel/linker.ld -o $T/k1.elf $o && $T/krel $T/k1.elf $T/krel.s >/dev/null &&
    as -o $T/krel.o $T/krel.s && ld -n -T examples/kernel/linker.ld -o $T/k.ld $o $T/krel.o &&
    $FLD --format kernel -o $T/k.fld $o && same kernel/$k $T/k.ld $T/k.fld
done
[ -f build/efi/loader.o ] && { $FLD --format pe --subsystem 10 --image-base 0x10000000 -e efi_main -o $T/l.efi build/efi/loader.o && python3 tests/fld/pecheck.py $T/l.efi || { echo "FAIL fld/pe-structure"; fail=1; }; }
# diagnostics
printf '.globl _start\n_start: call nowhere\n' > $T/u.s; build/fas -o $T/u.o $T/u.s
$FLD -o $T/u $T/u.o 2>$T/e; [ $? = 1 ] && grep -q "undefined symbol nowhere" $T/e && echo "PASS fld undefined-symbol" || { echo "FAIL fld undefined-symbol"; fail=1; }
printf '.globl _start\n_start: ret\n' > $T/d.s; build/fas -o $T/d.o $T/d.s
$FLD -o $T/d $T/d.o $T/d.o 2>$T/e; [ $? = 1 ] && grep -q "duplicate symbol _start" $T/e && echo "PASS fld duplicate-symbol" || { echo "FAIL fld duplicate-symbol"; fail=1; }
exit $fail

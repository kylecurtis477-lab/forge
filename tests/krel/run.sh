#!/bin/sh
# tests/krel: tools/krel.forge (build/krel) must match tools/krel.py byte-for-byte (OUT.s, stdout, exit code)
# on the real kernels (ld -q links of build/kernel/{kernel,kernel-next}.o), a synthetic high-half image with
# every relocation kind, a low-only image (empty table) and an error image (R_X86_64_32 to the high half).
cd "$(dirname "$0")/../.." || exit 1
T=$(mktemp -d); trap 'rm -rf $T' EXIT
fail=0
build/forgec tools/krel.forge $T/krel.s && build/fas -o $T/krel.o $T/krel.s && build/fld -o $T/krel $T/krel.o || { echo "FAIL krel build"; exit 1; }
command -v ld >/dev/null && command -v python3 >/dev/null || { echo "SKIP krel (needs GNU ld + python3 for the reference)"; exit 0; }
chk() { # name elf
  python3 tools/krel.py $2 $T/p.s >$T/p.out 2>&1; pr=$?
  $T/krel $2 $T/f.s >$T/f.out 2>&1; fr=$?
  [ -f $T/p.s ] || : >$T/p.s; [ -f $T/f.s ] || : >$T/f.s
  if [ $pr = $fr ] && cmp -s $T/p.s $T/f.s && cmp -s $T/p.out $T/f.out; then echo "PASS krel $1 (rc=$fr, $(wc -l <$T/f.s) lines)"; else echo "FAIL krel $1"; fail=1; fi
  rm -f $T/p.s $T/f.s
}
for k in kernel kernel-next; do
  o=build/kernel/$k.o; [ -f $o ] || { echo "SKIP krel $k (no $o)"; continue; }
  ld -n -q -T examples/kernel/linker.ld -o $T/$k.elf $o && chk $k $T/$k.elf
done
cat >$T/s.s <<'S'
.section .low,"ax"
.globl lowfn
lowfn: ret
.text
.globl _start
_start: call lowfn
  jmp lowfn@PLT
  movq $hi, %rax
  movq hi, %rbx
  ret
.data
hi: .quad hi, _start, lowfn
  .quad 0
S
printf '.text\n.globl _start\n_start: ret\n.data\nx: .long x\n' >$T/e.s
cat >$T/l.ld <<'S'
SECTIONS { . = 0x100000; .low : { *(.low) } . = 0xFFFFFFFF80200000; .text : { *(.text) } .data : { *(.data) } }
S
as -o $T/s.o $T/s.s && ld -q --no-warn-rwx-segments -T $T/l.ld -o $T/s.elf $T/s.o && chk synthetic $T/s.elf
as -o $T/s.o $T/s.s && ld -q -o $T/u.elf $T/s.o 2>/dev/null && chk low-only $T/u.elf
as -o $T/e.o $T/e.s && ld -q --noinhibit-exec --no-warn-rwx-segments -T $T/l.ld -o $T/e.elf $T/e.o 2>/dev/null; [ -f $T/e.elf ] && chk r32-error $T/e.elf
exit $fail

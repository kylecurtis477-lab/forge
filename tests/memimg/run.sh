#!/bin/sh
# tests/memimg: tests/fld/memimg.forge must print exactly what tests/fld/memimg.py (kept as the reference)
# prints, with the same exit code: identical images, different segment lists, a flipped byte in a low and a
# high-half (0xffffffff8...) segment, a changed entry point, a larger .bss, and both argument orders.
cd "$(dirname "$0")/../.." || exit 1
T=$(mktemp -d); trap 'rm -rf $T' EXIT
fail=0
build/forgec tests/fld/memimg.forge $T/m.s && build/fas -o $T/m.o $T/m.s && build/fld --base 0x100000000000 -o $T/memimg $T/m.o || { echo "FAIL memimg build"; exit 1; }
command -v python3 >/dev/null || { echo "SKIP memimg (needs python3 for the reference)"; exit 0; }
chk() { # name a b
  python3 tests/fld/memimg.py $2 $3 >$T/py.out 2>&1; pr=$?
  $T/memimg $2 $3 >$T/fo.out 2>&1; fr=$?
  if [ $pr = $fr ] && cmp -s $T/py.out $T/fo.out; then echo "PASS memimg $1 ($(cut -c1-4 $T/fo.out | tail -1))"; else echo "FAIL memimg $1"; fail=1; fi
}
# poke FILE OFFSET BYTES: overwrite bytes in place
poke() { printf "$3" | dd of=$1 bs=1 seek=$(($2)) conv=notrunc 2>/dev/null; }
# phoff/field helpers: offset of program header N's field
ph() { echo $(( $(od -An -tu8 -j32 -N8 $1 | tr -d ' ') + 56 * $2 + $3 )); }
chk self build/forgec build/forgec
chk tools build/forgec build/fas
chk tools-rev build/fas build/forgec
cp build/forgec $T/flip; poke $T/flip 8000 'Z'; chk low-byte build/forgec $T/flip
cp build/forgec $T/ent; poke $T/ent 0x18 '\001'; chk entry build/forgec $T/ent
cp build/forgec $T/bss; poke $T/bss $(ph $T/bss 1 40) '\377\377'; chk bss-size build/forgec $T/bss
K=build/kernel/kernel.elf
if [ -f $K ] && [ -f build/kernel/kernel-next.elf ]; then
  chk kernel $K $K
  chk kernel-next $K build/kernel/kernel-next.elf
  # a byte inside program header 2's file image (a high-half segment)
  cp $K $T/k; o=$(od -An -tu8 -j$(ph $K 2 8) -N8 $K | tr -d ' '); poke $T/k $((o + 100)) '\252'; chk kernel-high-byte $K $T/k
else
  echo "SKIP memimg kernel (no build/kernel/*.elf)"
fi
$T/memimg $T/missing build/fas 2>$T/e; r=$?
[ $r = 1 ] && grep -q "memimg: cannot read" $T/e && echo "PASS memimg missing-input" || { echo "FAIL memimg missing-input"; fail=1; }
exit $fail

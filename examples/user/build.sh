#!/bin/sh
# Build the Forge user programs as static ELF64 executables (ring 3, loaded by the kernel's ELF loader).
set -e
cd "$(dirname "$0")"
B=../../build/user; mkdir -p $B
# Phase 6.1 sealing key: generated once per build tree, never committed; the kernel embeds it
K=$B/seal.key
[ -s $K ] || head -c 32 /dev/urandom > $K
for p in hello fault sh cat forktest blkd crash sum bg captest chantest sbtest wxtest capdeny; do
  ../../build/forgec $p.forge $B/$p.s $FORGEC_FLAGS
  as -o $B/$p.o $B/$p.s
  ld -static -nostdlib -e _start -z noexecstack -z separate-code -o $B/$p.elf $B/$p.o
  cp $B/$p.elf $B/$p.raw
  python3 ../../tools/forgeseal.py $p.forge $B/$p.elf $K ../../tests/unsafe/baseline.txt >/dev/null
done
# 6.3 negative images: an unsealed copy and a sealed copy with one code byte flipped
cp $B/hello.raw $B/unsealed.elf
python3 -c "import sys; d=bytearray(open(sys.argv[1],'rb').read()); d[0x1010]^=1; open(sys.argv[2],'wb').write(d)" $B/hello.elf $B/tampered.elf
# ... and a sealed copy whose manifest claims every capability (MAC no longer matches)
python3 -c "import sys; d=bytearray(open(sys.argv[1],'rb').read()); d[-128+32]=0x7f; open(sys.argv[2],'wb').write(d)" $B/hello.elf $B/forged.elf

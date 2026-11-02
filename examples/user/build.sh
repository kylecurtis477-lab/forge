#!/bin/sh
# Build the Forge user programs as static ELF64 executables (ring 3, loaded by the kernel's ELF loader).
set -e
cd "$(dirname "$0")"
B=../../build/user; mkdir -p $B
# Sealing key: an Ed25519 seed generated once per build tree, never committed and never embedded;
# the kernel embeds only the public key (seal.pub)
K=$B/seal.key
[ -s $K ] || head -c 32 /dev/urandom > $K
python3 ../../tools/forgeseal.py --pub $K > $B/seal.pub
for p in hello fault sh cat forktest blkd crash sum bg captest chantest sbtest wxtest capdeny; do
  ../../build/forgec $p.forge $B/$p.s $FORGEC_FLAGS
  ../../build/fas -o $B/$p.o $B/$p.s
  ../../build/fld -o $B/$p.elf $B/$p.o
  cp $B/$p.elf $B/$p.raw
  python3 ../../tools/forgeseal.py $p.forge $B/$p.elf $K ../../tests/unsafe/baseline.txt >/dev/null
done
# 6.3 negative images: an unsealed copy and a sealed copy with one code byte flipped
cp $B/hello.raw $B/unsealed.elf
python3 -c "import sys; d=bytearray(open(sys.argv[1],'rb').read()); d[0x1010]^=1; open(sys.argv[2],'wb').write(d)" $B/hello.elf $B/tampered.elf
# ... and a sealed copy whose manifest claims every capability (signature no longer matches)
python3 -c "import sys; d=bytearray(open(sys.argv[1],'rb').read()); d[-160+32]=0x7f; open(sys.argv[2],'wb').write(d)" $B/hello.elf $B/forged.elf
# 6.4: hello re-tagged as a Linux ABI binary (EI_OSABI = 3) and properly sealed: still refused
python3 -c "import sys; d=bytearray(open(sys.argv[1],'rb').read()); d[7]=3; open(sys.argv[2],'wb').write(d)" $B/hello.raw $B/linuxabi.elf
python3 ../../tools/forgeseal.py hello.forge $B/linuxabi.elf $K ../../tests/unsafe/baseline.txt >/dev/null

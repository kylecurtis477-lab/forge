#!/bin/sh
# Build the test kernel modules as relocatable ELF64 objects (.ko = plain `as` output, ET_REL).
set -e
cd "$(dirname "$0")"
B=../../build/modules; mkdir -p $B
for m in hello badabi unresolved newer tty1 tty2 getpid2; do
  ../../build/forgec $m.forge $B/$m.s $FORGEC_FLAGS
  as -o $B/$m.ko $B/$m.s
  cp $B/$m.ko $B/$m.raw
  python3 ../../tools/forgeseal.py --module $m.forge $B/$m.ko ../../build/user/seal.key ../../tests/unsafe/baseline.txt >/dev/null
done
# negative modules: unsealed, one code byte flipped after sealing, a sealed user program
cp $B/hello.raw $B/unsealed.ko
cp $B/tty1.raw $B/tty1u.ko
python3 -c "import sys; d=bytearray(open(sys.argv[1],'rb').read()); d[0x50]^=1; open(sys.argv[2],'wb').write(d)" $B/hello.ko $B/tampered.ko
cp ../../build/user/hello.elf $B/userimg.ko

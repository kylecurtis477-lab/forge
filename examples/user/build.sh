#!/bin/sh
# Build the Forge user programs as static ELF64 executables (ring 3, loaded by the kernel's ELF loader).
set -e
cd "$(dirname "$0")"
B=../../build/user; mkdir -p $B
for p in hello fault sh cat forktest blkd crash sum; do
  ../../build/forgec $p.forge $B/$p.s $FORGEC_FLAGS
  as -o $B/$p.o $B/$p.s
  ld -static -nostdlib -e _start -z noexecstack -z separate-code -o $B/$p.elf $B/$p.o
done

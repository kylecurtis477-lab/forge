#!/bin/sh
# Build the test kernel modules as relocatable ELF64 objects (.ko = plain `as` output, ET_REL).
set -e
cd "$(dirname "$0")"
B=../../build/modules; mkdir -p $B
for m in hello badabi unresolved newer; do
  ../../build/forgec $m.forge $B/$m.s $FORGEC_FLAGS
  as -o $B/$m.ko $B/$m.s
done

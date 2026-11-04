#!/bin/sh
# tests/rtport: selfhost/runtime.forge (the hosted runtime as naked Forge functions with inline asm,
# v1.3 item 9) must compile to exactly the instruction/data stream of the reference selfhost/runtime.s:
# after dropping comments, indentation and the section directives forgec adds around each function,
# the two assembly texts are identical, and fas assembles them to identical .text/.data/.bss bytes.
# (build.sh's SEED OK is the end-to-end check: a forgec embedding build/runtime.s is byte-identical
# to the seed, which embedded the hand-written runtime.s.)
cd "$(dirname "$0")/../.." || exit 1
T=$(mktemp -d); trap 'rm -rf $T' EXIT
norm() { sed -e 's/^[[:space:]]*#.*//' -e 's/[[:space:]][[:space:]]*#.*$//' -e 's/^[[:space:]]*//' -e 's/[[:space:]]*$//' "$1" | grep -v -e '^$' -e '^\.section \.text,"ax",@progbits$' -e '^\.section \.rodata$' -e '^\.section \.note\.GNU-stack,"",@progbits$'; }
build/forgec selfhost/runtime.forge $T/rt.s || { echo "FAIL rtport build"; exit 1; }
norm selfhost/runtime.s > $T/a; norm $T/rt.s > $T/b
cmp -s $T/a $T/b && echo "PASS rtport-asm-identical" || { echo "FAIL rtport-asm-identical"; diff $T/a $T/b | head; exit 1; }
cmp -s $T/rt.s build/runtime.s && echo "PASS rtport-build-current" || { echo "FAIL rtport-build-current (rerun build.sh)"; exit 1; }

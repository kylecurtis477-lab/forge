#!/bin/sh
# Full bootstrap + self-compilation fixpoint check.
#   Rust bootstrap (src/main.rs) compiles tools/embed.forge and the self-hosted compiler (stage1);
#   stage1 -> stage2 -> stage3 -> stage4 must produce identical assembly.
set -e
cd "$(dirname "$0")"
mkdir -p build
LD="ld -Ttext-segment=0x100000000000"
cargo build --release -q
./target/release/mylang tools/embed.forge -o build/embed
build/embed selfhost/compiler.forge selfhost/prelude.forge selfhost/runtime.s selfhost/forgec.forge
./target/release/mylang selfhost/forgec.forge -o build/stage1           # Rust bootstrap -> stage1
build/stage1 selfhost/forgec.forge build/stage2.s || { echo "stage1 failed"; exit 1; }
as -o build/stage2.o build/stage2.s && $LD -o build/stage2 build/stage2.o
build/stage2 selfhost/forgec.forge build/stage3.s || { echo "stage2 failed"; exit 1; }
as -o build/stage3.o build/stage3.s && $LD -o build/stage3 build/stage3.o
build/stage3 selfhost/forgec.forge build/stage4.s
cmp build/stage2.s build/stage3.s && cmp build/stage3.s build/stage4.s && echo "FIXPOINT OK: stage2.s == stage3.s == stage4.s"
cp build/stage3 build/forgec

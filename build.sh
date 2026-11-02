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
# seed: stage2 and the stage2-built tools/fas + tools/fld are the only binaries still assembled and
# linked by binutils; every later stage (and everything else in the repo) goes through fas/fld
as -o build/stage2.o build/stage2.s && $LD -o build/stage2 build/stage2.o
seed() { build/stage2 tools/$1.forge build/$1.s && as -o build/$1.o build/$1.s && $LD -o build/$1 build/$1.o; }
seed fas && seed fld
FAS=build/fas; FLD="build/fld --base 0x100000000000"
build/stage2 selfhost/forgec.forge build/stage3.s || { echo "stage2 failed"; exit 1; }
$FAS -o build/stage3.o build/stage3.s && $FLD -o build/stage3 build/stage3.o
build/stage3 selfhost/forgec.forge build/stage4.s
cmp build/stage2.s build/stage3.s && cmp build/stage3.s build/stage4.s && echo "FIXPOINT OK: stage2.s == stage3.s == stage4.s"
cp build/stage3 build/forgec
# rebuild fas/fld with the fixpoint compiler and themselves; the result must reproduce the seed's output
for t in fas fld; do build/forgec tools/$t.forge build/$t.s && build/fas -o build/$t.o build/$t.s && build/fld --base 0x100000000000 -o build/$t.new build/$t.o && mv build/$t.new build/$t; done
build/fas -o build/stage3.chk.o build/stage3.s && cmp build/stage3.o build/stage3.chk.o && build/fld --base 0x100000000000 -o build/stage3.chk build/stage3.chk.o && cmp build/stage3 build/stage3.chk && echo "TOOLS OK: self-built fas/fld reproduce stage3"

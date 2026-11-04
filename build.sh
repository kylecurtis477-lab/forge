#!/bin/sh
# Full bootstrap + self-compilation fixpoint check, starting from the checked-in seed compiler.
#   seed/forgec-seed (a forgec binary, sha256 pinned in seed/forgec-seed.sha256) compiles tools/embed.forge
#   and the self-hosted compiler (stage2); stage2 -> stage3 -> stage4 must produce identical assembly.
#   seed/fas-seed and seed/fld-seed (Forge's own assembler and linker, also pinned) assemble and link
#   everything the seed compiler produces, so neither Rust nor binutils is needed. FORGE_RUST_CHECK=1 additionally builds the old Rust bootstrap (src/main.rs) and
#   checks that its stage1 produces the same stage2.s (optional cross-check).
#   tools/reseed.sh refreshes the seed from a green build.
set -e
cd "$(dirname "$0")"
mkdir -p build
SEED=seed/forgec-seed
(cd seed && sha256sum -c --quiet seed.sha256) || { echo "seed hash mismatch"; exit 1; }
SAS=seed/fas-seed; SLD="seed/fld-seed --base 0x100000000000"
# stage2, embed and the stage2-built fas/fld are assembled and linked by the seed fas/fld; every later
# stage (and everything else in the repo) goes through the freshly built fas/fld. No GNU as/ld.
$SEED tools/embed.forge build/embed.s && $SAS -o build/embed.o build/embed.s && $SLD -o build/embed build/embed.o
# the hosted runtime is Forge (naked functions + inline asm); the seed compiles it to build/runtime.s
$SEED selfhost/runtime.forge build/runtime.s || { echo "runtime.forge failed"; exit 1; }
build/embed selfhost/compiler.forge selfhost/prelude.forge build/runtime.s selfhost/forgec.forge
$SEED selfhost/forgec.forge build/stage2.s || { echo "seed failed"; exit 1; }
$SAS -o build/stage2.o build/stage2.s && $SLD -o build/stage2 build/stage2.o
seed() { build/stage2 tools/$1.forge build/$1.s && $SAS -o build/$1.o build/$1.s && $SLD -o build/$1 build/$1.o; }
seed fas && seed fld
FAS=build/fas; FLD="build/fld --base 0x100000000000"
build/stage2 selfhost/forgec.forge build/stage3.s || { echo "stage2 failed"; exit 1; }
$FAS -o build/stage3.o build/stage3.s && $FLD -o build/stage3 build/stage3.o
build/stage3 selfhost/forgec.forge build/stage4.s
cmp build/stage2.s build/stage3.s && cmp build/stage3.s build/stage4.s && echo "FIXPOINT OK: stage2.s == stage3.s == stage4.s"
cp build/stage3 build/forgec
if cmp -s build/forgec $SEED && cmp -s build/stage2 $SEED; then echo "SEED OK: the rebuilt compiler (and the seed-built stage2) are byte-identical to the seed"; else echo "SEED NOTE: compiler differs from the seed (source changed since the last reseed; tools/reseed.sh)"; fi
# rebuild fas/fld with the fixpoint compiler and themselves; the result must reproduce the seed's output
for t in fas fld; do build/forgec tools/$t.forge build/$t.s && build/fas -o build/$t.o build/$t.s && build/fld --base 0x100000000000 -o build/$t.new build/$t.o && mv build/$t.new build/$t; done
build/fas -o build/stage3.chk.o build/stage3.s && cmp build/stage3.o build/stage3.chk.o && build/fld --base 0x100000000000 -o build/stage3.chk build/stage3.chk.o && cmp build/stage3 build/stage3.chk && echo "TOOLS OK: self-built fas/fld reproduce stage3"
if cmp -s build/fas $SAS && cmp -s build/fld seed/fld-seed; then echo "SEED TOOLS OK: rebuilt fas/fld are byte-identical to the seed fas/fld"; else echo "SEED NOTE: fas/fld differ from the seed (tools/reseed.sh)"; fi
if [ "${FORGE_RUST_CHECK:-0}" = 1 ]; then
  cargo build --release -q
  ./target/release/mylang selfhost/forgec.forge -o build/stage1.rust
  build/stage1.rust selfhost/forgec.forge build/stage2.rust.s && cmp build/stage2.s build/stage2.rust.s && echo "RUST CROSS-CHECK OK: Rust bootstrap stage1 produces the same stage2.s"
fi

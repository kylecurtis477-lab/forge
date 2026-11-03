# Bootstrap: seed compiler, assembler and linker

Files: `build.sh`, `seed/forgec-seed`, `seed/fas-seed`, `seed/fld-seed`, `seed/seed.sha256`,
`tools/reseed.sh`, `tools/fas.forge`, `tools/fld.forge`, `tools/embed.forge`, and `selfhost/forgec.forge`
(the self-hosted compiler, generated from `selfhost/compiler.forge`).

## What it does
The build needs no Rust and no GNU binutils. Three checked-in x86-64 Linux binaries start it:
- `forgec-seed` is a build of the Forge compiler.
- `fas-seed` is Forge's assembler. It reads GNU-as syntax and writes ELF64 relocatable objects.
- `fld-seed` is Forge's static linker.

`seed/seed.sha256` pins all three. `build.sh` exits if a hash doesn't match.

## Stages
1. The seed compiles `tools/embed.forge` and `selfhost/forgec.forge` into stage2. The seed fas and fld assemble and link it.
2. stage2 builds `fas` and `fld` from `tools/`.
3. stage2 compiles the compiler into stage3, and stage3 compiles it again into stage4. The new fas and fld assemble and link both.
4. **FIXPOINT OK**: `stage2.s == stage3.s == stage4.s`. stage3 becomes `build/forgec`.
5. **SEED OK**: `build/forgec` and stage2 are byte-identical to `forgec-seed`. If they differ, build.sh
   prints `SEED NOTE`, which means the compiler source changed since the last reseed.
6. **TOOLS OK**: the self-built fas and fld reproduce stage3's object and binary exactly. **SEED TOOLS OK**: they
   are byte-identical to the seed's fas and fld.

`FORGE_RUST_CHECK=1` also builds the old Rust bootstrap (`src/main.rs`) as an optional cross-check.

## Reseeding
Any commit that changes the compiler, fas or fld has to reseed:
1. Run `./build.sh` and check that FIXPOINT is green.
2. Run `tools/reseed.sh`. It requires `stage2.s == stage4.s`, copies build/forgec, fas and fld into
   `seed/`, and rewrites `seed.sha256`.
3. Run `./build.sh` again. It must print SEED OK and SEED TOOLS OK.
4. Commit the seed files with the source change.

## Testing
Run the invariant check from a clean copy with `as`, `ld`, `cargo` and `rustc` blocked on PATH by stub scripts.
`./build.sh` must print FIXPOINT OK, SEED OK, TOOLS OK and SEED TOOLS OK. `./run_tests.sh` then
runs the full suite at O2, and `FORGEC_FLAGS=-O0` runs it again at O0. GNU as and ld appear only as
reference oracles in `tests/fas` and `tests/fld`.

## Known limits
- The seeds are opaque binaries. Trust comes from the pinned hashes plus the fixpoint and the
  optional Rust cross-check (diverse double-compiling), not from source alone.
- The seeds run only on x86-64 Linux hosts.

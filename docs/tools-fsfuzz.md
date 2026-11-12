# fsfuzz (tools/fsfuzz.forge)

Differential fuzzer for the freestanding backend's `--fs-opt` pass (Phase 12.1). It is the gate before any further
step 4 work on the register allocator.

    fsfuzz [--seed N] [--n COUNT] [--keep]       (defaults: seed 1, 20 programs)

## What it checks

It writes COUNT random programs to build/fsfuzz/pK.forge and builds each one four ways with build/forgec, build/fas
and build/fld:

| config | flags |
|---|---|
| 0 | default (O2) |
| 1 | `-O0` |
| 2 | `--fs-opt` |
| 3 | `-O0 --fs-opt` |

Each binary runs under `/usr/bin/timeout 10`. The result is the exit status plus stdout. All four must be equal. Any
difference (or a build failure) counts as a diff and prints the four results, truncated to 300 characters.

It ends with `fsfuzz: seed N, COUNT programs (L lines), 4 builds each (...): A agree, D differ` and exits 1 when D > 0.
Without `--keep` it deletes each program's .s, .o and binaries after the comparison.

## The programs

The generator is an LCG (`seed * 1103515245 + 12345` mod 2^31), so a seed always gives the same programs. Each
program has several functions `fK(p0..pN-1: i64 [, w: i64[4]]) -> i64` and a `main` that calls them and prints the
results. A function can have:

- up to 8 parameters (stack arguments), and more scalar locals than the pass has free registers;
- a `w: i64[4]` array parameter (copied with `rep movsb`), a frame array `t: i64[8]`, and `addr()` of a local (that
  local must stay in memory: step 4a pinning);
- a call to an earlier function, so values live across calls (callee-saved promotion);
- `if`/`else` blocks and nested `while` loops with constant bounds. Loop counters are read-only inside the loop, so
  every program ends.
- wrapping `+ - *`, `& | ^`, shifts by masked counts, unsigned shifts, narrow casts (u8, i8, u16, i32, u32), signed
  and unsigned compares, division and modulo by constants (the magic-number path) and by variables forced positive
  (`(b & 1023) + 1`), and array reads and writes with masked indices.

None of this is undefined: ints wrap, divisors are positive, shift counts and indices are masked. The programs are
safe code, so step 4a's promotion next to frame aggregates applies.

## Tests

run_tests.sh builds the tool and runs `build/fsfuzz-tool --seed 1 --n 20` (`PASS fs-opt-fuzz`) at both O2 and
FORGEC_FLAGS=-O0. The tool itself is built with those flags, but every program is always built in all four configs.

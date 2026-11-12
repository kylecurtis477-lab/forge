# bench (tools/bench.forge)

Phase 12.4 performance tracking. `./forge bench ...` builds the tool to `build/bench` on demand. It's the Forge
port and extension of `bench/run_bench.py`, which is kept as the reference.

## Commands
- `bench run [--reps N] [--no-record] [--history FILE]`: compiles each `bench/<name>.forge` with `./forge` and its
  native reference `bench/<name>.rs` with `rustc -C opt-level=3`. It checks that both print the same output, and
  times both binaries (best of N, default 5). It also times the self-compile, `build/forgec selfhost/forgec.forge`
  (best of N). It prints a markdown table and appends one record to `bench/history.tsv`. A failed compile or an
  output mismatch exits 1 and records nothing.
- `bench check [--history FILE] [--baseline FILE] [--tol PCT] [--window N]`: compares the newest record in the
  history with the newest one in `bench/baseline.tsv`. Each history value is taken as its best over the newest
  N records (default 3: the lowest self-compile time and, per bench, the lowest ratio), so a slowdown has to show
  up in N runs in a row. One noisy run on the shared box (the lists bench and its Rust reference each varied
  about 2x while QEMU ran) doesn't fail the check. It fails (exit 1) when the self-compile time, or any bench's
  Forge/reference run-time ratio, is more than PCT percent (default 25) above the baseline. Ratios are used for
  the benches so that a faster or slower machine cancels out. It also prints how far each 12.4 target is.

## Record format
One line, tab separated: `<local time> <git sha> selfcompile_ms=<ms> <bench>=<forge_us>/<ref_us> ...`. The sha is
HEAD when the run started, so a record taken before committing a change names the commit below it.
Lines starting with `#`, and lines that aren't records, are ignored.

## Targets (BUILD_PLAN Phase 12.4)
- Self-compile under 1 s.
- Parity on the bench suite: the Forge time is at most the native reference time for every bench.

Both are measured and reported, not gated. The native reference is rustc -O3 (LLVM). The plan names gcc -O2;
that column needs C reference programs, which the All-Forge rule keeps out of the tree for now, so the Rust
programs that were already in bench/ stand in for it.

## Baseline policy
`bench/baseline.tsv` changes only on purpose: when a commit is meant to change performance, copy a fresh
`bench run` record into it in the same commit. Never refresh it to hide a regression.

## Tests
Timing on a shared machine is noisy, so `run_tests.sh` never runs `bench run`. It checks the record parser and
the comparison logic on fixed records in `tests/bench/`: `ok` (within tolerance, exit 0), `slow` (self-compile
+30%, exit 1, with a non-record line that must be skipped) and `ratio` (one bench ratio +30%, exit 1, and the
self-compile target reported as met), and `window` (three records where only the oldest is fast: ok with the
default window, exit 1 with `--window 1`). The full output must match each `.expected` file.

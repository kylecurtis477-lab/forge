# grammar_fuzz (tools/grammar_fuzz.forge)

This is the Forge port of `tools/grammar_fuzz.py`. For the same seed it produces identical output: the same stdout, exit
code and build/fuzz files.

    grammar_fuzz [--seed N] [--n COUNT]       (defaults: seed 1, 200 programs)

## What it checks

It writes COUNT programs to build/fuzz, alternating valid and invalid ones:

- **Valid (`vK.forge`):** generated from docs/GRAMMAR.md. Each must pass `forgec --emit=ast-json`, and its
  `--emit=pretty` form must parse to the same AST. The comparison ignores the file name and line numbers.
- **Invalid (`iK.forge`):** one top-level line is broken (unbalanced, dangling, `= =`, bare keyword, header without a
  block, or header without `:`). Each must exit 1 with an E0100–E0105 diagnostic.

It prints `PASS grammar-fuzz (seed N: V valid round-tripped, I invalid rejected)` or one FAIL line per problem.

## How it matches the Python version

Byte-identical output relies on a bit-exact copy of CPython's `random.Random`:

- MT19937 with `init_by_array`
- `_randbelow` via `getrandbits`, and `choice`
- `random()` taken as a 53-bit integer and compared with integer thresholds

Every draw happens in the order the Python generator makes it. forgec runs through `/bin/sh` with stderr sent to a
temporary file, `/tmp/grammar_fuzz.<pid>.err`, which is deleted at the end.

## Tests

- tests/grammar/run.sh runs this build with seed 1 and 160 programs.
- tests/grammar-fuzz/run.sh compares it with the Python version across 8 seed/count runs.

# RFC: query-based incremental compiler

Status: **proposed** (v1.3 queue item 5). This is a design document only: no compiler code changes
("no refactor"). Nothing in this RFC is implemented yet; each step under *Rollout* is its own
future plan item with its own tests.

## 1. Where we are today

`forgec in.forge out.s` (selfhost/compiler.forge, `forge_main`) is a whole-program, single-shot compiler:

1. The prelude (`prelude_text()`) is lexed first, then `load_imports` lexes every imported module
   (`load_mod`, std-module renaming) into **one global token stream**; `# forge: no-runtime` programs
   instead textually inline `include`s (`fs_include`).
2. `check_unclosed` and `reject_effects` (E0105/E0106) run, then `typecheck()` over the whole stream.
3. `rewrite_tokens`, `prescan`, then `one_pass()` repeats until type inference converges (at least two
   passes, a further hybrid-IR pass at -O2, capped at 40). Inference is global: a call site in one module
   can change the inferred type of a function in another.
4. `reach_all()` drops unreachable functions; the runtime, globals and field tables are appended; one
   `.s` file comes out. fas/fld assemble and link it.

Cost: every compile re-lexes, re-checks and re-generates the prelude and every module. The LSP
(tools/lsp.forge, Phase 7.5 / 11.3) works around this with a *text-level* cache (a document is
recompiled only if its text changed; sibling modules are parsed once per on-disk text; saving a module
rechecks the open importers), but each recheck is still a full forgec run.

## 2. Goals and non-goals

Goals
- A rebuild after an edit does work proportional to what the edit can affect.
- **Byte-identical output**: an incremental build produces exactly the `.s` (and therefore the binary)
  a clean build produces. FIXPOINT, SEED OK and seal hashes must not be able to tell the difference.
- One mechanism for the CLI, `build.sh` and the LSP.

Non-goals
- Changing the language, the diagnostics schema (v1) or the AST schema (v1).
- Parallel compilation (possible later on top of queries, not part of this RFC).
- Caching across compiler versions: a different compiler is a different cache.

## 3. Unit of granularity: the module, then the function

- **Module** (one `.forge` / `.fsi` file, after `import` resolution; the prelude is module 0) is the unit
  for lexing, parsing, the AST and name collection. Kernel `.fsi` include chains (freestanding mode) are
  treated as one module per included file, keyed by the include site so that textual inclusion order is
  part of the key.
- **Function** is the unit for type checking results, code generation and the -O2 IR passes. Classes
  contribute a *layout* query (fields, `find_field` slots) that functions depend on.
- Whole-program steps stay whole-program but become cheap joins over cached per-function results:
  inference convergence, `reach_all`, global/field-table emission and final concatenation.

## 4. Queries

Every phase becomes a pure function of its inputs, memoised by key. Proposed queries:

| query | input | output |
|---|---|---|
| `source(path)` | file bytes | text + SHA-256 |
| `tokens(mod)` | `source` | token array (kinds, values, lines, file id) |
| `ast(mod)` | `tokens` | strict AST (same as `--emit=ast-json`) |
| `imports(mod)` | `ast` | resolved module list (`import`, `from ... import`, `as`) |
| `items(mod)` | `ast` | top-level names: defs, classes, globals, externs, decorators, pragmas |
| `signature(fn)` | `items` + annotations | declared parameter/return types, `@unsafe`/`@patchable`/... |
| `layout(class)` | `items` | field order, slots, struct/float classification (SysV) |
| `check(fn)` | body AST + `signature`s + `layout`s it names | diagnostics + local type facts |
| `infer(program)` | all `check` facts | converged global type assignment (fixpoint) |
| `codegen(fn)` | body + `infer` slice for `fn` + flags | asm text for `fn` |
| `reach(program)` | call graph from `codegen` | reachable set |
| `link_s(program)` | everything above + runtime | final `.s` |

Rules: a query may read only other queries (never globals left over from a previous query); its result
is a value that can be hashed; reading a query records a dependency edge.

## 5. Dependency graph and invalidation

- Edges are recorded dynamically while a query runs (red-green style). After an edit, `source` of the
  changed file is re-hashed; every dependent query is marked *maybe-stale*.
- **Early cutoff**: a maybe-stale query re-runs only if one of its inputs' *result hashes* changed. Editing
  a function body re-runs `tokens`/`ast` of that module, but if `items` and every `signature` hash the
  same, no other module's `check` is touched.
- **The inference fixpoint is the hard part.** Today inference iterates over the whole program. Proposal:
  keep the iteration, but make each round a join over per-function *type summaries* (the facts a
  function exports: its inferred parameter/return/global types). A function is re-checked in a round only
  if a summary it reads changed in the previous round. A body edit that does not change the function's
  summary converges in one round with no other function revisited. If the summary does change, the
  worklist spreads exactly along the call/use edges, which is what the global loop does today, only
  narrower. The pass cap (40) and the "did not converge" error are kept unchanged.
- Program-wide inputs are explicit keys, not hidden state: compiler identity, `-O0`/`-O2`, `--test`,
  `--freestanding`, `# forge: wrapping-ints` / `no-runtime` pragmas, `--nohyb`. Changing any of them
  invalidates everything downstream of `tokens`.

## 6. Cache format and keys

- Location: `build/.forgec-cache/` (never committed; `build/` is already ignored). Content-addressed:
  `objects/<sha256>` holds a result, `index/<query>/<key-sha256>` maps a key to the object hash plus the
  list of dependency (query, key, result-hash) triples it read.
- **Key** = SHA-256 over: cache format version (starts at 1), compiler identity (the SHA-256 of the running
  forgec binary, i.e. the value pinned in seed/seed.sha256 for the seed), query name, the program-wide flag
  set, and the query's input result hashes. Never timestamps, paths outside the project, PIDs or the
  environment.
- **Values** are written in a canonical, versioned binary form (little-endian lengths + bytes; token arrays
  and summaries in a fixed field order) so equal results hash equally. Asm text is stored verbatim.
- Writes go to a temporary file and are renamed into place; a truncated or corrupt object fails its hash
  check and is treated as a miss. The cache is an optimisation only: deleting it must never change output.
- Eviction: none in v1 (size-capped LRU later); `forgec --cache=off` disables it, `--cache-verify` recomputes
  every hit and fails on a mismatch (used by the tests below).

## 7. Interaction with the LSP re-check cache

- The LSP keeps its own text cache (identical text → republish cached diagnostics) as the first level;
  it costs nothing and stays.
- Behind it, the LSP stops spawning a full forgec per recheck and instead runs the queries in a
  long-lived process (in-process, or a `forgec --serve` child speaking the same JSON-RPC framing). Unsaved
  buffers become `source` overrides keyed by their text hash, which removes the hidden sibling temp file.
- "Saving a module rechecks open importers" becomes automatic: importers depend on that module's `items`
  and `signature`s; with early cutoff, saving a body-only change rechecks nothing else.
- Goto-definition, hover, references and completion read `ast`/`items`/`check` instead of re-parsing
  JSON, and can get columns once the AST carries them (a known 7.5 gap; a schema v2 change, out of scope).
- `forge/stats` grows per-query hit/miss counts so tests/lsp can assert the cutoff works.

## 8. Determinism, seals and provenance

- The bootstrap gates are the contract: FIXPOINT (stage2.s == stage3.s == stage4.s) and SEED OK must hold
  with the cache on, off, cold and warm. `build.sh` and `tools/reseed.sh` run with `--cache=off` until the
  equivalence tests below have been green for a release; the seed is always built cold.
- Output order must not depend on cache hit order: functions are emitted in source order of the
  concatenated program, globals and `FT_` tables in their current (first-seen) order, exactly as today.
- Seals (seal v3, proof manifests) hash the final image, so they need nothing new **as long as output is
  byte-identical**. A cache must never be a way to get a different image under the same source hash; that
  is why compiler identity is in every key and why `--cache-verify` exists.
- Provenance and unsafe accounting (`--provenance-lint`, tests/unsafe baseline counts) are computed from
  `check(fn)` results; they are per-function facts, so cached and fresh counts are the same by
  construction, and the lint stays a whole-program report over all of them.
- Reproducibility of a proof manifest therefore stays: same sources + same compiler binary ⇒ same image.
  The cache adds no input that the manifest does not already pin.

## 9. Test strategy

1. **Equivalence (the main test)**: for every program run_tests.sh compiles, and for the compiler itself,
   cold-cache output == `--cache=off` output == warm-cache output, byte for byte, at -O0 and -O2.
2. **Edit scripts**: tests/incremental/ applies scripted edits (body-only change, signature change, new
   import, class field added, pragma toggled, file deleted) to a copy of a project and checks after each
   edit (a) output == clean build, (b) the set of re-run queries is exactly the expected one (e.g. a
   body-only edit re-runs `tokens/ast/items/check/codegen` for one module and one function only).
3. **Fuzz**: tools/grammar_fuzz programs, compiled cold, then mutated one function at a time and
   recompiled warm; outputs compared with cold builds.
4. **Corruption**: truncate / flip bytes in cache objects and index entries; the build must still produce
   the clean output (treated as misses) and report nothing else.
5. **Bootstrap**: build.sh FIXPOINT/SEED OK with the cache warm from a previous build, and
   `--cache-verify` over the compiler.
6. **LSP**: tests/lsp/run.sh extended with per-query stats: identical edits hit, body edits do not
   recheck importers, signature edits do.

## 10. Rollout (each a separate plan item, none started)

1. Make phases queries in-process with no persistent cache (pure refactor; output byte-identical; gate:
   equivalence test 1 trivially and all existing gates).
2. Per-function type summaries and the worklist fixpoint (gate: same `.s` for every test, pass counts reported).
3. Persistent content-addressed cache + `--cache=off|verify` (gates: tests 1, 2, 4, 5).
4. LSP on queries (gate: tests/lsp + test 6).

## 11. Open questions

- Whether `reach_all` should prune before or after cached codegen (pruning first saves work, but makes a
  function's codegen depend on the whole call graph).
- Whether the -O2 hybrid IR pass can be keyed per function, or needs the whole-program type assignment.
- Cache sharing between the host compiler and the kernel build (`.fsi` modules compiled in freestanding
  mode are a different flag set, so they would not share entries today).

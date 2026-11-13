# optcov (tools/optcov.forge)

Block coverage of the freestanding backend's optimizer passes: build-plan item 3 (optimizer correctness gate),
parts (a) and (c). It answers "which code paths of each optimizer pass does our test corpus actually reach?",
and with `--check` it turns the answer into a gate: every block is either reached or listed, with a reason, as
unreachable.

    optcov [--quick | --check] [--out FILE] [--allow FILE]   (default report: build/optcov/report.txt)

## How it works

1. It instruments `selfhost/forgec.forge` (the compiler source plus the embedded prelude and runtime; its first
   lines are exactly `selfhost/compiler.forge`, so line numbers in the report point into compiler.forge). Every
   block of the optimizer functions gets a counter: the function body, and the body of every `if`, `elif`, `else`,
   `while` and `for`. The counters are added into `build/optcov/counts.txt` when a freestanding compile finishes
   (a compile error adds nothing).
2. It builds that compiler (`build/optcov/forgec-cov`) with build/forgec, build/fas and build/fld. The
   instrumented compiler produces byte-identical assembly to build/forgec (checked on the kernel, default and
   `--fs-temps`).
3. It compiles the corpus with it under five configurations: default, `--fs-opt`, `--fs-live`, `--fs-temps` and
   `--no-bounds`. The corpus is every freestanding program in tests/ and examples/ that should compile (bad_*,
   invalid/, diag/ and errors/ cases are left out), plus the fsfuzz programs in build/fsfuzz if a
   `fsfuzz --keep` run left them there.
4. It writes the report: per pass group and per function, blocks hit out of blocks total, then every block that
   was never hit, with its compiler.forge line and header.

Pass groups: the `--fs-opt` allocator (`fs_regalloc`, `fs_ra_*`), its peepholes (`fs_rapeep`), `--fs-live`
liveness (`fs_lv_*`), `--fs-temps` (`fs_tmp_*`), constant division by magic numbers (`fs_cdiv_try`, used under
any of the three flags) and bounds-check elision (`fs_bsafe`, always on unless `--no-bounds`).

`--quick`: two programs, two configurations; it fails if the instrumented compiler does not build, writes no
counts, or hits no block of a pass group other than bounds-check elision (a smoke test of the tool itself).

## The gate: `--check` (part c)

`--check` (run_tests.sh `optimizer-coverage`, about 45 s) runs the full corpus without the fsfuzz programs, so
the result depends only on the tree, then compares the blocks never hit with the allow-list
`tools/optcov-allow.txt`:

- a never-hit block that is not on the list fails (`CHECK never hit and not allowed`): a new optimizer path landed
  without a program that reaches it;
- an entry that names no block fails (`CHECK stale ... no such block`), and so does an entry the corpus now hits
  (`CHECK stale ... now covered, remove it`): the list cannot hide a path that is reachable.

Allow-list lines are `function | block header | reason`. The header is the block's `if`/`elif`/`else`/`while`/
`for` line as written in selfhost/compiler.forge, stripped; when a function has several blocks with the same header
the entry ends with ` #N` (N-th in source order, from 1; `--check` prints the key it wants). Entries are keyed by
text, not line numbers, so compiler edits elsewhere do not touch the list.

A reachable hole gets a program in `tests/optgate/` instead (with a `.expected` output); optcov picks it up as part
of the corpus, and run_tests.sh builds and runs every tests/optgate program under default, `--fs-opt`,
`--fs-live` and `--fs-temps` (checks `optgate*`), so the program also proves the path computes the right answer.

## Registry and landing rule (part d)

`--registry` (also run first by `--check`, so run_tests.sh `optimizer-coverage` covers it) reads
selfhost/compiler.forge without compiling anything and fails when

- the compiler parses a `--fs-*` option that is not one of optcov's configurations, or that tools/fsfuzz.forge
  does not mention (so the combination fuzzer would never build with it);
- a line that tests a pass switch (`if`/`elif`/`while` naming `fs_ra`, `fs_live` or `fs_temps`) calls, or its
  block calls, an `fs_*` function that is in no optcov pass group (so its blocks would never be counted).

`--src FILE` points it at another compiler source; a copy with a planted `fs_newpass(s0)` under `if fs_temps:`
and a `"--fs-newpass"` option gives three CHECK lines and exit 1.

**Landing rule** (BUILD_PLAN item 3, from 2026-11-13): a new fs optimizer pass, or a change to an existing one,
lands only when, in the same gated batch,

1. `optcov --check` passes: the pass is in a GROUPS entry (and its switch in GATES if it has a new one), every
   block it adds is hit by the corpus or a new tests/optgate program, or is on tools/optcov-allow.txt with a reason
   that holds for every program the code generator can emit;
2. `fs-opt-combos` passes with the new flag in fsfuzz's flag sets (run_tests.sh runs `--combos --seed 7 --n 3`),
   and a longer `fsfuzz --combos` run (at least 25 programs) is recorded in the commit's docs;
3. the pass does not raise the kernel's `--unsafe-report` count and the O2 and O0 gate runs are clean.

### Part (c) result (2026-11-13)

Of the 32 blocks never hit in the part (a) report, 2 were reachable and 30 are not:

- Reachable: the pin table limit. A function with no unsafe operation that names more than 4096 frame offsets in
  `lea`/other %rbp forms fills `fsp_pin`; `fs_ra_pin` returns -1 and `fs_regalloc` gives up on the function.
  `tests/optgate/pins.forge` reads a frame array 4224 times in one function and covers both blocks (`fs_ra_pin:
  if npin >= 4096` and `fs_regalloc: if npin < 0`); its output is the same under every level.
- Unreachable, 29 defensive bail-outs and 1 capacity limit, each listed with its reason in tools/optcov-allow.txt.
  The defensive ones re-check the shape of text the fs code generator itself emits (every jump targets an
  fs_newlab `.LFn` label, `rep` is only `movsb`/`stosb`, frame offsets are decimal, at most six arguments are
  reloaded into registers, each function exit has one `leave`). The capacity one (`fs_ra_use: if nsl >= 4096`)
  needs more than 4096 distinct slots in one function, and the compiler stops before that: see the finding below.
- Finding (not fixed in this batch): a freestanding function with more than 4096 locals stops the compiler with an
  internal error (`list assignment index 4096 out of range for length 4096`, from the `fsl_*` tables) instead of a
  diagnostic. Found while probing the slot limit; queued as a fix commit for the next batch.

## Report (2026-11-13, at commit "tools: optcov", after `fsfuzz --seed 1 --n 20 --keep`; part (a) baseline)

```
optcov: 98 programs x 5 configurations (481 compiles counted, 9 refused), 284 optimizer blocks
  fs-opt allocator (fs_regalloc, fs_ra_*): 107 / 125 blocks (85.6%)
  fs-opt peepholes (fs_rapeep): 8 / 8 blocks (100.0%)
  fs-live liveness (fs_lv_*): 63 / 68 blocks (92.6%)
  fs-temps (fs_tmp_*): 49 / 57 blocks (85.9%)
  constant division (fs_cdiv_try): 19 / 20 blocks (95.0%)
  bounds-check elision (fs_bsafe): 6 / 6 blocks (100.0%)
  total: 252 / 284 blocks (88.7%)

per function (hit / blocks):
  fs_bsafe: 6 / 6
  fs_cdiv_try: 19 / 20
  fs_ra_slot: 4 / 8
  fs_ra_use: 4 / 6
  fs_ra_mnok: 2 / 2
  fs_regalloc: 37 / 44
  fs_ra_pin: 6 / 7
  fs_ra_pinned: 3 / 3
  fs_ra_scalar: 3 / 3
  fs_ra_opnd: 3 / 4
  fs_ra_rsparg: 4 / 5
  fs_ra_assign: 22 / 22
  fs_lv_lab: 2 / 2
  fs_lv_num: 2 / 4
  fs_lv_ref: 12 / 12
  fs_lv_build: 34 / 37
  fs_lv_assign: 13 / 13
  fs_ra_comma: 2 / 2
  fs_ra_immq: 2 / 3
  fs_ra_1line: 2 / 3
  fs_ra_save: 7 / 7
  fs_ra_gpr: 1 / 1
  fs_ra_src: 3 / 3
  fs_ranext: 2 / 2
  fs_rapeep: 8 / 8
  fs_tmp_peep: 6 / 6
  fs_tmp_prev: 2 / 2
  fs_tmp_load: 5 / 7
  fs_tmp_names: 6 / 6
  fs_tmp_ok: 3 / 3
  fs_tmp_args: 3 / 3
  fs_tmp_call: 21 / 27
  fs_tmp_isld: 3 / 3

blocks never hit (compiler.forge line: header):
  18388 fs_cdiv_try: if not (str_eq(o, "/") or str_eq(o, "//") or str_eq(o, "%")):
  19817 fs_ra_slot: if not ends(o, "(%rbp)"):
  19820 fs_ra_slot: if len(t) == 0:
  19825 fs_ra_slot: if i >= len(t):
  19829 fs_ra_slot: if c < 48 or c > 57:
  19840 fs_ra_use: if v == 2:
  19848 fs_ra_use: if nsl >= 4096:
  19883 fs_regalloc: if not ends(l, ":\n") or contains(l, " "):
  19899 fs_regalloc: else:
  19909 fs_regalloc: if starts(mn, "j") and not starts(ops, ".L"):
  19932 fs_regalloc: if npin < 0:
  19936 fs_regalloc: if nsl < 0:
  19954 fs_regalloc: if nret != nlv:
  19957 fs_regalloc: while r < 11:
  19991 fs_ra_pin: if npin >= 4096:
  20017 fs_ra_opnd: if len(ops) < 6:
  20037 fs_ra_rsparg: if (c < 48 or c > 57) and c != 45:
  20117 fs_lv_num: if len(t) == 0:
  20122 fs_lv_num: if c < 48 or c > 57:
  20181 fs_lv_build: if n == 0:
  20223 fs_lv_build: if not starts(ops, ".LF") or lmin < 0:
  20226 fs_lv_build: if v < lmin or v > lmax or lpos[v - lmin] < 0:
  20329 fs_ra_immq: if k >= len(ops):
  20337 fs_ra_1line: if char_at(l, j) == 10:
  20466 fs_tmp_load: if k < 0:
  20469 fs_tmp_load: if r < 0:
  20521 fs_tmp_call: if n >= 6:
  20534 fs_tmp_call: if pad < 0:
  20550 fs_tmp_call: if d < 0 or d % 8 != 0 or d // 8 >= n or tr[d // 8] >= 0:
  20554 fs_tmp_call: if lr[x] == lr[q]:
  20570 fs_tmp_call: if dep == 0:
  20578 fs_tmp_call: if found < n:
```

## What the report says

- 88.7% of the 284 optimizer blocks are reached. Most blocks never hit are defensive bail-outs: an operand that is
  not an rbp slot, a label that is not a plain label, table-size limits (4096 slots or pins), a call with more than
  six register arguments in `fs_tmp_call`, or a jump to a non-local label. They decide when a pass gives up, so a
  bug there would show up as a miscompile only on unusual code; the combination fuzzer (`fsfuzz --combos`) and
  new targeted programs are how they get covered. Part (c) settled every one of them (above).
- The only `fs_cdiv_try` block never hit is its "not a division" early return: callers already filter for `/`,
  `//` and `%`.
- Finding: `-O0` changes nothing in freestanding mode. The fs backend ignores `opt_on` (it only drives the hosted
  optimizer), so `forgec x.forge out.s -O0` gives byte-identical assembly to the default build for every
  freestanding program (checked on the kernel). Half of the eight fsfuzz builds per program were therefore
  duplicates, and run_tests.sh's `FORGEC_FLAGS=-O0` pass does not exercise a different code generator for the
  kernel or user programs. The combination fuzzer counts distinct assemblies instead of builds.
- Not covered by this tool yet: the hosted optimizer (constant folding, inlining, tail calls, typed ints), which
  the fs passes do not share.

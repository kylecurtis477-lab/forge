# Forge

Forge is a small language with Python-like syntax. It is compiled ahead of time to x86-64 Linux assembly (GNU `as`, AT&T syntax) and linked with `ld`. It has no libc and no runtime dependencies. The compiler, `selfhost/forgec.forge`, is written in Forge and compiles itself to a fixpoint.

## Status
- **Self-hosting works.** The Rust bootstrap compiles forgec into stage1. Then stage1 builds stage2, stage2 builds stage3, and stage3 builds stage4. `stage2.s == stage3.s == stage4.s` byte for byte. Once stage1 exists, Rust is no longer needed: forgec emits its own runtime asm. Only binutils `as`/`ld` is still required.
- **Tests:** `./run_tests.sh` runs 23 tests: 12 program tests (t1–t4, bools, none, floats, floats_basic, inherit, exceptions, closures, gc), 4 runtime-error tests and 7 compile-error tests. All pass. `closures.expected` and `floats.expected` are CPython output.
- **Recovery:** the source transcript cut every `main.rs` write off at 4101 chars. The lexer and the edit fragments were recovered (see `recovered/`). The parser, codegen and runtime in `src/` were rewritten. `selfhost_start.forge` is the transcript's latest fragment, taken verbatim; it compiles and prints `scan loop fixed`.

## Build
```sh
./build.sh                 # cargo build bootstrap -> stage1..stage4, checks fixpoint, installs build/forgec
./forge prog.forge -o prog # compile with self-hosted forgec + as + ld
./run_tests.sh
bench/run_bench.py         # Forge vs Rust vs CPython
```
Optional bootstrap: `target/release/mylang file.forge [-o out] [-S]`, or `-e 'src'`.

## Syntax
```python
# comments; indentation blocks; optional type annotations
class Point:
    x: int
    y: int

def fib(n: int) -> int:
    if n < 2:
        return n
    return fib(n - 1) + fib(n - 2)

def main():
    p = Point(3, 4)
    p.x = p.x + 1
    xs = [1, 2, 3]
    xs[0] = p.x * p.y
    total = 0
    for i in range(3):
        total += xs[i]
    while True:
        total -= 1
        if total < 10:
            break
        elif total % 2 == 0:
            continue
        else:
            pass
    s = concat("fib=", str(fib(20)))
    puts(s)
    if not str_eq(s, "") and char_at(s, 0) == 'f':
        print(total)
    if argc() > 1:
        puts(argv(1))
    write_file("/tmp/x.txt", "data\n")
    puts(read_file("/tmp/x.txt"))
main()
```

- Statements: `def`, `class` (records with field lines), `if/elif/else`, `while`, `for v in range(a[, b])`, `return`, `break`, `continue`, `pass`, `=`, `+=`/`-=` (on plain variables), index and field assignment.
- Expressions: `+ - * / %` (C semantics: `-7 % 3 == -1`), `== != < <= > >=`, `and or not` (short-circuit), unary `-`, calls, `a[i]`, `a.f`, list literals `[..]`, `"strings"` with `\n \t \0 \\ \"`, char literals `'a'` (ints), `True False None` (1/0/0).
- Values are 64-bit words. Strings are NUL-terminated byte pointers. Lists are fixed-size word arrays.
- Builtins: `print(int)`, `puts eputs`, `str(int)`, `chr`, `concat(a,b)`, `join(list,n)`, `len(s)`, `char_at substr(s,i,n) set_byte str_eq`, `list(n) alloc(bytes)`, `read_file write_file`, `argc argv(i)`, `exit(code)`.
- Variables at top level are globals. Inside a `def`, assigning to an existing global name writes the global.

## Implementation
- forgec is a single-pass recursive-descent compiler (~1040 lines). Output lines are collected in a list and joined once at the end, so string building is not quadratic.
- Optimizations:
  - The first 5 params/locals live in callee-saved registers `rbx, r12–r15`.
  - Simple right-hand operands are used directly, without push/pop.
  - Constant folding for small immediates.
  - Comparisons fuse with conditional jumps.
  - `addq`/`subq` with immediates for `+=`/`-=`.
- Runtime (asm): direct syscalls, a mark-sweep garbage-collected heap (see below), and buffered output.

## Phase 2 additions
String `+`/`*`, f-strings, `for x in list/str/dict`, `enumerate`, tuple assign/swap, ternary, comprehensions with filters, `+=` etc. on names/fields/indexes, dicts (open-addressing hash map, insertion-ordered), slices, `split`/`join`/`upper`, `sorted`/`min`/`max`, classes with `__init__` and methods and per-class field layout (dynamic dispatch by name), Python floor `//` and `%`.
Runtime errors carry source lines: index out of range, missing key, division by zero (`tests/runtime`).
Memory: see Phase 3 (GC). `mark()` returns bytes in use; `release(m)` forces a full collection.

## Phase 3 additions
- Values: real `True`/`False` (tagged), `None` as a distinct value that prints `None`, boxed 64-bit floats with Python formatting and mixed arithmetic.
- Classes: single inheritance, `super()`, `isinstance` (`tests/inherit`); exceptions with `try/except/else/finally`, exception classes, `raise`, line-numbered uncaught errors (`tests/exceptions`).
- Functions: `lambda`, nested `def`, closures with `nonlocal`, first-class functions (incl. builtins like `str`/`len` as values), `map`/`filter`, `sorted`/`sort` with `key=`/`reverse=` (`tests/closures`, compared with CPython).
- **Garbage collector:** a non-moving, conservative mark-sweep collector in `selfhost/runtime.s`.
  - Heap: 64 KiB pages in a 64 GiB reserved region, plus a page table. Small objects use 38 segregated size classes (16 B steps up to 512 B, then powers of two up to 32 KiB) with free lists. Larger objects get page runs (first fit). Free pages and runs are returned to the OS with `madvise(DONTNEED)`.
  - Roots: all registers (spilled at the allocation point), the whole machine stack up to the entry `%rsp` (which also covers the exception-handler frames saved on the stack), every global and data object (`rt_dlo..rt_dhi`), and `rt_exc_val`. Interior pointers are accepted. Strings and floats are not scanned.
  - A collection triggers when the bytes allocated reach `max(32 MB, 2 × live)`.
  - `tests/gc` churns about 1 GB of strings, lists, cyclic objects, floats, closures and exceptions. Its peak RSS is 44 MB with GC versus 819 MB without. The compiler runs with GC on while self-compiling (the fixpoint holds). Its peak is still ~300 MB because most of what it allocates stays live: the emitted assembly lines are kept until the final `join`.
Optimizer: multi-pass int-type inference, magic-number division by constants, top-5 weighted locals/params in callee-saved registers, scratch registers for temporaries, inlining of single-`return` leaf functions, hoisting of invariant safe calls in loops, jump-based conditions and rotated loops, and dead-function pruning.

## Benchmarks

Best of 3 wall clock. Rust: rustc -C opt-level=3 (unstripped binaries). Phase 1 Forge: fib 0.015s, loop 0.274s, strbuild 0.093s, lists 0.189s.

| bench | Forge run | Rust -O3 run | CPython run | Forge compile | Rust compile | Forge bin | Rust bin | output |
|---|---|---|---|---|---|---|---|---|
| fib | 0.015s | 0.006s | 0.19s | 173ms | 271ms | 85,104 B | 11,395,072 B | 2178309 |
| loop | 0.088s | 0.073s | 5.57s | 187ms | 200ms | 85,136 B | 11,395,024 B | 199999997 |
| strbuild | 0.090s | 0.070s | 0.11s | 285ms | 332ms | 85,216 B | 11,399,736 B | 6888890 |
| lists | 0.176s | 0.061s | 3.10s | 237ms | 241ms | 85,552 B | 11,399,120 B | 49950000000 |

(Phase 3 run, GC on. Forge binaries now include the prelude, floats, exceptions and the GC, hence about 85 KB.)

## Limitations
- No tuples as a separate type (they behave as, and print as, lists). No bigints, generators, `with`, or keyword arguments on indirect calls.
- No lambda default arguments, no `min`/`max` `key=`, and no `nonlocal` through an intermediate scope that does not itself use the name. Nested `def` is not allowed at module level.
- The collector is conservative and non-moving, so an integer that looks like a heap pointer can keep an object alive.
- Requires binutils `as`/`ld` and Linux x86-64.

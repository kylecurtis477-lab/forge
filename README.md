# Forge

Forge is a small language with Python-like syntax. It is compiled ahead of time to x86-64 Linux assembly (GNU `as`, AT&T syntax) and linked with `ld`. It has no libc and no runtime dependencies. The compiler, `selfhost/forgec.forge`, is written in Forge and compiles itself to a fixpoint.

## Status
- **Self-hosting works.** The Rust bootstrap compiles forgec into stage1. Then stage1 builds stage2, stage2 builds stage3, and stage3 builds stage4. `stage2.s == stage3.s == stage4.s` byte for byte. Once stage1 exists, Rust is no longer needed: forgec emits its own runtime asm. Only binutils `as`/`ld` is still required.
- **Tests:** `./run_tests.sh` runs 2 program tests and 6 error tests (errors report line numbers). All pass.
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
- Runtime (asm): direct syscalls, a 2 GiB bump allocator in `.bss` with no GC, and unbuffered looped `write` syscalls.

## Benchmarks
Measured on this box: best-of-3 wall clock. Rust uses `rustc -C opt-level=3`. CPython is 3.13.5.

| bench | Forge run | Rust -O3 run | CPython run | Forge compile | Rust compile | Forge bin | Rust bin | output |
|---|---|---|---|---|---|---|---|---|
| fib | 0.015s | 0.006s | 0.20s | 8ms | 216ms | 10,008 B | 11,395,072 B | 2178309 |
| loop | 0.274s | 0.080s | 5.87s | 12ms | 259ms | 10,008 B | 11,395,024 B | 199999997 |
| strbuild | 0.093s | 0.071s | 0.12s | 9ms | 468ms | 10,072 B | 11,399,736 B | 6888890 |
| lists | 0.189s | 0.136s | 3.22s | 7ms | 258ms | 10,200 B | 11,399,120 B | 49950000000 |

How to read the results:
- **Forge is 1.3–3.4× slower than Rust on every benchmark** and 1.3–21× faster than CPython.
- Why Forge trails:
  - It does no inlining or loop optimization.
  - `%`/`/` use `idiv`, where LLVM uses multiply-by-magic-constant.
  - Complex expressions still go through the stack.
  - Locals beyond 5 use memory.
  - It has no bounds checks, which helps Forge, not Rust.
  - LLVM transforms recursive `fib` aggressively.
- Forge wins on compile time (~10 ms vs ~250–470 ms) and binary size (~10 KB vs 11 MB unstripped Rust; about 390 KB stripped).

## Limitations
- No GC: memory is never freed.
- No nested functions or closures, and no conditional expressions.
- `for` supports only `range`.
- No string `+`: use `concat`/`join`.
- Field offsets are resolved by field name across all classes, so a shared field name must be at the same position in every class.
- No bounds checks, and errors stop at the first one.
- Requires binutils `as`/`ld` and Linux x86-64.

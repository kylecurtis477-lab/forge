# Forge

Forge is a small language with Python-like syntax. It is compiled ahead of time to x86-64 Linux assembly (GNU `as`, AT&T syntax) and linked with `ld`. It has no libc and no runtime dependencies. The compiler, `selfhost/forgec.forge`, is written in Forge and compiles itself to a fixpoint.

## Status
- **Self-hosting works.** A checked-in seed compiler (`seed/forgec-seed`), with the seed assembler and linker (`seed/fas-seed`, `seed/fld-seed`; all sha256 pinned in `seed/seed.sha256`), builds stage2 with no GNU as/ld; stage2 builds stage3, and stage3 builds stage4. `stage2.s == stage3.s == stage4.s` byte for byte. Rust is not needed: the old Rust bootstrap (src/main.rs) is only an optional cross-check (`FORGE_RUST_CHECK=1 ./build.sh`). `tools/reseed.sh` refreshes the seed from a green build.
- **Tests:** `./run_tests.sh` runs 36 tests: 23 program tests, 6 runtime-error tests and 7 compile-error tests. All pass. Except for `gc`/`gc_retention` (which use `mark()`/`release()`), the program `.expected` files are CPython output.
- **Recovery:** the source transcript cut every `main.rs` write off at 4101 chars. The lexer and the edit fragments were recovered (see `recovered/`). The parser, codegen and runtime in `src/` were rewritten. `selfhost_start.forge` is the transcript's latest fragment, taken verbatim; it compiles and prints `scan loop fixed`.

## Build
```sh
./build.sh                 # seed compiler -> stage2..stage4, checks fixpoint, installs build/forgec
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

## Phase 4 additions
- Generators and `yield` (including generator expressions as call arguments), `with` statements and file I/O (`open`, `read`, `write`, `readlines`, iteration).
- Imports of other `.forge` modules (`import m`, `from m import f`) and a small stdlib: `sys.argv`, `sys.exit`, `os` helpers, `time.time`/`perf_counter`.
- Set comprehensions, dict comprehensions and nested comprehensions; real tuples.
- **Bigints:** dynamic (untyped) int arithmetic promotes to arbitrary precision on overflow (`+ - * // % **`, comparisons, hashing, `str`/`repr`, mixing with floats). Int-typed or inferred-int arithmetic in user code raises `OverflowError` instead of wrapping. The checks are a fall-through `jo` to a cold stub at the end of the function. `# forge: wrapping-ints` turns them off (the compiler itself uses it).
- Loose ends: keyword arguments and defaults on indirect calls (function values, closures, lambdas), lambda default arguments, `nonlocal` through intermediate scopes, `def` inside top-level `if`/`for`/`while` blocks, `for i in range(...)` leaves `i` at its last value as in Python.
- GC: `pop`, `del`, slice deletion, `clear` and dict deletion clear the vacated slots, and `release()` clears scratch registers first, so dropped objects are no longer retained (`tests/gc_retention`).

## Benchmarks

Best of 5 wall clock. Rust: rustc -C opt-level=3 (unstripped binaries). Phase 1 Forge: fib 0.015s, loop 0.274s, strbuild 0.093s, lists 0.189s. Phase 3: fib 0.015s, loop 0.088s, strbuild 0.090s, lists 0.176s.

| bench | Forge run | Rust -O3 run | CPython run | Forge compile | Rust compile | Forge bin | Rust bin | output |
|---|---|---|---|---|---|---|---|---|
| fib | 0.010s | 0.006s | 0.18s | 172ms | 233ms | 137,296 B | 11,395,072 B | 2178309 |
| loop | 0.094s | 0.073s | 5.50s | 203ms | 184ms | 137,264 B | 11,395,024 B | 199999997 |
| strbuild | 0.027s | 0.053s | 0.08s | 275ms | 328ms | 137,344 B | 11,399,736 B | 6888890 |
| lists | 0.042s | 0.045s | 2.96s | 190ms | 223ms | 137,384 B | 11,399,120 B | 49950000000 |
| floats | 0.031s | 0.015s | 0.18s | 237ms | 174ms | 137,776 B | 11,390,192 B | 1500122368 |
| strops | 0.018s | 0.017s | 0.04s | 165ms | 324ms | 138,656 B | 11,516,016 B | 38408 977889 22311 |

(Run 2026-11-01 20:08 CST with the default -O, including the 2**62 band checks; self-compile 1.25s. Full history and -O0 numbers in bench/results.md.)

### Optimizing backend (default; `-O0` turns it off)
Eligible functions are plain int functions with int params. They are lowered to a three-address IR with virtual registers and basic blocks. The passes are const/copy propagation with folding, unreachable-block and jump cleanup, local CSE, liveness-based DCE, and elimination of tag and bounds checks for typed non-escaping `list[int]` locals. Registers are assigned by linear scan over rsi/rdi/r8-r10 and rbx/r12-r15, with spilling. Division by a constant uses magic numbers. Any function that is not eligible uses the direct code generator.

## Systems programming (freestanding mode)
`# forge: no-runtime` or `--freestanding` selects a runtime-free backend. It has fixed-width ints, pointers, C-layout and `@packed` structs, inline asm, port I/O, CPU and atomic intrinsics, C and MS x64 ABI extern/export, and interrupt and UEFI entry attributes.
It powers `examples/kernel` and `examples/efi`. The kernel is a higher-half x86-64 kernel with a frame allocator, its own 4-level paging, a heap, demand paging, a framebuffer console, ACPI/IOAPIC/LAPIC timer, IST stacks, SMP with a preemptive scheduler, ring-3 Forge user programs via syscall/sysret, and PCI + virtio-blk + read-only FAT32 (it loads `\EFI\Forge\init.elf` from the ESP). `examples/efi` is a native UEFI loader written in Forge (`\EFI\Forge\forgex64.efi`) that loads the kernel from a GPT ESP shared with Windows and Linux. The kernel boots through GRUB on BIOS and UEFI, or through the Forge loader (see docs/SYSTEMS.md).
Both are boot-tested headless in QEMU/OVMF by `tests/os/run.sh`. See docs/SYSTEMS.md for the subset, dual-boot design, and roadmap, and docs/BUILD_PLAN.md for the phased build plan.

Subsystem docs (design, formats, APIs, tests, limits):

- docs/process.md: processes, the ELF loader, fork/exec/brk/mmap
- docs/bootstrap.md: the seed compiler, the self-hosting fixpoint and reseeding
- docs/objstore.md: the living object store (Phase 9): versions, blobs, the write-ahead log, persistence, indexes, system rewind and the /obj files-as-objects VFS view
- docs/privacy.md: privacy by design (Phase 10): per-user keys and the object store encrypted at rest; the per-app grant ledger with live revocation (`grants`, `revoke app`); the audit log of every grant event (`audit`)
- docs/linux-compat.md: the Linux syscall personality (Phase 11.1): sealed Linux-ABI images run only in the compat sandbox (`run app`), with the System V start stack and a Linux syscall subset; `lxtest` (raw syscalls, TLS) `lxthreads` (clone threads, futex, group exit) `lxfs` (openat, fstat, getdents64 over granted handles), `lxexec` (execve / execveat inside the sandbox) and `lxsig` (signals: handlers, masks, kill/tgkill of itself, SIGSEGV on a fault) are the self-built test programs; BusyBox 1.38.0, built from verified source against musl (`tools/busybox/build.sh`, foreign seal level 3), is the acceptance test (`run busybox`); syscalls added for it one per commit: geteuid 107, dup2 33, lseek 8, ppoll 271. Phase 11.1 is accepted: BusyBox ash runs a script (`busybox sh /efi/forge/docs/hello.sh`) in the sandbox; external commands and pipelines in ash need fork, not supported yet. Phase 11.2 (C FFI) has begun: SysV stack arguments (more than 6 words, both directions; tests/fs/cabi8), structs by value and struct returns (rax:rdx, hidden pointer over 16 bytes; tests/fs/cabi_struct), f64/f32 floats in xmm registers (tests/fs/cabi_float); plan v1.3 provenance rule: integer-to-pointer only in @unsafe functions, checked by `forgec --provenance-lint`, pointers to C keep provenance; seal v3 trailers (version, algorithm IDs, up to 3 signatures, reserved proof slot; Ed25519 only, v2 still accepted); planned: a C-to-Forge translator (Phase 11.3, C subset to @unsafe Forge) and all-Forge tooling (the Python tools, runtime.s and build scripts ported to Forge)
- docs/services.md: isolated driver services (kbdd, nvmed) and kexec driver state
- docs/livepatch.md: live patching of kernel functions
- docs/GRAMMAR.md, docs/SYSTEMS.md, docs/BUILD_PLAN.md: the language grammar, systems features and the plan

## Limitations
- Int-typed or inferred-int code raises `OverflowError` rather than promoting to a bigint, e.g. `fact(40)` called with a literal. `int(big)` raises. There are no bitwise operators on bigints.
- No `*args`/`**kwargs`, and `type()` is not supported.
- A module-level `def` name cannot also be rebound as a variable. `class` inside a block is not supported. Two `def`s of the same name in different top-level branches count as duplicates.
- `nonlocal` writes through an intermediate closure only reach the defining scope when that closure has no captured variables of its own.
- The collector is conservative and non-moving, so an integer that looks like a heap pointer, or a stale value in a scratch register at an automatic collection, can keep an object alive.
- Requires binutils `as`/`ld` and Linux x86-64.

## Phase 5

- **Static gradual type checker** (written in Forge, runs before codegen). It checks int/float/str/bool/None, list[T], dict[K,V], set[T], tuple[...], Optional[T] / T|None, class types, and Callable, and infers local types. Code without annotations is treated as dynamic. Errors look like `error: line N col M: ...` followed by the source line and a caret. Tests: 15 type-error tests and typed_basics.
- **Optimizer additions:** self tail-call elimination, plus an accumulator transform for `return X + self(...)`. This brings fib from 0.015s to 0.008s. Constant folding, small-function inlining, unboxed typed ints and fixed field offsets were already in place.
- **Still missing:** there is no SSA/linear IR and no linear-scan allocator (locals still go to rbx/r12-r15 first-come). List elements are not typed, so `lists` still uses tag-checked adds and bounds checks. The compiler still uses as/ld; it does not emit ELF itself. Explicit `Base.__init__(self, ...)` calls are not supported, and Optional types are not narrowed by control flow.

Benchmarks (best of 5): see bench/results.md.

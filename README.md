# Forge

Forge is a small language with Python-like syntax. It is compiled ahead of time to x86-64 Linux assembly (GNU `as`, AT&T syntax) and linked with `ld`. It has no libc and no runtime dependencies. The compiler, `selfhost/forgec.forge`, is written in Forge and compiles itself to a fixpoint.

## Status
- **Self-hosting works.** A checked-in seed compiler (`seed/forgec-seed`), with the seed assembler and linker (`seed/fas-seed`, `seed/fld-seed`; all sha256 pinned in `seed/seed.sha256`), builds stage2 with no GNU as/ld; stage2 builds stage3, and stage3 builds stage4. `stage2.s == stage3.s == stage4.s` byte for byte. Rust is not needed: the old Rust bootstrap (src/main.rs) is only an optional cross-check (`FORGE_RUST_CHECK=1 ./build.sh`). `./forge reseed` (tools/reseed.forge; tools/reseed.sh is the reference) refreshes the seed from a green build.
- **Tests:** `./run_tests.sh` runs 36 tests: 23 program tests, 6 runtime-error tests and 7 compile-error tests. All pass. Except for `gc`/`gc_retention` (which use `mark()`/`release()`), the program `.expected` files are CPython output.
- **Plan order:** docs/BUILD_PLAN.md, "Current execution order": 12.1 step 4 (SSA IR and register allocation for fs/kernel mode), then the language server, then the plugin/package manager, with the v1 reservations mixed in; Phase 8 leftovers last. Post-1.0 ideas are in its "Version 2" section.
- **Recovery:** the source transcript cut every `main.rs` write off at 4101 chars. The lexer and the edit fragments were recovered (see `recovered/`). The parser, codegen and runtime in `src/` were rewritten. `selfhost_start.forge` is the transcript's latest fragment, taken verbatim; it compiles and prints `scan loop fixed`.

## Build
```sh
./build.sh                 # seed compiler -> stage2..stage4, checks fixpoint, installs build/forgec
./forge prog.forge -o prog # compile with self-hosted forgec + as + ld
./run_tests.sh
bench/run_bench.py         # Forge vs Rust vs CPython (reference; tools/bench.forge is the Forge tracker)
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
- **Garbage collector:** a non-moving, conservative mark-sweep collector in the hosted runtime, `selfhost/runtime.forge` (naked Forge functions with inline asm; `selfhost/runtime.s` is the original, kept as the byte-check reference).
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

Performance is tracked by `./forge bench run` (tools/bench.forge): each bench against its Rust -O3 reference, plus the
self-compile time, appended to bench/history.tsv under the full commit sha (a tree with uncommitted changes is refused, or recorded marked `+dirty` with `--allow-dirty`). `./forge bench check` compares the newest record with
bench/baseline.tsv (fails above +25%) and reports the Phase 12.4 targets. See docs/tools-bench.md.

### Optimizing backend (default; `-O0` turns it off)
Eligible functions are plain int functions with int params. They are lowered to a three-address IR with virtual registers and basic blocks. The passes are const/copy propagation with folding, unreachable-block and jump cleanup, local CSE, liveness-based DCE, and elimination of tag and bounds checks for typed non-escaping `list[int]` locals. Registers are assigned by linear scan over rsi/rdi/r8-r10 and rbx/r12-r15, with spilling. Division by a constant uses magic numbers. Any function that is not eligible uses the direct code generator.

## Systems programming (freestanding mode)
`# forge: no-runtime` or `--freestanding` selects a runtime-free backend. It has fixed-width ints, pointers, C-layout and `@packed` structs, inline asm, port I/O, CPU and atomic intrinsics, C and MS x64 ABI extern/export, and interrupt and UEFI entry attributes. Float globals take float literals, stored as IEEE bits (f32 rounded to nearest even at compile time).
It powers `examples/kernel` and `examples/efi`. The kernel is a higher-half x86-64 kernel with a frame allocator, its own 4-level paging, a heap, demand paging, a framebuffer console, ACPI/IOAPIC/LAPIC timer, IST stacks, SMP with a preemptive scheduler, ring-3 Forge user programs via syscall/sysret, and PCI + virtio-blk + read-only FAT32 (it loads `\EFI\Forge\init.elf` from the ESP); its NVMe driver takes I/O completions on an MSI-X interrupt and its AHCI driver on an MSI interrupt (sleeping readers), and its e1000 and virtio-net drivers resolve the QEMU gateway by ARP, ping it (ICMP echo) and serve UDP echo on port 7. Its xHCI driver enumerates the first USB device and configures a HID boot keyboard (configuration descriptor walk, SET_CONFIGURATION, SET_PROTOCOL(boot), SET_IDLE), then adds its interrupt IN endpoint (Configure Endpoint, a transfer ring of report TRBs) and decodes the 8-byte boot reports into text; after that the keyboard runs on an MSI-X interrupt and its keys go into the shell's tty input. On Intel VT-d machines it programs every remapping unit with default-deny DMA: only devices the kernel drives and user-mode driver DMA grants get context entries, and an audit checks all 65536 requester IDs. DMA faults are read and reported, and requester-ID aliases behind PCI bridges get entries too. Without a remapping unit the boot log states the permissive DMA policy, and a strict mode refuses driver DMA grants; strict is chosen at boot by a sealed `\EFI\Forge\boot.cfg` (`forgeseal --config`) or by `forge.iommu=strict` on the command line, which can only tighten (examples/kernel/bootpol.fsi). With interrupt remapping, device MSI/MSI-X messages go through source-validated remapping entries. `examples/efi` is a native UEFI loader written in Forge (`\EFI\Forge\forgex64.efi`) that loads the kernel from a GPT ESP shared with Windows and Linux. The kernel boots through GRUB on BIOS and UEFI, or through the Forge loader (see docs/SYSTEMS.md).
Both are boot-tested headless in QEMU/OVMF by `tests/os/run.sh` (the boot harness checks the framebuffer only once two consecutive screendumps after `KERNEL OK` decode identically). See docs/SYSTEMS.md for the subset, dual-boot design, and roadmap, and docs/BUILD_PLAN.md for the phased build plan.
`forgec --fs-opt` (Phase 12.1 steps 1–3, 4a) keeps scalar locals in registers (callee-saved ones across calls, and since step 4a also in safe functions with frame arrays, structs or `addr()`), peepholes the stack-machine code and divides by constants with a multiply. The kernel is built with it since step 3 (kernel code 534,272 to 495,157 bytes), after an audit of every hand-written asm routine for SysV register discipline (a checklist in docs/SYSTEMS.md that tests/asm-audit/run.sh keeps complete); the UEFI loader and the kernel modules since step 3b (loader -8.7%, modules -14%); the user programs since step 3c (-4.2%, frame accesses -20%). tools/fsfuzz.forge checks it differentially: random programs built with and without it, at O2 and -O0, must print the same (`fs-opt-fuzz` in run_tests). `forgec --fs-live` (step 4b) adds liveness-based register sharing: slots whose live ranges do not overlap share one register. The kernel is built with it (frame accesses 11,975 to 11,125, text 596,784 to 595,024 bytes). `forgec --fs-temps` (step 4c, opt-in) passes call arguments straight in registers and loads constants without the stack; the kernel is built with it since then (instructions -14.5%, .text 529,365 to 465,056 bytes, `push %rax` 13,117 to 3,912).
The kernel unsafe ceiling in `tests/unsafe/baseline.txt` is 170 functions, 0 blocks, 709 ops, 7 top-level asm blocks (`forgec --unsafe-report` on `examples/kernel/kernel.forge`); the test uses `-le`, so counts may shrink and must not rise.

Subsystem docs (design, formats, APIs, tests, limits):

- CHANGELOG.md: release notes from 0.17.1 on (0.17.2, 0.17.3 security/VT-d, 0.17.4 fixes, 0.18.0, unreleased 0.18.1), each entry naming its commit; no tags
- docs/process.md: processes, the ELF loader, fork/exec/brk/mmap
- docs/bootstrap.md: the seed compiler, the self-hosting fixpoint and reseeding
- docs/objstore.md: the living object store (Phase 9): versions, blobs, the write-ahead log, persistence, indexes, system rewind and the /obj files-as-objects VFS view
- docs/privacy.md: privacy by design (Phase 10): per-user keys and the object store encrypted at rest; the per-app grant ledger with live revocation (`grants`, `revoke app`); the audit log of every grant event (`audit`)
- docs/linux-compat.md: the Linux syscall personality (Phase 11.1): sealed Linux-ABI images run only in the compat sandbox (`run app`), with the System V start stack and a Linux syscall subset; `lxtest` (raw syscalls, TLS) `lxthreads` (clone threads, futex, group exit) `lxfs` (openat, fstat, getdents64 over granted handles), `lxexec` (execve / execveat inside the sandbox) and `lxsig` (signals: handlers, masks, kill/tgkill of itself, SIGSEGV on a fault, resumed with every register restored: rt_sigreturn leaves by iretq) are the self-built test programs; BusyBox 1.38.0, built from verified source against musl (`tools/busybox/build.sh`, foreign seal level 3), is the acceptance test (`run busybox`); syscalls added for it one per commit: geteuid 107, dup2 33, lseek 8, ppoll 271. Phase 11.1 is accepted: BusyBox ash runs a script (`busybox sh /efi/forge/docs/hello.sh`) in the sandbox; external commands and pipelines in ash need fork, not supported yet. Phase 11.2 (C FFI) is complete: SysV stack arguments (more than 6 words, both directions; tests/fs/cabi8), structs by value and struct returns (rax:rdx, hidden pointer over 16 bytes; tests/fs/cabi_struct), f64/f32 floats in xmm registers (tests/fs/cabi_float), structs with float fields by value (SysV eightbyte classes SSE/INTEGER, xmm + gpr, returns in xmm0/xmm1/rax/rdx; tests/fs/cabi_floatstruct); plan v1.3 provenance rule: integer-to-pointer only in @unsafe functions, checked by `forgec --provenance-lint`, pointers to C keep provenance; seal v3 trailers (version, algorithm IDs, up to 3 signatures, reserved proof slot; Ed25519 only, v2 still accepted); C-to-Forge (tools/c2forge.forge, planned subset steps 1–7 done: lexer through a first real library matching gcc at O2/O0; remaining gaps listed in BUILD_PLAN item 4); Python FFI 11.5(a) done (pinned CPython 3.13.5 built from official source by tools/cpython/build.forge; Forge embed API in tools/cpython/embed.fsi calls libpython through the 11.2 C ABI: Python calls Forge @export functions and Forge calls Python, including a Python function that calls back into Forge, linked with gcc against libpython3.13.a; Python-in-Forge remains a planned track); and all-Forge tooling (the Python tools, runtime.s and the example/BusyBox build scripts ported to Forge; top-level build.sh/run_tests.sh still needs a decision)
- docs/services.md: isolated driver services (kbdd, nvmed) and kexec driver state
- docs/livepatch.md: live patching of kernel functions
- docs/GRAMMAR.md, docs/SYSTEMS.md, docs/BUILD_PLAN.md: the language grammar (v1.2; effect annotations `def f() uses fs, ipc:` are reserved syntax that the checker rejects with E0106), systems features and the plan
- docs/kernel-core.md: kernel core interface spec v1 (scheduler, IPC channels, capabilities), with Appendix A (verification scope: what the tests pin, what they do not, assumptions, the seven DMA/IOMMU gaps, all closed now with their remaining limits, verifier-ready conventions)
- docs/SYSTEMS.md "Process-state format": process-state v1 (examples/kernel/pstate.fsi), the single versioned per-process record; the kexec handoff (format v5) carries exactly these
- docs/SYSTEMS.md "Nondeterminism hook": the kernel's single hook point for syscall results, timer interrupts, scheduler choices and random values (examples/kernel/nondet.fsi), recording off by default
- docs/tools-fsfuzz.md: the `--fs-opt` differential fuzzer (each random program built O2, -O0, --fs-opt, -O0 --fs-opt, --fs-live, -O0 --fs-live, --fs-temps and -O0 --fs-temps; outputs must match)
- docs/tools-bench.md: the Phase 12.4 performance tracker (bench records, baseline, regression check, targets)
- docs/rfc/incremental.md: RFC (proposed, design only) for a query-based incremental compiler: module/function queries, dependency graph with early cutoff, content-addressed cache, LSP integration, byte-identical output (FIXPOINT, seals unaffected) and the test plan

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

## All-Forge tooling
- `forge provenance [RANGE]` (tools/provenance.forge): every commit since the tool was added carries an AI-provenance trailer (`Provenance:` human, ai-assisted or ai-generated, and `AI-Tool:` for AI-written commits); see docs/PROVENANCE.md.

Policy: every tool that can be written in Forge is written in Forge (one language to build, debug and troubleshoot). Python tools are ported one per commit and must reproduce the Python original byte-for-byte on representative inputs before any script switches over.

| tool | Forge source | status | verified by |
|---|---|---|---|
| krel (kernel .krel table, KASLR reference) | `tools/krel.forge` | ported; tests/fld uses it; `tools/krel.py` kept as the reference for tests/krel | `tests/krel/run.sh` ([docs/tools-krel.md](docs/tools-krel.md)) |
| forge-part (create / read the Forge FS GPT partition) | `tools/forge-part.forge` | ported; tests/os/forgefs.sh uses it; `tools/forge-part.py` kept as the reference for tests/forge-part | `tests/forge-part/run.sh` ([docs/tools-forge-part.md](docs/tools-forge-part.md)) |
| mkgpt (GPT test disk: ESP + Windows/Linux placeholders) | `examples/efi/mkgpt.forge` | ported; examples/efi/build.forge (and the build.sh reference) use it; `mkgpt.py` kept as the reference for tests/mkgpt | `tests/mkgpt/run.sh` ([docs/tools-mkgpt.md](docs/tools-mkgpt.md)) |
| ed25519 (build-time signing: public key / signature) | `tools/ed25519.forge` | ported; `ed25519.py` kept as the reference (forgeseal.forge shares its signing code via `tools/ed25519sign.fsi`) | `tests/ed25519-tool/run.sh` ([docs/tools-ed25519.md](docs/tools-ed25519.md)) |
| grammar_fuzz (grammar fuzzer for docs/GRAMMAR.md) | `tools/grammar_fuzz.forge` | ported; tests/grammar/run.sh uses it; `grammar_fuzz.py` kept as the reference | `tests/grammar-fuzz/run.sh` ([docs/tools-grammar_fuzz.md](docs/tools-grammar_fuzz.md)) |
| mkfont (8x16 kernel console font from a TrueType font) | `tools/mkfont.forge` | ported (own TrueType hinter + rasterizer, byte-identical to PIL/FreeType); `mkfont.py` kept as the reference | `tests/mkfont/run.sh` ([docs/tools-mkfont.md](docs/tools-mkfont.md)) |
| forgeseal (Seal v3/v2 proof manifests, `--module`, `--foreign`, `--pub`) | `tools/forgeseal.forge` | ported; examples/user, examples/modules and tools/busybox build scripts use it; `forgeseal.py` kept as the reference | `tests/forgeseal/run.sh` ([docs/tools-forgeseal.md](docs/tools-forgeseal.md)) |
| examples/modules/build.sh (test kernel modules, sealed `--module`, plus negatives) | `examples/modules/build.forge` | ported; examples/kernel/build.sh runs it; `build.sh` kept as the reference | `tests/modules-build/run.sh` |
| examples/user/build.sh (user programs, sealed; seal/ABI negatives; Linux-ABI tests; BusyBox when the source is on the box) | `examples/user/build.forge` | ported; examples/kernel/build.sh runs it; `build.sh` kept as the reference | `tests/user-build/run.sh` |
| tools/busybox/build.sh (BusyBox from pinned source, musl static, EI_OSABI 3, sealed --foreign) | `tools/busybox/build.forge` | ported; examples/user/build.forge runs it; `build.sh` kept as the reference | `tests/busybox-build/run.sh` |
| tools/busybox/bzip2 (stdin/stdout bz2 filter for `tar -xjf` on the BusyBox build PATH) | `tools/busybox/bzip2.forge` | ported; build/busybox-build puts `build/bzip2` on PATH; Python shim kept as the reference | `tests/bzip2/run.sh` |
| examples/efi/build.sh (UEFI loader, FAT32 ESP under \EFI\Forge\, GPT test disk) | `examples/efi/build.forge` | ported; run_tests.sh and tests/os run it; `build.sh` kept as the reference | `tests/efi-build/run.sh` |
| examples/kernel/build.sh (multiboot2 kernel, kernel-next, GRUB ISO; runs user/modules/BusyBox builds) | `examples/kernel/build.forge` | ported; run_tests.sh, tests/os and efi/build.forge run it; `build.sh` kept as the reference | `tests/kernel-build/run.sh` |
| fsfuzz (`--fs-opt` differential fuzzer) | `tools/fsfuzz.forge` | written in Forge (Phase 12.1, gate before further step 4 work) | run_tests.sh `fs-opt-fuzz`, seed 1, 20 programs × 6 builds ([docs/tools-fsfuzz.md](docs/tools-fsfuzz.md)) |
| forge lsp (language server: diagnostics, definition, hover, references, rename, completion, incremental sync + re-check, UTF-16 positions) | `tools/lsp.forge` | written in Forge (Phase 7.5, extended in 11.3) | `tests/lsp/run.sh`, 45 checks ([BUILD_PLAN 7.5](docs/BUILD_PLAN.md)) |
| c2forge (C subset to @unsafe freestanding Forge) | `tools/c2forge.forge` | planned subset (steps 1–7) done; integer/float/array/struct globals (0.15.2); string literals as values; constant-expression global initializers (C `/` and `%`, shifts, bitwise ops); 1-D integer brace lists at file scope and `T a[] = {...}`; tentative and extern redeclarations; float and double globals with initializers; remaining gaps listed in BUILD_PLAN item 4 | `tests/c2forge/run.sh` |
| cpython-build (pinned CPython 3.13.5 from official source; no distro python3-dev) | `tools/cpython/build.forge` | 11.5(a) step 1: pin + install python3, Python.h, libpython3.13.a | `tests/cpython-build/run.sh` |
| cpython-embed (Python↔Forge via 11.2 C ABI; gcc + libpython3.13.a, not fld) | `tools/cpython/embed.fsi`, `tools/cpython/pylayout.forge` → generated `pylayout.fsi` | 11.5(a) done: Forge embed API, Python-to-Forge @export calls and Forge-to-Python eval/object calls; PyConfig/PyStatus/PyMethodDef generated from the pinned headers; non-pinned, free-threaded or debug libpython refused | `tests/cpython-embed/run.sh` |

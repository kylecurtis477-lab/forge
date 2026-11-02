# Forge build plan

This is the plan to execute, in phase order. It merges the open items from docs/SYSTEMS.md (roadmap items 1–9), the compiler performance list, and six pillars:

- **A. Provable safety.** Forge is memory-safe by default:
  - ownership/borrow or region checking;
  - bounds checks, with elision where they can be proven unnecessary;
  - no null;
  - `unsafe` blocks that are checked.

  The OS uses capabilities, and everything runs sandboxed.
- **B. AI-native.**
  - A tiny, regular grammar.
  - Machine-readable diagnostics and a JSON AST.
  - Built-in test/verify tooling.
  - Later, an OS intent/command layer.
- **C. One language everywhere.**
  - Forge for the kernel, apps, and servers.
  - Generics, slices, a package manager, a stdlib, an LSP, a formatter, and DWARF.
  - Later, ARM64 and GPU backends.
- **D. Instant and seamless.**
  - Fast boot.
  - Reproducible, declarative system configuration.
  - A sandbox per app.
- **E. Auto-parallelism.** SIMD, parallel-for, and a multicore runtime.
- **F. Compatibility.**
  - A Linux syscall ABI layer that runs static Linux ELF binaries.
  - C and Python FFI.
  - Real hardware drivers: NVMe, AHCI, USB/xHCI, e1000/virtio-net, GOP.
  - Our own assembler and linker.

**Invariants for every phase:**

- `./build.sh` prints FIXPOINT OK.
- `./run_tests.sh` and `FORGEC_FLAGS=-O0 ./run_tests.sh` both report 0 FAIL. This includes the QEMU boot tests (kernel-wx, kernel-bios, kernel-uefi, efi-loader-gpt).
- Dual-boot rules hold:
  - install only into `\EFI\Forge\`;
  - GPT only, never taking over a whole disk;
  - the kernel only reads the disk until Phase 6 adds a separate Forge data partition, created only with explicit opt-in.
- Each deliverable is committed as soon as its test passes.

A phase is done when every listed test passes in CI (`run_tests.sh`) on both -O2 and -O0.

---

## Phase 1 — Interactive multi-process kernel (in progress)
*Pillars: C, D, F. Closes SYSTEMS.md items 3–5.*

| Deliverable | Test that proves it |
|---|---|
| FPU/SSE/AVX state saved with xsave/fxsave on every context switch **(done, b14b23e)** | User programs fill xmm0–15, yield 20 times across 4 CPUs, and verify every register |
| SMEP + SMAP, with copy_user/copy_str_user and #PF fixups **(done)** | Probe syscall: a kernel read without `stac` is blocked and a kernel call into user code is blocked; bad pointers return EFAULT |
| TLB shootdown IPIs; frames reused only after the shootdown **(done)** | Pinned readers on all CPUs see the new frame after a global page is remapped |
| Wait queues, sleeping mutexes, condvars, `sleep_ticks` | Mutex-protected counter that yields inside the critical section; a producer/consumer ring; no lost wakeups over 10k iterations |
| Proc objects: fork (eager copy), exec, wait, exit, brk, anonymous mmap/munmap, getargs | `forktest`: parent and child diverge memory, the child execs `hello`, and the parent's wait returns its code. A brk/mmap stress test frees every frame (leak = 0) |
| VFS with fd table: open/read/close/stat/getdents; FAT32 long names (LFN) | `cat` reads a long-named file under `\EFI\Forge\bin\`; stat sizes match the host-side `mdir` |
| virtio-blk with MSI-X interrupts; requests sleep instead of polling | The interrupt count equals the request count, and the CPU idles while I/O is in flight |
| PS/2 keyboard (IRQ1) and COM1 RX (IRQ4) feeding one tty input queue | The harness types `ls /efi/forge/bin` and `hello` through QEMU `sendkey`, and the output appears |
| User-mode Forge shell `sh` loaded from disk: `ls`, run a program (fork+exec+wait), `exit` | Boot test reaches `sh$`, lists the programs, runs `hello`, gets its exit code, and then reaches KERNEL OK |

## Phase 2 — Machine-readable compiler and test tooling
*Pillars: B, C.*
1. `forgec --diagnostics=json` emits file/line/col, a code, a message, and fix-it spans for every error. Test: a golden JSON file for each error class in tests/diag.
2. `forgec --emit=ast-json` produces a stable, versioned AST schema. Test: a round-trip, AST → pretty-printer → AST, is identical for the whole selfhost compiler.
3. Grammar freeze:
   - an EBNF in docs/GRAMMAR.md;
   - a conformance suite that parses every example;
   - a test that fails if the parser accepts anything outside the grammar (fuzzed with a grammar-based generator).
4. `forge fmt` is a canonical formatter. Test: it is idempotent on all sources, and the repo is kept formatted in CI.
5. `forge test` runs inline `test "name":` blocks and gives a JSON report. `forge verify` runs the Phase 3 checkers as a gate.

## Phase 3 — Memory-safe Forge by default
*Pillar: A. Closes SYSTEMS.md item 6.*
1. **Slices** `[]T` carry (ptr, len), with bounds checks everywhere. A range-analysis pass elides checks proven safe. Tests:
   - out-of-bounds code traps with a precise diagnostic;
   - the bench suite is ≤ 5% slower with checks on;
   - the elision count is reported.
2. **No null:** `ptr[T]` is non-null, and `?ptr[T]` must be checked before use. Test: compile-fail cases, and the kernel compiles with zero unchecked nullables.
3. **`unsafe:` blocks:** raw `vload/vstore`, int→pointer casts, inline asm, and MMIO are allowed only inside them. A `forge verify --unsafe-report` lists every block. Test: the counts are tracked in CI, and the kernel's unsafe surface shrinks over time.
4. **Ownership and borrowing** (affine `own T`, `&T` and `&mut T` scoped borrows, regions for arenas and kernel per-request memory). Tests: compile-fail tests for use-after-free, double free, and aliasing a `&mut`. The user programs and shell compile in safe mode.
5. **Generics** (monomorphized) and `defer`. Stdlib containers become generic: `Vec[T]`, `Map[K,V]`.
6. **Weaker atomics orderings** (acquire/release).

## Phase 4 — Capability OS and sandboxing
*Pillars: A, D.*
1. **Handles replace ambient authority.** Every fd/proc/memory object is a capability with rights bits. Syscalls check rights, `dup` can only narrow them, and capabilities pass over IPC channels. Test: a child given a read-only handle cannot write; no global path lookup is possible without a directory capability.
2. **IPC channels** carry messages plus handles, with wait queues. Test: ping-pong latency is measured, and a handle is transferred.
3. **Per-app sandboxes:** a manifest declares the capabilities an app gets (dirs, devices, network). The shell launches apps through the manifest. Test: an app that reaches outside its manifest is denied and logged.
4. **Kernel hardening:** KASLR, stack canaries, guard pages per thread, and W^X for user ELF (already enforced for the kernel).

## Phase 5 — Own toolchain
*Pillars: C, F. Closes SYSTEMS.md item 7.*
1. Built-in x86-64 assembler. Test: it is byte-identical to GNU `as` for all compiler output and the kernel asm blocks.
2. ELF64 and PE32+ writer and linker, plus linker-script equivalents. Test: the kernel, the EFI loader, and user programs build with no `as`, `ld`, or `objcopy`, and all boot tests pass.
3. DWARF line tables. Test: gdb on the QEMU gdbstub breaks at `schedule` and shows Forge source lines.
4. Package manager: `forge.toml`, a content-hashed lockfile, and a local registry directory. Test: a reproducible build gives the same hash twice.
5. LSP server (diagnostics, goto-definition, hover) built on the JSON AST. Test: scripted LSP sessions.

## Phase 6 — Real hardware and storage
*Pillar: F. Closes SYSTEMS.md items 1, 2, and 5.*

**Kernel infrastructure:**
- buddy allocator
- vmalloc
- PCID
- x2APIC
- HPET/TSC-deadline
- per-CPU run queues and load balancing

**Drivers:**

| Driver | Test |
|---|---|
| NVMe | Read-only in QEMU `-device nvme` |
| AHCI | QEMU `-device ahci` |
| xHCI with a USB keyboard and mass storage | QEMU `qemu-xhci` |
| virtio-net, then e1000 | ARP/ICMP, then a UDP echo through QEMU user networking |
| GOP mode setting and a double-buffered console | — |

Each driver gets its own boot test, and none of them writes to disks it does not own.

**Writable storage:** an ext2- or log-structured Forge FS, only on a Forge-owned GPT partition that is created explicitly. Test: crash-consistency fuzzing in QEMU (kill the VM at random points, then fsck).

## Phase 7 — Linux compatibility and FFI
*Pillar: F.*
1. Linux syscall ABI personality for static ELF binaries:
   - read, write, openat, close, fstat, mmap, brk, exit_group, clone (threads), futex, getdents64, ioctl (tty subset);
   - auxv and argv.

   Test: static musl `busybox` runs `ls`, `cat`, and `sh` in a sandbox.
2. C FFI with the SysV ABI (`extern "C"`, struct layout). Test: link against a C object file and call both ways.
3. Python FFI: embed or extend CPython (hosted mode). Test: call a Forge function from Python and back.

## Phase 8 — Parallelism and performance
*Pillar: E. Also the compiler perf list.*
1. Register allocator and IR backend for fs/kernel mode (today only hosted code uses the IR backend).
2. Auto-vectorization of simple loops (SSE2/AVX2, now that the kernel saves xsave state), plus explicit `simd[T, N]` types.
3. `parallel for` over ranges and slices, with a work-stealing multicore runtime in both hosted and kernel modes. Test: speedup on 4 cores is ≥ 3× on bench kernels, with deterministic results.
4. Perf targets:
   - self-compile under 1 s;
   - bench parity with gcc -O2 on the bench suite (tracked in bench/).

## Phase 9 — Instant and declarative system
*Pillar: D.*
1. Fast boot: a parallel device init and boot-time budget tracked by the boot test (firmware handoff to shell ≤ 300 ms in QEMU).
2. Declarative system config (`system.forge`: users, apps, capabilities, services). It builds an immutable, content-addressed image with atomic A/B switch and rollback, still installed only under `\EFI\Forge\`. Test: rebuilding the same config gives the same hash, and a rollback boots the previous image.
3. Secure Boot: shim or MOK signing (SYSTEMS.md item 9).

## Phase 10 — New targets and the intent layer
*Pillars: B, C.*
1. AArch64 backend plus UEFI on QEMU `virt` (PL011, GIC, generic timer), running the same boot-test suite (SYSTEMS.md item 8).
2. GPU backend (SPIR-V compute from `parallel for` kernels). Test: a vector-add and a matmul match the CPU results.
3. OS intent/command layer: a typed command schema over the capability system, so an agent can drive the OS through structured, permission-checked requests. Every action goes through capabilities, is logged, and can be reversed where possible.

---

**Execution order:** 1 → 2 → 3 → 4 → 5 → 6 → 7 → 8 → 9 → 10.

Phase 2 is cheap and makes everything after it easier to test, so it comes before the language safety work. The safety work (Phase 3) has to land before capabilities (Phase 4), so new kernel code is written safe-first. Performance items from Phase 8 can be pulled forward whenever a phase leaves slack.

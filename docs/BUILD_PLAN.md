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
  - the kernel only reads the disk until Phase 8 adds a separate Forge data partition, created only with explicit opt-in.
- Each deliverable is committed as soon as its test passes.

A phase is done when every listed test passes in CI (`run_tests.sh`) on both -O2 and -O0.

---

## Phase 1 — Interactive multi-process kernel (done)
*Pillars: C, D, F. Closes SYSTEMS.md items 3–5.*

| Deliverable | Test that proves it |
|---|---|
| FPU/SSE/AVX state saved with xsave/fxsave on every context switch **(done, b14b23e)** | User programs fill xmm0–15, yield 20 times across 4 CPUs, and verify every register |
| SMEP + SMAP, with copy_user/copy_str_user and #PF fixups **(done)** | Probe syscall: a kernel read without `stac` is blocked and a kernel call into user code is blocked; bad pointers return EFAULT |
| TLB shootdown IPIs; frames reused only after the shootdown **(done)** | Pinned readers on all CPUs see the new frame after a global page is remapped |
| Wait queues, sleeping mutexes, condvars, `sleep_ticks` **(done, 2ba9709)** | Mutex-protected counter that yields inside the critical section; a producer/consumer ring; no lost wakeups over 10k iterations |
| Proc objects: fork (eager copy), exec, wait, exit, brk, anonymous mmap/munmap, getargs **(done, f1d151f)** | `forktest`: parent and child diverge memory, the child execs `hello`, and the parent's wait returns its code. A brk/mmap stress test frees every frame (leak = 0) |
| VFS with fd table: open/read/close/stat/getdents; FAT32 long names (LFN) **(done, f1d151f)** | `cat` reads `\EFI\Forge\docs\Long File Name Notes.txt` (path with spaces); the read byte count matches `stat`; `ls /EFI/Forge` shows case-preserved names |
| virtio-blk with MSI-X interrupts; requests sleep instead of polling **(done)** | Boot test: every request after MSI-X setup completes by interrupt (211 requests = 211 completions = 211 sleeps, 0 polled) |
| PS/2 keyboard (IRQ1) and COM1 RX (IRQ4) feeding one tty input queue **(done, f1d151f)** | The harness types `ls`, `forktest`, `nosuch` and `ls /EFI/Forge` (shifted keys) through QEMU `sendkey`, and `cat …`, `hello a b` and `exit` over COM1 |
| User-mode Forge shell `sh` loaded from disk: `ls`, run a program (fork+exec+wait), `exit` **(done, f1d151f)** | Boot test reaches `sh$`, lists the programs, runs `hello`, gets its exit code, and then reaches KERNEL OK |

## Phase 2 — Never reboot: self-healing, live-updatable system (signature feature 1)
*This phase needs Phase 1's processes, services, and wait queues. Each sub-step is finished and tested before the next one starts.*
1. **Kernel module ABI.** Relocatable Forge modules (ELF ET_REL) are loaded and unloaded at runtime. They get a versioned symbol table and an explicit `init/exit/save_state/restore_state` contract. Test: load, call, and unload a module 1000 times with no leaks.
   - **Status: done.** `examples/kernel/module.fsi`; test modules in `examples/modules/` (forgec + `as` → `.ko`, shipped in `\EFI\Forge\mod\`). KABI 1.0: exports carry a since-minor; wrong major, newer minor, imports newer than the module's ABI, unresolved symbols and truncated images are rejected. Relocations R_X86_64_64/PC32/PLT32/32S with range checks; sections mapped W^X in 1 MiB slots at `0xFFFFFFFFD0000000`. Boot test: 1000 load/call/unload cycles, 0 frames and 0 heap bytes leaked, save/restore state across reload, 4 bad modules rejected.
2. **Drivers as isolated services.** Each driver (virtio-blk, tty/keyboard, later the network) runs as a user-mode or isolated service process. It reaches its device only through granted MMIO/IRQ/DMA capabilities and talks to clients over IPC. Test: injecting a crash into the driver service leaves clients alive.
   - **Status: done (virtio-blk).** `examples/kernel/drv.fsi` + `examples/user/blkd.forge`. The ring-3 `blkd` gets capability syscalls 400–407: port I/O checked against the device's 64-byte I/O window, MSI-X interrupt wait, one contiguous DMA region mapped into it, recv/reply IPC. `vb_read` (so FAT and all file I/O) is routed to it after a handover under `vb_m`. On driver death the kernel resets the device before the DMA frames are freed and fails the in-flight request with EIO. Boot test: init.elf read through blkd is byte-identical; a non-driver caller gets EPERM; an injected NULL write with a request in flight on the device kills blkd (#PF, exit 0x10e), the client gets EIO and continues, the kernel driver takes the device back, 0 frames / 0 heap bytes leaked. Gaps: no IOMMU (DMA not confined), one request in flight, tty/keyboard still in-kernel.
3. **Supervisor with automatic restart.** Death is detected, the service restarts with its saved state, and in-flight requests are replayed idempotently. Test: the boot test kills the block driver in the middle of `cat`, and the output is still byte-identical; the restart count is reported.
   - *Status: done.* For the whole shell session `blkd` runs under a supervisor kernel thread (`drv.fsi`). The boot script runs `crash 40` (syscall 408, a fault-injection hook) and then `sum` on the 96 KiB `\EFI\Forge\docs\big.bin`. blkd dies with a request in flight. The supervisor reaps it, respawns it from the cached image with its restart generation as an argument, and the kernel-held request journal replays the read. Replies are matched by sequence number, so a stale reply cannot complete a newer request. The FNV-1a hash of the file matches the one computed on the host, and the test reports "blkd restarts 1 … replayed 1". After 5 restarts the supervisor gives up and fails requests with EIO. The session leaks 0 frames and 0 heap. Gaps: the test file is read with `sum` (which hashes it) rather than `cat`, so the comparison is by hash; the device is the only driver state, and it is rebuilt on restart (there is no state blob to save); there is one request slot; writes are not replayed, since only reads exist.
4. **Hot-swap with state transfer.** Replace a running driver or module with v2. The old version quiesces, serialises its typed state, the new version restores it, and clients see only latency. Test: swap the tty driver while the shell is reading, and no keystroke is lost.
   - *Status: done.* The tty line discipline goes through three function pointers: push, lines and pop. v0 is built into the kernel, and `tty1.ko` (a 512-byte head+count ring) and `tty2.ko` (a 1 KiB ring with free-running indices, which recounts lines on restore) are interchangeable modules that share one state format. During the shell session a swapper thread cycles v1↔v2 every 2 ticks, and at the end it swaps back to v0. Each swap loads the new module, then, under rq_lock, saves the old state, restores it into the new module and switches the pointers. All tty calls run under rq_lock, so no CPU can still be in the old code when it is unloaded. If a restore fails, the swap is aborted. Boot test: about 120 swaps, about 40 of them with a partial line queued; pushed == consumed + erased, lost 0, dropped 0; no leaks. Gaps: the swap is triggered by a timer rather than by an operator command; the echo path is not part of the module. The fault-injection syscall 408 now returns EPERM unless the kernel's boot self-test has armed it for its shell session, and drv_test checks this.
5. **Live kernel patching.** Function-level patches are applied with stop-machine via IPIs. Each patched function gets a jump at its entry, verified by its hash. Rollback is supported. Test: patch `sys_getpid` while 4 CPUs hammer it; results switch atomically and rollback restores them.
6. **kexec-style handoff (fallback).** Boot a new kernel image while keeping user state: process memory, fds, and the console are serialised into a handoff region the new kernel adopts. Dual-boot rules still apply, and nothing is written to disk. Test: the shell survives a full kernel replacement and the next command works.

## Phase 3 — Machine-readable compiler and test tooling
*Pillars: B, C.*
1. `forgec --diagnostics=json` emits file/line/col, a code, a message, and fix-it spans for every error. Test: a golden JSON file for each error class in tests/diag.
2. `forgec --emit=ast-json` produces a stable, versioned AST schema. Test: a round-trip, AST → pretty-printer → AST, is identical for the whole selfhost compiler.
3. Grammar freeze:
   - an EBNF in docs/GRAMMAR.md;
   - a conformance suite that parses every example;
   - a test that fails if the parser accepts anything outside the grammar (fuzzed with a grammar-based generator).
4. `forge fmt` is a canonical formatter. Test: it is idempotent on all sources, and the repo is kept formatted in CI.
5. `forge test` runs inline `test "name":` blocks and gives a JSON report. `forge verify` runs the Phase 4 checkers as a gate.

## Phase 4 — Memory-safe Forge by default
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

## Phase 5 — Capability OS and sandboxing
*Pillars: A, D.*
1. **Handles replace ambient authority.** Every fd/proc/memory object is a capability with rights bits. Syscalls check rights, `dup` can only narrow them, and capabilities pass over IPC channels. Test: a child given a read-only handle cannot write; no global path lookup is possible without a directory capability.
2. **IPC channels** carry messages plus handles, with wait queues. Test: ping-pong latency is measured, and a handle is transferred.
3. **Per-app sandboxes:** a manifest declares the capabilities an app gets (dirs, devices, network). The shell launches apps through the manifest. Test: an app that reaches outside its manifest is denied and logged.
4. **Kernel hardening:** KASLR, stack canaries, guard pages per thread, and W^X for user ELF (already enforced for the kernel).

## Phase 6 — Proven-safe programs only (signature feature 2)
- The loader accepts only images produced by the Forge verifier: a signed proof manifest stating the safety level (no unchecked unsafe, or an audited unsafe list) and the declared capabilities.
- The kernel grants exactly the declared capabilities and nothing else.
- Test: an unverified or tampered ELF is refused. A verified program that tries an undeclared capability is denied at the syscall boundary.
- The Linux ABI layer (Phase 11) runs only inside an explicit compatibility sandbox.

## Phase 7 — Own toolchain
*Pillars: C, F. Closes SYSTEMS.md item 7.*
1. Built-in x86-64 assembler. Test: it is byte-identical to GNU `as` for all compiler output and the kernel asm blocks.
2. ELF64 and PE32+ writer and linker, plus linker-script equivalents. Test: the kernel, the EFI loader, and user programs build with no `as`, `ld`, or `objcopy`, and all boot tests pass.
3. DWARF line tables. Test: gdb on the QEMU gdbstub breaks at `schedule` and shows Forge source lines.
4. Package manager: `forge.toml`, a content-hashed lockfile, and a local registry directory. Test: a reproducible build gives the same hash twice.
5. LSP server (diagnostics, goto-definition, hover) built on the JSON AST. Test: scripted LSP sessions.

## Phase 8 — Real hardware and storage
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

## Phase 9 — Living object store replacing files (signature feature 3)
- Typed objects (schema in Forge types), with every write versioned (copy-on-write, content-addressed), plus indexed search by type, field, and full text.
- `rewind` works for any object or the whole system to a timestamp.
- The POSIX-like VFS becomes a view onto the store.
- Tests: write/rewind/diff round-trips; system rewind after a bad change in QEMU; search latency on 100k objects; crash consistency.

## Phase 10 — Private by design (signature feature 4)
- Per-user keys (TPM-sealed when available), with every object and swap page encrypted at rest.
- Per-app access grants are visible in one ledger and revocable live, and revocation takes effect on open handles.
- Tests: the disk image contains no plaintext; revoking access cuts an app off mid-read; an audit log of every grant and use is kept.

## Phase 11 — Linux compatibility and FFI
*Pillar: F.*
1. Linux syscall ABI personality for static ELF binaries:
   - read, write, openat, close, fstat, mmap, brk, exit_group, clone (threads), futex, getdents64, ioctl (tty subset);
   - auxv and argv.

   Test: static musl `busybox` runs `ls`, `cat`, and `sh` in a sandbox.
2. C FFI with the SysV ABI (`extern "C"`, struct layout). Test: link against a C object file and call both ways.
3. Python FFI: embed or extend CPython (hosted mode). Test: call a Forge function from Python and back.

## Phase 12 — Parallelism and performance
*Pillar: E. Also the compiler perf list.*
1. Register allocator and IR backend for fs/kernel mode (today only hosted code uses the IR backend).
2. Auto-vectorization of simple loops (SSE2/AVX2, now that the kernel saves xsave state), plus explicit `simd[T, N]` types.
3. `parallel for` over ranges and slices, with a work-stealing multicore runtime in both hosted and kernel modes. Test: speedup on 4 cores is ≥ 3× on bench kernels, with deterministic results.
4. Perf targets:
   - self-compile under 1 s;
   - bench parity with gcc -O2 on the bench suite (tracked in bench/).

## Phase 13 — Instant and declarative system
*Pillar: D.*
1. Fast boot: a parallel device init and boot-time budget tracked by the boot test (firmware handoff to shell ≤ 300 ms in QEMU).
2. Declarative system config (`system.forge`: users, apps, capabilities, services). It builds an immutable, content-addressed image with atomic A/B switch and rollback, still installed only under `\EFI\Forge\`. Test: rebuilding the same config gives the same hash, and a rollback boots the previous image.
3. Secure Boot: shim or MOK signing (SYSTEMS.md item 9).

## Phase 14 — One computer across devices (signature feature 5)
*Needs Phase 8 networking and Phase 9 objects.*
- Authenticated, encrypted device mesh.
- Pooled object store with replication.
- Live process migration (serialise address space, fds, and capabilities, then resume on the other node).
- Test: two QEMU nodes; a running program migrates mid-computation and finishes with the same result; storage stays readable when either node is offline.

## Phase 15 — Intent layer with local AI (signature feature 6)
- A local model runs on the device (CPU SIMD, later the GPU backend). It turns intents into typed, capability-checked command plans over the object store and services.
- The user sees and approves plans, and every action can be reversed through rewind.
- Tests: an intent suite with expected plans, permission-denial cases, and an offline-only check (no network capability).

## Phase 16 — New targets (AArch64, GPU)
*Pillars: B, C.*
1. AArch64 backend plus UEFI on QEMU `virt` (PL011, GIC, generic timer), running the same boot-test suite (SYSTEMS.md item 8).
2. GPU backend (SPIR-V compute from `parallel for` kernels). Test: a vector-add and a matmul match the CPU results.
3. (The intent layer itself is Phase 15.)

---

**Execution order:** strictly one phase at a time: 1 → 2 (never reboot) → 3 → 4 → 5 → 6 (proven-safe programs) → 7 → 8 → 9 (object store) → 10 (private by design) → 11 → 12 → 13 → 14 (one computer) → 15 (intent + local AI) → 16.

Each phase is finished, tested, and committed before the next one starts. The signature features (phases 2, 6, 9, 10, 14, 15) sit right after the phases they depend on. Never-reboot comes first because restart-free updates build directly on Phase 1's processes and services. Performance items from Phase 12 may be pulled forward when a phase leaves slack.

# Kernel core interface spec (scheduler, IPC, capabilities)

Status: **spec v1** (plan v1.3 item 8). This document states the interface the rest of the kernel and
user programs may rely on for the three core services, as implemented today in
`examples/kernel/sched.fsi`, `chan.fsi`, `proc.fsi` (handle rights) and `seal.fsi` (syscall
capabilities). It is a description of the contract, not new code: nothing in the kernel changed with it.
Anything not written here is an implementation detail and may change without notice. A change to
anything written here bumps the spec version and must update this file in the same commit, with tests.
Appendix A records the verification scope for this contract and for the kernel properties the boot
tests pin, including the known DMA/IOMMU gaps. It does not change the interface in §§0–5.

Related formats that are specified elsewhere and referenced, not repeated: process-state v1 and the
kexec handoff (SYSTEMS.md "Process-state format", `pstate.fsi`), the nondeterminism hook
(SYSTEMS.md, `nondet.fsi`), the seal / proof manifest (SYSTEMS.md "Proof manifests", "Seal v3").

## 0. Conventions
- Errors are returned as negative values in a u64 (`EPERM`, `EBADF`, `EFAULT`, `EINVAL`, `EMFILE`,
  `EACCES`, `EPIPE` = -32). Argument and permission errors are reported before any side effect.
- Locking: one global spinlock `rq_lock` guards every run queue, wait queue, mutex, condvar and
  channel. Functions ending in `_l` require `rq_lock` held **and interrupts off**; all others take and
  release it themselves and may be called with interrupts on. Lock order: `rq_lock` is innermost; no
  other lock may be taken while holding it.
- "Blocks" means the caller may be descheduled; it must not hold `rq_lock` (non-`_l` API) or any
  spinlock other than `rq_lock` (the `_l` API, which releases it while parked).

## 1. Scheduler (`sched.fsi`)
### Model
- Kernel threads (`Thread`), preemptive, round-robin on the 100 Hz LAPIC timer; one run queue per
  CPU (up to 8) with stealing; a per-CPU idle thread. A user process runs on one or more threads; the
  thread carries the address space (`cr3`), FPU area, user FS base and the syscall in progress.
- States: `T_READY` 0, `T_RUNNING` 1, `T_DEAD` 2, `T_IDLE` 3, `T_BLOCKED` 4.
  Transitions: new -> READY (`thread_start_ready`); READY -> RUNNING (picked); RUNNING -> READY
  (preempted / yield); RUNNING -> BLOCKED (`wq_block_l`); BLOCKED -> READY (`wake_l`);
  RUNNING -> DEAD (`thread_exit`). No other transition is legal.
- Every pick passes the nondeterminism hook (`nd_hook(ND_SCHED, cpu, thread)`).
- Each thread has a 16 KiB kernel stack with a guard page; overflowing it ends the thread
  (`thread_overflow_exit`), not the kernel.

### Calls
| call | contract |
|---|---|
| `thread_spawn(fn, arg) -> ptr[Thread]` | create and make READY; `fn(arg)` runs, returning from it = `thread_exit` |
| `thread_new` / `thread_start_ready` | the same in two steps (set fields such as `pin`, `proc` in between) |
| `yield_cpu()` | give up the CPU; returns READY->RUNNING later |
| `thread_exit()` | never returns; the thread is reaped by `thread_reap` (its stack and FPU area freed) |
| `sleep_ticks(n)` | block for at least n timer ticks (10 ms each) |
| `schedule(exiting)` / `schedule_l` | internal: irqs off; `_l` returns with `rq_lock` released |

### Wait queues and sleeping locks
| call | contract |
|---|---|
| `wq_wait(q)` / `wq_block_l(q)` | park the caller on q; `_l` returns with `rq_lock` re-held |
| `wq_wake_one_l(q) -> 0/1`, `wq_wake_all[_l](q) -> n` | FIFO wake; woken threads become READY |
| `mutex_lock` / `mutex_unlock` | sleeping, non-recursive (recursive lock panics), owner-checked unlock (panics otherwise) |
| `cond_wait(c, m)` / `cond_signal` / `cond_broadcast` | release m and park atomically; m re-held on return; callers re-check their condition (wake-ups may be spurious) |

There is no priority and no fairness guarantee beyond FIFO wait queues and round-robin run queues.

## 2. Capabilities
Two independent layers; a call must pass both.

### 2a. Process capabilities (syscall classes, `seal.fsi`)
- Bits: `fs` 1, `spawn` 2, `ipc` 4, `drv` 8, `kexec` 16, `probe` 32, `ktest` 64, `grant` 128.
- Source: the sealed manifest of the executable (`# forge: caps(...)`). exec sets
  `caps = held & declared` (never more than the parent); fork copies; kexec hands them over in the
  process-state record (flags word).
- Check: `sys_need(nr)` at the top of `syscall_dispatch`, before anything else the syscall does.
  Missing bit -> `EPERM` and a `cap:` log line. Classes: fs = open, stat, getdents, openat;
  spawn = fork, exec, wait, exec_sandboxed; ipc = chan, send, recv; drv = 400-407, 411-418;
  ktest = 408; kexec = 409; probe = 200; grant = 428-430. All other native syscalls need none.
- Linux-personality processes never reach this table (they see only the Linux syscall table).

### 2b. Handle rights (per fd, `proc.fsi`)
- An fd is a handle: `Proc.fds[i]` (a `File`, refcounted) plus `Proc.fdr[i]` rights:
  `READ` 1, `WRITE` 2, `LOOKUP` 4, `DUP` 8, `XFER` 16 (`CAP_ALL` 31). 16 fds per process.
- **Rights only shrink.** `dup(fd, r)` needs DUP and `r ⊆ rights(fd)`; `restrict(fd, r)` narrows in
  place; `openat` gives the directory's rights minus WRITE; a handle received over a channel keeps the
  rights it was sent with. No call widens a right.
- Ambient authority is fd 3 only (the root directory with LOOKUP, `CAP_ROOT_FD`); a sandboxed
  process loses it at exec, a driver process never gets it.
- Grants (Phase 10.2): a handle installed by a manifest launch carries a grant id, inherited by every
  handle derived from it; once revoked every use returns `EACCES` at the next syscall.

## 3. IPC channels (`chan.fsi`)
- A channel is two endpoint handles (File type `F_CHAN`), created together with READ|WRITE|DUP|XFER.
  At most 16 channels exist at once.
- Message: 1..64 bytes plus at most one handle. Each direction is a 16-slot FIFO ring; order is
  preserved per direction, nothing is reordered or merged.
- Syscalls (class `ipc`):
  - 423 `chan(u64[2]* fds) -> 0`: `EFAULT`, `EMFILE` (needs two free fds).
  - 424 `send(fd, buf, n, h) -> 0`: needs WRITE on fd; n in 1..64 else `EINVAL`; h is `NO_HANDLE`
    (~0) or an fd with XFER that is not fd itself; the handle is **moved** (gone from the sender
    when send succeeds); blocks while the ring is full; `EPIPE` if the peer is closed.
  - 425 `recv(fd, buf, n, &h) -> length`: needs READ; blocks while empty; returns 0 once the peer is
    closed and the ring is drained; `h` gets the received fd or `NO_HANDLE`. A free fd is reserved
    and both destinations are written once **before** dequeueing, so `EMFILE` (no free fd) and
    `EFAULT` (bad buffer) leave the message queued. Bytes beyond n are discarded (no partial reads).
- Close: when an endpoint's last reference goes, the peer's blocked senders/receivers wake; when
  both ends are gone, queued handles are released and the slot freed.
- kexec is refused while any channel is open (`chan_live != 0`); channels are not part of
  process-state v1.

## 4. Guarantees the rest of the system may rely on
1. No syscall widens a handle right or a process capability.
2. Capability checks happen before any side effect of a syscall.
3. A blocked thread is on exactly one wait queue; a READY thread on exactly one run queue.
4. Moved handles exist in exactly one place (sender table, channel slot, or receiver table).
5. Scheduler choices, timer ticks, syscall results and random values all pass `nd_hook`.

## 5. Not yet specified (future items)
Priorities / real-time scheduling, channel select/poll across endpoints, capability-typed effects
(`uses` clauses, reserved: GRAMMAR v1.2 / E0106), and channels in the kexec handoff.

Tests that pin this contract: `examples/user/captest.forge`, `chantest.forge`, the kernel sync /
sched / TLB self-tests and the grant and sandbox checks in `tests/os/run.sh`.

## Appendix A. Verification scope

This appendix is a statement of what the executable tests actually check, what they do not, and
under which assumptions. "Verified" here means a named test in `run_tests.sh` (including
`tests/os/run.sh`) has passed on the CI configuration, at both -O2 and -O0 unless a note says
otherwise. It is not a machine-checked proof, and it is not a claim about hardware or firmware
outside that configuration. The interface in §§0–5 is unchanged.

### A.1 What is verified, and by which tests

Markers in this section are exact serial (or framebuffer) substrings required by `tests/os/run.sh`
or a kernel self-test. A result name is one of the 35 entries in `tests/os/expected-results.txt`
(`os/result-count` fails if any is missing). The framebuffer check on the BIOS, UEFI, EFI-loader
and IOMMU boots requires `back == front` and a row that decodes as `KERNEL OK`.

**Boot and memory.** `os/kernel-bios`, `os/kernel-bios-smp1`, `os/kernel-uefi` and
`os/efi-loader-gpt` each reach `KERNEL OK` through GRUB BIOS, GRUB UEFI, or the Forge UEFI loader.
After `KERNEL OK` the kernel prints `debug-exit N ms after KERNEL OK` (TSC-bounded 5 s wait for the
harness screenshot); `os/efi-loader-gpt-post-ok` requires N < 8000, so a 60 s stall after `KERNEL OK`
fails the suite.
`os/kernel-wx` requires that `kernel.elf` has no RWX `PT_LOAD`. Identity mapping of low RAM is
dropped (`identity 0x0000000000100000 -> unmapped`). Image and heap/stack KASLR are pinned by
`os/kaslr` (`KASLR: big heap at`, `KASLR: kernel image at`, `link-address alias unmapped`,
`unslid .text` unmapped, loader and kexec relocatable counts). The buddy allocator, vmalloc reuse
and demand paging print `buddy allocator OK`, `vmalloc VA reuse OK` and `demand paging: 3 faults`.
SMEP/SMAP and `copy_user` are checked by the uaccess self-test (`SMAP blocks kernel read`,
`SMEP blocks kernel exec`, `uaccess:`) and by `examples/user/badptr.forge` (every pointer-taking
syscall, four bad addresses, all `EFAULT`).

**Scheduler, IPC and capabilities (§§1–4).** The legal thread states, wait queues, mutexes and
condvars are exercised by the kernel sync self-test (`wait queues + sleeping mutexes + condvars OK`,
`6 threads x 2000 increments = 12000`). Preemption and stealing: `preemptive SMP scheduler OK`,
`per-CPU run queues:`, `SMP: 4 cpus online` (and `SMP: 1 cpus online` with zero IPIs on
`os/kernel-bios-smp1`). Overflow into a kernel stack guard and a smashed canary kill the thread,
not the kernel (`kernel stack guard pages:`, `stack canaries:`). Stop-machine rendezvous:
`stop_machine stress OK: 3000 rounds`. Handle rights that only shrink, and the syscall-class
gate, are pinned by `examples/user/captest.forge` (`captest: PASS`) and `capdeny` (`capability
gate: undeclared spawn and probe denied at the syscall boundary`). Channel move, XFER, EOF on
peer close, and `EFAULT` leaving a message queued are pinned by `chantest.forge` (`chantest: PASS`)
and `badptr`. Grants and revocation: `sbtest: PASS`, `grtest:` mid-read cut-off, `audit: ledger
check`. W^X in user space: `wxtest: PASS`. Sealed images: boot lines `verify: /efi/forge/bin/unsealed
refused: no proof manifest`, `tampered` / `forged` / `linuxabi` refusals; `os/seal-private-key-not-shipped`;
`tests/ed25519/run.sh` and `tests/forgeseal/run.sh`.

**Drivers, isolation and VT-d.** `PCI + virtio-blk + FAT32 (read-only) OK` and a byte-identical
`init.elf` read through `blkd` (`blkd: read`, `through the user-mode driver, identical`) plus
`isolated driver service OK` and `supervised driver restart OK` pin the Phase 2.2 virtio-blk
service. `os/kernel-iommu` boots q35 with `-device intel-iommu,intremap=off` and a modern
virtio-blk (`disable-legacy=on,iommu_platform=on`) and requires `VT-d: 1 remapping unit`,
`haw `, `bits, flags 0x`, `base 0x`, `translation on`, `drv 403 returns IOVA`,
`slpt maps only the grant`, and `KERNEL OK`. Default deny and table coverage (A.2 gaps 1, 3, 6, closed): `translation on, 1 of 1 remapping unit programmed (default deny`, `VT-d: default deny: `, `scanned device(s) and every unscanned requester ID blocked`, `context audit: 65536 requester IDs, `, `0 stray, 0 missing`, and the negative case `context audit catches a stray pass-through entry`. Observed faults and aliasing (A.2 gaps 4, 5, closed): the same boot adds QEMU's `edu` device behind a root port and a PCIe-to-PCI bridge; `(alias via PCIe-to-PCI bridge `, `denied edu DMA faulted: source ` (an unclaimed device's DMA is recorded by the hardware model with the alias as source), `requester-ID context(s): 64 bytes into edu and back`, `identical, 0 faults` (DMA through the alias context arrives intact), and `out-of-grant edu DMA faulted at IOVA 0x0000000000810000: source ` (DMA outside the mapping faults at that address). DMA policy (A.2 gap 7, closed): `VT-d policy: remapping active (deny by default, grants confined)` on `os/kernel-iommu`; `VT-d policy: no remapping unit, DMA unconfined (permissive default` and the negative case `strict mode refuses drv 402 without a remapping unit: EPERM` on `os/kernel-bios`, `os/kernel-uefi` and `os/efi-loader-gpt`. The IOVA in that report is not the host PA
(`iommu_grant_ok` refuses an identity grant). Without a remapping unit the existing BIOS/UEFI
boots still reach `KERNEL OK` and 403 still returns a host PA. Input and NVMe services,
Forge FS and the object store have their own result names (`os/drivers`, `os/forgefs-*`,
`os/objstore-*`, `os/objfs-*`, `os/nvme-service-swap-restart-boot1` / `boot2`).

**Kexec and process-state.** `kexec handoff OK`, `kexec: handoff format v5`, `process-state: format
v1 (adopts v1..v1), header checks OK`, `TLB IPI: 1 stale shootdown IPI(s) from the previous kernel
handled before SMP`, and `driver state carried across kexec OK`. `os/kexec-no-disk-write` requires
the GPT image to be byte-identical after the loader boot. The bios suite repeats the handoff
(`kexec: generation 3 kernel adopted`).

**Linux personality, modules, live patch, nondeterminism.** `lxtest: PASS`, `lxthreads: PASS`,
`lxfs: PASS`, `lxexec: PASS`, `lxsig: PASS`, and BusyBox ash (`hello from ash, forge`,
`script done: ok`) run only in the compat sandbox; a plain exec of a Linux image is refused.
Modules: `module ABI OK: 1002 loads, 1002 unloads, 7 rejected`. Live patch:
`live patching OK`. The nondeterminism hook: `nondet: recording off by default`, `nondet hook OK`.

**Unsafe surface.** `tests/unsafe/run.sh` (in `run_tests.sh`) compiles the allow/deny cases,
checks `forgec --provenance-lint`, and fails if any program's function/block/op/asm count in
`tests/unsafe/baseline.txt` grows. `forgec --unsafe-report` on `examples/kernel/kernel.forge`
is part of that baseline. Counts may shrink; they must not rise.

### A.2 What is not verified

The tests do not prove the kernel correct on every machine, under every interleaving, or against
a formal model of x86, ACPI, PCI or VT-d. In particular they do not cover: timing or
cache/side-channel behaviour; AArch64 or physical hardware; Secure Boot; multiple outstanding
virtio requests; packed virtqueues; interrupt remapping; DMA under translation from devices other
than the granted virtio-blk and the edu test device; or more than one remapping unit.
Commentary in SYSTEMS.md or BUILD_PLAN.md that is not pinned by a
named test or marker in A.1 is not a verification claim.

**Known DMA/IOMMU gaps** (these are deliberate current limits, not omitted test bugs):

1. **Closed: deny is the default.** The root table starts empty. Only a device the kernel drives
   itself (claimed through `pci_busmaster`, pass-through, domain 1) or a device with a DMA grant
   (translate-and-confine, domain 2) has a context entry; every other requester ID is blocked.
   Remaining limit: a kernel-driven device is trusted with host physical addresses (no per-device
   kernel DMA domains). Pinned in A.1.
2. **No interrupt remapping.** The `os/kernel-iommu` command line sets `intremap=off`. MSI-X
   still ends at the LAPIC through the ordinary path; IRTE programming and IRQ remapping faults
   are not implemented and are not tested.
3. **Closed: every DRHD is programmed.** All units the probe records (up to `IOMMU_MAX` = 4) get the
   shared root table, are invalidated together, and have translation enabled; the second-level
   width is one every unit supports. Remaining limit: the test topology has one unit (QEMU
   presents one), so the multi-unit path is not exercised by a boot, and device-scope entries are
   not parsed (sharing one root table is correct for any scope).
4. **Closed: DMAR faults are read and tested.** `iommu_fault_drain` reads FSTS and every
   fault-recording register, prints each fault (unit, source ID, reason, address) and clears it.
   The edu self-test injects DMA from a denied device and DMA outside a mapping and requires the
   matching fault records. Remaining limit: faults are polled by the self-test, not taken as an
   interrupt, and nothing polls them in normal operation.
5. **Closed: requester-ID aliasing.** `iommu_aliases` follows the bridges above a device (PCIe-to-PCI:
   secondary bus, devfn 0; conventional PCI bridge: the bridge's ID); claims and grants install a
   context entry for every alias. Pinned by the edu device behind a PCIe-to-PCI bridge. Remaining
   limit: quirky devices that use other requester IDs (DMA-alias quirks) are not modelled.
6. **Closed: context-table coverage is checked.** Context tables are allocated per bus on demand
   (up to 8 buses). `iommu_audit` walks all 65536 requester IDs and requires that exactly the
   claimed and granted ones are present, with the right translation type and domain, and that
   nothing else is (unused entries are not present, never pass-through). A planted stray entry
   must be caught. Pinned in A.1.
7. **Closed: written policy for a machine with no remapping unit.** If the DMAR table is missing,
   has no DRHD, or `iommu_enable` fails, translation stays off and the policy is permissive by
   default: drivers keep working with host physical addresses (drv 403 returns the host PA) and
   the boot log states that DMA is unconfined. The strict policy (`iommu_strict`) refuses drv 402
   with `EPERM` in that case. Remaining limit: strict is a build-time default (the kernel has no
   command line yet), and it gates user-mode driver grants only, not kernel-driven devices.

QEMU's transitional virtio-blk bypasses the IOMMU unless `VIRTIO_F_ACCESS_PLATFORM` is negotiated.
The IOMMU boot therefore uses a modern (non-transitional) device; a pass of `os/kernel-bios` does
not imply that device DMA was translated.

### A.3 Assumptions

- The host is QEMU running the command lines in `tests/os/run.sh` (256 MiB RAM; 4 VCPUs, or 1 on
  `os/kernel-bios-smp1`; SeaBIOS or OVMF; a read-only virtio-blk ESP, plus the extra disks only
  on the driver/Forge-FS/object-store tests).
- For A.1's VT-d claims: `-machine q35 -device intel-iommu,intremap=off` and
  `virtio-blk-pci,disable-legacy=on,iommu_platform=on`, plus `pcie-root-port` -> `pcie-pci-bridge` -> `edu`
  for the fault and aliasing claims. The firmware DMAR describes at least one
  DRHD whose register base the kernel can map, and the granted device is on bus 0 in that unit's
  scope.
- ACPI tables, PCI configuration, and the IOMMU's register interface are those QEMU presents.
  The tests do not attack the firmware or the emulator.
- The kernel under test is the one built by `examples/kernel/build.forge` / `examples/efi/build.forge`
  with the self-hosted compiler (build.sh `FIXPOINT OK`, `SEED OK`, `TOOLS OK`, `SEED TOOLS OK`).
- `ktest_hooks` (syscalls 408/409) is armed only for the boot self-test session. Recording in the
  nondeterminism hook is off by default.
- Claims are about the functional behaviour those tests observe (including "0 frames leaked" and
  "0 heap leaked" where a marker says so), not about worst-case latency, throughput, or
  resistance to a concurrent attacker with a second DMA-capable device.

### A.4 Verifier-ready conventions

These conventions are how a later verifier, or a human discharging a claim, should read this
spec and the tests. They fit on one page; they are not themselves a proof system.

1. **A claim is a sentence plus a pin.** The pin is a result name (`os/kernel-iommu`), a test
   program (`examples/user/captest.forge`), and/or an exact marker substring. Text without a pin
   is commentary.
2. **Markers are literal.** Matching is substring inclusion of the text in A.1, as
   `tests/os/run.sh` does. Do not normalise whitespace, case or punctuation when discharging a
   marker claim.
3. **Name the configuration** when the claim depends on hardware or QEMU flags (machine type,
   IOMMU, virtio mode, CPU count). A pass on `-machine pc` does not discharge a q35+VT-d claim.
4. **Negative claims need a rejected case.** "Refused", `EPERM`, `EACCES`, `EFAULT`, `EPIPE` and
   seal reason lines are the evidence. The absence of a crash is not evidence that a path is
   forbidden.
5. **Rights and capabilities only shrink**, and **argument/permission errors occur before any
   side effect** (§4). A verifier may treat a counterexample (a widened right, or a mutation
   before `EFAULT`) as a failed claim even if some other test still passes.
6. **Software table walks are not hardware observations.** `slpt maps only the grant` confirms
   the page table the kernel wrote. It does not, by itself, confirm that the IOMMU walked that
   table or that an out-of-grant transaction faulted. The edu fault markers (A.1, A.2.4) are the
   hardware-model observations.
7. **Default behaviour is stated, not inferred.** Under VT-d the default is deny (A.2.1): a
   requester ID with no claim and no grant has no context entry. Kernel-driven devices are
   pass-through, so do not read "DMA is confined" as applying to them; only a granted device is
   confined to its grant.
8. **The unsafe baseline is a budget, not a memory-safety proof.** `tests/unsafe/baseline.txt`
   and `--unsafe-report` bound the counted surface. They do not prove that every `@unsafe`
   function is correct, or that unmarked code is free of other undefined behaviour.
9. **Spec and tests change together.** A change to anything in §§0–5 bumps the spec version and
   updates this file and the pins in the same commit (page 1). A new kernel guarantee is not
   "verified" until it has a pin in A.1. Closing a gap in A.2 is a new pin, not a silent
   deletion of the gap.
10. **Stable identifiers.** Prefer the result name, the source path, and the marker text over
    line numbers or commit hashes. Line numbers move; markers and result names are the
    interface of the test suite.

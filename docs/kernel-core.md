# Kernel core interface spec (scheduler, IPC, capabilities)

Status: **spec v1** (plan v1.3 item 8). This document states the interface the rest of the kernel and
user programs may rely on for the three core services, as implemented today in
`examples/kernel/sched.fsi`, `chan.fsi`, `proc.fsi` (handle rights) and `seal.fsi` (syscall
capabilities). It is a description of the contract, not new code: nothing in the kernel changed with it.
Anything not written here is an implementation detail and may change without notice. A change to
anything written here bumps the spec version and must update this file in the same commit, with tests.

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

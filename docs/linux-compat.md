# Linux compatibility (Phase 11.1)

Forge can run static x86-64 Linux programs through a **Linux syscall personality**
(`examples/kernel/linux.fsi`). It is a second syscall table, not a second kernel: memory, the
console, scheduling and capabilities are Forge's, seen through Linux numbers and Linux structs.

## Who may run under it (the 6.4 gate)

A Linux image is one whose ELF header says EI_OSABI = 3. It runs only if **all** of these hold:

1. It is **sealed** (`tools/forgeseal.py`): the verifier's unsafe report matches the audited baseline
   and the build key signed it. Unsealed (reason 1) and tampered (reason 5) images are refused before
   the ABI byte is looked at. The seal covers the OSABI byte, so a Forge program cannot be re-tagged
   after sealing.
2. The Linux layer is up (`compat_sandbox_ready`, set by `lx_init` at boot).
3. It was launched **in the compat sandbox**: sh `run app`, i.e. syscall 426 (exec with sandbox),
   which verifies with `SEAL_USER | SEAL_COMPAT`. The process gets only the handles its
   `/efi/forge/apps/<app>.mf` manifest grants (Phase 5.3 / 10.2), plus its sealed capabilities.

Every other path still refuses it with reason 6 ("Linux ABI image needs the compatibility sandbox"):
plain `exec` from sh, the boot loader's init, the seal self-test (`linuxabi`). Today the only Linux
images on the ESP are built here from source (`lxtest`); no third-party binary is shipped.

Inside the personality a process reaches **only** the Linux table. No Forge syscall is reachable,
including the driver (400+), kexec/ktest (408/409, still also gated by `ktest_hooks`) and grant
syscalls: they return -ENOSYS like any unknown number.

## Syscalls (11.1a, 11.1b, 11.1c; 20 in all)

| nr | name | notes |
|----|------|-------|
| 0 | read | Forge read on the fd (grants and rights checked) |
| 1 | write | Forge write |
| 3 | close | |
| 5 | fstat | (11.1c) 144-byte struct stat: st_dev 0xF0, st_ino = first cluster + 1, st_mode S_IFREG 0444 / S_IFDIR 0555 / S_IFCHR 0620 (console, st_rdev 0x401) / S_IFIFO 0600, st_size, st_blksize 4096, st_blocks; bad fd -> EBADF, bad pointer -> EFAULT, revoked grant -> EACCES |
| 217 | getdents64 | (11.1c) linux_dirent64 records (d_ino synthetic, d_off = directory position, d_type DT_DIR / DT_REG, 8-byte aligned); buffer must be >= 768 bytes (else EINVAL); non-directory -> ENOTDIR, no read right -> EPERM, revoked grant -> EACCES |
| 257 | openat | (11.1c) only relative to a directory handle the sandbox holds (Forge `openat` rules: no absolute paths, no `..`); AT_FDCWD -> EACCES (there is no ambient cwd), write/create/truncate/append flags -> EROFS, O_DIRECTORY on a file -> ENOTDIR; O_CLOEXEC / O_NONBLOCK / O_NOFOLLOW / O_LARGEFILE accepted |
| 9 | mmap | anonymous + private only; PROT_EXEC -> EACCES (W^X: no new code), MAP_FIXED -> EINVAL, file mappings -> ENODEV; r8/r9 come from the saved frame |
| 11 | munmap | |
| 12 | brk | Linux semantics: returns the current break on failure |
| 24 | sched_yield | (11.1b) |
| 16 | ioctl | console fds: TCGETS (cooked, echoing termios), TIOCGWINSZ (framebuffer rows x cols, 25x80 on serial); other requests -> ENOTTY, non-console -> ENOTTY, bad fd -> EBADF |
| 39 / 110 / 186 | getpid / getppid / gettid | getpid is the process (main thread) id; gettid is per thread |
| 56 | clone | (11.1b) threads only: CLONE_VM\|FS\|FILES\|SIGHAND\|THREAD required, plus SYSVSEM, SETTLS, PARENT_SETTID, CHILD_SETTID, CHILD_CLEARTID, DETACHED; at most 8 clone()d threads live; fork-style clone -> ENOSYS, other flags or a bad stack -> EINVAL, TLS above USER_TOP -> EPERM, no slot -> EAGAIN |
| 202 | futex | (11.1b) FUTEX_WAIT / FUTEX_WAKE (PRIVATE flag accepted); stale value -> EAGAIN, misaligned or kernel address -> EINVAL, timeouts -> EINVAL (not yet), other ops -> ENOSYS |
| 218 | set_tid_address | sets the calling thread's clear-child-tid word, returns its tid |
| 60 / 231 | exit / exit_group | exit ends one clone()d thread (the main thread waits for the others first); exit_group ends the whole process |
| 158 | arch_prctl | (11.1b) ARCH_SET_FS (address must be below USER_TOP, else EPERM), ARCH_GET_FS (bad pointer -> EFAULT); ARCH_SET_GS / others -> EINVAL |
| other | | -ENOSYS, counted per process (`Proc.lx_unsup`), first 4 logged: `linux: pid N syscall X not supported -> ENOSYS` |

Errno values are Linux's: Forge's error codes were already the negated Linux errnos.

## Registers

The Linux ABI preserves every register except rax, rcx and r11 across `syscall`. Since 11.1a the
kernel's syscall frame (`examples/kernel/user.fsi`, `SF_SIZE = 128`) saves rdi, rsi, rdx, r10, r8 and
r9 and restores them on return, for Forge programs too (previously they were zeroed). A fresh exec
still starts with all registers zero. The kexec handoff carries the bigger frame (format v4) and the
process's ABI (record word +56, bit 40).

## Thread-local storage (11.1b)

Each kernel `Thread` has an `fsbase`. `arch_prctl(ARCH_SET_FS)` stores it and loads
IA32_FS_BASE (MSR 0xC0000100) at once; `schedule_l` reloads the MSR whenever the next thread's
`fsbase` differs from the current one. The kernel itself never uses FS, so the MSR always belongs to
the running user thread. fork copies the caller's FS base, exec resets it to 0, and the kexec
handoff carries it (process record word +120).

## Threads and futex (11.1b part 2)

- `clone` reuses the fork path in thread mode (`sys_fork(frame, ustack)`): a new kernel Thread with
  a copy of the caller's syscall frame (rax = 0, RSP = the new stack), the same Proc and page tables,
  the caller's FPU state and FS base (or the CLONE_SETTLS one). It is listed in `Proc.xthr[8]`.
- All threads of a process are pinned to the CPU its main thread runs on: no cross-CPU TLB
  shootdown is needed for munmap/brk, and futex check-then-sleep is atomic with interrupts off.
  Parallel threads on several CPUs are future work.
- Futex waiters sit in a 64-entry kernel table keyed by (address space, user address).
- CLONE_CHILD_CLEARTID: when a thread exits the kernel writes 0 to the word and FUTEX_WAKEs it
  (pthread_join).
- Group exit (exit_group, or the main thread exiting first): the process is marked exiting; every
  waiter is woken; each other thread ends at its next syscall, futex wake-up or user-mode timer tick;
  the main thread waits until it is the last, then exits with the group status. Threads are freed
  with the process: `linux: pid N finished: K threads created, all joined and freed; ...`.
- Limits: a thread blocked in a console read delays a group exit until the read returns; close of
  an fd returns EBUSY while another thread is inside read/write/ioctl; kexec already refuses sandboxed
  Linux processes, so threads never need to cross a kexec.

## Test: lxthreads

`run lxthreads` (examples/user/lxthreads.forge, raw syscalls only): 4 clone()d workers, each on its
own mmap'd stack with its own TLS block, add 2000 each to one counter under a futex mutex (three-state
lock, yielding inside the critical section so the others sleep in FUTEX_WAIT): `counter 8000 (expected
8000)`. Then per-thread `%fs` checks, distinct gettid and shared getpid, join through
CLONE_CHILD_CLEARTID, futex EAGAIN/EINVAL, clone(fork) -> ENOSYS, and a group exit with one thread
spinning in user mode and one asleep in FUTEX_WAIT: `lxthreads: PASS (10 checks)`, the kernel's
`6 threads created, all joined and freed` and `[lxthreads exited 0 (sandboxed)]`.

## Process start

exec of a Linux image builds the System V x86-64 start stack at `USTACK_TOP - 1024`:

    rsp -> argc
           argv[0..argc-1]   argv[0] = last path component, then the space-separated args
           NULL
           NULL              (empty environment)
           auxv: AT_PAGESZ 4096, AT_ENTRY, AT_UID/EUID/GID/EGID 1000, AT_SECURE 0,
                 AT_RANDOM -> 16 bytes, AT_EXECFN -> the path, AT_NULL

AT_RANDOM bytes are rdtsc-seeded splitmix64. That is enough for a test program's stack canary; it is
**not** a cryptographic RNG.

## Test: lxtest

`examples/user/lxtest.forge` is a Linux program written in Forge with no Forge runtime: a naked
`_start` hands rsp to `lx_main`, and every call is a raw `syscall` with Linux numbers. build.sh
compiles it with forgec/fas/fld, sets EI_OSABI = 3, then seals it. tests/os/run.sh checks:

- `lxtest` from plain sh -> `verify: /efi/forge/bin/lxtest refused: Linux ABI image needs the compatibility sandbox`
- `run lxtest` -> argc/argv, 9 auxv entries (AT_PAGESZ, AT_ENTRY == &_start, AT_SECURE, AT_RANDOM),
  brk grow/touch/shrink, mmap/munmap, the PROT_EXEC and MAP_FIXED refusals, TCGETS/TIOCGWINSZ,
  ENOTTY/EBADF, getpid/gettid/set_tid_address/getppid, 12 registers preserved across a syscall,
  TLS (arch_prctl SET/GET_FS, `%fs:0` self pointer read 400000 times across 400 yields and timer
  preemptions, plus the EPERM/EFAULT/EINVAL refusals), Forge syscall 409 -> ENOSYS, then
  `lxtest: PASS (25 checks)` and `[lxtest exited 0 (sandboxed)]`.

## Files (11.1c)

A sandboxed Linux binary has no root capability and no cwd: it reaches files only through the
handles its manifest grants (from fd 4 on). `openat(dirfd, path, flags)` maps to Forge `openat` on
that handle, so the grant ledger, the audit log and revocation all apply unchanged (an `openat`
appears in `audit` as a use of the grant). Everything is read-only: write-ish flags get EROFS before
any lookup. `getdents64` converts Forge directory records into linux_dirent64 in a kernel buffer
(the directory is asked for at most rem*11/28 bytes so converted records always fit), which is why
it needs a buffer of at least 768 bytes (glibc and musl use 32 KiB). `fstat` synthesizes a stat
from the handle's type, size and first cluster.

## Test: lxfs

`run lxfs` (manifest `examples/apps/lxfs.mf`: `dir /efi/forge/docs`, fd 4) fstats the console
(S_IFCHR) and the dir (S_IFDIR), lists it with getdents64 (2 records: `Long File Name Notes.txt`,
`big.bin`, both DT_REG; a second call returns 0), opens `big.bin` with openat + O_CLOEXEC, fstats it
(S_IFREG, 98304 bytes) and reads 4096 bytes, then checks the refusals: AT_FDCWD, absolute and `..`
paths -> EACCES, O_WRONLY -> EROFS, O_DIRECTORY on a file -> ENOTDIR, a missing name -> ENOENT,
fstat of a bad fd -> EBADF, getdents64 on the console -> ENOTDIR and with a 100-byte buffer ->
EINVAL. 14 checks; it exits with the failure count.

## Not yet

- Timed futex waits, FUTEX_REQUEUE/WAKE_OP, threads running in parallel on several CPUs.
- stat / lstat / newfstatat by path, readlink, getcwd, writable files.
- execve, signals, /proc, sockets, file-backed mmap.
- Busybox (the Phase 11.1 acceptance test) needs a third-party static binary; it will be added only
  after the owner approves downloading one, and it will run under the same gate (sealed + sandboxed).
- OSABI 0 Linux binaries (most static musl builds) are not detected as Linux; they are refused today
  only because they are unsealed.

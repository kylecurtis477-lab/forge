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

## Syscalls (11.1a to 11.1e; 36 in all)

| nr | name | notes |
|----|------|-------|
| 0 | read | Forge read on the fd (grants and rights checked) |
| 1 | write | Forge write |
| 2 | open | (11.1e part 4) no cwd, so the path must be absolute and name a held directory handle's label exactly (-> a dup of that handle, sharing its position) or lie under it (-> openat(handle, rest)); relative, `..`, outside every handle, or a mere label prefix -> EACCES; write flags -> EROFS; O_DIRECTORY / O_CLOEXEC as for openat |
| 3 | close | |
| 20 | writev | (11.1e part 2) iovecs copied in one at a time, each through write (same fd checks); iovcnt > 1024 -> EINVAL, bad iov -> EFAULT; a later fault or short write returns the bytes so far |
| 35 | nanosleep | (11.1e part 9) sleeps whole 10 ms scheduler ticks (100 Hz), rounded up, capped at one day; not interrupted by signals yet, so rem is never written; tv_nsec >= 1e9 or negative tv_sec -> EINVAL, bad pointer -> EFAULT |
| 63 | uname | (11.1e part 3) sysname `Linux`, nodename `forge`, release `0.2.6-forge`, version `#1 Forge Linux compat sandbox`, machine `x86_64`, domainname `(none)`; bad pointer -> EFAULT |
| 72 | fcntl | (11.1e part 7) F_DUPFD 0 / F_DUPFD_CLOEXEC 1030 (dup's rules, lowest free fd >= arg; arg >= 16 -> EINVAL), F_GETFD 1 / F_SETFD 2 (FD_CLOEXEC), F_GETFL 3 (console O_RDWR, everything else O_RDONLY), F_SETFL 4 only when it changes nothing (O_NONBLOCK etc. -> EINVAL); bad fd -> EBADF, other commands -> EINVAL |
| 79 | getcwd | (11.1e part 8) always "/" (returns 2, the length with the NUL): there is no working directory, relative paths stay EACCES; size < 2 -> ERANGE, bad pointer -> EFAULT |
| 4 | stat | (11.1e part 5) path resolved exactly like open (absolute, at or under a held directory handle; else EACCES / ENOENT), then fstat of that handle, which is closed again; bad buffer -> EFAULT |
| 6 | lstat | (11.1e part 6) identical to stat 4: the FAT32 ESP has no symbolic links, so there is no link to not follow; same EACCES / ENOENT / EFAULT |
| 5 | fstat | (11.1c) 144-byte struct stat: st_dev 0xF0, st_ino = first cluster + 1, st_mode S_IFREG 0444 / S_IFDIR 0555 / S_IFCHR 0620 (console, st_rdev 0x401) / S_IFIFO 0600, st_size, st_blksize 4096, st_blocks; bad fd -> EBADF, bad pointer -> EFAULT, revoked grant -> EACCES |
| 107 | geteuid | (11.1e part 10) 1000, the uid the auxv already reports (AT_UID = AT_EUID = AT_GID = AT_EGID = 1000): an ordinary unprivileged id, because a sandboxed image holds no root capability and nothing in the kernel checks Linux uids; getuid 102 / getgid 104 / getegid 108 are not in the table yet (ENOSYS) and would return the same 1000 |
| 217 | getdents64 | (11.1c) linux_dirent64 records (d_ino synthetic, d_off = directory position, d_type DT_DIR / DT_REG, 8-byte aligned); buffer must be >= 768 bytes (else EINVAL); non-directory -> ENOTDIR, no read right -> EPERM, revoked grant -> EACCES |
| 59 / 322 | execve / execveat | (11.1d) sealed Linux images only, reached through a held handle (see Exec below); Forge, unsealed or tampered images -> ENOEXEC, no cwd (relative path, AT_FDCWD) or outside every handle -> EACCES, live clone()d threads -> EBUSY, bad pointers -> EFAULT, argv over 16 entries / 127 bytes -> E2BIG, an argument with a space -> EINVAL, unknown flags -> EINVAL |
| 13 | rt_sigaction | (11.1d part 2) signals 1..31; sigsetsize must be 8; SIGKILL / SIGSTOP, unknown flags (SA_ONSTACK included), or a handler without SA_RESTORER -> EINVAL; bad pointers -> EFAULT; SIG_IGN drops a pending one |
| 14 | rt_sigprocmask | (11.1d part 2) per-thread blocked set; SIG_BLOCK / SIG_UNBLOCK / SIG_SETMASK, other how -> EINVAL; SIGKILL / SIGSTOP silently stay unblocked |
| 15 | rt_sigreturn | (11.1d part 2) restores the frame written at delivery (see Signals); a frame without the Forge magic, or rip / rsp outside user space -> killed with SIGSEGV |
| 62 | kill | (11.1d part 2) only its own pid (or 0); any other pid -> EPERM, logged (the sandbox); signal 0 probes; > 31 -> EINVAL |
| 234 | tgkill | (11.1d part 2) tgid must be its own pid (else EPERM); tid must be its main thread or a live clone()d thread (else ESRCH) |
| 257 | openat | (11.1c) only relative to a directory handle the sandbox holds (Forge `openat` rules: no absolute paths, no `..`); AT_FDCWD with a relative path -> EACCES (there is no ambient cwd); AT_FDCWD with an absolute path follows open (11.1e), write/create/truncate/append flags -> EROFS, O_DIRECTORY on a file -> ENOTDIR; O_CLOEXEC / O_NONBLOCK / O_NOFOLLOW / O_LARGEFILE accepted |
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
  `lxtest: PASS (30 checks)` and `[lxtest exited 0 (sandboxed)]`.

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

## Exec (11.1d)

The process stays in the compat sandbox across execve: same pid, same sandbox name, no root
capability (it was dropped at `run` and nothing gives it back). The image is reached only through a
handle the process already holds:

- `execve("/abs/path", argv, envp)`: the path must start with the label of one of its directory
  handles with LOOKUP (a manifest `dir` grant is labelled with its path), and the rest is opened
  relative to that handle exactly like `openat` (no `..`, grant ledger and revocation apply).
- `execveat(dirfd, name, argv, envp, 0)`: `name` relative to `dirfd`; AT_FDCWD -> EACCES.
- `execveat(fd, "", argv, envp, AT_EMPTY_PATH)`: the file handle itself (fexecve).

The file needs READ on its handle, is charged to its grant as an `exec` use in `audit`, and is
verified with SEAL_USER | SEAL_COMPAT: an unsealed or tampered image fails the seal, and a sealed
Forge image (OSABI 0) is refused because a Linux process may only become another Linux process. After
the checks, handles opened with O_CLOEXEC are closed; all others (including the manifest grants) are
kept, and `caps &= new seal's caps` -- carried or narrowed, never widened. argv[1..] are passed
(space-joined, at most 16 / 127 bytes), envp is not (the new image starts with an empty
environment, as at `run`). execve while clone()d threads live -> EBUSY (Linux would kill them; not
yet).

## Test: lxexec

`run lxexec` (manifest `examples/apps/lxexec.mf`: `dir /efi/forge/bin`, fd 4) runs three images in
one process. Stage 1 checks the refusals (Forge `cat`, `unsealed`, `tampered` -> ENOEXEC;
`/efi/forge/docs/big.bin`, a relative path, `..`, AT_FDCWD -> EACCES; bad path / argv pointers ->
EFAULT; an argument with a space -> EINVAL), opens `hello` twice (once O_CLOEXEC) and execve()s
`/efi/forge/bin/lxexec stage2 ...`. Stage 2 checks the same pid, the O_CLOEXEC fd gone (EBADF), the
plain fd and fd 4 still there, AT_FDCWD still refused, then `execveat(4, "lxexec")` -> stage 3, which
checks fexecve (AT_EMPTY_PATH) of the held Forge `hello` -> ENOEXEC and an unknown flag -> EINVAL.
21 checks across the three images.

## Signals (11.1d part 2)

Signals 1..31, delivered only to the process itself (kill / tgkill of any other pid -> EPERM, logged
`linux: pid N kill(a, s) refused: outside the sandbox (only its own pid)`). Pending signals are per
process (kill) or per thread (tgkill); the blocked set is per thread, inherited by clone() and kept
across execve (handlers reset to SIG_DFL, as on Linux).

- Delivery happens on the way out of a Linux syscall (lowest unblocked signal first); a thread
  waiting in futex is not interrupted.
- A handler runs with rdi = signo, rsi = siginfo (si_signo, si_code SI_USER or SI_TKILL or
  SEGV_MAPERR / SEGV_ACCERR, si_pid or si_addr), rdx = the frame. Its signal is blocked unless
  SA_NODEFER; SA_RESETHAND resets it to SIG_DFL. It returns through its SA_RESTORER.
- The frame is Forge's own layout, not Linux's ucontext: 20 words below the red zone, word 0 the
  restorer, 1 magic | signo, 2 saved mask, 3 rax (the interrupted syscall's result), 4..11 rcx rdx
  rsi rdi r8 r9 r10 r11, 12 rip, 13 rsp, 14 rflags, 16..19 siginfo. A handler may edit rip / rax
  (lxsig does, to step over a faulting instruction); rt_sigreturn keeps rflags to the arithmetic
  flags + IF and refuses a kernel rip / rsp. Callee-saved registers survive through the handler's ABI.
- A user page fault with a SIGSEGV handler installed and unblocked runs it
  (`linux: pid N SIGSEGV at rip=.. addr=.. -> its handler`); otherwise the process is killed as before.
- Default actions: SIGCHLD, SIGURG, SIGWINCH, SIGCONT and the stop signals are ignored (no job
  control); every other signal ends the process with status 128 + signo
  (`linux: pid N killed by signal S (default action)`).

## Test: lxsig

`run lxsig` (34 checks): rt_sigaction refusals; a SA_SIGINFO handler run by kill(self) (siginfo,
the signal blocked inside the handler, the mask and kill's result restored by rt_sigreturn);
SIG_BLOCK keeps SIGUSR1 pending until SIG_SETMASK; SIGKILL / SIGSTOP unblockable; tgkill of its own
tid (SI_TKILL), an unknown tid -> ESRCH, pid 1 -> EPERM; SIG_IGN, SIGCHLD ignored by default,
SA_RESETHAND; a read of 0x10 (SEGV_MAPERR) and a write to its own code (SEGV_ACCERR), each resumed
by the handler past the instruction with a new rax. `run lxsig term` sends itself SIGTERM with the
default action and exits 143.

## Not yet

- Timed futex waits, FUTEX_REQUEUE/WAKE_OP, threads running in parallel on several CPUs.
- newfstatat by path, readlink, writable files.
- Real-time signals (32..64), sigaltstack, signals to other processes, interrupting a futex wait.
- /proc, sockets, file-backed mmap.
- Busybox (the Phase 11.1 acceptance test) needs a third-party static binary; it will be added only
  after the owner approves downloading one, and it will run under the same gate (sealed + sandboxed).
- OSABI 0 Linux binaries (most static musl builds) are not detected as Linux; they are refused today
  only because they are unsealed.

## BusyBox acceptance (11.1e)

BusyBox is built from source only; no prebuilt third-party binary is used.

- Sources (fetched into `$BB_SRC`, default /workspace/bb, outside the repo), pinned in `tools/busybox/build.sh`:
  - busybox-1.38.0.tar.bz2, sha256 matches busybox.net's .sha256; GPG signature GOOD (Denys Vlasenko, C9E9416F76E610DBD09D040F47B70C55ACC9965B).
  - musl-1.2.5.tar.gz: good signature (gpg GOODSIG/VALIDSIG, made 2024-02-29 20:08 CST) by the musl libc key `8364 8929 0BB6 B70F 99FF  DA05 56BC DB59 3020 450F` (musl libc <musl@libc.org>, created 2014-04-27). The fingerprint was cross-checked on 2026-11-03 against three independent sources that agree: musl.libc.org/musl.pub, keyserver.ubuntu.com, and Debian's packaging (sources.debian.org musl 1.2.5-3 `debian/upstream/signing-key.asc`); keys.openpgp.org has no copy. The key is kept at `tools/busybox/keys/musl.pub.asc` (import it into `$BB_SRC/gnupg`); the SHA-256 pin, which also matches Alpine's published sha512, still applies.
  - make-4.4.1.tar.gz (host had no make): GPG signature made in 2023 by a key that has since expired (EXPKEYSIG, accepted with a note).
- Host compiler: Debian gcc 14.2.0, used to build make and musl; BusyBox is compiled with `musl-gcc --static`. bzip2 decompression uses Python's bz2 (`tools/busybox/bzip2` shim).
- Config: `tools/busybox/busybox.config` (allnoconfig + a small applet set).
- Image: static EXEC at 0x400000, EI_OSABI set to 3, sealed `forgeseal.py --foreign` (level 3, unsafe 0, caps 0). `image_verify` refuses a level-3 seal on anything but a capability-free Linux image (reason 8); the 6.4 gate still refuses it outside the compat sandbox.
- Manifest `examples/apps/busybox.mf`: one `dir /efi/forge/docs` grant.

Status: echo, true, false, basename, env work. Added: writev 20 (11.1e part 2; seq, expr now work; lxtest checks it); uname 63 (11.1e part 3: sysname Linux, nodename forge, release 0.2.6-forge, machine x86_64; `uname -s -m` works). open 2 (11.1e part 4: absolute paths under a grant; wc, sha256sum and cat of granted files work, a file outside is EACCES). stat 4 (11.1e part 5; `ls` of a directory outside the grant -> Permission denied). lstat 6 (11.1e part 6; same as stat, no symlinks). fcntl 72 (11.1e part 7; `ls` of the granted directory and `printf` work). getcwd 79 (11.1e part 8; `pwd` prints /). nanosleep 35 (11.1e part 9; `sleep 1` works). geteuid 107 (11.1e part 10; ash starts without an ENOSYS, `sh -c exit 3` exits 3). They will be added one per commit.

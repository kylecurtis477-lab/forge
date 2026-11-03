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

## Syscalls (11.1a, 11.1b)

| nr | name | notes |
|----|------|-------|
| 0 | read | Forge read on the fd (grants and rights checked) |
| 1 | write | Forge write |
| 3 | close | |
| 9 | mmap | anonymous + private only; PROT_EXEC -> EACCES (W^X: no new code), MAP_FIXED -> EINVAL, file mappings -> ENODEV; r8/r9 come from the saved frame |
| 11 | munmap | |
| 12 | brk | Linux semantics: returns the current break on failure |
| 24 | sched_yield | (11.1b) |
| 16 | ioctl | console fds: TCGETS (cooked, echoing termios), TIOCGWINSZ (framebuffer rows x cols, 25x80 on serial); other requests -> ENOTTY, non-console -> ENOTTY, bad fd -> EBADF |
| 39 / 110 / 186 | getpid / getppid / gettid | one thread per process until 11.1b |
| 218 | set_tid_address | returns the tid; clear-child-tid comes with threads |
| 60 / 231 | exit / exit_group | |
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

## Not yet

- 11.1b part 2: clone (threads sharing the address space), futex, clear-child-tid.
- 11.1c: openat, fstat, getdents64 over the sandbox's granted handles.
- execve, signals, /proc, sockets, file-backed mmap.
- Busybox (the Phase 11.1 acceptance test) needs a third-party static binary; it will be added only
  after the owner approves downloading one, and it will run under the same gate (sealed + sandboxed).
- OSABI 0 Linux binaries (most static musl builds) are not detected as Linux; they are refused today
  only because they are unsealed.

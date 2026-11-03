# Phase 11.1: the Linux syscall personality (docs/linux-compat.md).
#
# A sealed ELF tagged EI_OSABI = 3 (Linux) runs under this personality, and only when it was launched
# in the compat sandbox: sh `run app` (sys_exec with sb = 1 verifies with SEAL_COMPAT). Plain exec,
# the boot loader and the 6.1 self-test still refuse it (seal reason 6), and an unsealed or tampered
# Linux image is refused before the ABI is even looked at. So the only Linux code that can run is code
# that went through forgeseal (the verifier's unsafe report and the build key's signature).
#
# Such a process (Proc.abi = LX_ABI) reaches only lx_dispatch: Linux syscall numbers, Linux argument
# registers (rdi, rsi, rdx, r10, r8, r9; r8/r9 come from the saved syscall frame), Linux -errno
# results. No Forge syscall is reachable from it -- not even through a number both tables share -- so
# the driver, kexec, ktest and grant syscalls (400+) answer ENOSYS like any other unknown number.
# It keeps the sandbox's handles and its sealed capabilities; this table needs none of them.
#
# 11.1a implements: read 0, write 1, close 3, mmap 9 (anonymous, private, never executable),
# munmap 11, brk 12, ioctl 16 (TCGETS, TIOCGWINSZ on the console), getpid 39, exit 60,
# getppid 110, gettid 186, (11.1b) sched_yield 24, arch_prctl 158 (ARCH_SET_FS / ARCH_GET_FS: a
# per-thread FS base that sched.fsi reloads on every context switch), set_tid_address 218, exit_group 231. Everything else returns -ENOSYS and
# is counted (and logged the first 4 times per process). Process start follows the System V x86-64
# ABI: rsp -> argc, argv[], NULL, envp[] (empty), NULL, auxv (AT_PAGESZ, AT_ENTRY, AT_UID/EUID/GID/
# EGID, AT_SECURE, AT_RANDOM -> 16 bytes, AT_EXECFN), AT_NULL. AT_RANDOM bytes are rdtsc-seeded
# splitmix64: unpredictable enough for stack canaries in a test program, not a CSPRNG.
# Not yet: clone/futex (threads: 11.1b part 2), openat/fstat/getdents64 (11.1c), execve,
# signals.
LX_ABI = 1
LX_NSYS = 15  # syscalls in the table above
ARCH_SET_FS = 0x1002
ARCH_GET_FS = 0x1003
ENOTTY = 0xFFFFFFFFFFFFFFE7
ENODEV = 0xFFFFFFFFFFFFFFED
lx_procs: u64  # Linux-personality execs
lx_enosys: u64  # unsupported calls, all processes

def lx_init():
    compat_sandbox_ready = 1
    puts("linux: Linux ABI personality ready (")
    put_u64(LX_NSYS)
    puts(" syscalls), only for sealed images launched in the compat sandbox\n")

def lx_mix(x: u64) -> u64:
    z = x + 0x9E3779B97F4A7C15
    z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9
    z = (z ^ (z >> 27)) * 0x94D049BB133111EB
    return z ^ (z >> 31)

def lx_stack(p: ptr[Proc], entry: u64, path: slice[u8], args: slice[u8]) -> u64:
# build the initial Linux stack in the (current, fresh) address space; returns rsp or 0.
# 1 KiB at USTACK_TOP - 1024: words 0..63 are argc/argv/envp/auxv (16-byte aligned, as the ABI
# requires), bytes 512.. hold the strings (path at +0, args at +128) and AT_RANDOM (+256)
    v: u64[64]
    st: u8[512]
    k: u64 = 0
    while k < 64:
        v[k] = 0
        k += 1
    k = 0
    while k < 512:
        st[k] = 0
        k += 1
    sbase = USTACK_TOP - 512
    nm: u64 = 0
    k = 0
    while k < 127 and k < len(path) and path[k] != 0:
        st[k] = path[k]
        if path[k] == 47:
            nm = k + 1  # argv[0] is the last path component
        k += 1
    k = 0
    while k < 127 and k < len(args) and args[k] != 0:
        st[128 + k] = args[k]
        k += 1
    r = lx_mix(rdtsc() ^ (p.pid << 32))
    r2 = lx_mix(r ^ rdtsc())
    k = 0
    while k < 8:
        st[256 + k] = u8((r >> (k * 8)) & 0xFF)
        st[264 + k] = u8((r2 >> (k * 8)) & 0xFF)
        k += 1
    w: u64 = 1  # word 0 = argc
    v[w] = sbase + nm
    w += 1
    k = 0
    while k < 128 and st[128 + k] != 0 and w < 17:
        if st[128 + k] == 32:
            st[128 + k] = 0
        elif k == 0 or st[128 + k - 1] == 0:
            v[w] = sbase + 128 + k
            w += 1
        k += 1
    v[0] = w - 1
    w += 2  # argv NULL, empty envp's NULL
    w = lx_aux(v[:], w, 6, PAGE)  # AT_PAGESZ
    w = lx_aux(v[:], w, 9, entry)  # AT_ENTRY
    w = lx_aux(v[:], w, 11, 1000)  # AT_UID
    w = lx_aux(v[:], w, 12, 1000)  # AT_EUID
    w = lx_aux(v[:], w, 13, 1000)  # AT_GID
    w = lx_aux(v[:], w, 14, 1000)  # AT_EGID
    w = lx_aux(v[:], w, 23, 0)  # AT_SECURE
    w = lx_aux(v[:], w, 25, sbase + 256)  # AT_RANDOM
    w = lx_aux(v[:], w, 31, sbase)  # AT_EXECFN
    w = lx_aux(v[:], w, 0, 0)  # AT_NULL
    usp = USTACK_TOP - 1024
    if copy_user(usp, u64(addr(v[0])), 512) != 0 or copy_user(sbase, u64(addr(st[0])), 512) != 0:
        return 0
    lx_procs += 1
    f = gr_pbegin()
    puts("linux: pid ")
    put_u64(p.pid)
    puts(" runs ")
    puts(addr(path[0]))
    puts(" under the Linux ABI personality (compat sandbox), argc ")
    put_u64(v[0])
    puts("\n")
    gr_pend(f)
    return usp

def lx_aux(v: slice[u64], w: u64, t: u64, x: u64) -> u64:
    if w + 2 > len(v):
        return w
    v[w] = t
    v[w + 1] = x
    return w + 2

def lx_console(fd: u64) -> u64:
    fl = fd_get(cur_proc(), fd)
    if u64(fl) == 0:
        return EBADF
    if fl.type != F_CONSOLE:
        return ENOTTY
    return 0

def lx_ioctl(fd: u64, req: u64, arg: u64) -> u64:
# the console is a cooked, echoing line terminal; programs probe it with TCGETS (isatty)
    r = lx_console(fd)
    if r != 0:
        return r
    kb: u64[8]
    i: u64 = 0
    while i < 8:
        kb[i] = 0
        i += 1
    if req == 0x5401:
    # struct termios (60 bytes): iflag ICRNL, oflag OPOST|ONLCR, cflag B38400|CS8|CREAD,
    # lflag ISIG|ICANON|ECHO|ECHOE|ECHOK|ECHOCTL|ECHOKE|IEXTEN, line 0, c_cc zero
        kb[0] = 0x100 | (0x5 << 32)
        kb[1] = 0xBF | (0x8A3B << 32)
        if user_ptr_ok(arg, 60) == 0:
            return EFAULT
        return copy_user(arg, u64(addr(kb[0])), 60)
    if req == 0x5413:
    # struct winsize: ws_row, ws_col (u16 each), pixels 0
        rows = fb_rows
        cols = fb_cols
        if rows == 0 or cols == 0:
            rows = 25  # serial-only console
            cols = 80
        kb[0] = (rows & 0xFFFF) | ((cols & 0xFFFF) << 16)
        if user_ptr_ok(arg, 8) == 0:
            return EFAULT
        return copy_user(arg, u64(addr(kb[0])), 8)
    return ENOTTY

def lx_mmap(a: u64, n: u64, prot: u64, flags: u64, fd: u64, off: u64) -> u64:
# anonymous private memory only; never writable+executable or executable at all (W^X: a Linux
# program here cannot create code), no MAP_FIXED (the kernel picks every address)
    if (prot & 4) != 0:
        return EACCES
    if (flags & 0x10) != 0:
        return EINVAL
    if (flags & 0x20) == 0 or (flags & 3) != 2:
        return ENODEV
    if off != 0:
        return EINVAL
    return sys_mmap(n)

def lx_unsupported(nr: u64) -> u64:
    p = cur_proc()
    p.lx_unsup += 1
    lx_enosys += 1
    if p.lx_unsup <= 4:
        f = gr_pbegin()
        puts("linux: pid ")
        put_u64(p.pid)
        puts(" syscall ")
        put_u64(nr)
        puts(" not supported -> ENOSYS\n")
        gr_pend(f)
    return ENOSYS

# arch_prctl (158), routed by syscall_dispatch, which loads the MSR afterwards. Only the FS base is
# supported: ARCH_SET_FS takes any address below USER_TOP (a non-canonical value would fault wrmsr),
# ARCH_GET_FS stores the current one to a user u64.
def lx_arch_prctl(code: u64, a: u64) -> u64:
    t = cur_thread()
    if code == ARCH_SET_FS:
        if a >= USER_TOP:
            return EPERM
        t.fsbase = a
        return 0
    if code == ARCH_GET_FS:
        v: u64[1]
        v[0] = t.fsbase
        if a >= USER_TOP - 8 or copy_user(a, u64(addr(v[0])), 8) != 0:
            return EFAULT
        return 0
    return EINVAL

def lx_dispatch(nr: u64, a: u64, b: u64, c: u64, d: u64) -> u64:
# mmap (9, six arguments) is routed by syscall_dispatch, which reads r8/r9 from the frame
    p = cur_proc()
    p.lx_calls += 1
    if nr == 0:
        return sys_read(a, b, c)
    if nr == 1:
        return sys_write(a, b, c)
    if nr == 3:
        return sys_close(a)
    if nr == 11:
        return sys_munmap(a, b)
    if nr == 12:
        return sys_brk(a)
    if nr == 16:
        return lx_ioctl(a, b, c)
    if nr == 39:
        return sys_getpid()
    if nr == 110:
        return sys_getppid()
    if nr == 186:
        return sys_getpid()  # one thread per process until 11.1b
    if nr == 218:
        return sys_getpid()
    if nr == 24:
        yield_cpu()
        return 0
    if nr == 60 or nr == 231:
        proc_exit(a & 0xFF)
    return lx_unsupported(nr)

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
# 11.1b part 2: clone 56 (threads only), futex 202 (WAIT, WAKE), set_tid_address clears and wakes
# the tid word at thread exit (see the threads section below).
# 11.1c: fstat 5, getdents64 217, openat 257 -- only relative to a directory handle the process
# holds (no AT_FDCWD, no absolute paths, read-only opens), with the grant ledger counting each use.
# 11.1d: execve 59, execveat 322 -- sealed Linux images reached through a held handle only; the
# sandbox, its handles (minus O_CLOEXEC) and its narrowed capabilities carry over (section below).
# Not yet: signals.
LX_ABI = 1
LX_NSYS = 22  # syscalls in the table above
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

def lx_stack(p: ptr[Proc], entry: u64, pa: slice[u8]) -> u64:
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
    while k < 127 and pa[k] != 0:  # pa = exec_pack(path, args)
        st[k] = pa[k]
        if pa[k] == 47:
            nm = k + 1  # argv[0] is the last path component
        k += 1
    k = 0
    while k < 127 and pa[128 + k] != 0:
        st[128 + k] = pa[128 + k]
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
    puts(addr(st[0]))
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

# ---- 11.1b: threads (clone), futexes, group exit ----
# A clone()d thread is a kernel Thread in the caller's Proc (sys_fork in thread mode): same pml4,
# same fds, same brk/mmap state, its own kernel stack, user stack, FS base and tid. Every thread of a
# process is pinned to the CPU the main thread ran on when it first cloned, so two threads of one
# process never run at the same time: no cross-CPU TLB shootdown is needed for munmap/brk, and
# a futex wait (value check + enqueue with irqs off) cannot race its waker. Real parallelism across
# CPUs is future work.
# The main thread (Proc.thread) is always the last thread to die, so wait/reap/destroy see a
# finished process exactly as before: SYS_exit of a clone()d thread ends that thread (clearing and
# futex-waking its clear-child-tid word); SYS_exit of the main thread waits for the others; a group
# exit (exit_group, a fatal fault in any thread) sets Proc.exiting, wakes the process's futex
# waiters, and every other thread dies at its next syscall, futex wake-up or user-mode timer tick.
# A thread blocked elsewhere (console read) holds the group exit until its call returns.
LX_MAXTHR = 8  # clone()d threads alive at once per process (Proc.xthr)
CLONE_THREAD_REQ = 0x10F00  # CLONE_VM | CLONE_FS | CLONE_FILES | CLONE_SIGHAND | CLONE_THREAD
CLONE_OK = 0x17D0F00  # ... | SYSVSEM | SETTLS | PARENT_SETTID | CHILD_CLEARTID | DETACHED | CHILD_SETTID
CLONE_SETTLS = 0x80000
CLONE_PARENT_SETTID = 0x100000
CLONE_CHILD_CLEARTID = 0x200000
CLONE_CHILD_SETTID = 0x1000000
EAGAIN = 0xFFFFFFFFFFFFFFF5
FX_N = 64  # futex waiters, all processes
fx_thr: u64[64]  # parked waiters (Thread.fx_as/fx_addr hold the key); slots change under rq_lock
lx_nthreads: u64  # clone()d threads, all processes
lx_fx_waits: u64
lx_fx_wakes: u64

def lx_uaddr32(ua: u64) -> u64:
    return ua != 0 and ua < USER_TOP - 4 and (ua & 3) == 0

def lx_put32(ua: u64, v: u64) -> u64:
    if lx_uaddr32(ua) == 0:
        return EFAULT
    k: u32[1]
    k[0] = u32(v & 0xFFFFFFFF)
    if copy_user(ua, u64(addr(k[0])), 4) != 0:
        return EFAULT
    return 0

def lx_get32(ua: u64) -> u64:
# the u32 at ua, or 1 << 32 if it cannot be read
    if lx_uaddr32(ua) == 0:
        return 0x100000000
    k: u32[1]
    k[0] = 0
    if copy_user(u64(addr(k[0])), ua, 4) != 0:
        return 0x100000000
    return u64(k[0])

def lx_clone(frame: u64, flags: u64, stack: u64, ptid: u64, ctid: u64, tls: u64) -> u64:
    p = cur_proc()
    t = cur_thread()
    if (flags & CLONE_THREAD_REQ) != CLONE_THREAD_REQ:
        return lx_unsupported(56)  # fork/vfork through clone: not in 11.1b
    if (flags & ~CLONE_OK) != 0 or stack == 0 or stack >= USER_TOP:
        return EINVAL
    if (flags & CLONE_SETTLS) != 0 and tls >= USER_TOP:
        return EPERM
    if p.exiting:
        return EAGAIN
    slot = LX_MAXTHR
    i: u64 = 0
    while i < LX_MAXTHR:
        x = p.xthr[i]
        if x == 0:
            slot = i
            break
        if as_thread(x).state == T_DEAD:
            thread_free(as_thread(x))
            p.xthr[i] = 0
            slot = i
            break
        i += 1
    if slot == LX_MAXTHR:
        return EAGAIN
    ct = as_thread(sys_fork(frame, stack))
    if (flags & CLONE_SETTLS) != 0:
        ct.fsbase = tls
    if (flags & CLONE_CHILD_CLEARTID) != 0:
        ct.ctid = ctid
    if (flags & CLONE_PARENT_SETTID) != 0:
        lx_put32(ptid, ct.id)
    if (flags & CLONE_CHILD_SETTID) != 0:
        lx_put32(ctid, ct.id)  # same address space: the parent's view is the child's
    f = irq_save()
    spin_lock(addr(rq_lock))
    if t.pin == 0:
        t.pin = lapic_cpu() + 1
    ct.pin = t.pin
    p.xthr[slot] = u64(ct)
    p.nxthr += 1
    p.nmade += 1
    lx_nthreads += 1
    rq_push(ct)
    spin_unlock(addr(rq_lock))
    irq_restore(f)
    return ct.id

def lx_fx_wake(asp: u64, ua: u64, n: u64) -> u64:
# wake up to n waiters on (as, ua); ua == 0 wakes every waiter in the address space (group exit)
    k: u64 = 0
    f = irq_save()
    spin_lock(addr(rq_lock))
    i: u64 = 0
    while i < FX_N and k < n:
        x = fx_thr[i]
        if x != 0 and as_thread(x).fx_as == asp and (ua == 0 or as_thread(x).fx_addr == ua):
            fx_thr[i] = 0
            wake_l(as_thread(x))
            k += 1
        i += 1
    lx_fx_wakes += k
    spin_unlock(addr(rq_lock))
    irq_restore(f)
    return k

def lx_futex(ua: u64, op: u64, val: u64, timeout: u64) -> u64:
# FUTEX_WAIT (0) and FUTEX_WAKE (1); FUTEX_PRIVATE_FLAG (128) is accepted and implied: keys are
# (address space, address), so only threads of one process meet on a futex
    cmd = op & ~u64(0x80)
    if lx_uaddr32(ua) == 0:
        return EINVAL
    p = cur_proc()
    if cmd == 1:
        return lx_fx_wake(p.pml4, ua, val)
    if cmd != 0:
        return lx_unsupported(202)
    if timeout != 0:
        return EINVAL  # timed waits: not in 11.1b
    t = cur_thread()
    f = irq_save()
    v = lx_get32(ua)
    if v > 0xFFFFFFFF:
        irq_restore(f)
        return EFAULT
    if v != (val & 0xFFFFFFFF):
        irq_restore(f)
        return EAGAIN
    spin_lock(addr(rq_lock))
    i: u64 = 0
    while i < FX_N and fx_thr[i] != 0:
        i += 1
    if i == FX_N:
        spin_unlock(addr(rq_lock))
        irq_restore(f)
        return ENOMEM
    t.fx_as = p.pml4
    t.fx_addr = ua
    fx_thr[i] = u64(t)
    t.state = T_BLOCKED
    lx_fx_waits += 1
    schedule_l(0)  # returns with rq_lock released once a waker took us out of fx_thr[]
    irq_restore(f)
    lx_exit_check()
    return 0

def lx_thread_exit(t: ptr[Thread], p: ptr[Proc]):
# a clone()d thread ends (never returns); its Thread is freed on slot reuse or with the process
    if t.ctid != 0:
        lx_put32(t.ctid, 0)
        lx_fx_wake(p.pml4, t.ctid, 1)
    irq_save()  # interrupts stay off: schedule_l(1) never returns here
    spin_lock(addr(rq_lock))
    p.nxthr -= 1
    wq_wake_all_l(addr(p.thrq))
    schedule_l(1)
    panic("dead thread resumed")

def lx_wait_threads(p: ptr[Proc]):
    f = irq_save()
    spin_lock(addr(rq_lock))
    while p.nxthr != 0:
        wq_block_l(addr(p.thrq))
    spin_unlock(addr(rq_lock))
    irq_restore(f)

def lx_exit_begin(t: ptr[Thread], p: ptr[Proc], code: u64) -> u64:
# proc_exit of a Linux process: a group exit. A clone()d caller ends here; the main thread returns
# the exit status once it is the only thread left.
    f = irq_save()
    if p.exiting == 0:
        p.exiting = 1
        p.gcode = code
    irq_restore(f)
    if p.nmade != 0:
        lx_fx_wake(p.pml4, 0, FX_N)
    if u64(t) != p.thread:
        lx_thread_exit(t, p)
    lx_wait_threads(p)
    return p.gcode

def lx_exit_check():
# at a syscall, a futex wake-up or a user-mode tick: obey a pending group exit
    t = cur_thread()
    if t.proc == 0:
        return
    p = as_proc(t.proc)
    if p.exiting == 0:
        return
    if u64(t) == p.thread:
        proc_exit(p.gcode)
    lx_thread_exit(t, p)

def lx_tick_check(fr: u64):
# from the timer: fr is the interrupt frame; at kstack_top - 40 it was pushed on entry from ring 3
    t = cur_thread()
    if u64(t) == 0 or t.user == 0 or t.proc == 0 or fr != t.kstack_top - 40:
        return
    if as_proc(t.proc).exiting:
        lx_exit_check()

def lx_proc_done(p: ptr[Proc]):
# proc_destroy of a process that cloned: free the dead clone()d threads, log the totals
    i: u64 = 0
    while i < LX_MAXTHR:
        if p.xthr[i] != 0:
            thread_free(as_thread(p.xthr[i]))
            p.xthr[i] = 0
        i += 1
    f = gr_pbegin()
    puts("linux: pid ")
    put_u64(p.pid)
    puts(" finished: ")
    put_u64(p.nmade)
    puts(" threads created, all joined and freed; futex waits ")
    put_u64(lx_fx_waits)
    puts(", wakes ")
    put_u64(lx_fx_wakes)
    puts("\n")
    gr_pend(f)

# ---- 11.1c: openat, fstat, getdents64 over the handles the sandbox granted ----
AT_FDCWD = 0xFFFFFFFFFFFFFF9C
O_ACCMODE = 3
O_WRITEISH = 0x640  # O_CREAT | O_TRUNC | O_APPEND
O_DIRECTORY = 0x10000
LX_DENT_MIN = 768  # getdents64 needs room for the largest record (documented)

def lx_openat(dirfd: u64, upath: u64, flags: u64) -> u64:
# no current directory, so no ambient lookup: AT_FDCWD and absolute paths are refused; a relative
# path resolves under a held directory handle with LOOKUP (sys_openat: rights, grants, no "..")
    if dirfd == AT_FDCWD:
        return EACCES
    if (flags & O_ACCMODE) != 0 or (flags & O_WRITEISH) != 0:
        return EROFS  # the mounted file systems are read-only to applications
    fd = sys_openat(dirfd & 0xFFFFFFFF, upath)
    if fd >= NFD:
        return fd
    if (flags & O_DIRECTORY) != 0:
        fl = fd_get(cur_proc(), fd)
        if u64(fl) == 0 or (fl.type != F_DIR and fl.type != F_ODIR):
            sys_close(fd)
            return ENOTDIR
    lx_cx(fd, (flags >> 19) & 1)  # 11.1d: O_CLOEXEC (0x80000) is honoured by execve
    return fd

def lx_cx(fd: u64, on: u64):
    p = cur_proc()
    p.lx_cloexec = (p.lx_cloexec & ~(u64(1) << fd)) | (on << fd)

def lx_fstat(fd: u64, ubuf: u64) -> u64:
# struct stat (x86-64, 144 bytes); uid/gid 0, times 0, st_ino the object's first cluster or id
    st: u64[18]
    i: u64 = 0
    while i < 18:
        st[i] = 0
        i += 1
    if user_ptr_ok(ubuf, 144) == 0:
        return EFAULT
    fl = fd_get(cur_proc(), fd)
    if u64(fl) == 0:
        return EBADF
    if gr_use(fl.gid, "fstat") != 0:
        return EACCES
    mode: u64 = 0x8124  # S_IFREG | 0444
    st[2] = 1
    if fl.type == F_DIR or fl.type == F_ODIR:
        mode = 0x416D  # S_IFDIR | 0555
        st[2] = 2
    elif fl.type == F_CONSOLE:
        mode = 0x2190  # S_IFCHR | 0620
        st[5] = 0x401  # rdev: tty1 (4, 1)
    elif fl.type == F_CHAN:
        mode = 0x1180  # S_IFIFO | 0600
    st[0] = 0xF0  # st_dev: one device number for the Forge VFS
    st[1] = fl.clu + 1
    st[3] = mode
    if fl.type == F_FILE or fl.type == F_OBJ:
        st[6] = fl.size
    st[7] = 4096
    st[8] = (st[6] + 511) / 512
    if copy_user(ubuf, u64(addr(st[0])), 144) != 0:
        return EFAULT
    return 0

def lx_getdents64(fd: u64, ubuf: u64, n: u64) -> u64:
# Forge getdents records (u64 size, u8 is_dir, name, NUL) converted to linux_dirent64
# (u64 ino, u64 off, u16 reclen, u8 type, name, NUL, 8-byte aligned). A Forge record of r bytes
# (r >= 11) becomes at most r + 17 <= r * 28 / 11 bytes, so asking the directory for rem * 11 / 28
# bytes always fits; d_ino is synthetic (index + 1), d_off the directory position after the entry.
    kb: u8[512]
    rec: u8[288]
    if user_ptr_ok(ubuf, n) == 0:
        return EFAULT
    p = cur_proc()
    fl = fd_get(p, fd)
    if u64(fl) == 0:
        return EBADF
    if fl.type != F_DIR and fl.type != F_ODIR:
        return ENOTDIR
    if (p.fdr[fd] & CAP_READ) == 0:
        return EPERM
    if gr_use(fl.gid, "getdents") != 0:
        return EACCES
    if n < LX_DENT_MIN:
        return EINVAL
    done: u64 = 0
    while (n - done) * 11 / 28 >= 266:
        fb = (n - done) * 11 / 28
        if fb > 512:
            fb = 512
        k: u64 = 0
        if fl.type == F_ODIR:
            k = odir_read(fl, 0, fb, 1, kb[:])
        else:
            k = dir_read(fl, u64(addr(kb[0])), fb, 1)
        if k > 0xFFFFFFFFFFFFF000:
            if done == 0:
                return k
            return done
        if k == 0:
            break
        j: u64 = 0
        while j + 10 <= k:
            l: u64 = 0
            while kb[j + 9 + l] != 0:
                l += 1
            rl = (19 + l + 1 + 7) & ~u64(7)
            i: u64 = 0
            while i < rl:
                rec[i] = 0
                i += 1
            p.lx_dino += 1
            lx_le(rec[:], 0, p.lx_dino, 8)
            lx_le(rec[:], 8, fl.pos, 8)
            lx_le(rec[:], 16, rl, 2)
            rec[18] = 8
            if kb[j + 8] != 0:
                rec[18] = 4
            i = 0
            while i < l:
                rec[19 + i] = kb[j + 9 + i]
                i += 1
            if copy_user(ubuf + done, u64(addr(rec[0])), rl) != 0:
                return EFAULT
            done += rl
            j += 9 + l + 1
    return done

# ---- 11.1d: execve / execveat ----
# A sandboxed Linux process has no root capability and no cwd, so an image is reachable only through
# a handle it already holds: execve(path) needs an absolute path that lies under one of its directory
# handles (matched by the handle's label, then opened relative to it like openat); execveat(dirfd,
# name) opens name relative to dirfd, or with AT_EMPTY_PATH runs the file handle itself (fexecve).
# The image must be sealed AND tagged Linux (OSABI 3); it is verified with SEAL_COMPAT because the
# caller is already inside the compat sandbox. The new image keeps the sandbox (no root capability
# comes back), keeps its handles except O_CLOEXEC ones, and its capabilities are AND-ed with the new
# seal's: grants are carried or narrowed, never widened. argv[1..] become the space-separated args
# (at most 16, 127 bytes; an argument containing a space -> EINVAL); envp is not passed (empty env).
LX_NODIR = 0xFFFFFFFFFFFFFFFF
AT_EMPTY_PATH = 0x1000
E2BIG = 0xFFFFFFFFFFFFFFF9
EIO = 0xFFFFFFFFFFFFFFFB
lx_execs: u64
lx_exec_refused: u64

def lx_argv(uargv: u64, args: slice[u8]) -> u64:
# argv[1..] -> "a b c" in args (128 bytes); argv may be NULL
    w: u64 = 0
    args[0] = 0
    if uargv == 0:
        return 0
    s: u8[128]
    i: u64 = 1
    while i < 17:
        pv: u64 = 0
        if user_ptr_ok(uargv + i * 8, 8) == 0 or copy_user(u64(addr(pv)), uargv + i * 8, 8) != 0:
            return EFAULT
        if pv == 0:
            return 0
        n = copy_str_user(u64(addr(s[0])), pv, 128)
        if n > 127:
            return EFAULT
        k: u64 = 0
        while s[k] != 0:
            if s[k] == 32:
                return EINVAL
            k += 1
        if w + k + 1 > 127:
            return E2BIG
        if w != 0:
            args[w] = 32
            w += 1
        j: u64 = 0
        while j < k:
            args[w + j] = s[j]
            j += 1
        w += k
        args[w] = 0
        i += 1
    return E2BIG

def lx_under(fl: nptr[File], path: slice[u8]) -> u64:
# length of fl's label when path is "<label>/<rest>", else 0
    if u64(fl) == 0:
        return 0
    k: u64 = 0
    while k < 31 and fl.lab[k] != 0:
        if path[k] != fl.lab[k]:
            return 0
        k += 1
    if k == 0 or k >= 31 or path[k] != 47 or path[k + 1] == 0:
        return 0
    return k

def lx_exec_refuse(p: ptr[Proc], path: slice[u8], why: ptr[u8], e: u64) -> u64:
    lx_exec_refused += 1
    f = gr_pbegin()
    puts("linux: pid ")
    put_u64(p.pid)
    puts(" execve ")
    puts(addr(path[0]))
    puts(" refused: ")
    puts(why)
    puts("\n")
    gr_pend(f)
    return e

def lx_execve(frame: u64, dirfd: u64, upath: u64, uargv: u64, flags: u64) -> u64:
# execve(path, argv, envp) is dirfd = LX_NODIR; execveat(dirfd, path, argv, envp, flags) otherwise
    path: u8[128]
    args: u8[128]
    if copy_str_user(u64(addr(path[0])), upath, 128) > 127:
        return EFAULT
    p = cur_proc()
    if sb_slot(p.pid) == MAX_PROCS:
        return EACCES  # the Linux personality exists only inside the compat sandbox
    if (flags & ~AT_EMPTY_PATH) != 0:
        return EINVAL
    if dirfd == AT_FDCWD:
        return lx_exec_refuse(p, path[:], "AT_FDCWD (no cwd)", EACCES)
    ar = lx_argv(uargv, args[:])
    if ar != 0:
        return ar
    if p.nxthr != 0:
        return EBUSY  # documented limit: no execve while clone()d threads are alive
    fd: u64 = 0
    tmp: u64 = 1
    if dirfd == LX_NODIR:
        if path[0] != 47:
            return lx_exec_refuse(p, path[:], "no cwd; an absolute path under a held handle is needed", EACCES)
        if path_ok(path[:]) == 0:
            return lx_exec_refuse(p, path[:], "no .. or empty components", EACCES)
        i: u64 = 0
        l: u64 = 0
        while i < NFD:
            d = fd_get(p, i)
            if u64(d) != 0 and d.type == F_DIR and (p.fdr[i] & CAP_LOOKUP) != 0:
                l = lx_under(d, path[:])
                if l != 0:
                    break
            i += 1
        if i == NFD:
            return lx_exec_refuse(p, path[:], "not under a handle the sandbox holds", EACCES)
        fd = sys_openat(i, upath + l + 1)
    elif (flags & AT_EMPTY_PATH) != 0 and path[0] == 0:
        fd = dirfd & 0xFFFFFFFF
        tmp = 0
    else:
        fd = sys_openat(dirfd & 0xFFFFFFFF, upath)
    if fd > 0xFFFFFFFFFFFFF000:
        return fd
    fl = fd_get(p, fd)
    if u64(fl) == 0:
        return EBADF
    if path[0] == 0:
        k: u64 = 0
        while k < 31 and fl.lab[k] != 0:
            path[k] = fl.lab[k]  # fexecve: name the image by its handle's label
            k += 1
        path[k] = 0
    e: u64 = 0
    if fl.type != F_FILE:
        e = EACCES
    elif (p.fdr[fd] & CAP_READ) == 0:
        e = EACCES
    elif gr_use(fl.gid, "exec") != 0:
        e = EACCES
    img: u64 = 0
    sz = fl.size
    if e == 0:
        img = fs_read_clu(fl.clu, sz)
        if img == 0:
            e = EIO
    if tmp != 0:
        sys_close(fd)
    if e != 0:
        return e
    vc = image_verify(img, sz, SEAL_USER | SEAL_COMPAT)
    if vc == 0:
        seal_log(addr(path[0]))
        kfree(img)
        return ENOEXEC
    if lx_osabi(img) != 3:
        kfree(img)
        return lx_exec_refuse(p, path[:], "not a Linux image (Forge images run only through Forge exec)", ENOEXEC)
    # past every check: drop O_CLOEXEC handles, then replace the image
    i2: u64 = 0
    while i2 < NFD:
        if ((p.lx_cloexec >> i2) & 1) != 0:
            sys_close(i2)
        i2 += 1
    lx_execs += 1
    pa: u8[256]
    exec_pack(pa[:], path[:], args[:])
    return exec_image(frame, img, sz, vc, pa[:])

def lx_osabi(img: u64) -> u64:
    return (le32(img + 4) >> 24) & 0xFF  # e_ident[EI_OSABI]

def lx_le(b: slice[u8], off: u64, v: u64, n: u64):
    i: u64 = 0
    while i < n:
        b[off + i] = u8((v >> (i * 8)) & 255)
        i += 1

def lx_dispatch(nr: u64, a: u64, b: u64, c: u64, d: u64) -> u64:
# mmap (9, six arguments) is routed by syscall_dispatch, which reads r8/r9 from the frame
    p = cur_proc()
    p.lx_calls += 1
    if nr == 0 or nr == 1 or nr == 16 or nr == 5 or nr == 217 or nr == 257:
        atomic_fetch_add(addr(p.fdbusy), 1, acq_rel)
        r: u64 = 0
        if nr == 0:
            r = sys_read(a, b, c)
        elif nr == 1:
            r = sys_write(a, b, c)
        elif nr == 5:
            r = lx_fstat(a, b)
        elif nr == 217:
            r = lx_getdents64(a, b, c)
        elif nr == 257:
            r = lx_openat(a, b, c)
        else:
            r = lx_ioctl(a, b, c)
        atomic_fetch_add(addr(p.fdbusy), 0xFFFFFFFFFFFFFFFF, acq_rel)
        return r
    if nr == 3:
        if p.nxthr != 0 and atomic_load(addr(p.fdbusy), acquire) != 0:
            return EBUSY  # 11.1b: another thread is using a (shared) fd
        if a < NFD:
            lx_cx(a, 0)
        return sys_close(a)
    if nr == 11 or nr == 12:
        f = irq_save()  # threads share mm state (and one CPU): irqs off serialises it
        m: u64 = 0
        if nr == 11:
            m = sys_munmap(a, b)
        else:
            m = sys_brk(a)
        irq_restore(f)
        return m
    if nr == 39:
        return sys_getpid()
    if nr == 110:
        return sys_getppid()
    if nr == 186:
        return cur_thread().id  # the main thread's tid is the pid
    if nr == 218:
        cur_thread().ctid = a
        return cur_thread().id
    if nr == 202:
        return lx_futex(a, b, c, d)
    if nr == 24:
        yield_cpu()
        return 0
    if nr == 60:
        t = cur_thread()
        if u64(t) != p.thread:
            lx_thread_exit(t, p)
        lx_wait_threads(p)  # the main thread outlives its threads (Linux lets it go first)
        proc_exit(a & 0xFF)
    if nr == 231:
        proc_exit(a & 0xFF)
    return lx_unsupported(nr)

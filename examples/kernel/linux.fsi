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
# Not yet: openat/fstat/getdents64 (11.1c), execve,
# signals.
LX_ABI = 1
LX_NSYS = 17  # syscalls in the table above
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

def lx_dispatch(nr: u64, a: u64, b: u64, c: u64, d: u64) -> u64:
# mmap (9, six arguments) is routed by syscall_dispatch, which reads r8/r9 from the frame
    p = cur_proc()
    p.lx_calls += 1
    if nr == 0 or nr == 1 or nr == 16:
        atomic_fetch_add(addr(p.fdbusy), 1, acq_rel)
        r: u64 = 0
        if nr == 0:
            r = sys_read(a, b, c)
        elif nr == 1:
            r = sys_write(a, b, c)
        else:
            r = lx_ioctl(a, b, c)
        atomic_fetch_add(addr(p.fdbusy), 0xFFFFFFFFFFFFFFFF, acq_rel)
        return r
    if nr == 3:
        if p.nxthr != 0 and atomic_load(addr(p.fdbusy), acquire) != 0:
            return EBUSY  # 11.1b: another thread is using a (shared) fd
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

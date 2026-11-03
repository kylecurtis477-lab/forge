# Processes: Proc objects (pid, parent, address space, fds, brk/mmap, args), fork/exec/wait/exit,
# the VFS (console, FAT32 files and directories behind file descriptors), the tty line queue fed by
# PS/2 and COM1 interrupts, and the syscall table. Syscalls run with interrupts enabled, so they can
# sleep (disk, tty, wait) and be preempted.
P_ALIVE = 1
P_ZOMBIE = 2
MAX_PROCS = 64
NFD = 16
MMAP_BASE = 0x0000700000000000
MMAP_TOP = 0x00007F0000000000
BRK_MAX = 0x0000100000000000
ENOENT = 0xFFFFFFFFFFFFFFFE
ENOEXEC = 0xFFFFFFFFFFFFFFF8
EBADF = 0xFFFFFFFFFFFFFFF7
ECHILD = 0xFFFFFFFFFFFFFFF6
ENOMEM = 0xFFFFFFFFFFFFFFF4
EFAULT = 0xFFFFFFFFFFFFFFF2
EMFILE = 0xFFFFFFFFFFFFFFE8
ENOTDIR = 0xFFFFFFFFFFFFFFEC
EISDIR = 0xFFFFFFFFFFFFFFEB
EINVAL = 0xFFFFFFFFFFFFFFEA
EROFS = 0xFFFFFFFFFFFFFFE2
ENOSYS = 0xFFFFFFFFFFFFFFDA
EACCES = 0xFFFFFFFFFFFFFFF3
CAP_READ = 1
CAP_WRITE = 2
CAP_LOOKUP = 4
CAP_DUP = 8
CAP_XFER = 16
CAP_ALL = 31
CAP_ROOT_FD = 3

struct Proc:
    pid: u64
    ppid: u64
    pml4: u64
    thread: u64
    state: u64
    exit_code: u64
    brk_base: u64
    brk: u64
    mmap_next: u64
    fds: u64[16]
    fdr: u64[16]  # rights of each handle (CAP_*)
    args: u8[128]
    childq: WaitQ
    drv: u64
    dma_pa: u64
    dma_n: u64
    dma_va: u64
    caps: u64  # Phase 6.2: capabilities granted from the sealed manifest

procs: u64[64]
kx_freeze: u64  # a kexec handoff is collecting every user process at a syscall boundary
kx_caller: u64  # the Proc doing the handoff (it is not parked)

def kx_should_park() -> u64:
    if kx_freeze == 0:
        return 0
    p = cur_proc()
    return u64(p) != 0 and p.drv == 0 and u64(p) != kx_caller

def kx_park():
# park at a syscall boundary until the handoff happens (never returns then) or is abandoned
    t = cur_thread()
    t.kx_parked = 1
    while kx_freeze != 0:
        sleep_ticks(1)
    t.kx_parked = 0
nforks: u64
sandboxes: u64
sb_live: u64
sb_denials: u64
nexecs: u64

@unsafe
def proc_find_l(pid: u64) -> nptr[Proc]:
    i = 0
    while i < MAX_PROCS:
        if procs[i] != 0 and ptr[Proc](procs[i]).pid == pid:
            return ptr[Proc](procs[i])
        i += 1
    return 0

def proc_insert(p: ptr[Proc]) -> u64:
    f = irq_save()
    spin_lock(addr(rq_lock))
    i = 0
    while i < MAX_PROCS:
        if procs[i] == 0:
            procs[i] = u64(p)
            spin_unlock(addr(rq_lock))
            irq_restore(f)
            return 1
        i += 1
    spin_unlock(addr(rq_lock))
    irq_restore(f)
    return 0

def proc_remove_l(p: ptr[Proc]):
    i = 0
    while i < MAX_PROCS:
        if procs[i] == u64(p):
            procs[i] = 0
        i += 1

@unsafe
def cur_proc() -> ptr[Proc]:
    return ptr[Proc](cur_thread().proc)

# ---- files ----
F_CONSOLE = 1
F_FILE = 2
F_DIR = 3
F_CHAN = 4

struct File:
    type: u64
    ref: u64
    clu: u64  # first cluster (file) / directory cluster
    size: u64
    pos: u64  # byte offset (file) / entry index (dir)
    ccl: u64  # cached cluster for sequential reads
    cidx: u64  # its index in the chain

console_file: u64

@unsafe
def file_new(type: u64) -> ptr[File]:
    fl = ptr[File](kmalloc(sizeof(File)))
    memset(u64(fl), 0, sizeof(File))
    fl.type = type
    fl.ref = 1
    return fl

@unsafe
def file_get(fl: u64) -> u64:
    if fl != 0:
        atomic_fetch_add(addr(ptr[File](fl).ref), 1, acq_rel)
    return fl

@unsafe
def file_put(fl: u64):
    if fl == 0:
        return
    f = ptr[File](fl)
    if atomic_fetch_add(addr(f.ref), 0xFFFFFFFFFFFFFFFF, acq_rel) == 1:
        if f.type == F_CHAN:
            chan_end_close(f)
        kfree(fl)

console_obj: File

def console() -> u64:
# one static console File shared by every process; its huge refcount means it is never freed
    if console_file == 0:
        console_obj.type = F_CONSOLE
        console_obj.ref = 1000000000
        console_file = u64(addr(console_obj))
    return file_get(console_file)

@unsafe
def fd_get(p: ptr[Proc], fd: u64) -> nptr[File]:
    if fd >= NFD:
        return 0
    return ptr[File](p.fds[fd])

def vfs_open(path: ptr[u8]) -> u64:
    return vfs_open_at(fs_root, path)

def vfs_open_at(c: u64, path: ptr[u8]) -> u64:
    ent: u8[32]
    e = u64(addr(ent[0]))
    mutex_lock(addr(fs_m))
    r = fs_lookup_at(c, path, e)
    mutex_unlock(addr(fs_m))
    if r == 0:
        return 0
    if r == 2:
        fl = file_new(F_DIR)
        fl.clu = c
        return u64(fl)
    if ent[11] & 0x10:
        fl = file_new(F_DIR)
        fl.clu = ent_cluster(e)
        if fl.clu == 0:
            fl.clu = fs_root
        return u64(fl)
    fl = file_new(F_FILE)
    fl.clu = ent_cluster(e)
    fl.size = le32(e + 28)
    fl.ccl = fl.clu
    return u64(fl)

def file_read(fl: ptr[File], ubuf: u64, n: u64) -> u64:
# FAT32 file -> user buffer, sector by sector through a bounce buffer
    kb: u8[512]
    if fl.pos >= fl.size:
        return 0
    if n > fl.size - fl.pos:
        n = fl.size - fl.pos
    csz = fs_spc * 512
    done: u64 = 0
    mutex_lock(addr(fs_m))
    while done < n:
        want = fl.pos / csz
        if want < fl.cidx:
            fl.ccl = fl.clu
            fl.cidx = 0
        while fl.cidx < want:
            fl.ccl = fat_next(fl.ccl)
            fl.cidx += 1
        if fl.ccl < 2 or fl.ccl >= 0x0FFFFFF8:
            break
        off = fl.pos % csz
        if vb_read(clu_lba(fl.ccl) + off / 512, 1, u64(addr(kb[0]))) == 0:
            break
        k = 512 - off % 512
        if k > n - done:
            k = n - done
        if copy_user(ubuf + done, u64(addr(kb[0])) + off % 512, k) != 0:
            mutex_unlock(addr(fs_m))
            return EFAULT
        done += k
        fl.pos += k
    mutex_unlock(addr(fs_m))
    return done

@unsafe
def dir_read(fl: ptr[File], ubuf: u64, n: u64) -> u64:
# getdents records: u64 size, u8 is_dir, name, NUL (only whole records are returned)
    ent: u8[32]
    nm: u8[256]
    rec: u8[272]
    done: u64 = 0
    mutex_lock(addr(fs_m))
    while 1:
        nx = dir_next(fl.clu, fl.pos, addr(nm[0]), u64(addr(ent[0])))
        if nx == 0:
            break
        if nm[0] == 46:
            fl.pos = nx
            continue
        l: u64 = 0
        while nm[l] != 0:
            l += 1
        rl = 9 + l + 1
        if done + rl > n:
            break
        vstore64(u64(addr(rec[0])), le32(u64(addr(ent[28]))))
        rec[8] = u8((ent[11] >> 4) & 1)
        memcpy(u64(addr(rec[9])), u64(addr(nm[0])), l + 1)
        if copy_user(ubuf + done, u64(addr(rec[0])), rl) != 0:
            mutex_unlock(addr(fs_m))
            return EFAULT
        done += rl
        fl.pos = nx
    mutex_unlock(addr(fs_m))
    return done

# ---- tty: one input queue fed by PS/2 and COM1 interrupts; lines are handed out on '\n' ----
tty_buf: u8[512]
tty_head: u64
tty_count: u64
tty_lines: u64
tty_q: WaitQ
kbd_irqs: u64
com_irqs: u64

# The line discipline is hot-swappable (Phase 2.4): v0 below is built in; tty1.ko / tty2.ko provide the
# same three entry points. tty_fp_* are swapped together under rq_lock, and every call is made under
# rq_lock, so no CPU can be inside the old code once the swap's critical section ends.
tty_fp_push: u64
tty_fp_lines: u64
tty_fp_pop: u64
tty_pushed: u64
tty_popped: u64
tty_erased: u64
tty_dropped: u64

def kcur_pid() -> u64:
    p = cur_proc()
    if u64(p) == 0:
        return 0
    return p.pid

@patchable
def sys_getpid() -> u64:
# live-patch target (Phase 2.5): entry is a 5-byte nop that livepatch.fsi turns into a jmp
    return kcur_pid()

def kcur_ppid() -> u64:
    p = cur_proc()
    if u64(p) == 0:
        return 0
    return p.ppid

@patchable
def sys_getppid() -> u64:
# second live-patch site (no-restart gap 3)
    return kcur_ppid()

def tty0_push(c: u64) -> u64:
    if c == 8 or c == 127:
        if tty_count > 0 and tty_buf[(tty_head + tty_count - 1) & 511] != 10:
            tty_count -= 1
        return 2
    if tty_count >= 511:
        return 4
    tty_buf[(tty_head + tty_count) & 511] = u8(c)
    tty_count += 1
    if c == 10:
        tty_lines += 1
        return 1
    return 0

def tty0_lines() -> u64:
    return tty_lines

def tty0_pop() -> u64:
    c = u64(tty_buf[tty_head])
    tty_head = (tty_head + 1) & 511
    tty_count -= 1
    if c == 10:
        tty_lines -= 1
    return c

@unsafe
def tty0_save(st: u64, cap: u64) -> u64:
    if cap < 24 + tty_count:
        return 0
    vstore64(st, 0x54545931)
    vstore64(st + 8, tty_count)
    vstore64(st + 16, tty_lines)
    i: u64 = 0
    while i < tty_count:
        vstore8(st + 24 + i, u64(tty_buf[(tty_head + i) & 511]))
        i += 1
    return 24 + tty_count

@unsafe
def tty0_restore(st: u64, n: u64) -> u64:
    if n < 24 or vload64(st) != 0x54545931:
        return 0
    k = vload64(st + 8)
    if k > 511 or n != 24 + k:
        return 0
    tty_head = 0
    tty_count = k
    tty_lines = vload64(st + 16)
    i: u64 = 0
    while i < k:
        tty_buf[i] = u8(vload8(st + 24 + i))
        i += 1
    return 1

@unsafe
def tty_push_l(c: u64):
# rq_lock held (interrupt context)
    tty_pushed += 1
    if tty_fp_push == 0:
        tty_fp_push = u64(tty0_push)
        tty_fp_lines = u64(tty0_lines)
        tty_fp_pop = u64(tty0_pop)
    fl = call_ptr(tty_fp_push, c)
    if fl & 2:
        tty_erased += 1
    if fl & 4:
        tty_dropped += 1
    if fl & 1:
        wq_wake_all_l(addr(tty_q))

def tty_input(c: u64):
    spin_lock(addr(rq_lock))
    tty_push_l(c)
    spin_unlock(addr(rq_lock))

@unsafe
def tty_read(ubuf: u64, n: u64) -> u64:
    kb: u8[256]
    if n > 256:
        n = 256
    f = irq_save()
    spin_lock(addr(rq_lock))
    if tty_fp_push == 0:
        tty_fp_push = u64(tty0_push)
        tty_fp_lines = u64(tty0_lines)
        tty_fp_pop = u64(tty0_pop)
    while call_ptr(tty_fp_lines) == 0:
        if kx_should_park():
            spin_unlock(addr(rq_lock))
            irq_restore(f)
            kx_park()
            f = irq_save()
            spin_lock(addr(rq_lock))
            continue
        wq_block_l(addr(tty_q))
    got: u64 = 0
    while got < n:
        c = call_ptr(tty_fp_pop)
        tty_popped += 1
        kb[got] = u8(c)
        got += 1
        if c == 10:
            break
    spin_unlock(addr(rq_lock))
    irq_restore(f)
    # echo what the program consumed (cooked mode, echo at read time)
    spin_lock_irq_con()
    i: u64 = 0
    while i < got:
        putc(kb[i])
        i += 1
    spin_unlock_irq_con()
    if copy_user(ubuf, u64(addr(kb[0])), got) != 0:
        return EFAULT
    return got

con_irqf: u64

def spin_lock_irq_con():
    f = irq_save()
    spin_lock(addr(con_lock))
    con_irqf = f

def spin_unlock_irq_con():
    f = con_irqf
    spin_unlock(addr(con_lock))
    irq_restore(f)

SC_MAP: u8[58] = [0, 27, 49, 50, 51, 52, 53, 54, 55, 56, 57, 48, 45, 61, 8, 9, 113, 119, 101, 114, 116, 121, 117, 105, 111, 112, 91, 93, 10, 0, 97, 115, 100, 102, 103, 104, 106, 107, 108, 59, 39, 96, 0, 92, 122, 120, 99, 118, 98, 110, 109, 44, 46, 47, 0, 42, 0, 32]
SC_SHIFT: u8[58] = [0, 27, 33, 64, 35, 36, 37, 94, 38, 42, 40, 41, 95, 43, 8, 9, 81, 87, 69, 82, 84, 89, 85, 73, 79, 80, 123, 125, 10, 0, 65, 83, 68, 70, 71, 72, 74, 75, 76, 58, 34, 126, 0, 124, 90, 88, 67, 86, 66, 78, 77, 60, 62, 63, 0, 42, 0, 32]
kbd_shift: u64
kbd_e0: u64
VEC_KBD = 0x42
VEC_COM1 = 0x43

def kbd_decode_l(sc: u64):
# in-kernel scan-code decoder (rq_lock held); the input service (kbd.fsi) runs the same in ring 3
    if sc == 0xE0:
        kbd_e0 = 1
        return
    if kbd_e0:
        kbd_e0 = 0
        return
    if sc == 0x2A or sc == 0x36:
        kbd_shift = 1
    elif sc == 0xAA or sc == 0xB6:
        kbd_shift = 0
    elif sc < 58:
        c = u64(SC_MAP[sc])
        if kbd_shift:
            c = u64(SC_SHIFT[sc])
        if c != 0:
            tty_push_l(c)

@interrupt
@unsafe
def on_kbd(frame: ptr[u64]):
    atomic_fetch_add(addr(kbd_irqs), 1, acq_rel)
    while inb(0x64) & 1:
        sc = u64(inb(0x60))
        if kb_svc:
            kbd_event(0, sc)
        else:
            spin_lock(addr(rq_lock))
            kbd_decode_l(sc)
            spin_unlock(addr(rq_lock))
    lapic_eoi()

@interrupt
@unsafe
def on_com1(frame: ptr[u64]):
    atomic_fetch_add(addr(com_irqs), 1, acq_rel)
    while inb(0x3FD) & 1:
        c = u64(inb(0x3F8))
        if kb_svc:
            kbd_event(1, c)
            continue
        if c == 13:
            c = 10
        tty_input(c)
    lapic_eoi()

@unsafe
def input_init():
# PS/2: flush, enable the first port's interrupt (config bit 0), keep scan-code translation (set 1)
    while inb(0x64) & 1:
        inb(0x60)
    outb(0x64, 0x20)
    g = 0
    while (inb(0x64) & 1) == 0 and g < 100000:
        g += 1
    cfg = inb(0x60)
    outb(0x64, 0x60)
    outb(0x60, (cfg | 0x41) & 0xEF)
    outb(0x64, 0xAE)
    idt_set(VEC_KBD, u64(on_kbd), 0)
    ioapic_route(1, VEC_KBD)
    # COM1: receive-data interrupt, OUT2 gates the IRQ line
    idt_set(VEC_COM1, u64(on_com1), 0)
    ioapic_route(4, VEC_COM1)
    outb(0x3FC, inb(0x3FC) | 8)
    outb(0x3F9, 1)

@unsafe
def input_off():
    outb(0x3F9, 0)
    ioapic_mask(4)
    ioapic_mask(1)

# ---- address spaces: clone (fork), user page mapping (brk/mmap) ----
@unsafe
def clone_tbl(t: u64, lvl: u64) -> u64:
    nt = frame_alloc()
    memset(p2v(nt), 0, PAGE)
    i = 0
    while i < 512:
        v = vload64(p2v(t) + i * 8)
        if v & PTE_P:
            if lvl == 1:
                nf = frame_alloc()
                memcpy(p2v(nf), p2v(v & ADDR_MASK), PAGE)
                vstore64(p2v(nt) + i * 8, nf | (v & ~ADDR_MASK))
            else:
                vstore64(p2v(nt) + i * 8, clone_tbl(v & ADDR_MASK, lvl - 1) | (v & ~ADDR_MASK))
        i += 1
    return nt

@unsafe
def as_clone(src: u64) -> u64:
    pml4 = as_new()
    i = 0
    while i < 256:
        v = vload64(p2v(src) + i * 8)
        if v & PTE_P:
            vstore64(p2v(pml4) + i * 8, clone_tbl(v & ADDR_MASK, 3) | (v & ~ADDR_MASK))
        i += 1
    return pml4

@unsafe
def user_map_zero(pml4: u64, va: u64) -> u64:
    e = user_pte(pml4, va)
    if (vload64(e) & PTE_P) == 0:
        f = frame_alloc()
        if f == 0:
            return 0
        memset(p2v(f), 0, PAGE)
        vstore64(e, f | PTE_P | PTE_W | PTE_U | PTE_NX)
    return 1

@unsafe
def user_unmap(pml4: u64, va: u64):
    if user_page_ok(pml4, va, 0) == 0:
        return
    e = user_pte(pml4, va)
    v = vload64(e)
    vstore64(e, 0)
    invlpg(va)  # only this single-threaded process uses pml4, on this CPU
    pcid_forget(pml4)  # CPUs it ran on before may still cache va under its PCID
    frame_free(v & ADDR_MASK)

def page_up(v: u64) -> u64:
    return (v + PAGE - 1) & ~u64(PAGE - 1)

# ---- process lifecycle ----
def user_main(arg: u64):
    enter_ring3(arg, USTACK_TOP - 8)

def map_ustack(pml4: u64):
    i = 0
    while i < USTACK_PAGES:
        user_map_zero(pml4, USTACK_TOP - (i + 1) * PAGE)
        i += 1

@unsafe
def proc_new(ppid: u64) -> ptr[Proc]:
    p = ptr[Proc](kmalloc(sizeof(Proc)))
    memset(u64(p), 0, sizeof(Proc))
    p.ppid = ppid
    p.state = P_ALIVE
    p.mmap_next = MMAP_BASE
    return p

@unsafe
def proc_spawn_args(img: u64, len: u64, args: ptr[u8]) -> nptr[Thread]:
    vc = image_verify(img, len, SEAL_USER)
    if vc == 0:
        seal_log("kernel-supplied image")
        return 0
    pml4 = as_new()
    iend: u64 = 0
    entry = elf_load(pml4, img, len, u64(addr(iend)))
    if entry == 0:
        as_free(pml4)
        return 0
    map_ustack(pml4)
    p = proc_new(0)
    p.pml4 = pml4
    p.caps = vc & CAPB_ALL
    dk = cur_thread().spawn_drv  # per spawning thread: a concurrent ordinary spawn is never a driver
    p.drv = dk
    p.brk_base = page_up(iend)
    p.brk = p.brk_base
    p.fds[0] = console()
    p.fds[1] = console()
    p.fds[2] = console()
    p.fdr[0] = CAP_ALL
    p.fdr[1] = CAP_ALL
    p.fdr[2] = CAP_ALL
    if dk == 0 and fs_root != 0:
        rt = file_new(F_DIR)
        rt.clu = fs_root
        p.fds[CAP_ROOT_FD] = u64(rt)
        p.fdr[CAP_ROOT_FD] = CAP_ALL
    i: u64 = 0
    while i < 127 and args[i] != 0:
        p.args[i] = args[i]
        i += 1
    t = thread_new(u64(user_main), entry)
    t.cr3 = pml4
    t.user = 1
    t.proc = u64(p)
    p.pid = t.id
    p.thread = u64(t)
    proc_insert(p)
    thread_start_ready(t)
    return t

def proc_spawn(img: u64, len: u64) -> nptr[Thread]:
    return proc_spawn_args(img, len, "")

def proc_spawn_ok(img: u64, len: u64) -> ptr[Thread]:
# kernel self-tests: spawn an image that must load
    t = proc_spawn(img, len)
    if t == 0:
        panic("proc_spawn: image rejected by ELF loader")
    return t

@unsafe
def proc_destroy(p: ptr[Proc]):
# p is a zombie whose thread is dead and which is no longer in the table
    t = ptr[Thread](p.thread)
    thread_free(t)
    as_free(p.pml4)
    sb_clear(p.pid)
    kfree(u64(p))

@unsafe
def proc_reap(t: ptr[Thread]) -> u64:
# kernel-side wait for a process the kernel spawned
    while atomic_load(addr(t.state), acquire) != T_DEAD:
        yield_cpu()
    p = ptr[Proc](t.proc)
    code = p.exit_code
    f = irq_save()
    spin_lock(addr(rq_lock))
    proc_remove_l(p)
    spin_unlock(addr(rq_lock))
    irq_restore(f)
    proc_destroy(p)
    return code

@unsafe
def proc_exit(code: u64):
    t = cur_thread()
    p = ptr[Proc](t.proc)
    if p.drv == DRV_KBD:
        kbd_died()
    elif p.drv == DRV_NVME:
        nvs_died()
    elif p.drv != 0:
        drv_died(p)
    i = 0
    while i < NFD:
        file_put(p.fds[i])
        p.fds[i] = 0
        p.fdr[i] = 0
        i += 1
    cli()
    spin_lock(addr(rq_lock))
    p.exit_code = code
    t.exit_code = code
    p.state = P_ZOMBIE
    # orphans go to the kernel (ppid 0), which reaps them in proc_reap_orphans (driver processes
    # are reaped by their supervisor only)
    i = 0
    while i < MAX_PROCS:
        if procs[i] != 0 and ptr[Proc](procs[i]).ppid == p.pid:
            ptr[Proc](procs[i]).ppid = 0
        i += 1
    par = proc_find_l(p.ppid)
    if u64(par) != 0:
        wq_wake_all_l(addr(par.childq))
    schedule_l(1)
    panic("dead process resumed")

@unsafe
def proc_reap_orphans() -> u64:
    n: u64 = 0
    while 1:
        f = irq_save()
        spin_lock(addr(rq_lock))
        z: u64 = 0
        i = 0
        while i < MAX_PROCS:
            q = procs[i]
            if q != 0 and ptr[Proc](q).ppid == 0 and ptr[Proc](q).drv == 0 and ptr[Proc](q).state == P_ZOMBIE and ptr[Thread](ptr[Proc](q).thread).state == T_DEAD:
                procs[i] = 0
                z = q
                break
            i += 1
        spin_unlock(addr(rq_lock))
        irq_restore(f)
        if z == 0:
            return n
        proc_destroy(ptr[Proc](z))
        n += 1

@unsafe
def sys_fork(frame: u64) -> u64:
    t = cur_thread()
    p = ptr[Proc](t.proc)
    c = proc_new(p.pid)
    c.pml4 = as_clone(p.pml4)
    c.brk_base = p.brk_base
    c.brk = p.brk
    c.mmap_next = p.mmap_next
    c.caps = p.caps
    memcpy(u64(addr(c.args[0])), u64(addr(p.args[0])), 128)
    i = 0
    while i < NFD:
        c.fds[i] = file_get(p.fds[i])
        c.fdr[i] = p.fdr[i]
        i += 1
    ct = thread_new(fork_child_entry_addr(), 0)
    # the child's kernel stack: its own copy of the syscall frame at the top, the switch frame below it
    # (move the switch frame down first: the two regions overlap)
    cf = ct.kstack_top - SF_SIZE
    sp = cf - 72
    i = 0
    while i < 9:
        vstore64(sp + i * 8, vload64(ct.kstack_top - 72 + i * 8))
        i += 1
    memcpy(cf, frame, SF_SIZE)
    vstore64(sp + 16, cf)  # r13 = arg = frame
    ct.rsp = sp
    ct.cr3 = c.pml4
    ct.user = 1
    ct.proc = u64(c)
    fpu_save(ct.fpu)  # the parent's live FPU/SSE registers
    c.pid = ct.id
    c.thread = u64(ct)
    if proc_insert(c) == 0:
        as_free(c.pml4)
        thread_free_new(ct)
        kfree(u64(c))
        return ENOMEM
    nforks += 1
    sb_inherit(p.pid, c.pid)
    thread_start_ready(ct)
    return c.pid

def thread_free_new(t: ptr[Thread]):
    t.state = T_DEAD
    thread_free(t)

@unsafe
def sys_exec(frame: u64, upath: u64, uargs: u64, sb: u64) -> u64:
    path: u8[128]
    args: u8[128]
    if copy_str_user(u64(addr(path[0])), upath, 128) > 127:
        return EFAULT
    args[0] = 0
    if uargs != 0 and copy_str_user(u64(addr(args[0])), uargs, 128) > 127:
        return EFAULT
    if root_cap(cur_proc()) == 0:
        cap_deny(cur_proc(), "exec", addr(path[0]))
        return EACCES
    if path_ok(path[:]) == 0:
        return EACCES
    sz: u64 = 0
    img = fs_read_file(addr(path[0]), addr(sz))
    if img == 0:
        return ENOENT
    vc = image_verify(img, sz, SEAL_USER)
    if vc == 0:
        seal_log(addr(path[0]))
        kfree(img)
        return ENOEXEC
    pml4 = as_new()
    iend: u64 = 0
    entry = elf_load(pml4, img, sz, u64(addr(iend)))
    kfree(img)
    if entry == 0:
        as_free(pml4)
        return ENOEXEC
    map_ustack(pml4)
    t = cur_thread()
    p = ptr[Proc](t.proc)
    old = p.pml4
    f = irq_save()
    p.pml4 = pml4
    t.cr3 = pml4
    write_cr3(pml4)
    irq_restore(f)
    as_free(old)
    p.brk_base = page_up(iend)
    p.brk = p.brk_base
    p.mmap_next = MMAP_BASE
    p.caps = p.caps & vc & CAPB_ALL  # never more than the parent held
    memcpy(u64(addr(p.args[0])), u64(addr(args[0])), 128)
    # fresh registers: rip = entry, rsp = top of the new stack, everything else zero
    memset(frame, 0, SF_SIZE)
    vstore64(frame + SF_RIP * 8, entry)
    vstore64(frame + SF_RSP * 8, USTACK_TOP - 8)
    vstore64(frame + SF_RFLAGS * 8, 0x202)
    fpu_restore(fpu_template)
    nexecs += 1
    if sb:
        sandbox_enter(p, path[:])
    return 0

@unsafe
def sys_wait(pid: u64, ustatus: u64) -> u64:
    me = cur_proc()
    f = irq_save()
    spin_lock(addr(rq_lock))
    while 1:
        found: u64 = 0
        any: u64 = 0
        i = 0
        while i < MAX_PROCS:
            q = procs[i]
            if q != 0 and ptr[Proc](q).ppid == me.pid and (pid == 0xFFFFFFFFFFFFFFFF or ptr[Proc](q).pid == pid):
                any = 1
                if ptr[Proc](q).state == P_ZOMBIE and ptr[Thread](ptr[Proc](q).thread).state == T_DEAD:
                    procs[i] = 0
                    found = q
                    break
            i += 1
        if found != 0:
            spin_unlock(addr(rq_lock))
            irq_restore(f)
            c = ptr[Proc](found)
            cpid = c.pid
            code = c.exit_code
            if ustatus != 0 and (user_ptr_ok(ustatus, 8) == 0 or copy_user(ustatus, u64(addr(code)), 8) != 0):
            # status not delivered: put the zombie back so a later wait can still reap it
                f = irq_save()
                spin_lock(addr(rq_lock))
                procs[i] = found
                spin_unlock(addr(rq_lock))
                irq_restore(f)
                return EFAULT
            proc_destroy(c)
            return cpid
        if any == 0:
            spin_unlock(addr(rq_lock))
            irq_restore(f)
            return ECHILD
        if kx_should_park():
            spin_unlock(addr(rq_lock))
            irq_restore(f)
            kx_park()
            f = irq_save()
            spin_lock(addr(rq_lock))
            continue
        wq_block_l(addr(me.childq))
    return 0

def sys_brk(nb: u64) -> u64:
    p = cur_proc()
    if nb == 0 or nb < p.brk_base or nb > BRK_MAX:
        return p.brk
    a = page_up(p.brk)
    b = page_up(nb)
    while a < b:
        if user_map_zero(p.pml4, a) == 0:
            return p.brk
        a += PAGE
    while b < a:
        user_unmap(p.pml4, b)
        b += PAGE
    p.brk = nb
    return nb

def sys_mmap(len: u64) -> u64:
    p = cur_proc()
    if len == 0 or len > 0x40000000:
        return EINVAL
    n = page_up(len)
    if p.mmap_next + n > MMAP_TOP:
        return ENOMEM
    va = p.mmap_next
    i: u64 = 0
    while i < n:
        if user_map_zero(p.pml4, va + i) == 0:
        # roll back the pages mapped so far; the VA range is not consumed
            while i > 0:
                i -= PAGE
                user_unmap(p.pml4, va + i)
            return ENOMEM
        i += PAGE
    p.mmap_next += n + PAGE  # one unmapped guard page between mappings
    return va

def sys_munmap(va: u64, len: u64) -> u64:
    p = cur_proc()
    if (va & (PAGE - 1)) != 0 or va < MMAP_BASE or va + len > MMAP_TOP or va + len < va:
        return EINVAL
    # DMA pages stay pinned while the device may still use them (freed only with the process)
    if p.dma_n != 0 and va < p.dma_va + p.dma_n * PAGE and va + len > p.dma_va:
        return EPERM
    i: u64 = 0
    while i < len:
        user_unmap(p.pml4, va + i)
        i += PAGE
    return 0

def sys_open(upath: u64) -> u64:
    path: u8[256]
    if copy_str_user(u64(addr(path[0])), upath, 256) > 255:
        return EFAULT
    p = cur_proc()
    if root_cap(p) == 0:
        cap_deny(p, "open", addr(path[0]))
        return EACCES
    if path_ok(path[:]) == 0:
        return EACCES
    return open_install(p, fs_root, addr(path[0]), p.fdr[CAP_ROOT_FD])

@unsafe
def sys_stat(upath: u64, ubuf: u64) -> u64:
    st: u64[2]
    path: u8[256]
    if copy_str_user(u64(addr(path[0])), upath, 256) > 255:
        return EFAULT
    if root_cap(cur_proc()) == 0:
        cap_deny(cur_proc(), "stat", addr(path[0]))
        return EACCES
    if path_ok(path[:]) == 0:
        return EACCES
    fl = vfs_open(addr(path[0]))
    if fl == 0:
        return ENOENT
    st[0] = ptr[File](fl).size
    st[1] = u64(ptr[File](fl).type == F_DIR)
    file_put(fl)
    if user_ptr_ok(ubuf, 16) == 0 or copy_user(ubuf, u64(addr(st[0])), 16) != 0:
        return EFAULT
    return 0

def sys_read(fd: u64, ubuf: u64, n: u64) -> u64:
    if user_ptr_ok(ubuf, n) == 0:
        return EFAULT
    fl = fd_get(cur_proc(), fd)
    if u64(fl) == 0:
        return EBADF
    if (cur_proc().fdr[fd] & CAP_READ) == 0:
        return EPERM
    if fl.type == F_CONSOLE:
        return tty_read(ubuf, n)
    if fl.type == F_FILE:
        return file_read(fl, ubuf, n)
    return EISDIR

def sys_write(fd: u64, ubuf: u64, n: u64) -> u64:
    if user_ptr_ok(ubuf, n) == 0:
        return EFAULT
    fl = fd_get(cur_proc(), fd)
    if u64(fl) == 0:
        return EBADF
    if (cur_proc().fdr[fd] & CAP_WRITE) == 0:
        return EPERM
    if fl.type == F_CONSOLE:
        return con_write_user(ubuf, n)
    return EROFS

def sys_getdents(fd: u64, ubuf: u64, n: u64) -> u64:
    if user_ptr_ok(ubuf, n) == 0:
        return EFAULT
    fl = fd_get(cur_proc(), fd)
    if u64(fl) == 0:
        return EBADF
    if (cur_proc().fdr[fd] & CAP_READ) == 0:
        return EPERM
    if fl.type != F_DIR:
        return ENOTDIR
    return dir_read(fl, ubuf, n)

def sys_close(fd: u64) -> u64:
    p = cur_proc()
    if fd >= NFD or p.fds[fd] == 0:
        return EBADF
    file_put(p.fds[fd])
    p.fds[fd] = 0
    p.fdr[fd] = 0
    return 0

# ---- capabilities (Phase 5.1): every fd is a handle with rights; no ambient path authority ----
def fd_free(p: ptr[Proc]) -> u64:
    fd: u64 = 0
    while fd < NFD and p.fds[fd] != 0:
        fd += 1
    return fd

def root_cap(p: ptr[Proc]) -> u64:
# absolute paths resolve only through a held root-directory capability (fd 3 with LOOKUP)
    fl = fd_get(p, CAP_ROOT_FD)
    if u64(fl) == 0:
        return 0
    if fl.type != F_DIR or fl.clu != fs_root or (p.fdr[CAP_ROOT_FD] & CAP_LOOKUP) == 0:
        return 0
    return 1

def path_ok(s: slice[u8]) -> u64:
# no "." or ".." components: a directory capability cannot be escaped upward
    n = len(s)
    i: u64 = 0
    while i < n and s[i] != 0:
        while i < n and s[i] == 47:
            i += 1
        j = i
        while j < n and s[j] != 0 and s[j] != 47:
            j += 1
        if j - i == 1 and s[i] == 46:
            return 0
        if j - i == 2 and s[i] == 46 and s[i + 1] == 46:
            return 0
        i = j
    return 1

cap_denials: u64

# sandbox state lives beside the Proc table (keyed by pid) so Proc stays in its 512-byte slab class
sb_pid: u64[64]  # 0 = free slot
sb_name: u8[1024]  # 16 bytes per slot: the app's name
sb_lock: u64

def sb_slot(pid: u64) -> u64:
    i: u64 = 0
    while i < MAX_PROCS:
        if sb_pid[i] == pid:
            return i
        i += 1
    return MAX_PROCS

def sb_new(pid: u64) -> u64:
# a free slot always exists: there are as many slots as processes
    f = irq_save()
    spin_lock(addr(sb_lock))
    i: u64 = 0
    while i < MAX_PROCS and sb_pid[i] != 0:
        i += 1
    sb_pid[i] = pid
    spin_unlock(addr(sb_lock))
    irq_restore(f)
    atomic_fetch_add(addr(sb_live), 1, acq_rel)
    return i

def sb_inherit(ppid: u64, pid: u64):
# a sandboxed process forks a sandboxed child with the same name
    sp = sb_slot(ppid)
    if sp == MAX_PROCS:
        return
    n = sb_new(pid)
    k: u64 = 0
    while k < 16:
        sb_name[n * 16 + k] = sb_name[sp * 16 + k]
        k += 1

def sb_clear(pid: u64):
    sp = sb_slot(pid)
    if sp == MAX_PROCS:
        return
    sb_pid[sp] = 0
    atomic_fetch_add(addr(sb_live), 0xFFFFFFFFFFFFFFFF, acq_rel)

def sandbox_enter(p: ptr[Proc], path: slice[u8]):
# exec through a manifest: the new image keeps only the handles the launcher installed; the
# ambient root capability (fd 3) is dropped, so every other path is out of reach
    if p.fds[CAP_ROOT_FD] != 0:
        file_put(p.fds[CAP_ROOT_FD])
        p.fds[CAP_ROOT_FD] = 0
        p.fdr[CAP_ROOT_FD] = 0
    sp = sb_slot(p.pid)
    if sp == MAX_PROCS:
        sp = sb_new(p.pid)
    b: u64 = 0
    i: u64 = 0
    while i < len(path) and path[i] != 0:
        if path[i] == 47:
            b = i + 1
        i += 1
    k: u64 = 0
    while k < 15 and b + k < len(path) and path[b + k] != 0:
        sb_name[sp * 16 + k] = path[b + k]
        k += 1
    sb_name[sp * 16 + k] = 0
    n: u64 = 0
    i = 0
    while i < NFD:
        if i > 2 and p.fds[i] != 0:
            n += 1
        i += 1
    sandboxes += 1
    puts("sandbox: pid ")
    put_u64(p.pid)
    puts(" (")
    puts(addr(sb_name[sp * 16]))
    puts(") runs with ")
    put_u64(n)
    puts(" manifest handles, no root capability\n")

def sys_restrict(fd: u64, rights: u64) -> u64:
# narrow a handle's rights in place (never widen)
    p = cur_proc()
    if fd >= NFD or p.fds[fd] == 0:
        return EBADF
    if (rights & (~p.fdr[fd])) != 0:
        return EPERM
    p.fdr[fd] = rights
    return 0

def cap_deny(p: ptr[Proc], what: ptr[u8], path: ptr[u8]):
    cap_denials += 1
    sp = sb_slot(p.pid)
    if sp != MAX_PROCS:
        sb_denials += 1
        puts("sandbox: pid ")
        put_u64(p.pid)
        puts(" (")
        puts(addr(sb_name[sp * 16]))
        puts(") denied ")
        puts(what)
        puts(" ")
        puts(path)
        puts(": not in its manifest\n")
        return
    puts("cap: pid ")
    put_u64(p.pid)
    puts(" denied ")
    puts(what)
    puts(" (no root capability)\n")

def open_install(p: ptr[Proc], c: u64, path: ptr[u8], r: u64) -> u64:
    fd = fd_free(p)
    if fd == NFD:
        return EMFILE
    fl = vfs_open_at(c, path)
    if fl == 0:
        return ENOENT
    p.fds[fd] = fl
    p.fdr[fd] = r & (CAP_READ | CAP_LOOKUP | CAP_DUP | CAP_XFER)
    return fd

def sys_openat(dirfd: u64, upath: u64) -> u64:
    path: u8[256]
    if copy_str_user(u64(addr(path[0])), upath, 256) > 255:
        return EFAULT
    p = cur_proc()
    d = fd_get(p, dirfd)
    if u64(d) == 0:
        return EBADF
    if d.type != F_DIR:
        return ENOTDIR
    if (p.fdr[dirfd] & CAP_LOOKUP) == 0:
        return EPERM
    if path[0] == 47 or path_ok(path[:]) == 0:
        return EACCES
    return open_install(p, d.clu, addr(path[0]), p.fdr[dirfd])

def sys_dup(fd: u64, rights: u64) -> u64:
# a new handle to the same object with a subset of the rights: dup can only narrow
    p = cur_proc()
    if fd >= NFD or p.fds[fd] == 0:
        return EBADF
    old = p.fdr[fd]
    if (old & CAP_DUP) == 0 or (rights & (~old)) != 0:
        return EPERM
    n = fd_free(p)
    if n == NFD:
        return EMFILE
    p.fds[n] = file_get(p.fds[fd])
    p.fdr[n] = rights
    return n

def sys_rights(fd: u64) -> u64:
    p = cur_proc()
    if fd >= NFD or p.fds[fd] == 0:
        return EBADF
    return p.fdr[fd]

def user_ptr_ok(va: u64, len: u64) -> u64:
# only the range check: the copy itself survives unmapped pages through the #PF fixup
    return va < USER_TOP and va + len <= USER_TOP and va + len >= va

def con_write_user(p: u64, n: u64) -> u64:
    kb: u8[256]
    done: u64 = 0
    while done < n:
        k = n - done
        if k > 256:
            k = 256
        if copy_user(u64(addr(kb[0])), p + done, k) != 0:
            return EFAULT
        spin_lock_irq_con()
        i = 0
        while i < k:
            putc(kb[i])
            i += 1
        spin_unlock_irq_con()
        done += k
    return n

@export
@unsafe
def syscall_dispatch(nr: u64, a: u64, b: u64, c: u64, d: u64, frame: u64) -> u64:
# entered with interrupts off (FMASK); the asm exit path disables them again before sysret
    atomic_fetch_add(addr(syscalls), 1, acq_rel)
    sti()
    st = cur_thread()
    st.sc[0] = nr
    st.sc[1] = a
    st.sc[2] = b
    st.sc[3] = c
    st.sc[4] = d
    if kx_should_park():
        kx_park()
    need = sys_need(nr)
    if need != 0:
        sp = cur_proc()
        if (sp.caps & need) == 0:
            return cap_sys_deny(sp, nr, need)
    if nr == 0:
        return sys_read(a, b, c)
    if nr == 1:
        return sys_write(a, b, c)
    if nr == 2:
        return sys_open(a)
    if nr == 3:
        return sys_close(a)
    if nr == 4:
        return sys_stat(a, b)
    if nr == 9:
        return sys_mmap(b)
    if nr == 11:
        return sys_munmap(a, b)
    if nr == 12:
        return sys_brk(a)
    if nr == 24:
        yield_cpu()
        return 0
    if nr == 39:
        return sys_getpid()
    if nr == 57:
        return sys_fork(frame)
    if nr == 59:
        return sys_exec(frame, a, b, 0)
    if nr == 60:
        proc_exit(a & 0xFF)
    if nr == 61:
        return sys_wait(a, b)
    if nr == 420:
        return sys_dup(a, b)
    if nr == 421:
        return sys_openat(a, b)
    if nr == 422:
        return sys_rights(a)
    if nr == 426:
        return sys_exec(frame, a, b, 1)
    if nr == 427:
        return sys_restrict(a, b)
    if nr == 423:
        return sys_chan_create(a)
    if nr == 424:
        return sys_chan_send(a, b, c, d)
    if nr == 425:
        return sys_chan_recv(a, b, c, d)
    if nr == 78:
        return sys_getdents(a, b, c)
    if nr == 96:
        return lapic_ticks
    if nr == 97:
        return lapic_cpu()
    if nr == 110:
        return sys_getppid()
    if nr == 200:
    # self-test probes: 1 = kernel read of user memory without stac, 2 = kernel call into user code
        if user_ptr_ok(b, 8) == 0:
            return EFAULT
        if a == 1:
            return smap_probe(b)
        if a == 2:
            smep_probing = 1
            r = smep_probe(b)
            smep_probing = 0
            return r
        if a == 3:
            return u64(smep_on) | (u64(smap_on) << 1)
        return EINVAL
    if nr == 409:
        return sys_kexec(frame, a)
    if nr == 410:
        return kx_gen
    if nr >= 411 and nr <= 413:
        return sys_kbd(nr, a, b, c, d)
    if nr >= 414 and nr <= 418:
        return sys_nvs(nr, a, b, c)
    if nr >= 400 and nr <= 408:
        return sys_drv(nr, a, b, c)
    if nr == 300:
    # getargs(buf, n): the argument string given to exec
        p = cur_proc()
        l: u64 = 0
        while l < 127 and p.args[l] != 0:
            l += 1
        if l + 1 > b:
            return EINVAL
        if user_ptr_ok(a, l + 1) == 0 or copy_user(a, u64(addr(p.args[0])), l + 1) != 0:
            return EFAULT
        return l
    return ENOSYS

def user_kill(name: ptr[u8], vec: u64, rip: u64, addr_: u64):
# called from an exception taken in ring 3: report, and the process exits with 0x100 + vector
    t = cur_thread()
    atomic_fetch_add(addr(user_kills), 1, acq_rel)
    spin_lock(addr(con_lock))
    puts("user fault: pid ")
    put_u64(t.id)
    puts(" ")
    puts(name)
    puts(" at rip=")
    put_hex(rip)
    puts(" addr=")
    put_hex(addr_)
    puts(" -> killed\n")
    spin_unlock(addr(con_lock))
    proc_exit(0x100 + vec)

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
    args: u8[128]
    childq: WaitQ
    drv: u64
    dma_pa: u64
    dma_n: u64

procs: u64[64]
nforks: u64
nexecs: u64

def proc_find_l(pid: u64) -> ptr[Proc]:
    i = 0
    while i < MAX_PROCS:
        if procs[i] != 0 and ptr[Proc](procs[i]).pid == pid:
            return ptr[Proc](procs[i])
        i += 1
    return ptr[Proc](0)

def proc_insert(p: ptr[Proc]) -> u64:
    f = irq_save()
    spin_lock(u64(addr(rq_lock)))
    i = 0
    while i < MAX_PROCS:
        if procs[i] == 0:
            procs[i] = u64(p)
            spin_unlock(u64(addr(rq_lock)))
            irq_restore(f)
            return 1
        i += 1
    spin_unlock(u64(addr(rq_lock)))
    irq_restore(f)
    return 0

def proc_remove_l(p: ptr[Proc]):
    i = 0
    while i < MAX_PROCS:
        if procs[i] == u64(p):
            procs[i] = 0
        i += 1

def cur_proc() -> ptr[Proc]:
    return ptr[Proc](cur_thread().proc)

# ---- files ----
F_CONSOLE = 1
F_FILE = 2
F_DIR = 3

struct File:
    type: u64
    ref: u64
    clu: u64        # first cluster (file) / directory cluster
    size: u64
    pos: u64        # byte offset (file) / entry index (dir)
    ccl: u64        # cached cluster for sequential reads
    cidx: u64       # its index in the chain

console_file: u64

def file_new(type: u64) -> ptr[File]:
    fl = ptr[File](kmalloc(sizeof(File)))
    memset(u64(fl), 0, sizeof(File))
    fl.type = type
    fl.ref = 1
    return fl

def file_get(fl: u64) -> u64:
    if fl != 0:
        atomic_add64(u64(addr(ptr[File](fl).ref)), 1)
    return fl

def file_put(fl: u64):
    if fl != 0 and atomic_add64(u64(addr(ptr[File](fl).ref)), 0xFFFFFFFFFFFFFFFF) == 1:
        kfree(fl)

console_obj: File

def console() -> u64:
    # one static console File shared by every process; its huge refcount means it is never freed
    if console_file == 0:
        console_obj.type = F_CONSOLE
        console_obj.ref = 1000000000
        console_file = u64(addr(console_obj))
    return file_get(console_file)

def fd_get(p: ptr[Proc], fd: u64) -> ptr[File]:
    if fd >= NFD:
        return ptr[File](0)
    return ptr[File](p.fds[fd])

def vfs_open(path: ptr[u8]) -> u64:
    ent: u8[32]
    e = u64(addr(ent[0]))
    mutex_lock(addr(fs_m))
    r = fs_lookup(path, e)
    mutex_unlock(addr(fs_m))
    if r == 0:
        return 0
    if r == 2:
        fl = file_new(F_DIR)
        fl.clu = fs_root
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

def tty_push_l(c: u64):
    # rq_lock held (interrupt context)
    if c == 8 or c == 127:
        if tty_count > 0 and tty_buf[(tty_head + tty_count - 1) & 511] != 10:
            tty_count -= 1
        return
    if tty_count < 511:
        tty_buf[(tty_head + tty_count) & 511] = u8(c)
        tty_count += 1
        if c == 10:
            tty_lines += 1
            wq_wake_all_l(addr(tty_q))

def tty_input(c: u64):
    spin_lock(u64(addr(rq_lock)))
    tty_push_l(c)
    spin_unlock(u64(addr(rq_lock)))

def tty_read(ubuf: u64, n: u64) -> u64:
    kb: u8[256]
    if n > 256:
        n = 256
    f = irq_save()
    spin_lock(u64(addr(rq_lock)))
    while tty_lines == 0:
        wq_block_l(addr(tty_q))
    got: u64 = 0
    while got < n and tty_count > 0:
        c = tty_buf[tty_head]
        tty_head = (tty_head + 1) & 511
        tty_count -= 1
        kb[got] = c
        got += 1
        if c == 10:
            tty_lines -= 1
            break
    spin_unlock(u64(addr(rq_lock)))
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
    spin_lock(u64(addr(con_lock)))
    con_irqf = f

def spin_unlock_irq_con():
    f = con_irqf
    spin_unlock(u64(addr(con_lock)))
    irq_restore(f)

SC_MAP: u8[58] = [0, 27, 49, 50, 51, 52, 53, 54, 55, 56, 57, 48, 45, 61, 8, 9, 113, 119, 101, 114, 116, 121, 117, 105, 111, 112, 91, 93, 10, 0, 97, 115, 100, 102, 103, 104, 106, 107, 108, 59, 39, 96, 0, 92, 122, 120, 99, 118, 98, 110, 109, 44, 46, 47, 0, 42, 0, 32]
SC_SHIFT: u8[58] = [0, 27, 33, 64, 35, 36, 37, 94, 38, 42, 40, 41, 95, 43, 8, 9, 81, 87, 69, 82, 84, 89, 85, 73, 79, 80, 123, 125, 10, 0, 65, 83, 68, 70, 71, 72, 74, 75, 76, 58, 34, 126, 0, 124, 90, 88, 67, 86, 66, 78, 77, 60, 62, 63, 0, 42, 0, 32]
kbd_shift: u64
kbd_e0: u64
VEC_KBD = 0x42
VEC_COM1 = 0x43

@interrupt
def on_kbd(frame: ptr[u64]):
    atomic_add64(u64(addr(kbd_irqs)), 1)
    while inb(0x64) & 1:
        sc = u64(inb(0x60))
        if sc == 0xE0:
            kbd_e0 = 1
            continue
        if kbd_e0:
            kbd_e0 = 0
            continue
        if sc == 0x2A or sc == 0x36:
            kbd_shift = 1
        elif sc == 0xAA or sc == 0xB6:
            kbd_shift = 0
        elif sc < 58:
            c = u64(SC_MAP[sc])
            if kbd_shift:
                c = u64(SC_SHIFT[sc])
            if c != 0:
                tty_input(c)
    lapic_eoi()

@interrupt
def on_com1(frame: ptr[u64]):
    atomic_add64(u64(addr(com_irqs)), 1)
    while inb(0x3FD) & 1:
        c = u64(inb(0x3F8))
        if c == 13:
            c = 10
        tty_input(c)
    lapic_eoi()

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

def input_off():
    outb(0x3F9, 0)
    ioapic_mask(4)
    ioapic_mask(1)

# ---- address spaces: clone (fork), user page mapping (brk/mmap) ----
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

def as_clone(src: u64) -> u64:
    pml4 = as_new()
    i = 0
    while i < 256:
        v = vload64(p2v(src) + i * 8)
        if v & PTE_P:
            vstore64(p2v(pml4) + i * 8, clone_tbl(v & ADDR_MASK, 3) | (v & ~ADDR_MASK))
        i += 1
    return pml4

def user_map_zero(pml4: u64, va: u64) -> u64:
    e = user_pte(pml4, va)
    if (vload64(e) & PTE_P) == 0:
        f = frame_alloc()
        if f == 0:
            return 0
        memset(p2v(f), 0, PAGE)
        vstore64(e, f | PTE_P | PTE_W | PTE_U | PTE_NX)
    return 1

def user_unmap(pml4: u64, va: u64):
    if user_page_ok(pml4, va, 0) == 0:
        return
    e = user_pte(pml4, va)
    v = vload64(e)
    vstore64(e, 0)
    invlpg(va)              # only this single-threaded process uses pml4, on this CPU
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

def proc_new(ppid: u64) -> ptr[Proc]:
    p = ptr[Proc](kmalloc(sizeof(Proc)))
    memset(u64(p), 0, sizeof(Proc))
    p.ppid = ppid
    p.state = P_ALIVE
    p.mmap_next = MMAP_BASE
    return p

def proc_spawn_args(img: u64, len: u64, args: ptr[u8]) -> ptr[Thread]:
    pml4 = as_new()
    entry = elf_load(pml4, img, len)
    if entry == 0:
        as_free(pml4)
        return ptr[Thread](0)
    map_ustack(pml4)
    p = proc_new(0)
    p.pml4 = pml4
    p.drv = spawn_drv
    p.brk_base = page_up(elf_end)
    p.brk = p.brk_base
    p.fds[0] = console()
    p.fds[1] = console()
    p.fds[2] = console()
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

def proc_spawn(img: u64, len: u64) -> ptr[Thread]:
    return proc_spawn_args(img, len, "")

def proc_destroy(p: ptr[Proc]):
    # p is a zombie whose thread is dead and which is no longer in the table
    t = ptr[Thread](p.thread)
    thread_free(t)
    as_free(p.pml4)
    kfree(u64(p))

def proc_reap(t: ptr[Thread]) -> u64:
    # kernel-side wait for a process the kernel spawned
    while atomic_load64(u64(addr(t.state))) != T_DEAD:
        yield_cpu()
    p = ptr[Proc](t.proc)
    code = p.exit_code
    f = irq_save()
    spin_lock(u64(addr(rq_lock)))
    proc_remove_l(p)
    spin_unlock(u64(addr(rq_lock)))
    irq_restore(f)
    proc_destroy(p)
    return code

def proc_exit(code: u64):
    t = cur_thread()
    p = ptr[Proc](t.proc)
    if p.drv != 0:
        drv_died(p)
    i = 0
    while i < NFD:
        file_put(p.fds[i])
        p.fds[i] = 0
        i += 1
    cli()
    spin_lock(u64(addr(rq_lock)))
    p.exit_code = code
    t.exit_code = code
    p.state = P_ZOMBIE
    # orphans go to the kernel (ppid 0), which reaps them in proc_reap_orphans
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

def proc_reap_orphans() -> u64:
    n: u64 = 0
    while 1:
        f = irq_save()
        spin_lock(u64(addr(rq_lock)))
        z: u64 = 0
        i = 0
        while i < MAX_PROCS:
            q = procs[i]
            if q != 0 and ptr[Proc](q).ppid == 0 and ptr[Proc](q).state == P_ZOMBIE and ptr[Thread](ptr[Proc](q).thread).state == T_DEAD:
                procs[i] = 0
                z = q
                break
            i += 1
        spin_unlock(u64(addr(rq_lock)))
        irq_restore(f)
        if z == 0:
            return n
        proc_destroy(ptr[Proc](z))
        n += 1

def sys_fork(frame: u64) -> u64:
    t = cur_thread()
    p = ptr[Proc](t.proc)
    c = proc_new(p.pid)
    c.pml4 = as_clone(p.pml4)
    c.brk_base = p.brk_base
    c.brk = p.brk
    c.mmap_next = p.mmap_next
    memcpy(u64(addr(c.args[0])), u64(addr(p.args[0])), 128)
    i = 0
    while i < NFD:
        c.fds[i] = file_get(p.fds[i])
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
    vstore64(sp + 16, cf)              # r13 = arg = frame
    ct.rsp = sp
    ct.cr3 = c.pml4
    ct.user = 1
    ct.proc = u64(c)
    fpu_save(ct.fpu)                   # the parent's live FPU/SSE registers
    c.pid = ct.id
    c.thread = u64(ct)
    if proc_insert(c) == 0:
        as_free(c.pml4)
        thread_free_new(ct)
        kfree(u64(c))
        return ENOMEM
    nforks += 1
    thread_start_ready(ct)
    return c.pid

def thread_free_new(t: ptr[Thread]):
    t.state = T_DEAD
    thread_free(t)

def sys_exec(frame: u64, upath: u64, uargs: u64) -> u64:
    path: u8[128]
    args: u8[128]
    if copy_str_user(u64(addr(path[0])), upath, 128) > 127:
        return EFAULT
    args[0] = 0
    if uargs != 0 and copy_str_user(u64(addr(args[0])), uargs, 128) > 127:
        return EFAULT
    sz: u64 = 0
    img = fs_read_file(addr(path[0]), addr(sz))
    if img == 0:
        return ENOENT
    pml4 = as_new()
    entry = elf_load(pml4, img, sz)
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
    p.brk_base = page_up(elf_end)
    p.brk = p.brk_base
    p.mmap_next = MMAP_BASE
    memcpy(u64(addr(p.args[0])), u64(addr(args[0])), 128)
    # fresh registers: rip = entry, rsp = top of the new stack, everything else zero
    memset(frame, 0, SF_SIZE)
    vstore64(frame + SF_RIP * 8, entry)
    vstore64(frame + SF_RSP * 8, USTACK_TOP - 8)
    vstore64(frame + SF_RFLAGS * 8, 0x202)
    fpu_restore(fpu_template)
    nexecs += 1
    return 0

def sys_wait(pid: u64, ustatus: u64) -> u64:
    me = cur_proc()
    f = irq_save()
    spin_lock(u64(addr(rq_lock)))
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
            spin_unlock(u64(addr(rq_lock)))
            irq_restore(f)
            c = ptr[Proc](found)
            cpid = c.pid
            code = c.exit_code
            proc_destroy(c)
            if ustatus != 0 and copy_user(ustatus, u64(addr(code)), 8) != 0:
                return EFAULT
            return cpid
        if any == 0:
            spin_unlock(u64(addr(rq_lock)))
            irq_restore(f)
            return ECHILD
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
    p.mmap_next += n + PAGE            # one unmapped guard page between mappings
    i: u64 = 0
    while i < n:
        if user_map_zero(p.pml4, va + i) == 0:
            return ENOMEM
        i += PAGE
    return va

def sys_munmap(va: u64, len: u64) -> u64:
    p = cur_proc()
    if (va & (PAGE - 1)) != 0 or va < MMAP_BASE or va + len > MMAP_TOP or va + len < va:
        return EINVAL
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
    fd: u64 = 0
    while fd < NFD and p.fds[fd] != 0:
        fd += 1
    if fd == NFD:
        return EMFILE
    fl = vfs_open(addr(path[0]))
    if fl == 0:
        return ENOENT
    p.fds[fd] = fl
    return fd

def sys_stat(upath: u64, ubuf: u64) -> u64:
    st: u64[2]
    path: u8[256]
    if copy_str_user(u64(addr(path[0])), upath, 256) > 255:
        return EFAULT
    fl = vfs_open(addr(path[0]))
    if fl == 0:
        return ENOENT
    st[0] = ptr[File](fl).size
    st[1] = u64(ptr[File](fl).type == F_DIR)
    file_put(fl)
    if copy_user(ubuf, u64(addr(st[0])), 16) != 0:
        return EFAULT
    return 0

def sys_read(fd: u64, ubuf: u64, n: u64) -> u64:
    if user_ptr_ok(ubuf, n) == 0:
        return EFAULT
    fl = fd_get(cur_proc(), fd)
    if u64(fl) == 0:
        return EBADF
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
    if fl.type == F_CONSOLE:
        return con_write_user(ubuf, n)
    return EROFS

def sys_getdents(fd: u64, ubuf: u64, n: u64) -> u64:
    if user_ptr_ok(ubuf, n) == 0:
        return EFAULT
    fl = fd_get(cur_proc(), fd)
    if u64(fl) == 0:
        return EBADF
    if fl.type != F_DIR:
        return ENOTDIR
    return dir_read(fl, ubuf, n)

def sys_close(fd: u64) -> u64:
    p = cur_proc()
    if fd >= NFD or p.fds[fd] == 0:
        return EBADF
    file_put(p.fds[fd])
    p.fds[fd] = 0
    return 0

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
def syscall_dispatch(nr: u64, a: u64, b: u64, c: u64, d: u64, frame: u64) -> u64:
    # entered with interrupts off (FMASK); the asm exit path disables them again before sysret
    atomic_add64(u64(addr(syscalls)), 1)
    sti()
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
        return cur_proc().pid
    if nr == 57:
        return sys_fork(frame)
    if nr == 59:
        return sys_exec(frame, a, b)
    if nr == 60:
        proc_exit(a & 0xFF)
    if nr == 61:
        return sys_wait(a, b)
    if nr == 78:
        return sys_getdents(a, b, c)
    if nr == 96:
        return lapic_ticks
    if nr == 97:
        return lapic_cpu()
    if nr == 110:
        return cur_proc().ppid
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
        if copy_user(a, u64(addr(p.args[0])), l + 1) != 0:
            return EFAULT
        return l
    return ENOSYS

def user_kill(name: ptr[u8], vec: u64, rip: u64, addr_: u64):
    # called from an exception taken in ring 3: report, and the process exits with 0x100 + vector
    t = cur_thread()
    atomic_add64(u64(addr(user_kills)), 1)
    spin_lock(u64(addr(con_lock)))
    puts("user fault: pid ")
    put_u64(t.id)
    puts(" ")
    puts(name)
    puts(" at rip=")
    put_hex(rip)
    puts(" addr=")
    put_hex(addr_)
    puts(" -> killed\n")
    spin_unlock(u64(addr(con_lock)))
    proc_exit(0x100 + vec)

# ---- kexec-style handoff (Phase 2 item 6) ----
# sys_kexec (syscall 409, test/debug only: needs ktest_hooks) replaces the running kernel with a fresh copy
# of \EFI\Forge\kernel.elf read from the ESP, without a reboot and without writing anything to disk.
# a = 0: \EFI\Forge\kernel.elf, a = 1: \EFI\Forge\kernel-next.elf (a different build).
# Every user process survives (driver services are restarted by the new kernel instead): all of them
# are first collected at a syscall boundary (kx_park), then their user pages, brk/mmap state, args,
# syscall frame, the syscall in progress, FPU state, parent and fds (open files keep their position;
# shared File objects stay shared) are serialised into one physically contiguous handoff region that
# the new kernel sees as a MEM_HANDOFF entry in its BootInfo. The caller's syscall returns 1 there; a
# parked process re-issues its interrupted syscall (wait, read, yield, ...) in the new kernel. Pids are
# kept unless one is already taken in the new kernel.
# Handoff format v2 (pages):
#   0 header | 1-2 BootInfo | 3-10 boot page tables | 11 jump stub | 12.. two pages per process
#   (record, FPU) | file table (64 B per File) | page index (va, pte flags) | data pages
# header: +0 magic +8 version +16 header bytes +24 total pages +32 nproc +40 nfile +48 npg +56 caller
#   +64 fpu size +72 data hash +80 metadata hash +88 proc page +96 file page +104 index page
#   +112 data page +128 sender's kernel version (64 B) +384 RSDP copy (64 B)
# process record: +0 pid +8 ppid +16 state +24 exit code +32 brk_base +40 brk +48 mmap_next
#   +56 is caller | caps << 8 +64 npg +72 first page +80 syscall (nr, a, b, c, d) +128 fds (file index + 1) x16
#   +256 args (128 B) +384 syscall frame
# file record: +0 type +8 first cluster +16 size +24 position +32 fds referring to it
KXH_MAGIC = 0x464F48584B475246
KXH_VERSION = 3  # format written by this kernel
KXH_MIN_VERSION = 3  # oldest format this kernel can adopt
KXH_BYTES = 448
KX_FIXED_PAGES = 12
EBUSY = 0xFFFFFFFFFFFFFFF0
kx_nproc: u64
kx_nfile: u64
kx_files: u64[1024]
kx_gen: u64  # how many handoffs this kernel descends from (BootInfo.kx_gen)
kx_restored: u64
kx_freed: u64

# FORGEKRN header (boot.fsi): 8-byte aligned in the first loadable segment
struct KrnHdr:
    magic: u64
    entry: u64
    phys_start: u64
    phys_end: u64
    krel_start: u64
    krel_end: u64

def kx_grab(n: u64, align: u64, offs: u64) -> u64:
# n free contiguous frames whose start is = offs (mod align); 0 if none
    fl = irq_save()
    spin_lock(addr(pmm_lock))
    a = align / PAGE
    f = (offs + 0x200000) / PAGE
    res: u64 = 0
    while f + n <= frame_limit:
        j: u64 = 0
        while j < n and frame_used(f + j) == 0:
            j += 1
        if j == n:
            j = 0
            while j < n:
                frame_set(f + j)
                j += 1
            frames_free -= n
            res = f * PAGE
            break
        f += a
    spin_unlock(addr(pmm_lock))
    irq_restore(fl)
    return res

def kx_release(pa: u64, n: u64, total: u64):
    fl = irq_save()
    spin_lock(addr(pmm_lock))
    j: u64 = 0
    while j < n:
        frame_clr(pa / PAGE + j)
        buddy_insert(pa / PAGE + j, 0)
        j += 1
    frames_free += n
    frames_total += total
    spin_unlock(addr(pmm_lock))
    irq_restore(fl)

@unsafe
def kx_walk(pml4: u64, idx: u64, data: u64) -> u64:
# counts (idx = 0) or serialises every present 4 KiB user page of an address space
    n: u64 = 0
    i4: u64 = 0
    while i4 < 256:
        e4 = vload64(p2v(pml4) + i4 * 8)
        if e4 & PTE_P:
            i3: u64 = 0
            while i3 < 512:
                e3 = vload64(p2v(e4 & ADDR_MASK) + i3 * 8)
                if (e3 & PTE_P) and (e3 & PTE_PS) == 0:
                    i2: u64 = 0
                    while i2 < 512:
                        e2 = vload64(p2v(e3 & ADDR_MASK) + i2 * 8)
                        if (e2 & PTE_P) and (e2 & PTE_PS) == 0:
                            i1: u64 = 0
                            while i1 < 512:
                                e1 = vload64(p2v(e2 & ADDR_MASK) + i1 * 8)
                                if e1 & PTE_P:
                                    if idx != 0:
                                        vstore64(idx + n * 16, (i4 << 39) | (i3 << 30) | (i2 << 21) | (i1 << 12))
                                        vstore64(idx + n * 16 + 8, e1 & ~ADDR_MASK)
                                        memcpy(data + n * PAGE, p2v(e1 & ADDR_MASK), PAGE)
                                    n += 1
                                i1 += 1
                        i2 += 1
                i3 += 1
        i4 += 1
    return n

@unsafe
def kx_hash(p: u64, n: u64) -> u64:
    h: u64 = 0xCBF29CE484222325
    i: u64 = 0
    while i < n:
        h = (h ^ vload64(p + i)) * 0x100000001B3
        i += 8
    return h

def kx_region(nb: ptr[BootInfo], base: u64, len: u64, typ: u64):
    if len == 0 or nb.nregions >= MAX_REGIONS:
        return
    r = addr(nb.regions[nb.nregions])
    r.base = base
    r.len = len
    r.typ = typ
    nb.nregions += 1

def kx_fail(msg: ptr[u8], img: u64) -> u64:
    puts("kexec: ")
    puts(msg)
    putc(10)
    if img != 0:
        kfree(img)
    return ENOMEM

def kx_pid_taken(pid: u64) -> u64:
    f = irq_save()
    spin_lock(addr(rq_lock))
    r = u64(proc_find_l(pid)) != 0
    spin_unlock(addr(rq_lock))
    irq_restore(f)
    return r

@unsafe
def kx_wait_parked(me: ptr[Proc]) -> u64:
# 1 when every other non-driver user process is parked at a syscall boundary or is a zombie
    i = 0
    while i < MAX_PROCS:
        q = ptr[Proc](procs[i])
        if u64(q) != 0 and u64(q) != u64(me) and q.drv == 0 and q.state == P_ALIVE:
            if ptr[Thread](q.thread).kx_parked == 0:
                return 0
        i += 1
    return 1

@unsafe
def kx_freeze_all(me: ptr[Proc]) -> u64:
    kx_caller = u64(me)
    kx_freeze = 1
    tries: u64 = 0
    while tries < 300:
        f = irq_save()
        spin_lock(addr(rq_lock))
        wq_wake_all_l(addr(tty_q))
        i = 0
        while i < MAX_PROCS:
            if procs[i] != 0:
                wq_wake_all_l(addr(ptr[Proc](procs[i]).childq))
            i += 1
        spin_unlock(addr(rq_lock))
        irq_restore(f)
        if kx_wait_parked(me):
            return 1
        sleep_ticks(1)
        tries += 1
    kx_freeze = 0
    return 0

def kx_file_index(fl: u64) -> u64:
    i: u64 = 0
    while i < kx_nfile:
        if kx_files[i] == fl:
            return i
        i += 1
    kx_files[kx_nfile] = fl
    kx_nfile += 1
    return i

@unsafe
def kx_kept(q: ptr[Proc], me: ptr[Proc]) -> u64:
    return u64(q) != 0 and q.drv == 0 and (u64(q) == u64(me) or q.state == P_ZOMBIE or ptr[Thread](q.thread).kx_parked != 0)

@unsafe
def sys_kexec(frame: u64, which: u64) -> u64:
    if ktest_hooks == 0:
        return EPERM
    t = cur_thread()
    p = ptr[Proc](t.proc)
    j: u64 = 0
    n: u64 = 0
    # the handoff runs on the boot CPU: the successor treats the CPU it starts on as the BSP and wakes
    # the others with INIT-SIPI, which the real BSP would not answer
    t.pin = 1
    while lapic_cpu() != 0:
        sleep_ticks(1)
    if fpu_size > PAGE:
        return EINVAL
    sz: u64 = 0
    if which == 1:
        img = fs_read_file("/EFI/Forge/kernel-next.elf", addr(sz))
    else:
        img = fs_read_file("/EFI/Forge/kernel.elf", addr(sz))
    if img == 0:
        return kx_fail("cannot read the kernel image", 0)
    if sz < 64 or vload32(img) != 0x464C457F or vload16(img + 18) != 62:
        return kx_fail("kernel image is not an x86-64 ELF", img)
    phoff = vload64(img + 32)
    phsz = u64(vload16(img + 54))
    phn = u64(vload16(img + 56))
    lo: u64 = 0xFFFFFFFFFFFFFFFF
    hi: u64 = 0
    i: u64 = 0
    while i < phn:
        ph = ptr[Phdr](img + phoff + i * phsz)
        if (ph.w0 & 0xFFFFFFFF) == 1:
            if ((ph.w0 >> 32) & 3) == 3:
                return kx_fail("kernel image has a W+X segment", img)
            if ph.pa < lo:
                lo = ph.pa
            if ph.pa + ph.msz > hi:
                hi = ph.pa + ph.msz
        i += 1
    if chan_live != 0:
        return kx_fail("IPC channels are open (not handed over)", img)
    if sb_live != 0:
        return kx_fail("sandboxed processes are alive (not handed over)", img)
    if lo != 0x100000:
        return kx_fail("unexpected kernel load address", img)
    entry: u64 = 0
    nrel: u64 = 0
    off: u64 = 0
    while off + 48 <= sz:
        kh = ptr[KrnHdr](img + off)
        if kh.magic == 0x4E524B4547524F46:
            entry = kh.entry
            nrel = (kh.krel_end - kh.krel_start) / 8
            break
        off += 8
    if entry == 0:
        return kx_fail("FORGEKRN header not found", img)
    # the next kernel picks its own slide and relocates itself: it must carry its relocation table
    if nrel == 0 or nrel > 0x100000:
        return kx_fail("new kernel has no relocation table (not relocatable)", img)
    puts("kexec: new kernel relocatable, ")
    put_u64(nrel)
    puts(" relocations\n")
    # new kernel image: physical P with kdelta = P - 1 MiB 2 MiB aligned (the -2 GiB window uses 2 MiB pages)
    kpages = (hi - lo + PAGE - 1) / PAGE
    kp = kx_grab(kpages, 0x200000, 0x100000)
    if kp == 0:
        return kx_fail("no room for the new kernel", img)
    kd = kp - 0x100000
    i = 0
    while i < phn:
        ph = ptr[Phdr](img + phoff + i * phsz)
        if (ph.w0 & 0xFFFFFFFF) == 1:
            dst = p2v(kd + ph.pa)
            memcpy(dst, img + ph.off, ph.fsz)
            memset(dst + ph.fsz, 0, ph.msz - ph.fsz)
        i += 1
    kfree(img)
    # collect every other user process at a syscall boundary
    if kx_freeze_all(p) == 0:
        kx_release(kp, kpages, 0)
        puts("kexec: a process did not reach a syscall boundary, handoff abandoned\n")
        return EBUSY
    # size the handoff region
    kx_nproc = 0
    kx_nfile = 0
    npg: u64 = 0
    i = 0
    while i < MAX_PROCS:
        q = ptr[Proc](procs[i])
        if kx_kept(q, p):
            kx_nproc += 1
            if q.state == P_ALIVE:
                npg += kx_walk(q.pml4, 0, 0)
            j = 0
            while j < NFD:
                if q.fds[j] != 0:
                    kx_file_index(q.fds[j])
                j += 1
        i += 1
    pproc = KX_FIXED_PAGES
    pfile = pproc + 2 * kx_nproc
    pidx = pfile + (kx_nfile * 64 + PAGE - 1) / PAGE
    nidx = (npg * 16 + PAGE - 1) / PAGE
    pdata = pidx + nidx
    total = pdata + npg
    hb = kx_grab(total, PAGE, 0)
    if hb == 0 or hb + total * PAGE > 0x100000000:
        kx_freeze = 0
        kx_release(kp, kpages, 0)
        return kx_fail("no room for the handoff region", 0)
    h = p2v(hb)
    memset(h, 0, total * PAGE)
    # processes
    k: u64 = 0
    pg: u64 = 0
    i = 0
    while i < MAX_PROCS:
        q = ptr[Proc](procs[i])
        if kx_kept(q, p):
            qt = ptr[Thread](q.thread)
            r = h + (pproc + 2 * k) * PAGE
            vstore64(r, q.pid)
            vstore64(r + 8, q.ppid)
            vstore64(r + 16, q.state)
            vstore64(r + 24, q.exit_code)
            vstore64(r + 32, q.brk_base)
            vstore64(r + 40, q.brk)
            vstore64(r + 48, q.mmap_next)
            vstore64(r + 56, u64(u64(q) == u64(p)) | (q.caps << 8))
            n = 0
            if q.state == P_ALIVE:
                n = kx_walk(q.pml4, h + pidx * PAGE + pg * 16, h + (pdata + pg) * PAGE)
            vstore64(r + 64, n)
            vstore64(r + 72, pg)
            pg += n
            j = 0
            while j < 5:
                vstore64(r + 80 + j * 8, qt.sc[j])
                j += 1
            j = 0
            while j < NFD:
                if q.fds[j] != 0:
                    vstore64(r + 128 + j * 8, (kx_file_index(q.fds[j]) + 1) | (q.fdr[j] << 32))
                j += 1
            memcpy(r + 256, u64(addr(q.args[0])), 128)
            if u64(q) == u64(p):
                memcpy(r + 384, frame, SF_SIZE)
                fpu_save(r + PAGE)
            elif q.state == P_ALIVE:
                memcpy(r + 384, qt.kstack_top - SF_SIZE, SF_SIZE)
                memcpy(r + PAGE, qt.fpu, fpu_size)
            k += 1
        i += 1
    # open files (console, regular files, directories)
    i = 0
    while i < kx_nfile:
        fl = ptr[File](kx_files[i])
        r = h + pfile * PAGE + i * 64
        vstore64(r, fl.type)
        vstore64(r + 8, fl.clu)
        vstore64(r + 16, fl.size)
        vstore64(r + 24, fl.pos)
        i += 1
    i = 0
    while i < MAX_PROCS:
        q = ptr[Proc](procs[i])
        if kx_kept(q, p):
            j = 0
            while j < NFD:
                if q.fds[j] != 0:
                    r = h + pfile * PAGE + kx_file_index(q.fds[j]) * 64
                    vstore64(r + 32, vload64(r + 32) + 1)
                j += 1
        i += 1
    vstore64(h, KXH_MAGIC)
    vstore64(h + 8, KXH_VERSION)
    vstore64(h + 16, KXH_BYTES)
    vstore64(h + 24, total)
    vstore64(h + 32, kx_nproc)
    vstore64(h + 40, kx_nfile)
    vstore64(h + 48, npg)
    vstore64(h + 64, fpu_size)
    vstore64(h + 72, kx_hash(h + pdata * PAGE, npg * PAGE))
    vstore64(h + 80, kx_hash(h + pproc * PAGE, (pdata - pproc) * PAGE))
    vstore64(h + 88, pproc)
    vstore64(h + 96, pfile)
    vstore64(h + 104, pidx)
    vstore64(h + 112, pdata)
    v = kernel_version()
    i = 0
    while i < 63 and v[i] != 0:
        vstore8(h + 128 + i, u64(v[i]))
        i += 1
    memcpy(h + 384, u64(addr(rsdp_save[0])), 64)
    # BootInfo for the successor: same firmware view, the handoff region carved out of usable memory
    nb = ptr[BootInfo](h + PAGE)
    memcpy(u64(nb), u64(addr(bi)), sizeof(BootInfo))
    nb.method = BOOT_KEXEC
    nb.kdelta = kd
    nb.rsdp = hb + 384
    nb.info_base = 0
    nb.info_len = 0
    nb.handoff = hb
    nb.kx_gen = kx_gen + 1
    nb.nregions = 0
    he = hb + total * PAGE
    i = 0
    while i < bi.nregions:
        rg = addr(bi.regions[i])
        re = rg.base + rg.len
        if rg.typ == MEM_USABLE and rg.base < he and re > hb:
            a0 = rg.base
            if a0 < hb:
                kx_region(nb, a0, hb - a0, MEM_USABLE)
                a0 = hb
            a1 = re
            if a1 > he:
                a1 = he
            kx_region(nb, a0, a1 - a0, MEM_HANDOFF)
            if re > he:
                kx_region(nb, he, re - he, MEM_USABLE)
        else:
            kx_region(nb, rg.base, rg.len, rg.typ)
        i += 1
    # boot page tables: identity 0..4 GiB + -2 GiB -> new kernel, exactly what forgex64.efi builds
    tb = hb + 3 * PAGE
    tv = h + 3 * PAGE
    i = 0
    while i < 4:
        j = 0
        while j < 512:
            vstore64(tv + (4 + i) * PAGE + j * 8, (i * 0x40000000 + j * 0x200000) | 0x83)
            j += 1
        vstore64(tv + PAGE + i * 8, (tb + (4 + i) * PAGE) | 3)
        i += 1
    i = 0
    while i < 512:
        vstore64(tv + 3 * PAGE + i * 8, (kd + i * 0x200000) | 0x83)
        i += 1
    vstore64(tv, (tb + PAGE) | 3)
    vstore64(tv + 511 * 8, (tb + 2 * PAGE) | 3)
    vstore64(tv + 2 * PAGE + 510 * 8, (tb + 3 * PAGE) | 3)
    # jump stub (identity mapped in both page tables): clear CR4.PGE and CR4.PCIDE so no global entry of this kernel
    # survives, load the boot tables, enter kentry64 with rdi = BootInfo
    sb = hb + 11 * PAGE
    s = h + 11 * PAGE
    vstore64(s, 0xfdff7f2548e0200f)
    vstore64(s + 8, 0x48df220fe0220fff)
    vstore32(s + 16, u32(0xe6ffd789))
    puts("kexec: generation ")
    put_u64(kx_gen + 1)
    if which == 1:
        puts(" kernel (kernel-next.elf) loaded at ")
    else:
        puts(" kernel (kernel.elf) loaded at ")
    put_hex(kp)
    puts(": ")
    put_u64(kx_nproc)
    puts(" processes, ")
    put_u64(kx_nfile)
    puts(" open files, ")
    put_u64(npg)
    puts(" user pages in a ")
    put_u64(total * 4)
    puts(" KiB handoff region (format v")
    put_u64(KXH_VERSION)
    puts("), nothing written to disk\n")
    # quiesce: input and disk interrupts off, device reset, other CPUs halted for good, timer stopped
    input_off()
    hw_quiesce()
    pio_wr(vb_io + VIO_STATUS, 1, 0)
    i = 0
    while i < 24:
        ioapic_mask_pin(i)
        i += 1
    tlb_halt = 1
    stop_machine_begin()
    lapic_wr(0x320, 0x10000)
    lapic_wr(0x380, 0)
    write_cr3(kernel_pml4)
    map_page(kernel_pml4, sb, sb, 0)
    invlpg(sb)
    call_ptr(sb, tb, entry, hb + PAGE)
    panic("kexec stub returned")
    return 0

@unsafe
def kx_restore() -> nptr[Thread]:
# in the new kernel: rebuild every handed-over process, then give the handoff region back.
# Returns the caller's thread (0 if the handoff could not be adopted: the kernel boots fresh).
    hb = bi.handoff
    h = p2v(hb)
    j: u64 = 0
    if vload64(h) != KXH_MAGIC:
        panic("kexec: bad handoff magic")
    ver = vload64(h + 8)
    total = vload64(h + 24)
    puts("kexec: handoff format v")
    put_u64(ver)
    puts(" from ")
    puts(addr(ptr[u8](h + 128)[0]))
    puts(" (this kernel, ")
    puts(kernel_version())
    puts(", adopts v")
    put_u64(KXH_MIN_VERSION)
    puts("..v")
    put_u64(KXH_VERSION)
    puts(")\n")
    if ver < KXH_MIN_VERSION or ver > KXH_VERSION or vload64(h + 16) != KXH_BYTES:
        puts("kexec: unsupported handoff format, its processes are dropped\n")
        kx_release(hb, total, total)
        bi.handoff = 0
        return 0
    nproc = vload64(h + 32)
    nfile = vload64(h + 40)
    npg = vload64(h + 48)
    pproc = vload64(h + 88)
    pfile = vload64(h + 96)
    pidx = vload64(h + 104)
    pdata = vload64(h + 112)
    if kx_hash(h + pdata * PAGE, npg * PAGE) != vload64(h + 72) or kx_hash(h + pproc * PAGE, (pdata - pproc) * PAGE) != vload64(h + 80):
        panic("kexec: handoff data corrupted")
    # files
    i: u64 = 0
    while i < nfile:
        r = h + pfile * PAGE + i * 64
        if vload64(r) == F_CONSOLE:
            kx_files[i] = console()
        else:
            fl = file_new(vload64(r))
            fl.clu = vload64(r + 8)
            fl.size = vload64(r + 16)
            fl.pos = vload64(r + 24)
            fl.ccl = fl.clu
            fl.cidx = 0
            fl.ref = 0
            kx_files[i] = u64(fl)
        i += 1
    # pids: keep them; new thread ids start above every handed-over pid
    maxpid: u64 = 0
    i = 0
    while i < nproc:
        if vload64(h + (pproc + 2 * i) * PAGE) > maxpid:
            maxpid = vload64(h + (pproc + 2 * i) * PAGE)
        i += 1
    if next_tid < maxpid:
        next_tid = maxpid
    newp: u64[64]
    caller: u64 = 0
    kept: u64 = 0
    pg_total: u64 = 0
    i = 0
    while i < nproc:
        r = h + (pproc + 2 * i) * PAGE
        c = proc_new(0)
        c.pml4 = as_new()
        n = vload64(r + 64)
        first = vload64(r + 72)
        j = 0
        while j < n:
            e = h + pidx * PAGE + (first + j) * 16
            f = frame_alloc()
            memcpy(p2v(f), h + (pdata + first + j) * PAGE, PAGE)
            vstore64(user_pte(c.pml4, vload64(e)), f | vload64(e + 8))
            j += 1
        if kx_walk(c.pml4, 0, 0) != n:
            panic("kexec: page count mismatch")
        pg_total += n
        c.state = vload64(r + 16)
        c.exit_code = vload64(r + 24)
        c.brk_base = vload64(r + 32)
        c.brk = vload64(r + 40)
        c.mmap_next = vload64(r + 48)
        memcpy(u64(addr(c.args[0])), r + 256, 128)
        j = 0
        while j < NFD:
            fi = vload64(r + 128 + j * 8)
            if fi != 0:
                c.fds[j] = file_get(kx_files[(fi & 0xFFFFFFFF) - 1])
                c.fdr[j] = fi >> 32
            j += 1
        isc = vload64(r + 56)
        c.caps = (isc >> 8) & CAPB_ALL
        if isc & 1:
            ct = thread_new(kx_resume_entry_addr(), 0)
        else:
            ct = thread_new(kx_restart_entry_addr(), 0)
        cf = ct.kstack_top - SF_SIZE
        # switch frame below the frame and the 40-byte syscall record (kx_restart_entry reads it)
        sp = cf - 40 - 72
        j = 0
        while j < 9:
            vstore64(sp + j * 8, vload64(ct.kstack_top - 72 + j * 8))
            j += 1
        memcpy(cf, r + 384, SF_SIZE)
        j = 0
        while j < 5:
            vstore64(cf - 40 + j * 8, vload64(r + 80 + j * 8))
            j += 1
        vstore64(sp + 16, cf)
        ct.rsp = sp
        ct.cr3 = c.pml4
        ct.user = 1
        ct.proc = u64(c)
        if vload64(h + 64) == fpu_size:
            memcpy(ct.fpu, r + PAGE, fpu_size)
        c.pid = vload64(r)
        c.ppid = vload64(r + 8)
        if kx_pid_taken(c.pid):
            c.pid = ct.id
        c.thread = u64(ct)
        if c.state == P_ZOMBIE:
            ct.state = T_DEAD
        if proc_insert(c) == 0:
            panic("kexec: proc table full")
        newp[i] = u64(c)
        if isc & 1:
            caller = u64(ct)
        i += 1
    # parents whose pid had to change
    i = 0
    while i < nproc:
        c = ptr[Proc](newp[i])
        oldpid = vload64(h + (pproc + 2 * i) * PAGE)
        if c.pid != oldpid:
            j = 0
            while j < nproc:
                if ptr[Proc](newp[j]).ppid == oldpid:
                    ptr[Proc](newp[j]).ppid = c.pid
                j += 1
        i += 1
    puts("kexec: generation ")
    put_u64(kx_gen)
    puts(" kernel adopted ")
    put_u64(nproc)
    puts(" processes (pids")
    i = 0
    while i < nproc:
        putc(32)
        put_u64(vload64(h + (pproc + 2 * i) * PAGE))
        puts("->")
        put_u64(ptr[Proc](newp[i]).pid)
        i += 1
    puts("), ")
    put_u64(nfile)
    puts(" open files, ")
    put_u64(pg_total)
    puts(" user pages, hashes ok; handoff region ")
    put_u64(total * 4)
    puts(" KiB freed\n")
    kx_restored = pg_total
    kx_nproc = nproc
    kx_freed = total
    kx_release(hb, total, total)
    bi.handoff = 0
    i = 0
    while i < nproc:
        c = ptr[Proc](newp[i])
        if c.state == P_ALIVE:
            thread_start_ready(ptr[Thread](c.thread))
        i += 1
    return ptr[Thread](caller)

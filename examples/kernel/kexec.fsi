# ---- kexec-style handoff (Phase 2 item 6) ----
# sys_kexec (syscall 409, test/debug only: needs ktest_hooks) replaces the running kernel with a fresh copy
# of \EFI\Forge\kernel.elf read from the ESP, without a reboot and without writing anything to disk.
# The calling process (the shell) survives: its user pages, brk/mmap state, args, syscall frame and FPU
# state are serialised into one physically contiguous handoff region that the new kernel sees as a
# MEM_HANDOFF entry in its BootInfo; it rebuilds the process fork-style and the syscall returns 1.
#   handoff region (pages): 0 header | 1-2 BootInfo | 3-10 boot page tables | 11 jump stub | 12 FPU |
#                           13.. page index (va, pte flags) | data pages
KXH_MAGIC = 0x464F48584B475246
KX_HDR_PAGES = 13
kx_gen: u64          # how many handoffs this kernel descends from (BootInfo.kx_gen)
kx_restored: u64
kx_freed: u64

def kx_grab(n: u64, align: u64, offs: u64) -> u64:
    # n free contiguous frames whose start is = offs (mod align); 0 if none
    fl = irq_save()
    spin_lock(u64(addr(pmm_lock)))
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
    spin_unlock(u64(addr(pmm_lock)))
    irq_restore(fl)
    return res

def kx_release(pa: u64, n: u64, total: u64):
    fl = irq_save()
    spin_lock(u64(addr(pmm_lock)))
    j: u64 = 0
    while j < n:
        frame_clr(pa / PAGE + j)
        j += 1
    frames_free += n
    frames_total += total
    spin_unlock(u64(addr(pmm_lock)))
    irq_restore(fl)

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

def sys_kexec(frame: u64) -> u64:
    if ktest_hooks == 0:
        return EPERM
    t = cur_thread()
    p = ptr[Proc](t.proc)
    # the handoff runs on the boot CPU: the successor treats the CPU it starts on as the BSP and wakes
    # the others with INIT-SIPI, which the real BSP would not answer
    t.pin = 1
    while lapic_cpu() != 0:
        sleep_ticks(1)
    if fpu_size > PAGE:
        return EINVAL
    sz: u64 = 0
    img = fs_read_file("/EFI/Forge/kernel.elf", addr(sz))
    if img == 0:
        return kx_fail("cannot read \\EFI\\Forge\\kernel.elf", 0)
    if sz < 64 or vload32(img) != 0x464C457F or vload16(img + 18) != 62:
        return kx_fail("kernel.elf is not an x86-64 ELF", img)
    phoff = vload64(img + 32)
    phsz = u64(vload16(img + 54))
    phn = u64(vload16(img + 56))
    lo: u64 = 0xFFFFFFFFFFFFFFFF
    hi: u64 = 0
    i: u64 = 0
    while i < phn:
        ph = img + phoff + i * phsz
        if vload32(ph) == 1:
            if (vload32(ph + 4) & 3) == 3:
                return kx_fail("kernel.elf has a W+X segment", img)
            if vload64(ph + 24) < lo:
                lo = vload64(ph + 24)
            if vload64(ph + 24) + vload64(ph + 40) > hi:
                hi = vload64(ph + 24) + vload64(ph + 40)
        i += 1
    if lo != 0x100000:
        return kx_fail("unexpected kernel load address", img)
    entry: u64 = 0
    off: u64 = 0
    while off + 16 <= sz:
        if vload64(img + off) == 0x4E524B4547524F46:
            entry = vload64(img + off + 8)
            break
        off += 8
    if entry == 0:
        return kx_fail("FORGEKRN header not found", img)
    # new kernel image: physical P with kdelta = P - 1 MiB 2 MiB aligned (the -2 GiB window uses 2 MiB pages)
    kpages = (hi - lo + PAGE - 1) / PAGE
    kp = kx_grab(kpages, 0x200000, 0x100000)
    if kp == 0:
        return kx_fail("no room for the new kernel", img)
    kd = kp - 0x100000
    i = 0
    while i < phn:
        ph = img + phoff + i * phsz
        if vload32(ph) == 1:
            dst = p2v(kd + vload64(ph + 24))
            memcpy(dst, img + vload64(ph + 8), vload64(ph + 32))
            memset(dst + vload64(ph + 32), 0, vload64(ph + 40) - vload64(ph + 32))
        i += 1
    kfree(img)
    # the handoff region
    npg = kx_walk(p.pml4, 0, 0)
    nidx = (npg * 16 + PAGE - 1) / PAGE
    total = KX_HDR_PAGES + nidx + npg
    hb = kx_grab(total, PAGE, 0)
    if hb == 0 or hb + total * PAGE > 0x100000000:
        return kx_fail("no room for the handoff region", 0)
    h = p2v(hb)
    memset(h, 0, total * PAGE)
    fpu_save(h + 12 * PAGE)
    kx_walk(p.pml4, h + KX_HDR_PAGES * PAGE, h + (KX_HDR_PAGES + nidx) * PAGE)
    vstore64(h, KXH_MAGIC)
    vstore64(h + 8, npg)
    vstore64(h + 16, p.brk_base)
    vstore64(h + 24, p.brk)
    vstore64(h + 32, p.mmap_next)
    vstore64(h + 40, nidx)
    vstore64(h + 48, total)
    vstore64(h + 56, p.pid)
    vstore64(h + 64, fpu_size)
    vstore64(h + 72, kx_hash(h + (KX_HDR_PAGES + nidx) * PAGE, npg * PAGE))
    memcpy(h + 128, u64(addr(p.args[0])), 128)
    memcpy(h + 256, frame, SF_SIZE)
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
        r = addr(bi.regions[i])
        re = r.base + r.len
        if r.typ == MEM_USABLE and r.base < he and re > hb:
            a0 = r.base
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
            kx_region(nb, r.base, r.len, r.typ)
        i += 1
    # boot page tables: identity 0..4 GiB + -2 GiB -> new kernel, exactly what forgex64.efi builds
    tb = hb + 3 * PAGE
    tv = h + 3 * PAGE
    i = 0
    while i < 4:
        j: u64 = 0
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
    # jump stub (identity mapped in both page tables): clear CR4.PGE so no global entry of this kernel
    # survives, load the boot tables, enter kentry64 with rdi = BootInfo
    sb = hb + 11 * PAGE
    s = h + 11 * PAGE
    vstore64(s, 0xffff7f2548e0200f)
    vstore64(s + 8, 0x48df220fe0220fff)
    vstore32(s + 16, u32(0xe6ffd789))
    puts("kexec: generation ")
    put_u64(kx_gen + 1)
    puts(" kernel loaded at ")
    put_hex(kp)
    puts(", sh pid ")
    put_u64(p.pid)
    puts(": ")
    put_u64(npg)
    puts(" user pages in a ")
    put_u64(total * 4)
    puts(" KiB handoff region, nothing written to disk\n")
    # quiesce: input and disk interrupts off, device reset, other CPUs halted for good, timer stopped
    input_off()
    outb(u16(vb_io + VIO_STATUS), 0)
    i = 0
    while i < 24:
        ioapic_mask(i)
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

def kx_restore() -> ptr[Thread]:
    # in the new kernel: rebuild the handed-over process, then give the handoff region back
    hb = bi.handoff
    h = p2v(hb)
    if vload64(h) != KXH_MAGIC:
        panic("kexec: bad handoff magic")
    npg = vload64(h + 8)
    nidx = vload64(h + 40)
    total = vload64(h + 48)
    idx = h + KX_HDR_PAGES * PAGE
    data = h + (KX_HDR_PAGES + nidx) * PAGE
    if kx_hash(data, npg * PAGE) != vload64(h + 72):
        panic("kexec: handoff data corrupted")
    c = proc_new(0)
    c.pml4 = as_new()
    i: u64 = 0
    while i < npg:
        f = frame_alloc()
        memcpy(p2v(f), data + i * PAGE, PAGE)
        vstore64(user_pte(c.pml4, vload64(idx + i * 16)), f | vload64(idx + i * 16 + 8))
        i += 1
    if kx_walk(c.pml4, 0, 0) != npg:
        panic("kexec: page count mismatch")
    c.brk_base = vload64(h + 16)
    c.brk = vload64(h + 24)
    c.mmap_next = vload64(h + 32)
    memcpy(u64(addr(c.args[0])), h + 128, 128)
    c.fds[0] = console()
    c.fds[1] = console()
    c.fds[2] = console()
    ct = thread_new(kx_resume_entry_addr(), 0)
    cf = ct.kstack_top - SF_SIZE
    sp = cf - 72
    i = 0
    while i < 9:
        vstore64(sp + i * 8, vload64(ct.kstack_top - 72 + i * 8))
        i += 1
    memcpy(cf, h + 256, SF_SIZE)
    vstore64(sp + 16, cf)
    ct.rsp = sp
    ct.cr3 = c.pml4
    ct.user = 1
    ct.proc = u64(c)
    if vload64(h + 64) == fpu_size:
        memcpy(ct.fpu, h + 12 * PAGE, fpu_size)
    c.pid = ct.id
    c.thread = u64(ct)
    if proc_insert(c) == 0:
        panic("kexec: proc table full")
    puts("kexec: generation ")
    put_u64(kx_gen)
    puts(" kernel resumed sh (pid ")
    put_u64(vload64(h + 56))
    puts(" -> ")
    put_u64(c.pid)
    puts("): ")
    put_u64(npg)
    puts(" user pages restored, hash ok; handoff region ")
    put_u64(total * 4)
    puts(" KiB freed\n")
    kx_restored = npg
    kx_freed = total
    kx_release(hb, total, total)
    bi.handoff = 0
    thread_start_ready(ct)
    return ct

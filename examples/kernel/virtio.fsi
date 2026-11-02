# virtio-blk over the legacy (transitional) PCI interface: I/O BAR0, one split virtqueue.
# Completion is signalled by an MSI-X interrupt; requesters sleep on a mutex and a wait queue.
# Without MSI-X (or with interrupts off) a request is polled under a spinlock as before.
VIO_HOSTF = 0x00
VIO_GUESTF = 0x04
VIO_QPFN = 0x08
VIO_QSIZE = 0x0C
VIO_QSEL = 0x0E
VIO_QNOTIFY = 0x10
VIO_STATUS = 0x12
VIO_CAP = 0x14
VBLK_F_RO = 32  # bit 5

vb_io: u64
vb_q: u64  # virtual address of the ring area
vb_q_pa: u64
vb_qsz: u64
vb_used_off: u64
vb_last_used: u64
vb_req: u64  # one page: header at 0, status at 64, data at 512 (up to 7 sectors)
vb_req_pa: u64
vb_cap: u64
vb_ro: u64
vb_lock: u64
vb_reads: u64
vb_m: Mutex
vb_wq: WaitQ
vb_msix: u64
vb_irqs: u64
vb_sleeps: u64
vb_polled: u64
VEC_VBLK = 0x50
VIO_MSIX_CFG = 0x14
VIO_MSIX_Q = 0x16

def vb_init() -> u64:
    if pci_vblk == 0:
        return 0
    bdf = pci_vblk - 1
    bar = pci_rd32(bdf, 0x10)
    if (bar & 1) == 0:
        return 0
    vb_io = bar & 0xFFFC
    pci_wr32(bdf, 4, pci_rd32(bdf, 4) | 5)  # I/O space + bus master
    io = u16(vb_io)
    outb(io + VIO_STATUS, 0)
    outb(io + VIO_STATUS, 1)
    outb(io + VIO_STATUS, 3)
    hf = u64(inl(io + VIO_HOSTF))
    vb_ro = (hf >> 5) & 1
    outl(io + VIO_GUESTF, u32(hf & (u64(1) << VBLK_F_RO)))
    outw(io + VIO_QSEL, 0)
    vb_qsz = u64(inw(io + VIO_QSIZE))
    if vb_qsz == 0:
        return 0
    used = (16 * vb_qsz + 6 + 2 * vb_qsz + PAGE - 1) & ~u64(PAGE - 1)
    vb_used_off = used
    total = used + ((6 + 8 * vb_qsz + PAGE - 1) & ~u64(PAGE - 1))
    pa = frame_alloc_contig(total / PAGE)
    if pa == 0:
        return 0
    vb_q = p2v(pa)
    vb_q_pa = pa
    outl(io + VIO_QPFN, u32(pa >> 12))
    vb_req_pa = frame_alloc()
    vb_req = p2v(vb_req_pa)
    outb(io + VIO_STATUS, 7)
    vb_cap = u64(inl(io + VIO_CAP)) | (u64(inl(io + VIO_CAP + 4)) << 32)
    puts("virtio-blk: io 0x")
    put_hex16(vb_io)
    puts(", queue ")
    put_u64(vb_qsz)
    puts(", ")
    put_u64(vb_cap)
    puts(" sectors (")
    put_u64(vb_cap / 2048)
    puts(" MiB)")
    if vb_ro:
        puts(", read-only")
    putc(10)
    vb_msix_init(bdf)
    return 1

@interrupt
def on_vblk(frame: ptr[u64]):
    spin_lock(u64(addr(rq_lock)))
    vb_irqs += 1
    wq_wake_all_l(addr(vb_wq))
    spin_unlock(u64(addr(rq_lock)))
    lapic_eoi()

def vb_msix_init(bdf: u64):
# find the MSI-X capability (id 0x11), point table entry 0 at the BSP's LAPIC, bind queue 0 to it
    if (pci_rd32(bdf, 4) & 0x100000) == 0:
        return
    c = pci_rd32(bdf, 0x34) & 0xFC
    g = 0
    while c != 0 and g < 48:
        h = pci_rd32(bdf, c)
        if (h & 0xFF) == 0x11:
            break
        c = (h >> 8) & 0xFC
        g += 1
    if c == 0 or g == 48:
        puts("virtio-blk: no MSI-X, polling\n")
        return
    tb = pci_rd32(bdf, c + 4)
    bir = tb & 7
    bar = pci_rd32(bdf, 0x10 + bir * 4)
    if bar & 1:
        return
    base = bar & 0xFFFFFFF0
    if (bar & 6) == 4:
        base = base | (pci_rd32(bdf, 0x14 + bir * 4) << 32)
    if base == 0 or base >= 0x100000000:
        puts("virtio-blk: MSI-X table above 4 GiB, polling\n")
        return
    pci_wr32(bdf, 4, pci_rd32(bdf, 4) | 2)  # memory space for the table
    t = p2v(base + (tb & 0xFFFFFFF8))
    idt_set(VEC_VBLK, u64(on_vblk), 0)
    vstore32(t, u32(0xFEE00000 | (cpu_to_apic[0] << 12)))
    vstore32(t + 4, 0)
    vstore32(t + 8, VEC_VBLK)
    vstore32(t + 12, 0)
    # message control: enable (bit 15), clear function mask (bit 14)
    pci_wr32(bdf, c, (pci_rd32(bdf, c) | 0x80000000) & ~u64(0x40000000))
    io = u16(vb_io)
    outw(io + VIO_MSIX_CFG, 0xFFFF)
    outw(io + VIO_QSEL, 0)
    outw(io + VIO_MSIX_Q, 0)
    if inw(io + VIO_MSIX_Q) != 0:
        puts("virtio-blk: queue vector rejected, polling\n")
        return
    vb_msix = 1
    puts("virtio-blk: MSI-X vector 0x50 -> cpu0, sleeping requests\n")

def vb_desc(i: u64, pa: u64, len: u64, flags: u64, next: u64):
    d = vb_q + i * 16
    vstore64(d, pa)
    vstore64(d + 8, (len & 0xFFFFFFFF) | (flags << 32) | (next << 48))

def vb_submit(lba: u64, n: u64):
    vstore64(vb_req, 0)  # type IN, reserved
    vstore64(vb_req + 8, lba)
    vstore64(vb_req + 64, 0xFF)
    vb_desc(0, vb_req_pa, 16, 1, 1)
    vb_desc(1, vb_req_pa + 512, n * 512, 3, 2)
    vb_desc(2, vb_req_pa + 64, 1, 2, 0)
    av = vb_q + 16 * vb_qsz
    idx = (vload64(av) >> 16) & 0xFFFF
    vstore16(av + 4 + (idx % vb_qsz) * 2, 0)
    mfence()
    vstore16(av + 2, u16((idx + 1) & 0xFFFF))
    mfence()
    outw(u16(vb_io + VIO_QNOTIFY), 0)

def vb_done() -> u64:
    return ((vload64(vb_q + vb_used_off) >> 16) & 0xFFFF) != vb_last_used

def vb_finish(n: u64, dst: u64) -> u64:
    vb_last_used = (vb_last_used + 1) & 0xFFFF
    ok = (vload64(vb_req + 64) & 0xFF) == 0
    if ok:
        memcpy(dst, vb_req + 512, n * 512)
    vb_reads += 1
    return u64(ok)

def vb_read(lba: u64, n: u64, dst: u64) -> u64:
# reads n (<= 7) sectors into dst; returns 1 on success
    if n == 0 or n > 7 or lba + n > vb_cap:
        return 0
    if blkd_mode:
        return vb_read_drv(lba, n, dst)
    if vb_msix and sched_on and (read_rflags() & 0x200) != 0:
    # sleeping path: one request in flight, owned through vb_m; sleep until the MSI-X completion
        mutex_lock(addr(vb_m))
        vb_submit(lba, n)
        f = irq_save()
        spin_lock(u64(addr(rq_lock)))
        while vb_done() == 0:
            vb_sleeps += 1
            wq_block_l(addr(vb_wq))
        spin_unlock(u64(addr(rq_lock)))
        irq_restore(f)
        ok = vb_finish(n, dst)
        mutex_unlock(addr(vb_m))
        return ok
    # polled path (early boot, interrupts off): vb_m must be free; spinning on it keeps sleepers out
    f = irq_save()
    spin_lock(u64(addr(vb_lock)))
    while vb_m.locked:
        tlb_poll()
        cpu_relax()
    vb_submit(lba, n)
    spins: u64 = 0
    while vb_done() == 0:
        tlb_poll()
        cpu_relax()
        spins += 1
        if spins > 100000000:
            spin_unlock(u64(addr(vb_lock)))
            irq_restore(f)
            panic("virtio-blk: request timed out")
    ok = vb_finish(n, dst)
    vb_polled += 1
    spin_unlock(u64(addr(vb_lock)))
    irq_restore(f)
    return ok

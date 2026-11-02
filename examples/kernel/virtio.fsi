# virtio-blk over the legacy (transitional) PCI interface: I/O BAR0, one split virtqueue, polled.
VIO_HOSTF = 0x00
VIO_GUESTF = 0x04
VIO_QPFN = 0x08
VIO_QSIZE = 0x0C
VIO_QSEL = 0x0E
VIO_QNOTIFY = 0x10
VIO_STATUS = 0x12
VIO_CAP = 0x14
VBLK_F_RO = 32       # bit 5

vb_io: u64
vb_q: u64            # virtual address of the ring area
vb_qsz: u64
vb_used_off: u64
vb_last_used: u64
vb_req: u64          # one page: header at 0, status at 64, data at 512 (up to 7 sectors)
vb_req_pa: u64
vb_cap: u64
vb_ro: u64
vb_lock: u64
vb_reads: u64

def vb_init() -> u64:
    if pci_vblk == 0:
        return 0
    bdf = pci_vblk - 1
    bar = pci_rd32(bdf, 0x10)
    if (bar & 1) == 0:
        return 0
    vb_io = bar & 0xFFFC
    pci_wr32(bdf, 4, pci_rd32(bdf, 4) | 5)          # I/O space + bus master
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
    return 1

def vb_desc(i: u64, pa: u64, len: u64, flags: u64, next: u64):
    d = vb_q + i * 16
    vstore64(d, pa)
    vstore64(d + 8, (len & 0xFFFFFFFF) | (flags << 32) | (next << 48))

def vb_read(lba: u64, n: u64, dst: u64) -> u64:
    # reads n (<= 7) sectors into dst; returns 1 on success
    if n == 0 or n > 7 or lba + n > vb_cap:
        return 0
    f = irq_save()
    spin_lock(u64(addr(vb_lock)))
    vstore64(vb_req, 0)                 # type IN, reserved
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
    ud = vb_q + vb_used_off
    spins: u64 = 0
    while ((vload64(ud) >> 16) & 0xFFFF) == vb_last_used:
        cpu_relax()
        spins += 1
        if spins > 100000000:
            spin_unlock(u64(addr(vb_lock)))
            irq_restore(f)
            panic("virtio-blk: request timed out")
    vb_last_used = (vb_last_used + 1) & 0xFFFF
    ok = (vload64(vb_req + 64) & 0xFF) == 0
    if ok:
        memcpy(dst, vb_req + 512, n * 512)
    vb_reads += 1
    spin_unlock(u64(addr(vb_lock)))
    irq_restore(f)
    return u64(ok)

# Drivers as isolated services (Phase 2.2). The virtio-blk driver can run as a ring-3 process (`blkd`)
# that reaches its device only through capabilities the kernel grants it:
#   - port I/O limited to the device's I/O BAR window (DRV_IO_LEN bytes),
#   - waiting for the device's MSI-X interrupt (the kernel's handler counts and wakes),
#   - one physically contiguous DMA region mapped into its address space (no IOMMU yet: the device
#     itself could still DMA anywhere; the capability only limits what the driver code can touch).
# Clients (vb_read, so the FAT layer and every process doing file I/O) talk to it over a one-slot IPC
# channel: post a request, sleep, get the reply. When the driver process dies the kernel resets the
# device *before* its DMA frames are freed and fails the in-flight request with EIO (or keeps it for
# replay when a supervisor will restart the driver).
DRV_BLK = 1
DRV_IO_LEN = 0x40
EIO = 0xFFFFFFFFFFFFFFFB
EPERM = 0xFFFFFFFFFFFFFFFF
RQ_IDLE = 0
RQ_POSTED = 1
RQ_TAKEN = 2
RQ_DONE = 3

blkd_mode: u64        # 1: vb_read goes to the driver service
blkd_up: u64          # the driver process is alive
blkd_pid: u64
blkd_replay: u64      # 1: a dead driver's in-flight request is kept for replay (supervisor present)
rq_state: u64
rq_seq: u64
rq_lba: u64
rq_n: u64
rq_flags: u64
rq_status: u64
rq_buf: u64
rq_drvq: WaitQ        # driver waiting for a request
rq_cliq: WaitQ        # client waiting for its reply / for a restarted driver
blk_crash_next: u64   # fault injection: the next request carries the crash flag
drv_served: u64
drv_deaths: u64
drv_eio: u64
drv_replays: u64
spawn_drv: u64
drv_supq: WaitQ       # supervisor waiting for a driver death

def vb_read_drv(lba: u64, n: u64, dst: u64) -> u64:
    mutex_lock(addr(vb_m))
    if rq_buf == 0:
        rq_buf = kmalloc(4096)
    f = irq_save()
    spin_lock(u64(addr(rq_lock)))
    st = EIO
    if blkd_up or blkd_replay:
        rq_seq += 1
        rq_lba = lba
        rq_n = n
        rq_flags = 0
        if blk_crash_next:
            rq_flags = 1
            blk_crash_next = 0
        rq_state = RQ_POSTED
        wq_wake_all_l(addr(rq_drvq))
        while rq_state != RQ_DONE:
            wq_block_l(addr(rq_cliq))
        st = rq_status
        rq_state = RQ_IDLE
    spin_unlock(u64(addr(rq_lock)))
    irq_restore(f)
    ok: u64 = 0
    if st == 0:
        memcpy(dst, rq_buf, n * 512)
        ok = 1
        vb_reads += 1
    mutex_unlock(addr(vb_m))
    return ok

def drv_died(p: ptr[Proc]):
    # called from proc_exit of a driver process, before its address space (and DMA frames) is freed
    f = irq_save()
    spin_lock(u64(addr(rq_lock)))
    outb(u16(vb_io + VIO_STATUS), 0)       # device reset: stops all DMA into the driver's frames
    blkd_up = 0
    drv_deaths += 1
    if rq_state == RQ_TAKEN or rq_state == RQ_POSTED:
        if blkd_replay:
            rq_state = RQ_POSTED
            drv_replays += 1
        else:
            rq_status = EIO
            rq_state = RQ_DONE
            drv_eio += 1
    wq_wake_all_l(addr(rq_cliq))
    wq_wake_all_l(addr(drv_supq))
    spin_unlock(u64(addr(rq_lock)))
    irq_restore(f)

def drv_port_ok(port: u64, w: u64) -> u64:
    return port >= vb_io and port + w <= vb_io + DRV_IO_LEN and (w == 1 or w == 2 or w == 4)

def sys_drv(nr: u64, a: u64, b: u64, c: u64) -> u64:
    p = cur_proc()
    if u64(p) == 0 or p.drv != DRV_BLK:
        return EPERM
    if nr == 400:
        if drv_port_ok(a, b) == 0:
            return EPERM
        if b == 1:
            return u64(inb(u16(a)))
        if b == 2:
            return u64(inw(u16(a)))
        return u64(inl(u16(a)))
    if nr == 401:
        if drv_port_ok(a, b) == 0:
            return EPERM
        if b == 1:
            outb(u16(a), u8(c & 0xFF))
        elif b == 2:
            outw(u16(a), u16(c & 0xFFFF))
        else:
            outl(u16(a), u32(c & 0xFFFFFFFF))
        return 0
    if nr == 402:
        # dma_alloc(npages): one contiguous region per driver, mapped user RW NX
        if p.dma_n != 0 or a == 0 or a > 16:
            return EINVAL
        pa = frame_alloc_contig(a)
        if pa == 0:
            return ENOMEM
        va = p.mmap_next
        p.mmap_next += (a + 1) * PAGE
        i: u64 = 0
        while i < a:
            e = user_pte(p.pml4, va + i * PAGE)
            vstore64(e, (pa + i * PAGE) | PTE_P | PTE_W | PTE_U | PTE_NX)
            i += 1
        p.dma_pa = pa
        p.dma_n = a
        return va
    if nr == 403:
        return p.dma_pa
    if nr == 404:
        # irq_wait(seen): block until the device interrupt count differs from seen; returns the count
        f = irq_save()
        spin_lock(u64(addr(rq_lock)))
        while a != 0xFFFFFFFFFFFFFFFF and vb_irqs == a:
            wq_block_l(addr(vb_wq))
        r = vb_irqs
        spin_unlock(u64(addr(rq_lock)))
        irq_restore(f)
        return r
    if nr == 405:
        # recv(msg[4]): block for a request -> seq, lba, count, flags
        m: u64[4]
        f = irq_save()
        spin_lock(u64(addr(rq_lock)))
        while rq_state != RQ_POSTED:
            wq_block_l(addr(rq_drvq))
        rq_state = RQ_TAKEN
        m[0] = rq_seq
        m[1] = rq_lba
        m[2] = rq_n
        m[3] = rq_flags
        spin_unlock(u64(addr(rq_lock)))
        irq_restore(f)
        if copy_user(a, u64(addr(m[0])), 32) != 0:
            return EFAULT
        return 0
    if nr == 406:
        # reply(seq, status, data): data holds count*512 bytes when status == 0
        if b == 0 and copy_user(rq_buf, c, rq_n * 512) != 0:
            return EFAULT
        f = irq_save()
        spin_lock(u64(addr(rq_lock)))
        rr: u64 = EINVAL
        if rq_state == RQ_TAKEN and rq_seq == a:
            rq_status = EIO
            if b == 0:
                rq_status = 0
            rq_state = RQ_DONE
            drv_served += 1
            wq_wake_all_l(addr(rq_cliq))
            rr = 0
        spin_unlock(u64(addr(rq_lock)))
        irq_restore(f)
        return rr
    if nr == 407:
        if a == 0:
            return vb_io
        return vb_msix
    return ENOSYS

def vb_reinit():
    # take the device back into the kernel's own driver: reset, fresh ring in the original frames
    mutex_lock(addr(vb_m))
    io = u16(vb_io)
    outb(io + VIO_STATUS, 0)
    used = vb_used_off
    total = used + ((6 + 8 * vb_qsz + PAGE - 1) & ~u64(PAGE - 1))
    memset(vb_q, 0, total)
    vb_last_used = 0
    outb(io + VIO_STATUS, 1)
    outb(io + VIO_STATUS, 3)
    hf = u64(inl(io + VIO_HOSTF))
    outl(io + VIO_GUESTF, u32(hf & (u64(1) << VBLK_F_RO)))
    outw(io + VIO_QSEL, 0)
    outl(io + VIO_QPFN, u32(vb_q_pa >> 12))
    if vb_msix:
        outw(io + VIO_MSIX_CFG, 0xFFFF)
        outw(io + VIO_QSEL, 0)
        outw(io + VIO_MSIX_Q, 0)
    outb(io + VIO_STATUS, 7)
    blkd_mode = 0
    mutex_unlock(addr(vb_m))

def blkd_start(img: u64, sz: u64) -> ptr[Thread]:
    # hand the device over: no kernel-driver request is in flight while vb_m is held
    mutex_lock(addr(vb_m))
    spawn_drv = DRV_BLK
    t = proc_spawn_args(img, sz, "")
    spawn_drv = 0
    if u64(t) != 0:
        blkd_pid = t.id
        blkd_up = 1
        blkd_mode = 1
    mutex_unlock(addr(vb_m))
    return t

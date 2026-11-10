# Drivers as isolated services (Phase 2.2). The virtio-blk driver can run as a ring-3 process (`blkd`)
# that reaches its device only through capabilities the kernel grants it:
#   - port I/O limited to the device's I/O BAR window (DRV_IO_LEN bytes), or, for a modern virtio-blk
#     device, MMIO in that device's capability BAR through the same 400/401 syscalls,
#   - waiting for the device's MSI-X interrupt (the kernel's handler counts and wakes),
#   - one physically contiguous DMA region mapped into its address space. When VT-d translation is
#     on, drv 403 returns a non-identity IOVA and the IOMMU maps only that grant; without a remapping
#     unit, 403 still returns the host PA. Broader device grants stay gated on confinement.
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

blkd_mode: u64  # 1: vb_read goes to the driver service
blkd_up: u64  # the driver process is alive
blkd_pid: u64
blkd_replay: u64  # 1: a dead driver's in-flight request is kept for replay (supervisor present)
rq_state: u64
rq_seq: u64
rq_lba: u64
rq_n: u64
rq_flags: u64
rq_status: u64
rq_buf: u64
rq_drvq: WaitQ  # driver waiting for a request
rq_cliq: WaitQ  # client waiting for its reply / for a restarted driver
blk_crash_next: u64  # fault injection: the next request carries the crash flag
drv_served: u64
drv_deaths: u64
drv_eio: u64
drv_replays: u64
drv_supq: WaitQ  # supervisor waiting for a driver death

@unsafe
def vb_read_drv(lba: u64, n: u64, dst: u64) -> u64:
    mutex_lock(addr(vb_m))
    if rq_buf == 0:
        rq_buf = kmalloc(4096)
    f = irq_save()
    spin_lock(addr(rq_lock))
    st = EIO
    if blkd_up or blkd_replay:
        rq_seq += 1
        rq_lba = lba
        rq_n = n
        rq_flags = 0
        if blk_crash_next:
            rq_flags = 1
            blk_crash_next = 0
        if blk_crash_after != 0:
            blk_crash_after -= 1
            if blk_crash_after == 0:
                rq_flags = 1
        rq_state = RQ_POSTED
        wq_wake_all_l(addr(rq_drvq))
        while rq_state != RQ_DONE:
            wq_block_l(addr(rq_cliq))
        st = rq_status
        rq_state = RQ_IDLE
    spin_unlock(addr(rq_lock))
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
    spin_lock(addr(rq_lock))
    vb_status_wr(0)  # device reset: stops all DMA into the driver's frames
    iommu_unmap_grant()
    blkd_up = 0
    drv_deaths += 1
    if (rq_flags & 2) != 0:
        rq_state = RQ_IDLE
    elif rq_state == RQ_TAKEN or rq_state == RQ_POSTED:
        if blkd_replay:
            rq_state = RQ_POSTED
            drv_replays += 1
            rq_flags = rq_flags & ~u64(1)  # an injected fault is one-shot
        else:
            rq_status = EIO
            rq_state = RQ_DONE
            drv_eio += 1
    wq_wake_all_l(addr(rq_cliq))
    wq_wake_all_l(addr(drv_supq))
    spin_unlock(addr(rq_lock))
    irq_restore(f)

def drv_port_ok(port: u64, w: u64) -> u64:
    return port >= vb_io and port + w <= vb_io + DRV_IO_LEN and (w == 1 or w == 2 or w == 4)

def drv_mmio_ok(off: u64, w: u64) -> u64:
    if w != 1 and w != 2 and w != 4 and w != 8:
        return 0
    if (off & (w - 1)) != 0:
        return 0
    if off + w > vb_bar_len:
        return 0
    return 1

@unsafe
def sys_drv(nr: u64, a: u64, b: u64, c: u64) -> u64:
    p = cur_proc()
    if nr == 408:
    # fault-injection hook: make the block driver crash on the a-th next request. Disabled
    # (EPERM) unless the kernel's own boot self-test armed it for its shell session.
        if ktest_hooks == 0:
            return EPERM
        if b == 1:
            kb_crash_after = a  # the input service dies on the batch holding the a-th next entry
        else:
            blk_crash_after = a
        return 0
    if u64(p) == 0 or p.drv != DRV_BLK:
        return EPERM
    if nr == 400:
        if vb_modern:
            if drv_mmio_ok(a, b) == 0:
                return EPERM
            return mmio_rd(vb_cfg - vb_cfg_off + a, b)
        if drv_port_ok(a, b) == 0:
            return EPERM
        if b == 1:
            return pio_rd(a, 1)
        if b == 2:
            return pio_rd(a, 2)
        return pio_rd(a, 4)
    if nr == 401:
        if vb_modern:
            if drv_mmio_ok(a, b) == 0:
                return EPERM
            mmio_wr(vb_cfg - vb_cfg_off + a, b, c)
            return 0
        if drv_port_ok(a, b) == 0:
            return EPERM
        if b == 1:
            pio_wr((a), 1, c & 0xFF)
        elif b == 2:
            pio_wr((a), 2, c & 0xFFFF)
        else:
            pio_wr((a), 4, c & 0xFFFFFFFF)
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
        p.dma_va = va
        if pci_vblk != 0:
            iova = iommu_map_grant(pci_vblk - 1, pa, a)
            if iova != 0:
                return va
        return va
    if nr == 403:
        if iommu_grant_iova != 0:
            return iommu_grant_iova
        return p.dma_pa
    if nr == 404:
    # irq_wait(seen): block until the device interrupt count differs from seen; returns the count
        f = irq_save()
        spin_lock(addr(rq_lock))
        while a != 0xFFFFFFFFFFFFFFFF and vb_irqs == a:
            wq_block_l(addr(vb_wq))
        r = vb_irqs
        spin_unlock(addr(rq_lock))
        irq_restore(f)
        return r
    if nr == 405:
    # recv(msg[4]): block for a request -> seq, lba, count, flags
        m: u64[4]
        if user_ptr_ok(a, 32) == 0:
            return EFAULT
        f = irq_save()
        spin_lock(addr(rq_lock))
        while rq_state != RQ_POSTED:
            wq_block_l(addr(rq_drvq))
        rq_state = RQ_TAKEN
        m[0] = rq_seq
        m[1] = rq_lba
        m[2] = rq_n
        m[3] = rq_flags
        spin_unlock(addr(rq_lock))
        irq_restore(f)
        if copy_user(a, u64(addr(m[0])), 32) != 0:
        # undelivered: hand the request back so the next recv gets it
            f = irq_save()
            spin_lock(addr(rq_lock))
            if rq_state == RQ_TAKEN and rq_seq == m[0]:
                rq_state = RQ_POSTED
                wq_wake_one_l(addr(rq_drvq))
            spin_unlock(addr(rq_lock))
            irq_restore(f)
            return EFAULT
        return 0
    if nr == 406:
    # reply(seq, status, data): data holds count*512 bytes when status == 0
        if b == 0 and (rq_n > 8 or user_ptr_ok(c, rq_n * 512) == 0 or copy_user(rq_buf, c, rq_n * 512) != 0):
            return EFAULT
        f = irq_save()
        spin_lock(addr(rq_lock))
        rr: u64 = EINVAL
        if rq_state == RQ_TAKEN and rq_seq == a:
            rq_status = EIO
            if b == 0:
                rq_status = 0
            rq_state = RQ_DONE
            drv_served += 1
            wq_wake_all_l(addr(rq_cliq))
            rr = 0
        spin_unlock(addr(rq_lock))
        irq_restore(f)
        return rr
    if nr == 407:
        if a == 1:
            return vb_msix
        if a == 5:
            return vb_modern
        if vb_modern:
            if a == 0:
                return vb_cfg_off
            if a == 2:
                return vb_notify_off
            if a == 3:
                return vb_notify_mul
            if a == 4:
                return vb_devcfg_off
            return 0
        if a == 0:
            return vb_io
        return vb_msix
    return ENOSYS

@unsafe
def vb_reinit():
# take the device back into the kernel's own driver: reset, fresh ring in the original frames
    mutex_lock(addr(vb_m))
    used = vb_used_off
    total = used + ((6 + 8 * vb_qsz + PAGE - 1) & ~u64(PAGE - 1))
    memset(vb_q, 0, total)
    vb_last_used = 0
    if vb_modern:
        vb_modern_reset()
        vb_status_wr(1)
        vb_status_wr(3)
        if vb_modern_features() == 0:
            panic("virtio-blk: modern reinit features refused")
        vb_modern_queue(vb_q_pa)
        if vb_msix:
            mmio_wr(vb_cfg + 16, 2, 0xFFFF)
            mmio_wr(vb_cfg + 22, 2, 0)
            mmio_wr(vb_cfg + 26, 2, 0)
        mmio_wr(vb_cfg + 28, 2, 1)
        vb_status_wr(vb_status_rd() | 4)
    else:
        io = vb_io
        pio_wr(io + VIO_STATUS, 1, 0)
        pio_wr(io + VIO_STATUS, 1, 1)
        pio_wr(io + VIO_STATUS, 1, 3)
        hf = pio_rd(io + VIO_HOSTF, 4)
        pio_wr(io + VIO_GUESTF, 4, hf & (u64(1) << VBLK_F_RO))
        pio_wr(io + VIO_QSEL, 2, 0)
        pio_wr(io + VIO_QPFN, 4, vb_q_pa >> 12)
        if vb_msix:
            pio_wr(io + VIO_MSIX_CFG, 2, 0xFFFF)
            pio_wr(io + VIO_QSEL, 2, 0)
            pio_wr(io + VIO_MSIX_Q, 2, 0)
        pio_wr(io + VIO_STATUS, 1, 7)
    blkd_mode = 0
    mutex_unlock(addr(vb_m))

def blkd_start(img: u64, sz: u64) -> nptr[Thread]:
# hand the device over: no kernel-driver request is in flight while vb_m is held
    mutex_lock(addr(vb_m))
    cur_thread().spawn_drv = DRV_BLK
    t = proc_spawn_args(img, sz, "")
    cur_thread().spawn_drv = 0
    if u64(t) != 0:
        blkd_pid = t.id
        blkd_up = 1
        blkd_mode = 1
    mutex_unlock(addr(vb_m))
    return t

# ---- Supervisor (Phase 2.3) ----
# A kernel thread waits for the driver's death, reaps it, and respawns it from the cached image.
# The kernel keeps the request journal (the IPC slot): a request the dead driver had taken is put
# back to POSTED, so the new instance replays it. Block reads are idempotent and replies carry the
# request's sequence number, so a late reply from an old instance can never complete a newer request.
# The device is the driver's only state and is reset + re-initialised on every start; the restart
# generation and the journal position are handed to the new instance as its arguments.
SUP_MAX_RESTARTS = 5
sup_img: u64
sup_sz: u64
sup_stop: u64
sup_restarts: u64
sup_thread: u64
blkd_thread: u64
ktest_hooks: u64  # set only by the in-kernel boot self-test (shell_test)
blk_crash_after: u64  # fault injection: crash on the n-th next request (0 = off)

def blkd_spawn(args: ptr[u8]) -> nptr[Thread]:
    cur_thread().spawn_drv = DRV_BLK
    t = proc_spawn_args(sup_img, sup_sz, args)
    cur_thread().spawn_drv = 0
    return t

@unsafe
def supervisor(arg: u64):
    while 1:
        f = irq_save()
        spin_lock(addr(rq_lock))
        while blkd_up:
            wq_block_l(addr(drv_supq))
        spin_unlock(addr(rq_lock))
        irq_restore(f)
        old = ptr[Thread](blkd_thread)
        code = proc_reap(old)
        if sup_stop:
            return
        sup_restarts += 1
        if sup_restarts > SUP_MAX_RESTARTS:
            puts("supervisor: blkd keeps dying, giving up\n")
            f = irq_save()
            spin_lock(addr(rq_lock))
            blkd_replay = 0
            if rq_state == RQ_POSTED:
                rq_status = EIO
                rq_state = RQ_DONE
                wq_wake_all_l(addr(rq_cliq))
            spin_unlock(addr(rq_lock))
            irq_restore(f)
            return
        a: u8[64]
        memset(u64(addr(a[0])), 0, 64)
        memcpy(u64(addr(a[0])), u64("restart "), 8)
        a[8] = u8(48 + sup_restarts)
        puts("supervisor: blkd (pid ")
        put_u64(old.id)
        puts(") died with ")
        put_hex8(code)
        puts(", restart ")
        put_u64(sup_restarts)
        puts(", replaying request ")
        put_u64(rq_seq)
        putc(10)
        t = blkd_spawn(addr(a[0]))
        if u64(t) == 0:
            panic("supervisor: blkd image rejected")
        f = irq_save()
        spin_lock(addr(rq_lock))
        blkd_thread = u64(t)
        blkd_pid = t.id
        blkd_up = 1
        spin_unlock(addr(rq_lock))
        irq_restore(f)

def blkd_supervised_start(img: u64, sz: u64):
    sup_img = img
    sup_sz = sz
    sup_stop = 0
    sup_restarts = 0
    if rq_buf == 0:
        rq_buf = kmalloc(4096)
    mutex_lock(addr(vb_m))
    blkd_replay = 1
    t = blkd_spawn("")
    if u64(t) == 0:
        panic("blkd rejected by the ELF loader")
    blkd_thread = u64(t)
    blkd_pid = t.id
    blkd_up = 1
    blkd_mode = 1
    mutex_unlock(addr(vb_m))
    sup_thread = u64(thread_spawn(u64(supervisor), 0))

@unsafe
def blkd_supervised_stop():
# ask the driver to exit (request flag 2); the supervisor sees sup_stop and returns
    sup_stop = 1
    mutex_lock(addr(vb_m))
    f = irq_save()
    spin_lock(addr(rq_lock))
    rq_seq += 1
    rq_n = 0
    rq_flags = 2
    rq_state = RQ_POSTED
    wq_wake_all_l(addr(rq_drvq))
    spin_unlock(addr(rq_lock))
    irq_restore(f)
    mutex_unlock(addr(vb_m))
    thread_reap(ptr[Thread](sup_thread))
    f = irq_save()
    spin_lock(addr(rq_lock))
    rq_state = RQ_IDLE
    blkd_replay = 0
    spin_unlock(addr(rq_lock))
    irq_restore(f)
    vb_reinit()
    kfree(rq_buf)
    rq_buf = 0

# NVMe as a supervised, hot-swappable ring-3 service (Phase 2 gap 2). While the service is on, every
# I/O-queue command (read, write, flush) the kernel issues is posted as a request; the driver
# (`nvmed`, v1, or `nvmed2`, v2) takes it (414), turns it into an NVMe command (opcode, LBA dwords,
# block count) and submits it (415), reaps the completion (416) and replies (417) with the status
# and its state word (served count), which the kernel keeps (418) for the next instance.
# There is no IOMMU, so the driver never gets the BAR or a DMA address: the kernel writes the SQE
# only if it matches the posted request exactly (opcode for the op, namespace 1, the kernel's DMA
# buffer, LBA, block count, writes only inside the claimed Forge partition) and refuses anything
# else (counted). A request the driver took but did not reply to stays posted: when the driver dies
# the kernel drains any completion it left in the queue, and the restarted or swapped-in instance
# replays the request (reads and writes of the same buffer are idempotent). A swap asks the driver
# to quiesce: its next 414 returns 0 even with a request waiting, it exits 0 and the supervisor
# starts the other version.
DRV_NVME = 3
NVS_MAGIC = 0x4E5653  # "NVS" in state bits 63:32
NVS_MAX_RESTARTS = 5

nvs_seq: u64  # sequence number of the posted request (0 = none yet)
nvs_done: u64  # last replied sequence number
nvs_op: u64  # NVMe opcode: 2 read, 1 write, 0 flush
nvs_lba: u64
nvs_nb: u64
nvs_st: u64
nvs_taken: u64  # the current driver took the posted request
nvs_infl: u64  # an SQE for it is in the queue, completion not reaped
nvs_infl_cid: u64
nvs_reaped: u64  # completion reaped, status in nvs_rst
nvs_rst: u64
nvs_state: u64
nvs_up: u64
nvs_ver: u64
nvs_quit: u64
nvs_stop: u64
nvs_crash_after: u64  # fault injection: the n-th next request carries the crash flag
nvs_q: WaitQ  # driver waiting for a request
nvs_cq: WaitQ  # client waiting for a reply
nvs_supq: WaitQ
nvs_thread: nptr[Thread]
nvs_sup: nptr[Thread]
nvs_swp: nptr[Thread]
nvs_img: u64[3]
nvs_sz: u64[3]
nvs_reqs: u64
nvs_replays: u64
nvs_drained: u64
nvs_rejects: u64
nvs_restarts: u64
nvs_swaps: u64
nvs_swap_every: u64

def nvs_call(opc: u64, lba: u64, nb: u64) -> u64:
# client side (kernel thread): post one request and sleep until a driver replies
    f = irq_save()
    spin_lock(addr(rq_lock))
    nvs_seq += 1
    nvs_op = opc
    nvs_lba = lba
    nvs_nb = nb
    nvs_taken = 0
    nvs_reaped = 0
    nvs_reqs += 1
    wq_wake_all_l(addr(nvs_q))
    while nvs_done != nvs_seq:
        wq_block_l(addr(nvs_cq))
    st = nvs_st
    spin_unlock(addr(rq_lock))
    irq_restore(f)
    return st

def nvs_cur(p: nptr[Proc]) -> u64:
    return p != 0 and p.drv == DRV_NVME

def nvs_ok_l(a: u64, b: u64, c: u64) -> u64:
# the SQE the driver asks for must be exactly the posted request's
    if nvs_taken == 0 or nvs_infl != 0 or nvs_reaped != 0 or nvs_done == nvs_seq:
        return 0
    if a != nvs_op or b != nvs_lba:
        return 0
    if nvs_op == 0:
        return b == 0 and c == 0
    if nvs_nb == 0 or c != nvs_nb - 1:
        return 0
    if nvs_op == 1:
        return nv_own_hi != 0 and b >= nv_own_lo and b + nvs_nb <= nv_own_hi and b + nvs_nb <= nv_nsze
    return nvs_op == 2 and b + nvs_nb <= nv_nsze

def sys_nvs(nr: u64, a: u64, b: u64, c: u64) -> u64:
    if nvs_cur(cur_proc()) == 0:
        return EPERM
    if nr == 414:
    # recv(buf): buf = [seq, op (1 read, 2 write, 3 flush), lba, blocks, flags (bit 0 = crash)]; 0 = quiesce
        m: u64[5]
        if user_ptr_ok(a, 40) == 0:
            return EFAULT
        f = irq_save()
        spin_lock(addr(rq_lock))
        while nvs_done == nvs_seq and nvs_quit == 0:
            wq_block_l(addr(nvs_q))
        n: u64 = 0
        if nvs_quit == 0:
            n = 1
            m[0] = nvs_seq
            m[1] = 3
            if nvs_op == 2:
                m[1] = 1
            elif nvs_op == 1:
                m[1] = 2
            m[2] = nvs_lba
            m[3] = nvs_nb
            m[4] = 0
            if nvs_crash_after != 0 and nvs_taken == 0:
                nvs_crash_after -= 1
                if nvs_crash_after == 0:
                    m[4] = 1
            nvs_taken = 1
        spin_unlock(addr(rq_lock))
        irq_restore(f)
        if n != 0 and copy_user(a, u64(addr(m[0])), 40) != 0:
            return EFAULT
        return n
    if nr == 415:
    # submit(opcode, lba, blocks - 1): returns the command id
        f = irq_save()
        spin_lock(addr(rq_lock))
        r: u64 = EINVAL
        if nvs_ok_l(a, b, c):
            prp = nv_buf_pa
            if a == 0:
                prp = 0
            nv_d12 = c
            nvs_infl_cid = nv_submit(1, a, 1, prp, b & 0xFFFFFFFF, b >> 32)
            nvs_infl = 1
            r = nvs_infl_cid
        else:
            nvs_rejects += 1
        spin_unlock(addr(rq_lock))
        irq_restore(f)
        return r
    if nr == 416:
    # reap: (cid << 16) | status of the in-flight command. Only the driver's own thread touches the
    # I/O queue while a command is in flight, so the poll runs without rq_lock.
        if nvs_infl == 0:
            return EINVAL
        rr = nv_reap(1)
        f = irq_save()
        spin_lock(addr(rq_lock))
        nvs_infl = 0
        nvs_reaped = 1
        nvs_rst = rr & 0xFFFF
        if (rr & 0xFFFF) != 0xFFFF and (rr >> 16) != nvs_infl_cid:
            nvs_rst = 0xFFFD
        spin_unlock(addr(rq_lock))
        irq_restore(f)
        return rr
    if nr == 417:
    # reply(seq, status, state): the status must be the one the kernel reaped for this request
        f = irq_save()
        spin_lock(addr(rq_lock))
        r = EINVAL
        if a == nvs_seq and nvs_done != nvs_seq and nvs_reaped != 0 and (c >> 32) == NVS_MAGIC:
            st = b
            if st != nvs_rst:
                st = nvs_rst
                nvs_rejects += 1
            nvs_st = st
            nvs_state = c
            nvs_done = nvs_seq
            wq_wake_all_l(addr(nvs_cq))
            r = 0
        spin_unlock(addr(rq_lock))
        irq_restore(f)
        return r
    if nr == 418:
        return nvs_state
    return ENOSYS

def nvs_died():
# proc_exit of the NVMe driver: drain a completion it left behind; the request stays posted
    if nvs_infl != 0:
        nv_reap(1)
        nvs_infl = 0
        nvs_drained += 1
    f = irq_save()
    spin_lock(addr(rq_lock))
    if nvs_taken != 0 and nvs_done != nvs_seq:
        nvs_replays += 1
    nvs_taken = 0
    nvs_reaped = 0
    nvs_up = 0
    wq_wake_all_l(addr(nvs_supq))
    spin_unlock(addr(rq_lock))
    irq_restore(f)

def nvs_start_l(v: u64):
    f = irq_save()
    spin_lock(addr(rq_lock))
    nvs_ver = v
    nvs_quit = nvs_stop
    nvs_up = 1
    spin_unlock(addr(rq_lock))
    irq_restore(f)
    a: u8[4]
    a[0] = u8(48 + v)
    a[1] = 0
    cur_thread().spawn_drv = DRV_NVME
    t = proc_spawn_args(nvs_img[v], nvs_sz[v], addr(a[0]))
    cur_thread().spawn_drv = 0
    if t == 0:
        panic("NVMe service: nvmed image rejected")
    nvs_thread = t

def nvs_supervisor(arg: u64):
    while 1:
        f = irq_save()
        spin_lock(addr(rq_lock))
        while nvs_up:
            wq_block_l(addr(nvs_supq))
        swap = nvs_quit
        spin_unlock(addr(rq_lock))
        irq_restore(f)
        old = nvs_thread
        code: u64 = 0
        opid: u64 = 0
        if old != 0:
            opid = old.id
            code = proc_reap(old)
        nvs_thread = 0
        if nvs_stop:
            return
        v = nvs_ver
        if swap != 0 and code == 0:
            v = 3 - v
            nvs_swaps += 1
        else:
            nvs_restarts += 1
            puts("NVMe supervisor: nvmed v")
            put_u64(v)
            puts(" (pid ")
            put_u64(opid)
            puts(") died with ")
            put_hex(code)
            puts(", restart ")
            put_u64(nvs_restarts)
            puts(", replaying request ")
            put_u64(nvs_seq)
            putc(10)
            if nvs_restarts > NVS_MAX_RESTARTS:
                panic("NVMe supervisor: nvmed keeps dying")
        nvs_start_l(v)

def nvs_swapper(arg: u64):
# swap v1 <-> v2 every few requests while I/O runs
    last: u64 = 0
    while nvs_stop == 0:
        sleep_ticks(1)
        f = irq_save()
        spin_lock(addr(rq_lock))
        if nvs_up and nvs_quit == 0 and nvs_stop == 0 and nvs_reqs >= last + nvs_swap_every:
            last = nvs_reqs
            nvs_quit = 1
            wq_wake_all_l(addr(nvs_q))
        spin_unlock(addr(rq_lock))
        irq_restore(f)

def nvme_service_start(every: u64, crash: u64) -> u64:
    nvs_img[1] = fs_read_file("EFI/Forge/bin/nvmed", addr(nvs_sz[1]))
    nvs_img[2] = fs_read_file("EFI/Forge/bin/nvmed2", addr(nvs_sz[2]))
    if nvs_img[1] == 0 or nvs_img[2] == 0:
        return 0
    nvs_state = NVS_MAGIC << 32
    nvs_stop = 0
    nvs_swap_every = every
    nvs_crash_after = crash
    nvs_start_l(1)
    nvs_on = 1
    nvs_sup = thread_spawn(u64(nvs_supervisor), 0)
    nvs_swp = thread_spawn(u64(nvs_swapper), 0)
    return 1

def nvme_service_stop():
# no request is outstanding (the only client is the caller); quiesce, then the kernel drives the queue again
    nvs_stop = 1
    sw = nvs_swp
    if sw != 0:
        thread_reap(sw)
    f = irq_save()
    spin_lock(addr(rq_lock))
    nvs_quit = 1
    wq_wake_all_l(addr(nvs_q))
    spin_unlock(addr(rq_lock))
    irq_restore(f)
    sp = nvs_sup
    if sp != 0:
        thread_reap(sp)
    nvs_on = 0
    kfree(nvs_img[1])
    kfree(nvs_img[2])

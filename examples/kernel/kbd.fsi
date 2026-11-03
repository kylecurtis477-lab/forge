# Input as a supervised, hot-swappable ring-3 service (Phase 2 gap 1). While the service is on, the
# PS/2 and COM1 interrupt handlers only read the device and append the raw byte to the kernel-held
# input journal (kind 0 = PS/2 scan code, 1 = serial byte). The driver (`kbdd`, v1, or `kbdd2`, v2)
# takes a batch of journal entries (syscall 411), decodes them (scan-code set 1, shift and E0
# prefix state, CR -> LF) and commits the batch (412): its characters go into the tty line
# discipline, the journal's ack position moves to the end of the batch and the driver's decoder
# state word is stored in the kernel, all in one critical section under rq_lock. Nothing a driver
# took but did not commit is ever lost: when it dies, the entries are still in the journal and the
# restarted (or swapped-in) driver replays them from the ack position with the last committed
# decoder state (413). A swap asks the running driver to quiesce: its next 411 returns 0, it exits
# 0, and the supervisor starts the other version, which restores the state word. Interrupts that
# arrive while no driver runs just queue in the journal.
DRV_KBD = 2
KB_JN = 256  # journal entries (power of two)
KB_BATCH = 32
KB_STATE_MAGIC = 0x4B4244  # "KBD" in state bits 63:8; bits 7:0 are decoder flags
KB_MAX_RESTARTS = 5

kb_j: u16[256]
kb_head: u64  # next journal sequence number to write (free-running)
kb_ack: u64  # first entry not yet committed by a driver
kb_take: u64  # end of the batch the current driver holds
kb_state: u64  # last committed decoder state word
kb_svc: u64  # 1: the interrupt handlers feed the journal
kb_up: u64
kb_pid: u64
kb_ver: u64  # 1 or 2: the running driver
kb_quit: u64  # swap or stop requested: the driver's next recv returns 0
kb_stop: u64
kb_crash_after: u64  # fault injection: the batch holding the n-th next entry carries the crash flag
kb_q: WaitQ  # driver waiting for input
kb_supq: WaitQ  # supervisor waiting for a driver exit
kb_thread: nptr[Thread]
kb_sup: nptr[Thread]
kb_swapper: nptr[Thread]
kb_img: u64[3]
kb_sz: u64[3]
kb_in: u64
kb_dropped: u64
kb_commits: u64
kb_committed: u64  # journal entries committed by drivers
kb_drained: u64  # entries decoded by the kernel after the service stopped
kb_replayed: u64  # entries a dead driver held and a new one replayed
kb_restarts: u64
kb_swaps: u64
kb_swaps_queued: u64  # swaps with journal entries waiting
kb_chars: u64

def kbd_event(kind: u64, b: u64):
# interrupt context (interrupts off)
    spin_lock(addr(rq_lock))
    kb_in += 1
    if kb_head - kb_ack >= KB_JN:
        kb_dropped += 1
    else:
        kb_j[kb_head & (KB_JN - 1)] = u16((kind << 8) | (b & 0xFF))
        kb_head += 1
        wq_wake_all_l(addr(kb_q))
    spin_unlock(addr(rq_lock))

def kbd_cur(p: nptr[Proc]) -> u64:
# only the supervisor spawns DRV_KBD processes, one at a time (the previous one is reaped first),
# so the kind alone identifies the current instance (kb_pid is set only after it may already run)
    return p != 0 and p.drv == DRV_KBD

def sys_kbd(nr: u64, a: u64, b: u64, c: u64, d: u64) -> u64:
    p = cur_proc()
    if kbd_cur(p) == 0:
        return EPERM
    if nr == 411:
    # recv(buf, max): buf[0] = sequence number of the first entry, buf[1..n] = entries
    # (kind << 8 | byte, bit 16 = crash flag on the first entry); returns n, 0 = quiesce and exit
        m: u64[33]
        if b == 0 or b > KB_BATCH:
            return EINVAL
        if user_ptr_ok(a, (b + 1) * 8) == 0:
            return EFAULT
        f = irq_save()
        spin_lock(addr(rq_lock))
        while kb_head == kb_ack and kb_quit == 0:
            wq_block_l(addr(kb_q))
        n: u64 = 0
        if kb_quit == 0:
            n = kb_head - kb_ack
            if n > b:
                n = b
            m[0] = kb_ack
            i: u64 = 0
            while i < n:
                m[1 + i] = u64(kb_j[(kb_ack + i) & (KB_JN - 1)])
                i += 1
            if kb_crash_after != 0:
                if kb_crash_after <= n:
                    kb_crash_after = 0
                    m[1] = m[1] | 0x10000
                else:
                    kb_crash_after -= n
            kb_take = kb_ack + n
        else:
            m[0] = kb_ack
        spin_unlock(addr(rq_lock))
        irq_restore(f)
        if copy_user(a, u64(addr(m[0])), (n + 1) * 8) != 0:
            return EFAULT
        return n
    if nr == 412:
    # commit(end, chars, nchars, state): end is the sequence number after the batch
        kc: u8[64]
        if c > 64:
            return EINVAL
        if c != 0 and (user_ptr_ok(b, c) == 0 or copy_user(u64(addr(kc[0])), b, c) != 0):
            return EFAULT
        if (d >> 8) != KB_STATE_MAGIC:
            return EINVAL
        f = irq_save()
        spin_lock(addr(rq_lock))
        r: u64 = EINVAL
        if a > kb_ack and a <= kb_take:
            j: u64 = 0
            while j < c:
                tty_push_l(u64(kc[j]))
                j += 1
            kb_chars += c
            kb_committed += a - kb_ack
            kb_ack = a
            kb_state = d
            kb_commits += 1
            r = 0
        spin_unlock(addr(rq_lock))
        irq_restore(f)
        return r
    if nr == 413:
    # the last committed decoder state word (restored by a new instance)
        return kb_state
    return ENOSYS

def kbd_died():
# proc_exit of the input driver: everything after kb_ack stays in the journal for replay
    f = irq_save()
    spin_lock(addr(rq_lock))
    if kb_take > kb_ack:
        kb_replayed += kb_take - kb_ack
    kb_take = kb_ack
    kb_up = 0
    wq_wake_all_l(addr(kb_supq))
    spin_unlock(addr(rq_lock))
    irq_restore(f)

def kbd_spawn(v: u64) -> nptr[Thread]:
    a: u8[16]
    a[0] = u8(48 + v)
    a[1] = 0
    cur_thread().spawn_drv = DRV_KBD
    t = proc_spawn_args(kb_img[v], kb_sz[v], addr(a[0]))
    cur_thread().spawn_drv = 0
    return t

def kbd_start_l(v: u64):
# mark the service up before the driver can run: if it dies at once, kbd_died must see kb_up = 1
    f = irq_save()
    spin_lock(addr(rq_lock))
    kb_ver = v
    kb_quit = kb_stop
    kb_up = 1
    spin_unlock(addr(rq_lock))
    irq_restore(f)
    t = kbd_spawn(v)
    if t == 0:
        panic("input service: kbdd image rejected")
    kb_thread = t
    kb_pid = t.id

def kbd_supervisor(arg: u64):
    while 1:
        f = irq_save()
        spin_lock(addr(rq_lock))
        while kb_up:
            wq_block_l(addr(kb_supq))
        swap = kb_quit
        queued = kb_head - kb_ack
        spin_unlock(addr(rq_lock))
        irq_restore(f)
        old = kb_thread
        code: u64 = 0
        opid: u64 = 0
        if old != 0:
            opid = old.id
            code = proc_reap(old)
        kb_thread = 0
        if kb_stop:
            return
        v = kb_ver
        if swap != 0 and code == 0:
            v = 3 - v
            kb_swaps += 1
            if queued != 0:
                kb_swaps_queued += 1
        else:
            kb_restarts += 1
            puts("input supervisor: kbdd v")
            put_u64(v)
            puts(" (pid ")
            put_u64(opid)
            puts(") died with ")
            put_hex(code)
            puts(", restart ")
            put_u64(kb_restarts)
            puts(", replaying from entry ")
            put_u64(kb_ack)
            putc(10)
            if kb_restarts > KB_MAX_RESTARTS:
                puts("input supervisor: kbdd keeps dying, input goes back to the kernel\n")
                kb_svc = 0
                return
        kbd_start_l(v)

def kbd_swapper(arg: u64):
# swap v1 <-> v2 every 10 ticks while the shell session runs
    while kb_stop == 0:
        sleep_ticks(10)
        f = irq_save()
        spin_lock(addr(rq_lock))
        if kb_up and kb_quit == 0 and kb_stop == 0:
            kb_quit = 1
            wq_wake_all_l(addr(kb_q))
        spin_unlock(addr(rq_lock))
        irq_restore(f)

def kbd_service_start():
    kbd_service_start_from(0)

def kbd_service_start_from(carried: u64):
# carried = 1: journal, state word, version and counters were set from a kexec handoff (kxd_adopt)
    kb_img[1] = fs_read_file("EFI/Forge/bin/kbdd", addr(kb_sz[1]))
    kb_img[2] = fs_read_file("EFI/Forge/bin/kbdd2", addr(kb_sz[2]))
    if kb_img[1] == 0 or kb_img[2] == 0:
        panic("input service images missing")
    v: u64 = 1
    if carried != 0:
        v = kb_ver
    else:
        kb_head = 0
        kb_ack = 0
        kb_take = 0
        kb_state = KB_STATE_MAGIC << 8
    kb_stop = 0
    kbd_start_l(v)
    kb_svc = 1
    kb_sup = thread_spawn(u64(kbd_supervisor), 0)
    kb_swapper = thread_spawn(u64(kbd_swapper), 0)

def kbd_service_stop():
# quiesce the driver, then the kernel decodes whatever is still in the journal (same decoder state)
    kb_stop = 1
    sw = kb_swapper
    if sw != 0:
        thread_reap(sw)
    f = irq_save()
    spin_lock(addr(rq_lock))
    kb_quit = 1
    wq_wake_all_l(addr(kb_q))
    spin_unlock(addr(rq_lock))
    irq_restore(f)
    sp = kb_sup
    if sp != 0:
        thread_reap(sp)
    f = irq_save()
    spin_lock(addr(rq_lock))
    kb_svc = 0
    kbd_shift = kb_state & 5
    if kbd_shift != 0:
        kbd_shift = 1
    kbd_e0 = (kb_state >> 1) & 1
    while kb_ack != kb_head:
        e = u64(kb_j[kb_ack & (KB_JN - 1)])
        kb_ack += 1
        kb_drained += 1
        if (e >> 8) == 1:
            if (e & 0xFF) == 13:
                tty_push_l(10)
            else:
                tty_push_l(e & 0xFF)
        else:
            kbd_decode_l(e & 0xFF)
    spin_unlock(addr(rq_lock))
    irq_restore(f)
    kfree(kb_img[1])
    kfree(kb_img[2])

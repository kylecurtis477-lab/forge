# Phase 5.2: IPC channels. A channel is a pair of endpoint handles (File type F_CHAN, clu = Chan,
# pos = end 0/1). A message is up to CH_MSG bytes plus at most one handle, which is moved (not copied)
# from the sender's table into the receiver's. Each direction is a CH_SLOTS-deep ring; receivers block
# on rq[end] while it is empty, senders on sq[peer] while it is full. All state is guarded by rq_lock.
CH_SLOTS = 16
CH_MSG = 64
EPIPE = 0xFFFFFFFFFFFFFFE0
NO_HANDLE = 0xFFFFFFFFFFFFFFFF

struct Chan:
    head: u64[2]  # ring head of messages bound for end d
    cnt: u64[2]
    open: u64[2]
    mlen: u64[32]  # [d * CH_SLOTS + slot]
    mh: u64[32]  # moved File, or 0
    mr: u64[32]  # its rights
    mdata: u8[2048]  # [(d * CH_SLOTS + slot) * CH_MSG]
    rq0: WaitQ
    rq1: WaitQ
    sq0: WaitQ
    sq1: WaitQ

NCHAN = 16
chans: Chan[16]  # static table: File.clu holds the index, so no pointer casts
chan_used: u64[16]
chan_live: u64
chan_msgs: u64
chan_moved: u64

def chan_of_idx(i: u64) -> ptr[Chan]:
    return addr(chans[i])

def chan_of(fl: ptr[File]) -> ptr[Chan]:
    return addr(chans[fl.clu])

def chan_rq(c: ptr[Chan], e: u64) -> ptr[WaitQ]:
    if e == 0:
        return addr(c.rq0)
    return addr(c.rq1)

def chan_sq(c: ptr[Chan], e: u64) -> ptr[WaitQ]:
    if e == 0:
        return addr(c.sq0)
    return addr(c.sq1)

def sys_chan_create(ufds: u64) -> u64:
    p = cur_proc()
    if user_ptr_ok(ufds, 16) == 0:
        return EFAULT
    a = fd_free(p)
    if a == NFD:
        return EMFILE
    p.fds[a] = 1
    b = fd_free(p)
    p.fds[a] = 0
    if b == NFD:
        return EMFILE
    ci: u64 = 0
    f = irq_save()
    spin_lock(addr(rq_lock))
    while ci < NCHAN and chan_used[ci] != 0:
        ci += 1
    if ci < NCHAN:
        chan_used[ci] = 1
    spin_unlock(addr(rq_lock))
    irq_restore(f)
    if ci == NCHAN:
        return ENOMEM
    c = chan_of_idx(ci)
    d: u64 = 0
    while d < 2:
        c.head[d] = 0
        c.cnt[d] = 0
        d += 1
    c.open[0] = 1
    c.open[1] = 1
    e0 = file_new(F_CHAN)
    e0.clu = ci
    e1 = file_new(F_CHAN)
    e1.clu = ci
    e1.pos = 1
    atomic_fetch_add(addr(chan_live), 1, acq_rel)
    p.fds[a] = u64(e0)
    p.fdr[a] = CAP_READ | CAP_WRITE | CAP_DUP | CAP_XFER
    p.fds[b] = u64(e1)
    p.fdr[b] = CAP_READ | CAP_WRITE | CAP_DUP | CAP_XFER
    fds: u64[2]
    fds[0] = a
    fds[1] = b
    if copy_user(ufds, u64(addr(fds[0])), 16) != 0:
    # undo: drop both fds; the last file_put closes each end and frees the channel
        p.fds[a] = 0
        p.fdr[a] = 0
        p.fds[b] = 0
        p.fdr[b] = 0
        file_put(u64(e0))
        file_put(u64(e1))
        return EFAULT
    return 0

def chan_end_close(fl: ptr[File]):
# last reference to an endpoint went away: mark it closed, wake the peer; free with the second end
    c = chan_of(fl)
    e = fl.pos
    f = irq_save()
    spin_lock(addr(rq_lock))
    c.open[e] = 0
    wq_wake_all_l(chan_rq(c, 1 - e))
    wq_wake_all_l(chan_sq(c, 1 - e))
    wq_wake_all_l(chan_sq(c, e))
    last = c.open[1 - e] == 0
    spin_unlock(addr(rq_lock))
    irq_restore(f)
    if last:
        d: u64 = 0
        while d < 2:
            while c.cnt[d] != 0:
                s = d * CH_SLOTS + c.head[d]
                file_put(c.mh[s])
                c.head[d] = (c.head[d] + 1) % CH_SLOTS
                c.cnt[d] -= 1
            d += 1
        chan_used[fl.clu] = 0
        atomic_fetch_add(addr(chan_live), 0xFFFFFFFFFFFFFFFF, acq_rel)

def sys_chan_send(fd: u64, ubuf: u64, n: u64, hfd: u64) -> u64:
    kb: u8[64]
    p = cur_proc()
    fl = fd_get(p, fd)
    if u64(fl) == 0:
        return EBADF
    if fl.type != F_CHAN:
        return EINVAL
    if (p.fdr[fd] & CAP_WRITE) == 0:
        return EPERM
    if n == 0 or n > CH_MSG or user_ptr_ok(ubuf, n) == 0:
        return EINVAL
    if copy_user(u64(addr(kb[0])), ubuf, n) != 0:
        return EFAULT
    if hfd != NO_HANDLE:
        if hfd >= NFD or p.fds[hfd] == 0:
            return EBADF
        if hfd == fd:
            return EINVAL
        if (p.fdr[hfd] & CAP_XFER) == 0:
            return EPERM
    c = chan_of(fl)
    d = 1 - fl.pos
    f = irq_save()
    spin_lock(addr(rq_lock))
    while c.cnt[d] == CH_SLOTS and c.open[d]:
        wq_block_l(chan_sq(c, d))
    if c.open[d] == 0:
        spin_unlock(addr(rq_lock))
        irq_restore(f)
        return EPIPE
    s = d * CH_SLOTS + (c.head[d] + c.cnt[d]) % CH_SLOTS
    c.mlen[s] = n
    i: u64 = 0
    while i < n:
        c.mdata[s * CH_MSG + i] = kb[i]
        i += 1
    c.mh[s] = 0
    c.mr[s] = 0
    if hfd != NO_HANDLE:
    # move: the handle leaves the sender's table with the message
        c.mh[s] = p.fds[hfd]
        c.mr[s] = p.fdr[hfd]
        p.fds[hfd] = 0
        p.fdr[hfd] = 0
        chan_moved += 1
    c.cnt[d] += 1
    chan_msgs += 1
    wq_wake_one_l(chan_rq(c, d))
    spin_unlock(addr(rq_lock))
    irq_restore(f)
    return n

def sys_chan_recv(fd: u64, ubuf: u64, n: u64, uh: u64) -> u64:
    kb: u8[64]
    p = cur_proc()
    fl = fd_get(p, fd)
    if u64(fl) == 0:
        return EBADF
    if fl.type != F_CHAN:
        return EINVAL
    if (p.fdr[fd] & CAP_READ) == 0:
        return EPERM
    if user_ptr_ok(ubuf, n) == 0 or user_ptr_ok(uh, 8) == 0:
        return EFAULT
    # touch the destinations before dequeuing, so a fault cannot lose the message
    pre = n
    if pre > 64:
        pre = 64
    z: u64 = NO_HANDLE
    zi: u64 = 0
    while zi < 64:
        kb[zi] = 0
        zi += 1
    if copy_user(uh, u64(addr(z)), 8) != 0 or (pre > 0 and copy_user(ubuf, u64(addr(kb[0])), pre) != 0):
        return EFAULT
    # reserve an fd slot before dequeuing, so a message carrying a handle is never dropped for EMFILE
    rf = fd_free(p)
    if rf == NFD:
        return EMFILE
    p.fds[rf] = 1
    c = chan_of(fl)
    e = fl.pos
    f = irq_save()
    spin_lock(addr(rq_lock))
    while c.cnt[e] == 0:
        if c.open[1 - e] == 0:
            spin_unlock(addr(rq_lock))
            irq_restore(f)
            p.fds[rf] = 0
            return 0
        wq_block_l(chan_rq(c, e))
    s = e * CH_SLOTS + c.head[e]
    m = c.mlen[s]
    i: u64 = 0
    while i < m:
        kb[i] = c.mdata[s * CH_MSG + i]
        i += 1
    h = c.mh[s]
    hr = c.mr[s]
    c.head[e] = (c.head[e] + 1) % CH_SLOTS
    c.cnt[e] -= 1
    wq_wake_one_l(chan_sq(c, e))
    spin_unlock(addr(rq_lock))
    irq_restore(f)
    hf = NO_HANDLE
    if h != 0:
        hf = rf
        p.fds[hf] = h
        p.fdr[hf] = hr
    else:
        p.fds[rf] = 0
    if m > n:
        m = n
    if copy_user(ubuf, u64(addr(kb[0])), m) != 0 or copy_user(uh, u64(addr(hf)), 8) != 0:
        return EFAULT
    return m

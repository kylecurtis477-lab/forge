# Phase 10.2: the grant ledger -- revocable, logged permissions for sandboxed apps.
#
# When sh launches an app through its manifest (sys_exec_sandboxed), every handle the launcher
# installed becomes a *grant*: one ledger entry naming the app, the resource path, the rights, the pid,
# and a state. The grant id is stamped on the File object itself (File.gid), so every handle derived
# from it -- a dup, a fork's copy, a handle passed over a channel, a file opened through a granted
# directory with openat -- carries the same id and is cut off by the same revocation.
#
# Every use of a handle (read, write, getdents, openat through it, dup of it; a zero-length read
# counts, so a program can poll) calls gr_use(), which counts the use while the grant is active and
# returns EACCES once it is revoked. Revocation therefore takes effect at the app's very next
# syscall on that handle, mid-read, with no cooperation from the app.
#
# Syscalls (both need the 'grant' capability, CAPB_GRANT, which only sh declares):
#   428 grant_revoke(app_name) -> number of grants revoked (all active grants of that app)
#   429 grant_list()           -> number of entries; the kernel prints the ledger to the console
# States: 1 active, 2 revoked, 3 ended (the app exited; kept as history). Ids are never reused. A new
# grant takes a free entry or the oldest non-active one, so an active grant never ages out; with all
# GR_N entries active the handle is closed instead of being handed out untracked (fail closed).
GR_N = 64
GR_ACTIVE = 1
GR_REVOKED = 2
GR_ENDED = 3
gr_next: u64  # ids handed out so far
gr_id: u64[64]
gr_pid: u64[64]
gr_state: u64[64]
gr_rights: u64[64]
gr_uses: u64[64]
gr_denied: u64[64]
gr_app: u8[1024]  # 16 bytes per entry
gr_path: u8[2048]  # 32 bytes per entry
gr_lock: u64
gr_revocations: u64
gr_denials: u64

def gr_ix(g: u64) -> u64:
# ledger index of grant id g, or GR_N when g is 0 (not a granted handle)
    if g == 0:
        return GR_N
    i: u64 = 0
    while i < GR_N:
        if gr_id[i] == g:
            return i
        i += 1
    return GR_N

def gr_pick() -> u64:
# caller holds gr_lock: a free entry, else the oldest non-active one, else GR_N
    b = GR_N
    i: u64 = 0
    while i < GR_N:
        if gr_id[i] == 0:
            return i
        if gr_state[i] != GR_ACTIVE and (b == GR_N or gr_id[i] < gr_id[b]):
            b = i
        i += 1
    return b

def gr_new(pid: u64, sp: u64, fl: ptr[File], rights: u64) -> u64:
# a ledger entry for handle fl of the sandboxed pid whose name sits in sandbox slot sp
    f = irq_save()
    spin_lock(addr(gr_lock))
    i = gr_pick()
    g: u64 = 0
    if i != GR_N:
        gr_next += 1
        g = gr_next
        gr_id[i] = 0xFFFFFFFFFFFFFFFF  # reserved while being filled
        gr_state[i] = GR_ACTIVE
    spin_unlock(addr(gr_lock))
    irq_restore(f)
    if i == GR_N:
        return 0
    gr_pid[i] = pid
    gr_state[i] = GR_ACTIVE
    gr_rights[i] = rights
    gr_uses[i] = 0
    gr_denied[i] = 0
    k: u64 = 0
    while k < 15 and sb_name[sp * 16 + k] != 0:
        gr_app[i * 16 + k] = sb_name[sp * 16 + k]
        k += 1
    gr_app[i * 16 + k] = 0
    k = 0
    while k < 31 and fl.lab[k] != 0:
        gr_path[i * 32 + k] = fl.lab[k]
        k += 1
    gr_path[i * 32 + k] = 0
    gr_id[i] = g
    return g

def gr_pbegin() -> u64:
# a ledger message is printed whole under the console lock (the one user write() takes), so it never
# interleaves with an app's output or with a message from another CPU
    spin_lock_irq_con()
    return 0

def gr_pend(f: u64):
    spin_unlock_irq_con()

def gr_desc(i: u64):
    puts("grant ")
    put_u64(gr_id[i])
    puts(" (")
    puts(addr(gr_app[i * 16]))
    puts(": ")
    if (gr_rights[i] & CAP_LOOKUP) != 0:
        puts("dir ")
    else:
        puts("read ")
    puts(addr(gr_path[i * 32]))
    puts(")")

def gr_use(g: u64, what: ptr[u8]) -> u64:
# 0 = allowed (and counted); EACCES = the grant behind this handle was revoked
    i = gr_ix(g)
    if i == GR_N:
        return 0
    if gr_state[i] == GR_REVOKED:
        gr_denied[i] += 1
        atomic_fetch_add(addr(gr_denials), 1, acq_rel)
        if gr_denied[i] == 1:
            f = gr_pbegin()
            gr_desc(i)
            puts(": revoked; pid ")
            put_u64(cur_proc().pid)
            puts(" ")
            puts(what)
            puts(" denied (EACCES)\n")
            gr_pend(f)
        return EACCES
    gr_uses[i] += 1
    return 0

def gr_fd(p: ptr[Proc], fd: u64, what: ptr[u8]) -> u64:
    fl = fd_get(p, fd)
    if u64(fl) == 0:
        return 0
    return gr_use(fl.gid, what)

def gr_enter(p: ptr[Proc], sp: u64):
# sandbox_enter: each launcher-installed handle (fd > 2) becomes a ledger entry
    i: u64 = 3
    while i < NFD:
        fl = fd_get(p, i)
        if u64(fl) != 0 and fl.gid == 0 and u64(fl) != console():
            fl.gid = gr_new(p.pid, sp, fl, p.fdr[i])
            if fl.gid == 0:
                puts("grants: ledger full, handle not granted\n")
                file_put(p.fds[i])
                p.fds[i] = 0
                p.fdr[i] = 0
            else:
                f = gr_pbegin()
                puts("grants: ")
                gr_desc(gr_ix(fl.gid))
                puts(" active for pid ")
                put_u64(p.pid)
                puts("\n")
                gr_pend(f)
        i += 1

def gr_exit(pid: u64):
# the app is gone: its grants end (kept as history)
    i: u64 = 0
    while i < GR_N:
        if gr_id[i] != 0 and gr_pid[i] == pid and gr_state[i] == GR_ACTIVE:
            gr_state[i] = GR_ENDED
        i += 1

def gr_name_eq(i: u64, s: slice[u8]) -> u64:
    k: u64 = 0
    while k < 16:
        if gr_app[i * 16 + k] != s[k]:
            return 0
        if s[k] == 0:
            return 1
        k += 1
    return 1

def sys_grant_revoke(uname: u64) -> u64:
    nm: u8[16]
    if copy_str_user(u64(addr(nm[0])), uname, 16) > 15:
        return EFAULT
    n: u64 = 0
    i: u64 = 0
    while i < GR_N:
        if gr_id[i] != 0 and gr_state[i] == GR_ACTIVE and gr_name_eq(i, nm[:]) == 1:
            gr_state[i] = GR_REVOKED
            n += 1
            atomic_fetch_add(addr(gr_revocations), 1, acq_rel)
            f = gr_pbegin()
            puts("grants: revoked ")
            gr_desc(i)
            puts(" from pid ")
            put_u64(gr_pid[i])
            puts(" after ")
            put_u64(gr_uses[i])
            puts(" uses\n")
            gr_pend(f)
        i += 1
    if n == 0:
        return ENOENT
    return n

def gr_state_name(s: u64) -> ptr[u8]:
    if s == GR_ACTIVE:
        return "active"
    if s == GR_REVOKED:
        return "revoked"
    return "ended"

def sys_grant_list() -> u64:
    n: u64 = 0
    i: u64 = 0
    while i < GR_N:
        if gr_id[i] != 0:
            n += 1
        i += 1
    f = gr_pbegin()
    puts("grants: ledger, ")
    put_u64(n)
    puts(" entries\n")
    g: u64 = 1
    while g <= gr_next:
        j = gr_ix(g)
        if j != GR_N and gr_id[j] == g:
            puts("  ")
            gr_desc(j)
            puts(" pid ")
            put_u64(gr_pid[j])
            puts(" ")
            puts(gr_state_name(gr_state[j]))
            puts(", ")
            put_u64(gr_uses[j])
            puts(" uses, ")
            put_u64(gr_denied[j])
            puts(" denied\n")
        g += 1
    gr_pend(f)
    return n

# Phase 10.3: the grant audit log -- every grant, use, denial, revocation and end, in order.
#
# grant.fsi calls au_rec() for each event on a granted handle. A record names the grant, the pid, the
# event kind, the operation, how many times it happened and how many bytes moved. Consecutive uses of
# the same grant by the same pid with the same operation are coalesced into one record with a count
# (a program polling with zero-length reads would otherwise flood the log), so every single use is
# counted, in order, while the log stays small. Records are kept in a ring of AU_N; au_seq counts all
# records ever written, so eviction is visible (the dump says how many were lost) rather than silent.
#
# Syscall 430 audit_dump() (capability 'grant', which only sh declares) prints the log and then
# cross-checks it against the ledger: for every grant, the uses and denials in the log must add up to
# the ledger's counters. The check is skipped, and says so, when records have been evicted.
# Limits (docs/privacy.md): the log is in kernel memory only -- not persisted, not carried across
# kexec, not signed; a compromised kernel can rewrite it.
AU_N = 256
AU_GRANT = 1
AU_USE = 2
AU_DENY = 3
AU_REVOKE = 4
AU_END = 5
au_seq: u64  # records ever written
au_lock: u64
au_kind: u64[256]
au_gid: u64[256]
au_pid: u64[256]
au_op: u64[256]
au_n: u64[256]
au_bytes: u64[256]
au_tick: u64[256]

def au_opcode(what: ptr[u8]) -> u64:
# read 1, write 2, getdents 3, openat 4, dup 5, poll 6
    c = what[0]
    if c == 114:
        return 1
    if c == 119:
        return 2
    if c == 103:
        return 3
    if c == 111:
        return 4
    if c == 100:
        return 5
    return 6

def au_opname(o: u64) -> ptr[u8]:
    if o == 1:
        return "read"
    if o == 2:
        return "write"
    if o == 3:
        return "getdents"
    if o == 4:
        return "openat"
    if o == 5:
        return "dup"
    if o == 6:
        return "poll"
    return "-"

def au_rec(kind: u64, g: u64, pid: u64, op: u64):
    f = irq_save()
    spin_lock(addr(au_lock))
    if au_seq > 0 and (kind == AU_USE or kind == AU_DENY):
        j = (au_seq - 1) % AU_N
        if au_kind[j] == kind and au_gid[j] == g and au_pid[j] == pid and au_op[j] == op:
            au_n[j] += 1
            spin_unlock(addr(au_lock))
            irq_restore(f)
            return
    i = au_seq % AU_N
    au_kind[i] = kind
    au_gid[i] = g
    au_pid[i] = pid
    au_op[i] = op
    au_n[i] = 1
    au_bytes[i] = 0
    au_tick[i] = lapic_ticks
    au_seq += 1
    spin_unlock(addr(au_lock))
    irq_restore(f)

def au_moved(g: u64, r: u64):
# bytes a counted read/write moved; added to that grant's newest use record
    if g == 0 or r == 0 or r >= 0xFFFFFFFFFFFFF000:
        return
    f = irq_save()
    spin_lock(addr(au_lock))
    if au_seq > 0:
        j = (au_seq - 1) % AU_N
        if au_kind[j] == AU_USE and au_gid[j] == g:
            au_bytes[j] += r
    spin_unlock(addr(au_lock))
    irq_restore(f)

def au_sum(g: u64, kind: u64, lo: u64) -> u64:
    s: u64 = 0
    q = lo
    while q < au_seq:
        j = q % AU_N
        if au_gid[j] == g and au_kind[j] == kind:
            s += au_n[j]
        q += 1
    return s

def au_line(j: u64, q: u64):
    puts("  #")
    put_u64(q)
    puts(" grant ")
    put_u64(au_gid[j])
    puts(" pid ")
    put_u64(au_pid[j])
    puts(": ")
    k = au_kind[j]
    if k == AU_GRANT:
        i = gr_ix(au_gid[j])
        puts("granted")
        if i != GR_N:
            puts(" to ")
            puts(addr(gr_app[i * 16]))
            puts(", ")
            puts(addr(gr_path[i * 32]))
    if k == AU_USE:
        puts(au_opname(au_op[j]))
        puts(" x")
        put_u64(au_n[j])
        if au_bytes[j] != 0:
            puts(", ")
            put_u64(au_bytes[j])
            puts(" bytes")
    if k == AU_DENY:
        puts(au_opname(au_op[j]))
        puts(" denied x")
        put_u64(au_n[j])
    if k == AU_REVOKE:
        puts("revoked by pid ")
        put_u64(au_op[j])
    if k == AU_END:
        puts("ended (app exited)")
    puts("\n")

def sys_audit_dump() -> u64:
    f = gr_pbegin()
    spin_lock(addr(au_lock))
    lo: u64 = 0
    if au_seq > AU_N:
        lo = au_seq - AU_N
    puts("audit: ")
    put_u64(au_seq)
    puts(" records, ")
    put_u64(lo)
    puts(" evicted\n")
    q = lo
    while q < au_seq:
        au_line(q % AU_N, q)
        q += 1
    if lo != 0:
        puts("audit: ledger check skipped (records evicted)\n")
    else:
        bad: u64 = 0
        ng: u64 = 0
        i: u64 = 0
        while i < GR_N:
            if gr_id[i] != 0 and gr_id[i] != 0xFFFFFFFFFFFFFFFF:
                ng += 1
                if au_sum(gr_id[i], AU_USE, 0) != gr_uses[i] or au_sum(gr_id[i], AU_DENY, 0) != gr_denied[i]:
                    bad += 1
            i += 1
        if bad == 0:
            puts("audit: ledger check: uses and denials match the ledger for all ")
            put_u64(ng)
            puts(" grants\n")
        else:
            puts("audit: ledger check FAILED for ")
            put_u64(bad)
            puts(" grants\n")
    n = au_seq
    spin_unlock(addr(au_lock))
    gr_pend(f)
    return n

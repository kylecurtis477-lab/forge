# Phase 9.2b: the object store's write-ahead log persisted on the Forge partition. The log is one Forge FS
# file, objects.log (OD_NB blocks, reserved zero-filled); each commit writes the log blocks it dirtied and
# flushes before returning, so a returned commit is durable. Mount replays the log (obj.fsi ob_replay),
# drops a torn or corrupt tail and zeroes it on disk so later appends can never revive stale records.
# Only Forge FS calls are used, so every write stays inside the explicitly created Forge partition.
OD_NB = 64
od_st: u64
od_synced: u64
od_torn: u64
od_commits: u64

def od_reserve() -> u64:
    if ffs_ok == 0 or ffs_find("objects.log") != FFS_NI:
        return 0
    slot: u64 = 0
    while slot < FFS_NI and ffs_it[slot * 8] != 0:
        slot += 1
    if slot == FFS_NI:
        return 0
    st = ffs_alloc(OD_NB)
    if st == 0:
        return 0
    i: u64 = 0
    while i < 512:
        ffs_b[i] = 0
        i += 1
    b: u64 = 0
    while b < OD_NB:
        if ffs_put(st + b, 0) == 0:
            return 0
        b += 1
    if nvme_flush() == 0:
        return 0
    ffs_pack("objects.log")
    k = slot * 8
    i = 0
    while i < 5:
        ffs_it[k + i] = ffs_nm[i]
        i += 1
    ffs_it[k + 5] = OD_NB * 4096
    ffs_it[k + 6] = st
    ffs_it[k + 7] = OD_NB
    if ffs_commit() == 0:
        return 0
    od_st = st
    return 1

def od_put(b: u64) -> u64:
    i: u64 = 0
    while i < 512:
        ffs_b[i] = ob_log[b * 512 + i]
        i += 1
    return ffs_put(od_st + b, 0)

def od_sync() -> u64:
# write every log block touched since the last sync, then flush: the commit is durable on return
    if ob_lp == od_synced:
        return 1
    b = od_synced / 512
    e = (ob_lp - 1) / 512
    while b <= e:
        if od_put(b) == 0:
            return 0
        b += 1
    if nvme_flush() == 0:
        return 0
    od_synced = ob_lp
    od_commits += 1
    return 1

def od_open() -> u64:
# 0 = unavailable, 1 = recovered an existing log, 2 = created an empty one
    od_torn = 0
    od_commits = 0
    i = ffs_find("objects.log")
    if i == FFS_NI:
        if od_reserve() == 0:
            return 0
        ob_reset()
        ob_log_clear()
        w: u64 = 0
        while w < OB_NL:
            ob_log[w] = 0
            w += 1
        od_synced = 0
        return 2
    if ffs_it[i * 8 + 7] != OD_NB:
        return 0
    od_st = ffs_it[i * 8 + 6]
    b: u64 = 0
    while b < OD_NB:
        if ffs_get(od_st + b, 0) == 0:
            return 0
        k: u64 = 0
        while k < 512:
            ob_log[b * 512 + k] = ffs_b[k]
            k += 1
        b += 1
    ob_replay(OB_NL)
    # anything after the last good record is a torn/corrupt tail: zero it in memory and on disk
    last: u64 = 0
    p = ob_lp
    while p < OB_NL:
        if ob_log[p] != 0:
            od_torn += 1
            ob_log[p] = 0
            last = p
        p += 1
    if od_torn != 0:
        b = ob_lp / 512
        while b <= last / 512:
            if od_put(b) == 0:
                return 0
            b += 1
        if nvme_flush() == 0:
            return 0
    od_synced = ob_lp
    return 1

def od_fp() -> u64:
    h: u64 = 0xcbf29ce484222325
    o: u64 = 0
    while o < ob_nobj:
        n = obj_read(o)
        if n != OB_ERR:
            i: u64 = 0
            while i < n:
                h = (h ^ ob_out[i]) * 0x100000001b3
                i += 1
        h = (h ^ o) * 0x100000001b3
        o += 1
    return h ^ ob_clock

def od_put3(a: u64, b: u64, c: u64):
    ob_in[0] = a
    ob_in[1] = b
    ob_in[2] = c

def od_commit(o: u64) -> u64:
    if obj_write(o, 3) == OB_ERR or od_sync() == 0:
        panic("objstore: commit failed")
    return ob_clock

# Phase 9.4: whole-system rewind after a bad change. Type 2 objects are the "system configuration"
# (field 0 = slot, field 1 = 100 + slot, field 2 = 1 when healthy). A bad change flips every slot to
# broken, durably; the health check notices, sys_rewind takes every object back to the last good
# clock (each rewind is a new version, so the bad change stays inspectable in history), and the next
# boot finds the rewound state on disk.
OD_NCFG = 8

def od_health() -> u64:
# number of broken configuration slots (OB_ERR if the configuration is missing)
    bad: u64 = 0
    k: u64 = 0
    o = obj_type_first(2)
    while o != OB_ERR:
        if obj_read(o) == OB_ERR or ob_out[1] != 100 + ob_out[0] or ob_out[2] != 1:
            bad += 1
        k += 1
        o = obj_type_next(o)
    if k != OD_NCFG:
        return OB_ERR
    return bad

def od_sfp() -> u64:
# state fingerprint: current payloads of every object (no clock, so a rewind can reproduce it)
    return od_fp() ^ ob_clock

def objrw_test():
    if ob_type_def(2, 3) != 0:
        panic("objstore: config type failed")
    k: u64 = 0
    while k < OD_NCFG:
        o = obj_new(2)
        od_put3(k, 100 + k, 1)
        od_commit(o)
        k += 1
    tg = ob_clock
    fg = od_sfp()
    if od_health() != 0:
        panic("objstore: config unhealthy before the change")
    # the bad change: every slot broken, and object 1 overwritten, each write durable
    o = obj_type_first(2)
    k = 0
    while o != OB_ERR:
        od_put3(k, 7, 0)
        od_commit(o)
        k += 1
        o = obj_type_next(o)
    od_put3(9, 9, 9)
    od_commit(1)
    hb = od_health()
    puts("objstore: bad change committed, health check: ")
    put_u64(hb)
    puts(" broken slots\n")
    if hb != OD_NCFG:
        panic("objstore: bad change not detected")
    n = sys_rewind(tg)
    if od_sync() == 0:
        panic("objstore: rewind commit failed")
    if od_health() != 0 or od_sfp() != fg or n != OD_NCFG + 1:
        panic("objstore: system rewind did not restore the good state")
    puts("objstore: system rewound to clock ")
    put_u64(tg)
    puts(" (")
    put_u64(n)
    puts(" objects restored, bad change still in history), state fingerprint ")
    put_hex(fg)
    putc(10)
    puts("objstore system rewind OK\n")

def objrw_verify():
# boot after the rewind: the rewound state must be what the disk holds
    puts("objstore: after reboot, health check: ")
    put_u64(od_health())
    puts(" broken slots, state fingerprint ")
    put_hex(od_sfp())
    putc(10)
    if od_health() != 0:
        panic("objstore: rewound state lost")
    puts("objstore system rewind persisted OK\n")

def objstore_test():
    r = od_open()
    if r == 0:
        panic("objstore: objects.log unavailable")
    if r == 1 and ob_tf[2] != 0:
        objrw_verify()
        return
    if r == 2:
        if ob_type_def(1, 3) != 0 or od_sync() == 0:
            panic("objstore: type def failed")
        a = obj_new(1)
        b = obj_new(1)
        od_sync()
        j: u64 = 0
        while j < 50:
            od_put3(j, j * j, 7)
            od_commit(a)
            if j % 5 == 0:
                od_put3(j, 1000 + j, 8)
                od_commit(b)
            j += 1
        puts("objstore: objects.log created on the Forge partition, ")
        put_u64(od_commits)
        puts(" durable commits, clock ")
        put_u64(ob_clock)
        puts(", fingerprint ")
        put_hex(od_fp())
        putc(10)
        # crash: a version record header reaches the disk without the rest of the record
        p = ob_lp
        ob_log[p] = OB_RV
        ob_log[p + 1] = a
        ob_log[p + 2] = 0
        if od_put(p / 512) == 0 or nvme_flush() == 0:
            panic("objstore: torn write failed")
        puts("objstore: crash simulated (torn record after the last commit)\n")
        return
    puts("objstore: recovered ")
    put_u64(ob_nver)
    puts(" versions of ")
    put_u64(ob_nobj)
    puts(" objects, clock ")
    put_u64(ob_clock)
    puts(", torn tail dropped (")
    put_u64(od_torn)
    puts(" words), fingerprint ")
    put_hex(od_fp())
    putc(10)
    # every committed version must be readable at its timestamp with the values that were written
    bad: u64 = 0
    i: u64 = 0
    t0: u64 = 0
    v: u64 = 0
    while v < ob_nver:
        if ov_obj[v] == 0:
            if i == 0:
                t0 = ov_ts[v]
            obj_read_at(0, ov_ts[v])
            if ob_out[0] != i or ob_out[1] != i * i or ob_out[2] != 7:
                bad += 1
            i += 1
        v += 1
    if bad != 0 or i != 50 or obj_field(1, 1) != 1045:
        panic("objstore: recovered history wrong")
    # keep working after recovery: rewind a to its first version, then new commits
    if obj_rewind(0, t0) == OB_ERR or od_sync() == 0 or obj_field(0, 1) != 0:
        panic("objstore: rewind failed")
    od_put3(1, 2, 3)
    od_commit(1)
    puts("objstore: all 50 committed versions of object 0 read back, rewind + new commit durable\n")
    puts("objstore crash recovery OK\n")
    objrw_test()
    puts("objstore: boot done, state fingerprint ")
    put_hex(od_sfp())
    putc(10)

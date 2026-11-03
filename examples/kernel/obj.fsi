# Phase 9.1: the living object store core. Typed objects (a schema gives each type a fixed field count),
# every write is a new immutable version (copy-on-write: old versions are never touched) whose payload is
# a content-addressed blob (FNV-1a over the words; identical payloads share one blob), and every version
# carries a logical timestamp, so any object can be read or rewound to any earlier time. A rewind is
# itself a new version (history is never lost), and obj_diff compares an object at two times.
# Fully safe Forge: fixed tables, no raw pointers, so the kernel and hosted tests share it.
OB_ERR = 0xFFFFFFFFFFFFFFFF
OB_NT = 32  # types
OB_NO = 256  # objects
OB_NV = 2048  # versions
OB_NB = 1024  # blobs
OB_NW = 16384  # blob words
OB_MAXW = 64  # words per object payload
ob_tf: u64[32]  # type -> field count (0 = undefined)
ob_otype: u64[256]
ob_ohead: u64[256]  # object -> newest version + 1 (0 = none)
ob_nobj: u64
ov_obj: u64[2048]
ov_blob: u64[2048]
ov_prev: u64[2048]  # previous version + 1
ov_ts: u64[2048]
ob_nver: u64
ob_bh: u64[1024]
ob_bo: u64[1024]
ob_bn: u64[1024]
ob_nblob: u64
ob_data: u64[16384]
ob_used: u64
ob_clock: u64
ob_dedup: u64
ob_in: u64[64]  # payload staged for obj_write
ob_out: u64[64]  # payload produced by obj_read / obj_read_at
# 9.2 write-ahead log: every mutation appends one self-checking record (tag, fields, FNV of the record);
# records only refer to earlier ones (a version follows its blob and object), so any prefix of the log
# is a consistent store, and replay stops at the first torn or corrupt record.
OB_NL = 65536
OB_RT = 0x4F42540000000001  # type: t, nfields
OB_RO = 0x4F424F0000000002  # object: id, type
OB_RB = 0x4F42420000000003  # blob: n, words...
OB_RV = 0x4F42560000000004  # version: obj, blob, ts
ob_log: u64[65536]
ob_lp: u64
ob_lr: u64  # 1 while replaying (do not log)
ob_lfull: u64

def ob_reset():
    ob_nobj = 0
    ob_nver = 0
    ob_nblob = 0
    ob_used = 0
    ob_clock = 0
    ob_dedup = 0
    ob_lfull = 0
    i: u64 = 0
    while i < OB_NT:
        ob_tf[i] = 0
        i += 1

def ob_lsum(a: u64, n: u64) -> u64:
    h: u64 = 0xcbf29ce484222325
    i: u64 = 0
    while i < n:
        h = (h ^ ob_log[a + i]) * 0x100000001b3
        i += 1
    return h

def ob_lrec(tag: u64, a: u64, b: u64, c: u64, nw: u64) -> u64:
# append tag, a, b, c (blob records: tag, n, then the n staged words) and the checksum
    if ob_lr == 1:
        return 0
    n: u64 = 4
    if tag == OB_RB:
        n = 2 + nw
    elif tag != OB_RV:
        n = 3
    if ob_lp + n + 1 > OB_NL:
        ob_lfull = 1
        return OB_ERR
    p = ob_lp
    ob_log[p] = tag
    if tag == OB_RB:
        ob_log[p + 1] = nw
        i: u64 = 0
        while i < nw:
            ob_log[p + 2 + i] = ob_in[i]
            i += 1
    else:
        ob_log[p + 1] = a
        ob_log[p + 2] = b
        if tag == OB_RV:
            ob_log[p + 3] = c
    ob_log[p + n] = ob_lsum(p, n)
    ob_lp = p + n + 1
    return 0

def ob_log_clear():
    ob_lp = 0
    ob_lr = 0

def ob_type_def(t: u64, nfields: u64) -> u64:
    if t == 0 or t >= OB_NT or nfields == 0 or nfields > OB_MAXW:
        return OB_ERR
    if ob_lrec(OB_RT, t, nfields, 0, 0) == OB_ERR:
        return OB_ERR
    ob_tf[t] = nfields
    return 0

def ob_hash(n: u64) -> u64:
    h: u64 = 0xcbf29ce484222325
    i: u64 = 0
    while i < n:
        w = ob_in[i]
        k: u64 = 0
        while k < 8:
            h = (h ^ ((w >> (k * 8)) & 255)) * 0x100000001b3
            k += 1
        i += 1
    return h ^ n

def ob_blob_put(n: u64) -> u64:
# content-addressed: same hash and same words -> the existing blob
    h = ob_hash(n)
    b: u64 = 0
    while b < ob_nblob:
        if ob_bh[b] == h and ob_bn[b] == n:
            same: u64 = 1
            i: u64 = 0
            while i < n:
                if ob_data[ob_bo[b] + i] != ob_in[i]:
                    same = 0
                i += 1
            if same == 1:
                ob_dedup += 1
                return b
        b += 1
    if ob_nblob >= OB_NB or ob_used + n > OB_NW:
        return OB_ERR
    if ob_lrec(OB_RB, 0, 0, 0, n) == OB_ERR:
        return OB_ERR
    b = ob_nblob
    ob_bh[b] = h
    ob_bo[b] = ob_used
    ob_bn[b] = n
    i2: u64 = 0
    while i2 < n:
        ob_data[ob_used + i2] = ob_in[i2]
        i2 += 1
    ob_used += n
    ob_nblob += 1
    return b

def obj_new(t: u64) -> u64:
    if t == 0 or t >= OB_NT or ob_tf[t] == 0 or ob_nobj >= OB_NO:
        return OB_ERR
    o = ob_nobj
    if ob_lrec(OB_RO, o, t, 0, 0) == OB_ERR:
        return OB_ERR
    ob_otype[o] = t
    ob_ohead[o] = 0
    ob_nobj += 1
    return o

def ob_add_ver(o: u64, b: u64) -> u64:
    if ob_nver >= OB_NV:
        return OB_ERR
    v = ob_nver
    if ob_lrec(OB_RV, o, b, ob_clock + 1, 0) == OB_ERR:
        return OB_ERR
    ob_clock += 1
    ov_obj[v] = o
    ov_blob[v] = b
    ov_prev[v] = ob_ohead[o]
    ov_ts[v] = ob_clock
    ob_ohead[o] = v + 1
    ob_nver += 1
    return ob_clock

def obj_write(o: u64, n: u64) -> u64:
# typed: the payload must have exactly the type's field count; returns the new version's timestamp
    if o >= ob_nobj or n != ob_tf[ob_otype[o]]:
        return OB_ERR
    b = ob_blob_put(n)
    if b == OB_ERR:
        return OB_ERR
    return ob_add_ver(o, b)

def ob_ver_at(o: u64, ts: u64) -> u64:
# newest version of o with timestamp <= ts, + 1 (0 = none)
    if o >= ob_nobj:
        return 0
    v = ob_ohead[o]
    while v != 0 and ov_ts[v - 1] > ts:
        v = ov_prev[v - 1]
    return v

def ob_load(v: u64) -> u64:
    b = ov_blob[v - 1]
    n = ob_bn[b]
    i: u64 = 0
    while i < n:
        ob_out[i] = ob_data[ob_bo[b] + i]
        i += 1
    return n

def obj_read_at(o: u64, ts: u64) -> u64:
    v = ob_ver_at(o, ts)
    if v == 0:
        return OB_ERR
    return ob_load(v)

def obj_read(o: u64) -> u64:
    return obj_read_at(o, ob_clock)

def obj_field(o: u64, f: u64) -> u64:
    n = obj_read(o)
    if n == OB_ERR or f >= n:
        return OB_ERR
    return ob_out[f]

def obj_nversions(o: u64) -> u64:
    c: u64 = 0
    v = ob_ver_at(o, ob_clock)
    while v != 0:
        c += 1
        v = ov_prev[v - 1]
    return c

def obj_rewind(o: u64, ts: u64) -> u64:
# the state at ts becomes the newest version again (shares its blob); later versions stay in history
    v = ob_ver_at(o, ts)
    if v == 0:
        return OB_ERR
    if ob_ohead[o] == v:
        return ob_clock
    return ob_add_ver(o, ov_blob[v - 1])

def sys_rewind(ts: u64) -> u64:
# rewind every object that existed at ts; returns how many changed
    c: u64 = 0
    o: u64 = 0
    while o < ob_nobj:
        v = ob_ver_at(o, ts)
        if v != 0 and ob_ohead[o] != v:
            ob_add_ver(o, ov_blob[v - 1])
            c += 1
        o += 1
    return c

def obj_diff(o: u64, t1: u64, t2: u64) -> u64:
# number of fields that differ between o at t1 and at t2 (OB_ERR if o did not exist at either)
    v1 = ob_ver_at(o, t1)
    v2 = ob_ver_at(o, t2)
    if v1 == 0 or v2 == 0:
        return OB_ERR
    b1 = ov_blob[v1 - 1]
    b2 = ov_blob[v2 - 1]
    if b1 == b2:
        return 0
    c: u64 = 0
    i: u64 = 0
    while i < ob_bn[b1]:
        if ob_data[ob_bo[b1] + i] != ob_data[ob_bo[b2] + i]:
            c += 1
        i += 1
    return c

def obj_find(t: u64, f: u64, val: u64, start: u64) -> u64:
# first object id >= start of type t whose current field f == val (OB_ERR if none)
    o = start
    while o < ob_nobj:
        if ob_otype[o] == t and ob_ohead[o] != 0:
            b = ov_blob[ob_ohead[o] - 1]
            if f < ob_bn[b] and ob_data[ob_bo[b] + f] == val:
                return o
        o += 1
    return OB_ERR

def ob_replay(ln: u64) -> u64:
# rebuild the store from ob_log[0:ln]; stops at the first torn/corrupt/out-of-order record and
# truncates the log there. Returns the number of records applied.
    ob_reset()
    ob_lr = 1
    p: u64 = 0
    c: u64 = 0
    while p < ln:
        tag = ob_log[p]
        n: u64 = 3
        if tag == OB_RV:
            n = 4
        elif tag == OB_RB:
            if p + 1 >= ln or ob_log[p + 1] == 0 or ob_log[p + 1] > OB_MAXW:
                break
            n = 2 + ob_log[p + 1]
        elif tag != OB_RT and tag != OB_RO:
            break
        if p + n >= ln or ob_lsum(p, n) != ob_log[p + n]:
            break
        a = ob_log[p + 1]
        b = ob_log[p + 2]
        ok: u64 = 1
        if tag == OB_RT:
            ok = u64(ob_type_def(a, b) == 0)
        elif tag == OB_RO:
            ok = u64(a == ob_nobj and obj_new(b) == a)
        elif tag == OB_RB:
            i: u64 = 0
            while i < a:
                ob_in[i] = ob_log[p + 2 + i]
                i += 1
            ok = u64(ob_blob_put(a) == ob_nblob - 1)
        else:
            ok = u64(a < ob_nobj and b < ob_nblob and ob_log[p + 3] == ob_clock + 1 and ob_add_ver(a, b) != OB_ERR)
        if ok == 0:
            break
        p += n + 1
        c += 1
    ob_lp = p
    ob_lr = 0
    return c

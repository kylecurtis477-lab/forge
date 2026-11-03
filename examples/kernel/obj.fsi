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

def ob_reset():
    ob_nobj = 0
    ob_nver = 0
    ob_nblob = 0
    ob_used = 0
    ob_clock = 0
    ob_dedup = 0
    i: u64 = 0
    while i < OB_NT:
        ob_tf[i] = 0
        i += 1

def ob_type_def(t: u64, nfields: u64) -> u64:
    if t == 0 or t >= OB_NT or nfields == 0 or nfields > OB_MAXW:
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
    ob_otype[o] = t
    ob_ohead[o] = 0
    ob_nobj += 1
    return o

def ob_add_ver(o: u64, b: u64) -> u64:
    if ob_nver >= OB_NV:
        return OB_ERR
    v = ob_nver
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

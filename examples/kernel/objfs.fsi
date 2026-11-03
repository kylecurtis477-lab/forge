# Phase 9.5: files as objects. A file is an object of type OFS_T whose payload is one whole snapshot of
# the file: field 0 = FNV hash of the name (indexed, so lookup is an index probe), fields 1..4 = the name
# (up to 31 bytes, NUL padded, 8 bytes per word), field 5 = size in bytes, fields 6.. = the contents
# (up to OFS_MAX bytes). Every write is a new version, so every file can be read as of any clock, and a
# system rewind rewinds files too. Fully safe Forge (staging arrays, no raw pointers): shared by the
# kernel (proc.fsi serves it under /obj) and the hosted test tests/fs/objfs.forge.
# Needs obj.fsi included first.
OFS_T = 3
OFS_DW = OB_MAXW - 6  # data words
OFS_MAX = (OB_MAXW - 6) * 8  # bytes per file
ofs_nm: u8[32]  # staged name, NUL padded (ofs_lookup, ofs_put; ofs_name_of fills it)
ofs_buf: u8[OFS_MAX]  # staged contents (ofs_put) / contents read back (ofs_read_at)
ofs_ok: u64  # 1 once ofs_init ran on a mounted store

def ofs_init() -> u64:
    if ob_tf[OFS_T] == 0 and ob_type_def(OFS_T, OB_MAXW) != 0:
        return OB_ERR
    if ob_tf[OFS_T] != OB_MAXW:
        return OB_ERR
    ofs_ok = 1
    return 0

def ofs_nword(i: u64) -> u64:
    w: u64 = 0
    k: u64 = 0
    while k < 8:
        w |= u64(ofs_nm[i * 8 + k]) << (k * 8)
        k += 1
    return w

def ofs_nhash() -> u64:
    h: u64 = 0xcbf29ce484222325
    i: u64 = 0
    while i < 32 and ofs_nm[i] != 0:
        h = (h ^ u64(ofs_nm[i])) * 0x100000001b3
        i += 1
    return h

def ofs_lookup() -> u64:
# the file named ofs_nm (OB_ERR if none): an index probe on the name hash, then a full name compare
    if ofs_nm[0] == 0 or ofs_nm[31] != 0:
        return OB_ERR
    h = ofs_nhash()
    o = obj_find(OFS_T, 0, h, 0)
    while o != OB_ERR:
        same: u64 = 1
        i: u64 = 0
        while i < 4:
            if ob_cur(o, 1 + i) != ofs_nword(i):
                same = 0
            i += 1
        if same == 1:
            return o
        o = obj_find(OFS_T, 0, h, o + 1)
    return OB_ERR

def ofs_put(n: u64) -> u64:
# create or overwrite the file named ofs_nm with ofs_buf[0:n]; returns the new version's timestamp
    if ofs_ok == 0 or n > OFS_MAX:
        return OB_ERR
    o = ofs_lookup()
    if o == OB_ERR:
        if ofs_nm[0] == 0 or ofs_nm[31] != 0:
            return OB_ERR
        o = obj_new(OFS_T)
        if o == OB_ERR:
            return OB_ERR
    ob_in[0] = ofs_nhash()
    i: u64 = 0
    while i < 4:
        ob_in[1 + i] = ofs_nword(i)
        i += 1
    ob_in[5] = n
    i = 0
    while i < OFS_DW:
        w: u64 = 0
        k: u64 = 0
        while k < 8:
            if i * 8 + k < n:
                w |= u64(ofs_buf[i * 8 + k]) << (k * 8)
            k += 1
        ob_in[6 + i] = w
        i += 1
    return obj_write(o, OB_MAXW)

def ofs_read_at(o: u64, ts: u64) -> u64:
# contents of file o as of ts into ofs_buf; returns the size (OB_ERR if it did not exist then)
    if o >= ob_nobj or ob_otype[o] != OFS_T or obj_read_at(o, ts) == OB_ERR:
        return OB_ERR
    n = ob_out[5]
    i: u64 = 0
    while i < n:
        ofs_buf[i] = u8((ob_out[6 + i / 8] >> ((i % 8) * 8)) & 255)
        i += 1
    return n

def ofs_name_of(o: u64, ts: u64) -> u64:
# name of file o as of ts into ofs_nm; returns its length (OB_ERR if it did not exist then)
    if o >= ob_nobj or ob_otype[o] != OFS_T or obj_read_at(o, ts) == OB_ERR:
        return OB_ERR
    l: u64 = 0
    i: u64 = 0
    while i < 32:
        ofs_nm[i] = u8((ob_out[1 + i / 8] >> ((i % 8) * 8)) & 255)
        if ofs_nm[i] != 0 and l == i:
            l = i + 1
        i += 1
    return l

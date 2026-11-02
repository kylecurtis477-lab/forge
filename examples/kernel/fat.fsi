# Read-only filesystem: GPT -> EFI System Partition -> FAT32 with VFAT long names (case-insensitive lookup).
fs_part: u64  # partition start LBA
fs_spc: u64
fs_fat: u64  # FAT start LBA
fs_data: u64  # cluster 2 LBA
fs_root: u64
fs_sec: u64  # scratch sector (kmalloc 512)
fs_fatsec_lba: u64
fs_fatsec: u64

@unsafe
def le16(p: u64) -> u64:
    return vload64(p) & 0xFFFF

@unsafe
def le32(p: u64) -> u64:
    return vload64(p) & 0xFFFFFFFF

@unsafe
def gpt_find_esp() -> u64:
    hdr = kmalloc(512)
    ents = kmalloc(512)
    res: u64 = 0
    if vb_read(1, 1, hdr) and vload64(hdr) == 0x5452415020494645:
        elba = vload64(hdr + 72)
        n = le32(hdr + 80)
        esz = le32(hdr + 84)
        puts("GPT: ")
        put_u64(n)
        puts(" entries:")
        i: u64 = 0
        while i < n and esz == 128:
            if (i % 4) == 0:
                vb_read(elba + i / 4, 1, ents)
            e = ents + (i % 4) * 128
            if vload64(e) != 0 or vload64(e + 8) != 0:
                puts(" [")
                put_u64(i + 1)
                if vload64(e) == 0x11D2F81FC12A7328 and vload64(e + 8) == 0x3BC93EC9A0004BBA:
                    puts(" ESP")
                    if res == 0:
                        res = vload64(e + 32)
                elif vload64(e) == 0x4433B9E5EBD0A0A2:
                    puts(" basic-data")
                elif vload64(e) == 0x477284830FC63DAF:
                    puts(" linux")
                elif vload64(e) == 0x4DB80B5CE3C9E316:
                    puts(" msr")
                else:
                    puts(" other")
                puts("]")
            i += 1
        putc(10)
    kfree(hdr)
    kfree(ents)
    return res

@unsafe
def fs_mount() -> u64:
    fs_part = gpt_find_esp()
    if fs_part == 0:
        return 0
    fs_sec = kmalloc(512)
    fs_fatsec = kmalloc(512)
    fs_fatsec_lba = 0
    b = fs_sec
    if vb_read(fs_part, 1, b) == 0 or le16(b + 11) != 512 or le16(b + 510) != 0xAA55 or le16(b + 22) != 0:
        return 0
    fs_spc = vload64(b + 13) & 0xFF
    nfats = vload64(b + 16) & 0xFF
    fs_fat = fs_part + le16(b + 14)
    fs_data = fs_fat + nfats * le32(b + 36)
    fs_root = le32(b + 44)
    puts("FAT32: ESP at LBA ")
    put_u64(fs_part)
    puts(", ")
    put_u64(fs_spc)
    puts(" sector(s)/cluster, root cluster ")
    put_u64(fs_root)
    putc(10)
    return 1

def fat_next(c: u64) -> u64:
    lba = fs_fat + (c * 4) / 512
    if lba != fs_fatsec_lba:
        if vb_read(lba, 1, fs_fatsec) == 0:
            return 0x0FFFFFFF
        fs_fatsec_lba = lba
    return le32(fs_fatsec + (c * 4) % 512) & 0x0FFFFFFF

def clu_lba(c: u64) -> u64:
    return fs_data + (c - 2) * fs_spc

fs_m: Mutex  # serialises the FAT layer (shared sector buffers); holders may sleep on disk I/O
fs_dsec_lba: u64
fs_dsec: u64  # directory sector cache

def fs_sector(lba: u64) -> u64:
    if fs_dsec == 0:
        fs_dsec = kmalloc(512)
        fs_dsec_lba = 0xFFFFFFFFFFFFFFFF
    if lba != fs_dsec_lba:
        if vb_read(lba, 1, fs_dsec) == 0:
            fs_dsec_lba = 0xFFFFFFFFFFFFFFFF
            return 0
        fs_dsec_lba = lba
    return fs_dsec

@unsafe
def lfn_put(name: ptr[u8], pos: u64, ch: u64):
    if pos < 255:
        if ch == 0 or ch == 0xFFFF:
            name[pos] = 0
        elif ch < 128:
            name[pos] = u8(ch)
        else:
            name[pos] = 63

# Directory iteration with VFAT long names. Returns the index just past entry #idx-or-later that is a
# real file/dir (its 32-byte entry in ent, its name, NUL-terminated, in name), or 0 at the end.
# 8.3-only entries honour the NT lower-case flags (byte 12: 0x08 base, 0x10 extension).
LFN_OFFS: u8[13] = [1, 3, 5, 7, 9, 14, 16, 18, 20, 22, 24, 28, 30]

@unsafe
def dir_next(dc: u64, idx: u64, name: ptr[u8], ent: u64) -> u64:
    epc = fs_spc * 16
    c = dc
    k = idx / epc
    while k > 0:
        c = fat_next(c)
        if c < 2 or c >= 0x0FFFFFF8:
            return 0
        k -= 1
    i = idx
    have_lfn = 0
    while c >= 2 and c < 0x0FFFFFF8:
        sec = fs_sector(clu_lba(c) + (i % epc) / 16)
        if sec == 0:
            return 0
        d = sec + (i % 16) * 32
        first = vload64(d) & 0xFF
        if first == 0:
            return 0
        attr = vload64(d + 11) & 0xFF
        i += 1
        if first == 0xE5:
            have_lfn = 0
        elif attr == 0x0F:
            seq = first & 0x3F
            if first & 0x40:
                have_lfn = 1
                if seq * 13 < 255:
                    name[seq * 13] = 0
                else:
                    name[255] = 0
            if seq >= 1:
                lq: u64 = 0
                while lq < 13:
                    lfn_put(name, (seq - 1) * 13 + lq, le16(d + u64(LFN_OFFS[lq])))
                    lq += 1
        elif (attr & 8) == 0:
            memcpy(ent, d, 32)
            if have_lfn == 0:
                nt = vload64(d + 12) & 0xFF
                n: u64 = 0
                q: u64 = 0
                while q < 11:
                    ch = vload64(d + q) & 0xFF
                    if q == 8 and ch != 32:
                        name[n] = 46
                        n += 1
                    if ch != 32:
                        if ch >= 65 and ch <= 90 and ((q < 8 and (nt & 8)) or (q >= 8 and (nt & 0x10))):
                            ch += 32
                        name[n] = u8(ch)
                        n += 1
                    q += 1
                name[n] = 0
            return i
        else:
            have_lfn = 0
        if (i % epc) == 0:
            c = fat_next(c)
    return 0

def lower(c: u64) -> u64:
    if c >= 65 and c <= 90:
        return c + 32
    return c

@unsafe
def name_eq(a: ptr[u8], b: ptr[u8], blen: u64) -> u64:
# a: NUL-terminated directory name, b: path component (blen bytes); case-insensitive
    i: u64 = 0
    while i < blen:
        if a[i] == 0 or lower(u64(a[i])) != lower(u64(b[i])):
            return 0
        i += 1
    return a[i] == 0

def ent_cluster(ent: u64) -> u64:
    return (le16(ent + 20) << 16) | le16(ent + 26)

# walks a path like "/EFI/Forge/bin/Read Me.txt" (case-insensitive, long names); returns 1 with the
# entry in ent. The root itself ("" or "/") returns 2 (no entry).
@unsafe
def fs_lookup(path: ptr[u8], ent: u64) -> u64:
    nm: u8[256]
    c = fs_root
    i: u64 = 0
    while path[i] == 47:
        i += 1
    if path[i] == 0:
        return 2
    while 1:
        j = i
        while path[j] != 0 and path[j] != 47:
            j += 1
        found = 0
        idx: u64 = 0
        while 1:
            idx = dir_next(c, idx, addr(nm[0]), ent)
            if idx == 0:
                break
            if name_eq(addr(nm[0]), ptr[u8](u64(path) + i), j - i):
                found = 1
                break
        if found == 0:
            return 0
        while path[j] == 47:
            j += 1
        if path[j] == 0:
            return 1
        if (vload64(ent + 11) & 0x10) == 0:
            return 0
        c = ent_cluster(ent)
        if c == 0:
            c = fs_root
        i = j
    return 0

def fs_dir_cluster(path: ptr[u8]) -> u64:
    ent: u8[32]
    r = fs_lookup(path, u64(addr(ent[0])))
    if r == 2:
        return fs_root
    if r == 0 or (ent[11] & 0x10) == 0:
        return 0
    c = ent_cluster(u64(addr(ent[0])))
    if c == 0:
        return fs_root
    return c

def fs_list(path: ptr[u8]):
    ent: u8[32]
    nm: u8[256]
    mutex_lock(addr(fs_m))
    c = fs_dir_cluster(path)
    idx: u64 = 0
    while c != 0:
        idx = dir_next(c, idx, addr(nm[0]), u64(addr(ent[0])))
        if idx == 0:
            break
        if nm[0] != 46:
            puts(" ")
            puts(addr(nm[0]))
            if ent[11] & 0x10:
                puts("/")
            else:
                puts("(")
                put_u64(le32(u64(addr(ent[28]))))
                puts(")")
    mutex_unlock(addr(fs_m))

# reads a whole file into a new kmalloc buffer; returns it (size in *size) or 0
@unsafe
def fs_read_file(path: ptr[u8], size: ptr[u64]) -> u64:
    ent: u8[32]
    e = u64(addr(ent[0]))
    mutex_lock(addr(fs_m))
    if fs_lookup(path, e) != 1 or (vload64(e + 11) & 0x10) != 0:
        mutex_unlock(addr(fs_m))
        return 0
    sz = le32(e + 28)
    buf = kmalloc(sz + 512)
    c = ent_cluster(e)
    got: u64 = 0
    while got < sz:
        if c < 2 or c >= 0x0FFFFFF8:
            mutex_unlock(addr(fs_m))
            kfree(buf)
            return 0
        s: u64 = 0
        while s < fs_spc and got < sz:
            if vb_read(clu_lba(c) + s, 1, buf + got) == 0:
                mutex_unlock(addr(fs_m))
                kfree(buf)
                return 0
            got += 512
            s += 1
        c = fat_next(c)
    mutex_unlock(addr(fs_m))
    size[0] = sz
    return buf

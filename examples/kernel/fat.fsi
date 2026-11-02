# Read-only filesystem: GPT -> EFI System Partition -> FAT32 (8.3 names; LFN entries skipped).
fs_part: u64          # partition start LBA
fs_spc: u64
fs_fat: u64           # FAT start LBA
fs_data: u64          # cluster 2 LBA
fs_root: u64
fs_sec: u64           # scratch sector (kmalloc 512)
fs_fatsec_lba: u64
fs_fatsec: u64

def le16(p: u64) -> u64:
    return vload64(p) & 0xFFFF

def le32(p: u64) -> u64:
    return vload64(p) & 0xFFFFFFFF

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

def name83(comp: ptr[u8], n: u64, out: ptr[u8]):
    i = 0
    while i < 11:
        out[i] = 32
        i += 1
    j: u64 = 0
    k: u64 = 0
    while j < n:
        ch = comp[j]
        if ch == 46:
            k = 8
        elif k < 11:
            if ch >= 97 and ch <= 122:
                ch = ch - 32
            out[k] = ch
            k += 1
        j += 1

# finds name (11 bytes, 8.3) in the directory starting at cluster dc; returns the 32-byte entry copied
# into ent, or 0. With list != 0, prints every entry instead.
def dir_find(dc: u64, nm: ptr[u8], ent: u64, list: u64) -> u64:
    c = dc
    while c >= 2 and c < 0x0FFFFFF8:
        s: u64 = 0
        while s < fs_spc:
            if vb_read(clu_lba(c) + s, 1, fs_sec) == 0:
                return 0
            e: u64 = 0
            while e < 512:
                d = fs_sec + e
                first = vload64(d) & 0xFF
                if first == 0:
                    return 0
                attr = vload64(d + 11) & 0xFF
                if first != 0xE5 and attr != 0x0F and (attr & 8) == 0:
                    if list:
                        if first != 46:
                            puts(" ")
                            k = 0
                            while k < 11:
                                ch = vload64(d + k) & 0xFF
                                if ch != 32:
                                    if k == 8 and (attr & 0x10) == 0:
                                        putc(46)
                                    putc(ch)
                                k += 1
                            if attr & 0x10:
                                puts("/")
                            else:
                                puts("(")
                                put_u64(le32(d + 28))
                                puts(")")
                    else:
                        k = 0
                        same = 1
                        while k < 11:
                            if (vload64(d + k) & 0xFF) != u64(nm[k]):
                                same = 0
                            k += 1
                        if same:
                            memcpy(ent, d, 32)
                            return 1
                e += 32
            s += 1
        c = fat_next(c)
    return 0

def ent_cluster(ent: u64) -> u64:
    return (le16(ent + 20) << 16) | le16(ent + 26)

# walks a path like "EFI/Forge/init.elf"; returns 1 with the entry in ent
def fs_lookup(path: ptr[u8], ent: u64) -> u64:
    nm: u8[11]
    c = fs_root
    i: u64 = 0
    while 1:
        j = i
        while path[j] != 0 and path[j] != 47:
            j += 1
        name83(ptr[u8](u64(path) + i), j - i, addr(nm[0]))
        if dir_find(c, addr(nm[0]), ent, 0) == 0:
            return 0
        if path[j] == 0:
            return 1
        if ((vload64(ent + 11) & 0x10) == 0):
            return 0
        c = ent_cluster(ent)
        i = j + 1
    return 0

def fs_list(path: ptr[u8]):
    ent: u8[32]
    if fs_lookup(path, u64(addr(ent[0]))) == 0:
        return
    dir_find(ent_cluster(u64(addr(ent[0]))), ptr[u8](0), 0, 1)

# reads a whole file into a new kmalloc buffer; returns it (size in *size) or 0
def fs_read_file(path: ptr[u8], size: ptr[u64]) -> u64:
    ent: u8[32]
    e = u64(addr(ent[0]))
    if fs_lookup(path, e) == 0 or (vload64(e + 11) & 0x10) != 0:
        return 0
    sz = le32(e + 28)
    buf = kmalloc(sz + 512)
    c = ent_cluster(e)
    got: u64 = 0
    while got < sz:
        if c < 2 or c >= 0x0FFFFFF8:
            kfree(buf)
            return 0
        s: u64 = 0
        while s < fs_spc and got < sz:
            if vb_read(clu_lba(c) + s, 1, buf + got) == 0:
                kfree(buf)
                return 0
            got += 512
            s += 1
        c = fat_next(c)
    size[0] = sz
    return buf

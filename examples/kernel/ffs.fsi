# Phase 8: Forge FS, the only writable filesystem. It lives only on a GPT partition with the Forge type
# GUID D0C2E3A1-6B7F-4F0A-9E11-466F72676546 that was created explicitly (tools/forge-part.py). The kernel
# validates the GPT (header + entry CRCs, exactly one Forge partition, no overlap, inside the usable
# range), then claims that LBA range on the NVMe driver; nvme_write refuses anything outside it.
# Layout (4 KiB blocks from the partition start): 0 superblock, 1 inode table (64 x 64 bytes), 2.. data
# (contiguous extents, first fit). Commit order: data, inode table, superblock (generation + checksums), flush.
FFS_MAGIC = 0x3153464547524F46  # "FORGEFS1"
FFS_NI = 64
FFS_T0 = 0x4F0A6B7FD0C2E3A1
FFS_T1 = 0x466567726F46119E
ffs_lo: u64  # partition first LBA
ffs_hi: u64  # last LBA + 1
ffs_nblk: u64
ffs_gen: u64
ffs_ok: u64
ffs_b: u64[512]  # one block
ffs_sb: u64[512]
ffs_it: u64[512]  # inode table: per inode name w0..4, size, start, nblk
ffs_nm: u64[5]

def ffs_rd(w: u64, i: u64) -> u64:
# w: 0 block buffer, 1 superblock, 2 inode table
    if w == 1:
        return ffs_sb[i]
    if w == 2:
        return ffs_it[i]
    return ffs_b[i]

def ffs_wr(w: u64, i: u64, v: u64):
    if w == 1:
        ffs_sb[i] = v
    elif w == 2:
        ffs_it[i] = v
    else:
        ffs_b[i] = v

def ffs_fnvw(w: u64, n: u64) -> u64:
    h: u64 = 0xcbf29ce484222325
    i: u64 = 0
    while i < n:
        h = (h ^ ffs_rd(w, i)) * 0x100000001b3
        i += 1
    return h

def ffs_sec(s: u64) -> u64:
# address of 512-byte sector s inside the NVMe DMA buffer, 0 on failure
    b = nvme_read(s - s % 8)
    if b == 0:
        return 0
    return b + (s % 8) * 512

def ffs_crc(c: u64, a: u64, n: u64) -> u64:
    i: u64 = 0
    while i < n:
        c = c ^ mmio_rd(a + i, 1)
        k = 0
        while k < 8:
            if c & 1:
                c = (c >> 1) ^ 0xEDB88320
            else:
                c = c >> 1
            k += 1
        i += 1
    return c

def ffs_crcv(c: u64, v: u64, n: u64) -> u64:
    i: u64 = 0
    while i < n:
        c = c ^ ((v >> (i * 8)) & 0xFF)
        k = 0
        while k < 8:
            if c & 1:
                c = (c >> 1) ^ 0xEDB88320
            else:
                c = c >> 1
            k += 1
        i += 1
    return c

def ffs_find_part() -> u64:
# 1 if exactly one valid Forge partition was found (ffs_lo/ffs_hi set); prints why not otherwise
    if nv_regs == 0 or nv_lba_sz != 512:
        return 0
    h = ffs_sec(1)
    if h == 0 or mmio_rd(h, 8) != 0x5452415020494645:
        puts("Forge FS: nvme0 has no GPT, nothing written\n")
        return 0
    hs = mmio_rd(h + 12, 4)
    hcrc = mmio_rd(h + 16, 4)
    first = mmio_rd(h + 40, 8)
    last = mmio_rd(h + 48, 8)
    elba = mmio_rd(h + 72, 8)
    n = mmio_rd(h + 80, 4)
    esz = mmio_rd(h + 84, 4)
    ecrc = mmio_rd(h + 88, 4)
    # header CRC with the CRC field taken as zero
    if hs != 92:
        puts("Forge FS: GPT header size not 92, nothing written\n")
        return 0
    c = ffs_crc(0xFFFFFFFF, h, 16)
    c = ffs_crcv(c, 0, 4)
    c = ffs_crc(c, h + 20, hs - 20) ^ 0xFFFFFFFF
    if c != hcrc or esz != 128 or n > 128 or n == 0:
        puts("Forge FS: GPT header invalid (CRC or geometry), nothing written\n")
        return 0
    # entries: CRC, then look for Forge entries, overlap and range checks
    c = 0xFFFFFFFF
    s: u64 = 0
    while s < n / 4:
        e = ffs_sec(elba + s)
        if e == 0:
            return 0
        c = ffs_crc(c, e, 512)
        s += 1
    if (c ^ 0xFFFFFFFF) != ecrc:
        puts("Forge FS: GPT entry array CRC mismatch, nothing written\n")
        return 0
    found: u64 = 0
    lo: u64 = 0
    hi: u64 = 0
    i: u64 = 0
    while i < n:
        e = ffs_sec(elba + i / 4) + (i % 4) * 128
        if mmio_rd(e, 8) == FFS_T0 and mmio_rd(e + 8, 8) == FFS_T1:
            found += 1
            lo = mmio_rd(e + 32, 8)
            hi = mmio_rd(e + 40, 8) + 1
        i += 1
    if found != 1:
        if found == 0:
            puts("Forge FS: no Forge partition on nvme0, nothing written\n")
        else:
            puts("Forge FS: more than one Forge partition, nothing written\n")
        return 0
    if lo < first or hi > last + 1 or hi <= lo + 64 or (lo % 8) != 0:
        puts("Forge FS: Forge partition outside the usable range or misaligned, nothing written\n")
        return 0
    i = 0
    while i < n:
        e = ffs_sec(elba + i / 4) + (i % 4) * 128
        t0 = mmio_rd(e, 8)
        t1 = mmio_rd(e + 8, 8)
        if (t0 != 0 or t1 != 0) and (t0 != FFS_T0 or t1 != FFS_T1):
            plo = mmio_rd(e + 32, 8)
            phi = mmio_rd(e + 40, 8) + 1
            if plo < hi and lo < phi:
                puts("Forge FS: Forge partition overlaps another partition, nothing written\n")
                return 0
        i += 1
    ffs_lo = lo
    ffs_hi = hi
    return 1

def ffs_get(blk: u64, dst: u64) -> u64:
    if blk >= ffs_nblk and ffs_nblk != 0:
        return 0
    b = nvme_read(ffs_lo + blk * 8)
    if b == 0:
        return 0
    i: u64 = 0
    while i < 512:
        ffs_wr(dst, i, mmio_rd(b + i * 8, 8))
        i += 1
    return 1

def ffs_put(blk: u64, src: u64) -> u64:
    if blk >= ffs_nblk:
        return 0
    i: u64 = 0
    while i < 512:
        mmio_wr(nv_buf + i * 8, 8, ffs_rd(src, i))
        i += 1
    return nvme_write(ffs_lo + blk * 8)

def ffs_commit() -> u64:
# inode table, then superblock with its generation and both checksums, flushing in between
    if ffs_put(1, 2) == 0 or nvme_flush() == 0:
        return 0
    ffs_gen += 1
    ffs_sb[3] = ffs_gen
    ffs_sb[6] = ffs_fnvw(2, 512)
    ffs_sb[511] = ffs_fnvw(1, 511)
    if ffs_put(0, 1) == 0:
        return 0
    return nvme_flush()

def ffs_load() -> u64:
    if ffs_get(0, 1) == 0 or ffs_get(1, 2) == 0:
        return 0
    if ffs_sb[0] != FFS_MAGIC or ffs_sb[1] != 1 or ffs_sb[5] != ffs_lo or ffs_sb[2] != (ffs_hi - ffs_lo) / 8:
        return 0
    if ffs_sb[511] != ffs_fnvw(1, 511) or ffs_sb[6] != ffs_fnvw(2, 512):
        return 0
    ffs_gen = ffs_sb[3]
    return 1

def ffs_mount() -> u64:
# 0 = not mounted (nothing written), 1 = mounted, 2 = formatted a blank Forge partition and mounted
    ffs_ok = 0
    if ffs_find_part() == 0:
        return 0
    ffs_nblk = 0
    nvme_claim(ffs_lo, ffs_hi)
    ffs_nblk = (ffs_hi - ffs_lo) / 8
    if ffs_load():
        ffs_ok = 1
        return 1
    ffs_get(0, 1)
    i: u64 = 0
    while i < 512:
        if ffs_sb[i] != 0:
            puts("Forge FS: superblock invalid and not blank, refusing to format\n")
            nvme_claim(0, 0)
            return 0
        i += 1
    ffs_sb[0] = FFS_MAGIC
    ffs_sb[1] = 1
    ffs_sb[2] = ffs_nblk
    ffs_sb[4] = FFS_NI
    ffs_sb[5] = ffs_lo
    ffs_gen = 0
    i = 0
    while i < 512:
        ffs_it[i] = 0
        i += 1
    if ffs_commit() == 0:
        return 0
    ffs_ok = 1
    return 2

@unsafe
def ffs_ch(s: ptr[u8], i: u64) -> u64:
# the one unchecked read: byte i of a NUL-terminated kernel string literal (callers stop at the NUL)
    return u64(s[i])

def ffs_pack(s: ptr[u8]) -> u64:
    i: u64 = 0
    while i < 5:
        ffs_nm[i] = 0
        i += 1
    i = 0
    while ffs_ch(s, i) != 0:
        if i >= 39:
            return 0
        ffs_nm[i / 8] = ffs_nm[i / 8] | (ffs_ch(s, i) << ((i % 8) * 8))
        i += 1
    return i

def ffs_find(s: ptr[u8]) -> u64:
    if ffs_pack(s) == 0:
        return FFS_NI
    i: u64 = 0
    while i < FFS_NI:
        k = i * 8
        if ffs_it[k] != 0 and ffs_it[k] == ffs_nm[0] and ffs_it[k + 1] == ffs_nm[1] and ffs_it[k + 2] == ffs_nm[2] and ffs_it[k + 3] == ffs_nm[3] and ffs_it[k + 4] == ffs_nm[4]:
            return i
        i += 1
    return FFS_NI

def ffs_alloc(n: u64) -> u64:
# first fit over the used extents; 0 if no room
    st: u64 = 2
    moved: u64 = 1
    while moved:
        moved = 0
        i: u64 = 0
        while i < FFS_NI:
            k = i * 8
            if ffs_it[k] != 0 and ffs_it[k + 6] < st + n and st < ffs_it[k + 6] + ffs_it[k + 7]:
                st = ffs_it[k + 6] + ffs_it[k + 7]
                moved = 1
            i += 1
    if st + n > ffs_nblk:
        return 0
    return st

def ffs_gen_word(seed: u64, k: u64) -> u64:
    z = seed * 0x9E3779B97F4A7C15 + (k + 1) * 0xBF58476D1CE4E5B9
    z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9
    z = (z ^ (z >> 27)) * 0x94D049BB133111EB
    return z ^ (z >> 31)

def ffs_create(s: ptr[u8], size: u64, seed: u64, text: ptr[u8]) -> u64:
# new file of size bytes: contents from text if seed == 0, else the deterministic generator stream
    if ffs_ok == 0 or ffs_find(s) != FFS_NI or ffs_pack(s) == 0:
        return 0
    slot: u64 = 0
    while slot < FFS_NI and ffs_it[slot * 8] != 0:
        slot += 1
    nb = (size + 4095) / 4096
    if slot == FFS_NI:
        return 0
    st: u64 = 2
    if nb > 0:
        st = ffs_alloc(nb)
        if st == 0:
            return 0
    b: u64 = 0
    while b < nb:
        w: u64 = 0
        while w < 512:
            off = b * 4096 + w * 8
            v: u64 = 0
            if seed != 0:
                v = ffs_gen_word(seed, off / 8)
            else:
                j: u64 = 0
                while j < 8 and off + j < size:
                    v = v | (ffs_ch(text, off + j) << (j * 8))
                    j += 1
            if off + 8 > size:
                if off >= size:
                    v = 0
                else:
                    v = v & ((u64(1) << ((size - off) * 8)) - 1)
            ffs_b[w] = v
            w += 1
        if ffs_put(st + b, 0) == 0:
            return 0
        b += 1
    if nvme_flush() == 0:
        return 0
    k = slot * 8
    ffs_pack(s)
    i: u64 = 0
    while i < 5:
        ffs_it[k + i] = ffs_nm[i]
        i += 1
    ffs_it[k + 5] = size
    ffs_it[k + 6] = st
    ffs_it[k + 7] = nb
    return ffs_commit()

def ffs_unlink(s: ptr[u8]) -> u64:
    i = ffs_find(s)
    if ffs_ok == 0 or i == FFS_NI:
        return 0
    k: u64 = 0
    while k < 8:
        ffs_it[i * 8 + k] = 0
        k += 1
    return ffs_commit()

def ffs_hash(s: ptr[u8]) -> u64:
# fnv1a64 of the file's bytes, read back from the disk; 0 if missing
    i = ffs_find(s)
    if ffs_ok == 0 or i == FFS_NI:
        return 0
    size = ffs_it[i * 8 + 5]
    st = ffs_it[i * 8 + 6]
    h: u64 = 0xcbf29ce484222325
    off: u64 = 0
    while off < size:
        if (off % 4096) == 0:
            if ffs_get(st + off / 4096, 0) == 0:
                return 0
        h = (h ^ ((ffs_b[(off % 4096) / 8] >> ((off % 8) * 8)) & 0xFF)) * 0x100000001b3
        off += 1
    return h

def ffs_expect(size: u64, seed: u64, text: ptr[u8]) -> u64:
# the fnv1a64 the file should have, computed in memory
    h: u64 = 0xcbf29ce484222325
    off: u64 = 0
    while off < size:
        c: u64 = 0
        if seed != 0:
            c = (ffs_gen_word(seed, off / 8) >> ((off % 8) * 8)) & 0xFF
        else:
            c = ffs_ch(text, off)
        h = (h ^ c) * 0x100000001b3
        off += 1
    return h

def ffs_nfiles() -> u64:
    n: u64 = 0
    i: u64 = 0
    while i < FFS_NI:
        if ffs_it[i * 8] != 0:
            n += 1
        i += 1
    return n

def ffs_check(s: ptr[u8], size: u64, seed: u64, text: ptr[u8]):
    if ffs_hash(s) != ffs_expect(size, seed, text):
        puts(s)
        panic(": Forge FS read-back hash mismatch")

def ffs_txt() -> ptr[u8]:
    return "Forge FS: written by the Forge kernel on its own GPT partition.\n"

def ffs_test():
    txt = ffs_txt()
    r = ffs_mount()
    if r == 0:
        puts("Forge FS: not mounted; writes issued 0\n")
        return
    puts("Forge FS: partition LBA ")
    put_u64(ffs_lo)
    puts("..")
    put_u64(ffs_hi - 1)
    puts(", ")
    put_u64(ffs_nblk)
    puts(" blocks, generation ")
    put_u64(ffs_gen)
    if r == 2:
        puts(", formatted (blank Forge partition)\n")
    else:
        puts(", mounted\n")
    tl: u64 = 0
    while ffs_ch(txt, tl) != 0:
        tl += 1
    if ffs_find("persist.dat") != FFS_NI:
        ffs_check("persist.dat", 20000, 7, txt)
        ffs_check("hello.txt", tl, 0, txt)
        puts("Forge FS: persist.dat and hello.txt survived the reboot, hashes match\n")
    else:
        if ffs_create("persist.dat", 20000, 7, txt) == 0 or ffs_create("hello.txt", tl, 0, txt) == 0:
            panic("Forge FS: create failed")
        puts("Forge FS: created persist.dat (20000 bytes) and hello.txt\n")
    # scratch churn: create, verify, unlink, reuse the space
    n: u64 = 0
    while n < 5:
        if ffs_create("scratch.dat", 50000 + n * 777, 100 + n, txt) == 0:
            panic("Forge FS: scratch create failed")
        ffs_check("scratch.dat", 50000 + n * 777, 100 + n, txt)
        if ffs_unlink("scratch.dat") == 0:
            panic("Forge FS: unlink failed")
        n += 1
    if ffs_create("persist.dat", 1, 1, txt) != 0:
        panic("Forge FS: duplicate name accepted")
    # the NVMe driver owns only the Forge partition: a write to LBA 0 (protective MBR) must be refused
    w0 = nv_writes
    if nvme_write(0) != 0 or nvme_write(ffs_lo - 8) != 0 or nvme_write(ffs_hi) != 0 or nv_writes != w0:
        panic("Forge FS: write outside the Forge partition was not refused")
    # remount from disk and check everything again
    if ffs_load() == 0:
        panic("Forge FS: remount failed (checksum)")
    ffs_check("persist.dat", 20000, 7, txt)
    ffs_check("hello.txt", tl, 0, txt)
    puts("Forge FS: remount OK, ")
    put_u64(ffs_nfiles())
    puts(" files, generation ")
    put_u64(ffs_gen)
    puts(", ")
    put_u64(nv_writes)
    puts(" block writes, all inside the Forge partition; writes outside refused ")
    put_u64(nv_refused)
    putc(10)
    puts("Forge FS OK\n")

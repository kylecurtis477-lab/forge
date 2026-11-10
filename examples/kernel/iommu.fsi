# Intel VT-d (DMAR) probe. Parses the ACPI DMAR table through the RSDP saved by
# acpi_init (mmio_rd of a registered window, no new unsafe functions or blocks) and records each DMA remapping
# hardware unit's register base. Translation is not enabled yet: drv 403 still
# returns a host physical address, and broader device grants stay gated on
# confinement (the next step). Call iommu_probe() after acpi_init and before
# pmm_reclaim, while the ACPI pages are still valid.
SIG_DMAR = 0x52414D44
IOMMU_MAX = 4
DMAR_MAX = 4096

iommu_n: u64
iommu_base: u64[4]
iommu_haw: u64
iommu_flags: u64
iommu_all: u64
dmar_buf: u8[4096]
acpi_tmp: u8[4096]

# which=0 reads acpi_tmp, which=1 reads dmar_buf, which=2 reads rsdp_save
def iommu_byte(which: u64, o: u64) -> u64:
    if which == 0:
        return u64(acpi_tmp[o])
    if which == 1:
        return u64(dmar_buf[o])
    return u64(rsdp_save[o])

def iommu_u16(which: u64, o: u64) -> u64:
    return iommu_byte(which, o) | (iommu_byte(which, o + 1) << 8)

def iommu_u32(which: u64, o: u64) -> u64:
    return iommu_u16(which, o) | (iommu_u16(which, o + 2) << 16)

def iommu_u64(which: u64, o: u64) -> u64:
    return iommu_u32(which, o) | (iommu_u32(which, o + 4) << 32)

def iommu_sum(which: u64, n: u64) -> u64:
    s: u64 = 0
    i: u64 = 0
    while i < n:
        s += iommu_byte(which, i)
        i += 1
    return s & 0xFF

def iommu_put(which: u64, o: u64, v: u64):
    if which == 0:
        acpi_tmp[o] = u8(v)
    else:
        dmar_buf[o] = u8(v)

def iommu_copy(pa: u64, which: u64) -> u64:
    n: u64
    va: u64
    i: u64
    if pa == 0:
        return 0
    # mmio_rd width > 1 requires alignment; QEMU/SeaBIOS may place the RSDT
    # (and other tables) at a 4-byte-unaligned PA, so only width-1 reads are used.
    va = mmio_add(p2v(pa), DMAR_MAX)
    n = mmio_rd(va + 4, 1)
    n = n | (mmio_rd(va + 5, 1) << 8)
    n = n | (mmio_rd(va + 6, 1) << 16)
    n = n | (mmio_rd(va + 7, 1) << 24)
    if n < 36 or n > DMAR_MAX:
        return 0
    i = 0
    while i < n:
        iommu_put(which, i, mmio_rd(va + i, 1))
        i += 1
    if iommu_sum(which, n) != 0:
        return 0
    return n

def iommu_probe():
    rev: u64
    sdt: u64
    esz: u64
    nent: u64
    i: u64
    slen: u64
    tp: u64
    tn: u64
    sig: u64
    dn: u64
    o: u64
    typ: u64
    ln: u64
    saved: u64
    iommu_n = 0
    iommu_haw = 0
    iommu_flags = 0
    iommu_all = 0
    saved = mmio_n
    rev = u64(rsdp_save[15])
    sdt = iommu_u32(2, 16)
    esz = 4
    if rev >= 2 and iommu_u64(2, 24) != 0:
        sdt = iommu_u64(2, 24)
        esz = 8
    slen = iommu_copy(sdt, 0)
    if slen < 36:
        mmio_n = saved
        puts("VT-d: no DMAR table\n")
        return
    nent = (slen - 36) / esz
    i = 0
    dn = 0
    while i < nent:
        if esz == 8:
            tp = iommu_u64(0, 36 + i * 8)
        else:
            tp = iommu_u32(0, 36 + i * 4)
        tn = iommu_copy(tp, 1)
        if tn >= 36:
            sig = iommu_u32(1, 0)
            if sig == SIG_DMAR:
                dn = tn
                break
        i += 1
    if dn == 0:
        mmio_n = saved
        puts("VT-d: no DMAR table\n")
        return
    iommu_haw = iommu_byte(1, 36)
    iommu_flags = iommu_byte(1, 37)
    o = 48
    while o + 16 <= dn:
        typ = iommu_u16(1, o)
        ln = iommu_u16(1, o + 2)
        if ln < 16 or o + ln > dn:
            break
        if typ == 0 and iommu_n < IOMMU_MAX:
            iommu_base[iommu_n] = iommu_u64(1, o + 8)
            if (iommu_byte(1, o + 4) & 1) != 0:
                iommu_all = 1
            iommu_n += 1
        o += ln
    if iommu_n == 0:
        mmio_n = saved
        puts("VT-d: DMAR has no remapping unit\n")
        return
    mmio_n = saved
    puts("VT-d: ")
    put_u64(iommu_n)
    puts(" remapping unit")
    if iommu_n != 1:
        puts("s")
    puts(", haw ")
    put_u64(iommu_haw + 1)
    puts(" bits, flags 0x")
    put_hex8(iommu_flags)
    puts(", base ")
    put_hex(iommu_base[0])
    if iommu_all != 0:
        puts(" (including all PCI)")
    puts(", translation off (drv 403 still returns a host PA; grants stay gated)\n")

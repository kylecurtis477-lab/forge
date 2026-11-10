# Intel VT-d (DMAR) probe and DMA remapping. Parses the ACPI DMAR table through
# the RSDP saved by acpi_init (width-1 mmio_rd of a temporary window, no new
# unsafe functions or blocks), then after pci_scan, iommu_enable() programs the
# first remapping unit: a root table and a pass-through context for every bus,
# then translation is turned on. A DMA grant (drv 402) gets a non-identity IOVA
# in a per-device second-level page table that maps only those pages; drv 403
# returns that IOVA. Devices without a grant stay pass-through. Without a DMAR
# unit, behaviour is unchanged (403 still a host PA). Broader device grants stay
# gated. Call iommu_probe() after acpi_init and before pmm_reclaim.
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
# programmed unit (set by iommu_enable)
IOMMU_IOVA = 0x0000000020000000
iommu_reg: u64
iommu_te: u64
iommu_aw: u64
iommu_iro: u64
iommu_tmp_pa: u64
iommu_rt_va: u64
iommu_rt_pa: u64
iommu_ctx_pt_va: u64
iommu_ctx_pt_pa: u64
iommu_ctx_tr_va: u64
iommu_ctx_tr_pa: u64
iommu_sl_va: u64[4]
iommu_sl_pa: u64[4]
iommu_grant_iova: u64
iommu_grant_pa: u64
iommu_grant_n: u64
iommu_grant_bdf: u64

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
    putc(10)

def iommu_page() -> u64:
    va = dma_alloc(1, addr(iommu_tmp_pa))
    return va

def iommu_wait32(off: u64, mask: u64, want: u64) -> u64:
    i: u64 = 0
    while i < 1000000:
        if (mmio_rd(iommu_reg + off, 4) & mask) == want:
            return 1
        i += 1
    return 0

def iommu_wait64_clear(off: u64, mask: u64) -> u64:
    i: u64 = 0
    while i < 1000000:
        if (mmio_rd(iommu_reg + off, 8) & mask) == 0:
            return 1
        i += 1
    return 0

def iommu_wr64(off: u64, v: u64):
# 64-bit VT-d command registers: write the high half first, the low half last (the trigger)
    mmio_wr(iommu_reg + off + 4, 4, v >> 32)
    mmio_wr(iommu_reg + off, 4, v & 0xFFFFFFFF)

def iommu_inv():
# global context-cache and IOTLB invalidation (legacy register interface)
    iommu_wr64(0x28, (u64(1) << 63) | (u64(1) << 61))
    if iommu_wait64_clear(0x28, u64(1) << 63) == 0:
        return
    iommu_wr64(iommu_iro + 8, (u64(1) << 63) | (u64(1) << 60) | (u64(1) << 49) | (u64(1) << 48))
    iommu_wait64_clear(iommu_iro + 8, u64(1) << 63)

def iommu_te_set(on: u64) -> u64:
    if on != 0:
        mmio_wr(iommu_reg + 0x18, 4, (u64(1) << 31) | (u64(1) << 30))
        return iommu_wait32(0x1C, u64(1) << 31, u64(1) << 31)
    mmio_wr(iommu_reg + 0x18, 4, u64(1) << 30)
    return iommu_wait32(0x1C, u64(1) << 31, 0)

def iommu_ctx_fill(va: u64, tt: u64, slpa: u64, did: u64):
    i: u64 = 0
    lo: u64 = 0
    while i < 256:
        lo = 1 | (tt << 2)
        if tt == 0:
            lo = lo | slpa
        mmio_wr(va + i * 16, 8, lo)
        mmio_wr(va + i * 16 + 8, 8, (did << 8) | iommu_aw)
        i += 1

def iommu_root_set(bus: u64, ctx_pa: u64):
    mmio_wr(iommu_rt_va + bus * 16, 8, 1 | ctx_pa)
    mmio_wr(iommu_rt_va + bus * 16 + 8, 8, 0)

def iommu_enable():
    cap: u64
    ecap: u64
    sagaw: u64
    i: u64
    gsts: u64
    if iommu_n == 0 or iommu_base[0] == 0 or iommu_te != 0:
        return
    iommu_reg = mmio_map(iommu_base[0], 0x10000)
    if iommu_reg == 0:
        puts("VT-d: could not map remapping registers\n")
        return
    cap = mmio_rd(iommu_reg + 8, 8)
    ecap = mmio_rd(iommu_reg + 0x10, 8)
    sagaw = (cap >> 8) & 0x1F
    iommu_aw = 0
    if (sagaw & 4) != 0:
        iommu_aw = 2
    elif (sagaw & 2) != 0:
        iommu_aw = 1
    if iommu_aw == 0:
        puts("VT-d: no 39- or 48-bit second-level page tables\n")
        return
    if (ecap & 0x40) == 0:
        puts("VT-d: pass-through not supported, leaving translation off\n")
        return
    iommu_iro = ((ecap >> 8) & 0x3FF) * 16
    iommu_rt_va = iommu_page()
    iommu_rt_pa = iommu_tmp_pa
    iommu_ctx_pt_va = iommu_page()
    iommu_ctx_pt_pa = iommu_tmp_pa
    iommu_ctx_tr_va = iommu_page()
    iommu_ctx_tr_pa = iommu_tmp_pa
    i = 0
    while i < 4:
        iommu_sl_va[i] = iommu_page()
        iommu_sl_pa[i] = iommu_tmp_pa
        if iommu_sl_va[i] == 0:
            puts("VT-d: no frames for remapping tables\n")
            return
        i += 1
    if iommu_rt_va == 0 or iommu_ctx_pt_va == 0 or iommu_ctx_tr_va == 0:
        puts("VT-d: no frames for remapping tables\n")
        return
    iommu_ctx_fill(iommu_ctx_pt_va, 2, 0, 1)
    i = 0
    while i < 256:
        iommu_root_set(i, iommu_ctx_pt_pa)
        i += 1
    mmio_wr(iommu_reg + 0x20, 8, iommu_rt_pa)
    mmio_wr(iommu_reg + 0x18, 4, u64(1) << 30)
    if iommu_wait32(0x1C, u64(1) << 30, u64(1) << 30) == 0:
        puts("VT-d: set-root timed out\n")
        return
    if (cap & 0x10) != 0:
        mmio_wr(iommu_reg + 0x18, 4, u64(1) << 27)
        if iommu_wait32(0x1C, u64(1) << 27, 0) == 0:
            puts("VT-d: write-buffer flush timed out\n")
            return
    iommu_inv()
    gsts = mmio_rd(iommu_reg + 0x1C, 4)
    mmio_wr(iommu_reg + 0x18, 4, (gsts & (u64(1) << 30)) | (u64(1) << 31))
    if iommu_wait32(0x1C, u64(1) << 31, u64(1) << 31) == 0:
        puts("VT-d: translation enable timed out\n")
        return
    iommu_te = 1
    iommu_grant_n = 0
    iommu_grant_iova = 0
    puts("VT-d: translation on (pass-through default; drv 403 returns an IOVA for a DMA grant)\n")

def iommu_sl_top_va() -> u64:
    if iommu_aw == 2:
        return iommu_sl_va[0]
    return iommu_sl_va[1]

def iommu_sl_top_pa() -> u64:
    if iommu_aw == 2:
        return iommu_sl_pa[0]
    return iommu_sl_pa[1]

def iommu_sl_idx(iova: u64, sh: u64) -> u64:
    return (iova >> sh) & 0x1FF

def iommu_sl_rebuild():
# empty second-level tree hooked at IOMMU_IOVA; no leaf entries yet.
# Legacy virtio's queue PFN register is 32 bits, so the IOVA must be below 4 GiB
# (0x100000000 >> 12 << 12 truncates to 0 in a 32-bit PFN).
    i: u64 = 0
    while i < 4:
        k: u64 = 0
        while k < PAGE:
            mmio_wr(iommu_sl_va[i] + k, 8, 0)
            k += 8
        i += 1
    if iommu_aw == 2:
        mmio_wr(iommu_sl_va[0] + iommu_sl_idx(IOMMU_IOVA, 39) * 8, 8, iommu_sl_pa[1] | 3)
    mmio_wr(iommu_sl_va[1] + iommu_sl_idx(IOMMU_IOVA, 30) * 8, 8, iommu_sl_pa[2] | 3)
    mmio_wr(iommu_sl_va[2] + iommu_sl_idx(IOMMU_IOVA, 21) * 8, 8, iommu_sl_pa[3] | 3)

def iommu_map_grant(bdf: u64, pa: u64, n: u64) -> u64:
    i: u64
    bus: u64
    devfn: u64
    iova: u64
    off: u64
    if iommu_te == 0 or n == 0 or n > 16:
        return 0
    if iommu_grant_n != 0:
        iommu_unmap_grant()
    iommu_sl_rebuild()
    iova = IOMMU_IOVA
    off = iommu_sl_idx(iova, 12)
    i = 0
    while i < n:
        mmio_wr(iommu_sl_va[3] + (off + i) * 8, 8, (pa + i * PAGE) | 3)
        i += 1
    bus = (bdf >> 8) & 0xFF
    devfn = bdf & 0xFF
    i = 0
    while i < 256:
        mmio_wr(iommu_ctx_tr_va + i * 16, 8, mmio_rd(iommu_ctx_pt_va + i * 16, 8))
        mmio_wr(iommu_ctx_tr_va + i * 16 + 8, 8, mmio_rd(iommu_ctx_pt_va + i * 16 + 8, 8))
        i += 1
    mmio_wr(iommu_ctx_tr_va + devfn * 16, 8, 1 | iommu_sl_top_pa())
    mmio_wr(iommu_ctx_tr_va + devfn * 16 + 8, 8, (u64(2) << 8) | iommu_aw)
    iommu_root_set(bus, iommu_ctx_tr_pa)
    if iommu_te_set(0) == 0 or iommu_te_set(1) == 0:
        puts("VT-d: translation reload timed out\n")
        return 0
    iommu_inv()
    iommu_grant_iova = iova
    iommu_grant_pa = pa
    iommu_grant_n = n
    iommu_grant_bdf = bdf
    return iova

def iommu_unmap_grant():
    i: u64
    bus: u64
    if iommu_te == 0 or iommu_grant_n == 0:
        iommu_grant_n = 0
        iommu_grant_iova = 0
        return
    bus = (iommu_grant_bdf >> 8) & 0xFF
    iommu_root_set(bus, iommu_ctx_pt_pa)
    i = 0
    while i < 16:
        mmio_wr(iommu_sl_va[3] + i * 8, 8, 0)
        i += 1
    if iommu_te_set(0) == 0 or iommu_te_set(1) == 0:
        puts("VT-d: translation reload timed out\n")
    iommu_inv()
    iommu_grant_n = 0
    iommu_grant_iova = 0
    iommu_grant_pa = 0
    iommu_grant_bdf = 0

def iommu_grant_ok() -> u64:
    i: u64
    pte: u64
    mapped: u64
    off: u64
    if iommu_te == 0 or iommu_grant_n == 0:
        return 0
    # the grant PT is the only populated leaf table; every present entry must be the grant
    mapped = 0
    off = iommu_sl_idx(iommu_grant_iova, 12)
    i = 0
    while i < 512:
        pte = mmio_rd(iommu_sl_va[3] + i * 8, 8)
        if (pte & 1) != 0:
            if i < off or i >= off + iommu_grant_n:
                return 0
            if (pte & ~u64(0xFFF)) != (iommu_grant_pa + (i - off) * PAGE):
                return 0
            mapped += 1
        i += 1
    if mapped != iommu_grant_n or iommu_grant_iova == iommu_grant_pa:
        return 0
    # the 2 MiB window that would identity-map the host PA must stay empty
    if iommu_sl_idx(iommu_grant_pa, 21) != iommu_sl_idx(iommu_grant_iova, 21):
        if (mmio_rd(iommu_sl_va[2] + iommu_sl_idx(iommu_grant_pa, 21) * 8, 8) & 1) != 0:
            return 0
    return 1

def iommu_report():
    if iommu_te == 0:
        return
    if iommu_grant_n == 0 or iommu_grant_ok() == 0:
        puts("VT-d: drv 403 has no confined grant\n")
        return
    puts("VT-d: drv 403 returns IOVA ")
    put_hex(iommu_grant_iova)
    puts(" (PA ")
    put_hex(iommu_grant_pa)
    puts("), ")
    put_u64(iommu_grant_n)
    puts(" pages, slpt maps only the grant\n")

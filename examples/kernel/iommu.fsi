# Intel VT-d (DMAR) probe and DMA remapping. Parses the ACPI DMAR table through
# the RSDP saved by acpi_init (width-1 mmio_rd of a temporary window, no new
# unsafe functions or blocks), then after pci_scan, iommu_enable() programs every
# remapping unit (DRHD, up to IOMMU_MAX) with one shared root table and turns
# translation on. Default deny: a requester ID gets a context entry only when a
# kernel driver claims the device (iommu_claim, called by pci_busmaster when the
# kernel enables bus mastering: pass-through, domain 1) or a DMA grant covers it
# (drv 402: translate, domain 2, a second-level table that maps only the grant at a
# non-identity IOVA; drv 403 returns that IOVA). Every other requester ID, scanned
# or not, has no root or context entry, so its DMA is blocked by the IOMMU.
# Context tables are allocated per bus on demand (IOMMU_CTX_MAX buses);
# iommu_audit() walks all 65536 requester IDs and checks that exactly the claimed
# and granted ones are present, with the right translation type and domain.
# Without a DMAR unit, behaviour is unchanged (403 still a host PA).
# Call iommu_probe() after acpi_init and before pmm_reclaim.
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
# programmed units (set by iommu_enable)
IOMMU_IOVA = 0x0000000020000000
IOMMU_CTX_MAX = 8
IOMMU_CLAIM_MAX = 32
IOMMU_DID_PT = 1
IOMMU_DID_GRANT = 2
iommu_regs: u64[4]
iommu_iros: u64[4]
iommu_caps: u64[4]
iommu_nprog: u64
iommu_te: u64
iommu_aw: u64
iommu_tmp_pa: u64
iommu_rt_va: u64
iommu_rt_pa: u64
iommu_ctx_bus: u64[8]
iommu_ctx_va: u64[8]
iommu_ctx_pa: u64[8]
iommu_ctx_n: u64
iommu_claim_rid: u64[32]
iommu_claim_n: u64
iommu_sl_va: u64[4]
iommu_sl_pa: u64[4]
iommu_grant_iova: u64
iommu_grant_pa: u64
iommu_grant_n: u64
iommu_grant_bdf: u64
iommu_audit_present: u64
iommu_audit_stray: u64
iommu_audit_missing: u64

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

def iommu_wait32(u: u64, off: u64, mask: u64, want: u64) -> u64:
    i: u64 = 0
    while i < 1000000:
        if (mmio_rd(iommu_regs[u] + off, 4) & mask) == want:
            return 1
        i += 1
    return 0

def iommu_wait64_clear(u: u64, off: u64, mask: u64) -> u64:
    i: u64 = 0
    while i < 1000000:
        if (mmio_rd(iommu_regs[u] + off, 8) & mask) == 0:
            return 1
        i += 1
    return 0

def iommu_wr64(u: u64, off: u64, v: u64):
# 64-bit VT-d command registers: write the high half first, the low half last (the trigger)
    mmio_wr(iommu_regs[u] + off + 4, 4, v >> 32)
    mmio_wr(iommu_regs[u] + off, 4, v & 0xFFFFFFFF)

def iommu_inv():
# on every programmed unit: write-buffer flush (if required), then global context-cache and
# IOTLB invalidation (legacy register interface)
    u: u64 = 0
    while u < iommu_nprog:
        if (iommu_caps[u] & 0x10) != 0:
            mmio_wr(iommu_regs[u] + 0x18, 4, (mmio_rd(iommu_regs[u] + 0x1C, 4) & 0x96FFFFFF) | (u64(1) << 27))
            iommu_wait32(u, 0x1C, u64(1) << 27, 0)
        iommu_wr64(u, 0x28, (u64(1) << 63) | (u64(1) << 61))
        if iommu_wait64_clear(u, 0x28, u64(1) << 63) != 0:
            iommu_wr64(u, iommu_iros[u] + 8, (u64(1) << 63) | (u64(1) << 60) | (u64(1) << 49) | (u64(1) << 48))
            iommu_wait64_clear(u, iommu_iros[u] + 8, u64(1) << 63)
        u += 1

def iommu_sl_top_pa() -> u64:
    if iommu_aw == 2:
        return iommu_sl_pa[0]
    return iommu_sl_pa[1]

def iommu_sl_idx(iova: u64, sh: u64) -> u64:
    return (iova >> sh) & 0x1FF

def iommu_is_claimed(rid: u64) -> u64:
    i: u64 = 0
    while i < iommu_claim_n:
        if iommu_claim_rid[i] == rid:
            return 1
        i += 1
    return 0

def iommu_is_granted(rid: u64) -> u64:
    return iommu_grant_n != 0 and rid == iommu_grant_bdf

# the context entry a requester ID should have: [lo, hi] packed as lo (hi is derived from the
# domain); 0 = no entry (DMA blocked)
def iommu_want_lo(rid: u64) -> u64:
    if iommu_is_granted(rid):
        return 1 | iommu_sl_top_pa()
    if iommu_is_claimed(rid):
        return 1 | (u64(2) << 2)
    return 0

def iommu_want_hi(rid: u64) -> u64:
    if iommu_is_granted(rid):
        return (IOMMU_DID_GRANT << 8) | iommu_aw
    if iommu_is_claimed(rid):
        return (IOMMU_DID_PT << 8) | iommu_aw
    return 0

def iommu_ctx_find(bus: u64) -> u64:
# index + 1 of the context table for this bus (0 = none)
    i: u64 = 0
    while i < iommu_ctx_n:
        if iommu_ctx_bus[i] == bus:
            return i + 1
        i += 1
    return 0

def iommu_ctx_get(bus: u64) -> u64:
# the context table for a bus, allocated (zeroed: every entry not present) and hooked into the
# root table on first use; 0 when IOMMU_CTX_MAX buses are already in use
    k = iommu_ctx_find(bus)
    if k != 0:
        return iommu_ctx_va[k - 1]
    if iommu_ctx_n >= IOMMU_CTX_MAX:
        return 0
    va = iommu_page()
    if va == 0:
        return 0
    iommu_ctx_bus[iommu_ctx_n] = bus
    iommu_ctx_va[iommu_ctx_n] = va
    iommu_ctx_pa[iommu_ctx_n] = iommu_tmp_pa
    iommu_ctx_n += 1
    mmio_wr(iommu_rt_va + bus * 16 + 8, 8, 0)
    mmio_wr(iommu_rt_va + bus * 16, 8, 1 | iommu_tmp_pa)
    return va

# rewrite one requester ID's context entry from the claim / grant state: clear the present bit
# first, then the high word, then the low word (present last)
def iommu_apply(rid: u64) -> u64:
    lo = iommu_want_lo(rid)
    k = iommu_ctx_find((rid >> 8) & 0xFF)
    va: u64 = 0
    if k != 0:
        va = iommu_ctx_va[k - 1]
    elif lo != 0:
        va = iommu_ctx_get((rid >> 8) & 0xFF)
        if va == 0:
            puts("VT-d: out of context tables\n")
            return 0
    if va == 0:
        return 1
    e = va + (rid & 0xFF) * 16
    mmio_wr(e, 8, 0)
    mmio_wr(e + 8, 8, iommu_want_hi(rid))
    mmio_wr(e, 8, lo)
    return 1

# a kernel driver is about to enable bus mastering on bdf: give it a pass-through context
# (claims made before iommu_enable are applied when the tables are built)
def iommu_claim(bdf: u64):
    if iommu_is_claimed(bdf) != 0:
        return
    if iommu_claim_n >= IOMMU_CLAIM_MAX:
        puts("VT-d: claim table full\n")
        return
    iommu_claim_rid[iommu_claim_n] = bdf
    iommu_claim_n += 1
    if iommu_te != 0:
        iommu_apply(bdf)
        iommu_inv()

def iommu_enable():
    cap: u64
    ecap: u64
    sagaw: u64
    both: u64
    i: u64
    u: u64
    gsts: u64
    reg: u64
    if iommu_n == 0 or iommu_base[0] == 0 or iommu_te != 0:
        return
    # map every unit and pick a second-level width all of them support (48-bit, else 39-bit)
    both = 0x1F
    u = 0
    while u < iommu_n:
        if iommu_base[u] == 0:
            puts("VT-d: remapping unit has no register base\n")
            return
        reg = mmio_map(iommu_base[u], 0x10000)
        if reg == 0:
            puts("VT-d: could not map remapping registers\n")
            return
        iommu_regs[u] = reg
        cap = mmio_rd(reg + 8, 8)
        ecap = mmio_rd(reg + 0x10, 8)
        iommu_caps[u] = cap
        iommu_iros[u] = ((ecap >> 8) & 0x3FF) * 16
        both = both & ((cap >> 8) & 0x1F)
        if (ecap & 0x40) == 0:
            puts("VT-d: pass-through not supported, leaving translation off\n")
            return
        u += 1
    iommu_aw = 0
    if (both & 4) != 0:
        iommu_aw = 2
    elif (both & 2) != 0:
        iommu_aw = 1
    if iommu_aw == 0:
        puts("VT-d: no 39- or 48-bit second-level page tables\n")
        return
    iommu_rt_va = iommu_page()
    iommu_rt_pa = iommu_tmp_pa
    if iommu_rt_va == 0:
        puts("VT-d: no frames for remapping tables\n")
        return
    i = 0
    while i < 4:
        iommu_sl_va[i] = iommu_page()
        iommu_sl_pa[i] = iommu_tmp_pa
        if iommu_sl_va[i] == 0:
            puts("VT-d: no frames for remapping tables\n")
            return
        i += 1
    # default deny: the root table starts empty; only claimed devices get (pass-through) entries
    iommu_ctx_n = 0
    iommu_grant_n = 0
    iommu_grant_iova = 0
    i = 0
    while i < iommu_claim_n:
        if iommu_apply(iommu_claim_rid[i]) == 0:
            return
        i += 1
    u = 0
    while u < iommu_n:
        reg = iommu_regs[u]
        mmio_wr(reg + 0x20, 8, iommu_rt_pa)
        mmio_wr(reg + 0x18, 4, u64(1) << 30)
        if iommu_wait32(u, 0x1C, u64(1) << 30, u64(1) << 30) == 0:
            puts("VT-d: set-root timed out\n")
            return
        iommu_nprog = u + 1
        u += 1
    iommu_inv()
    u = 0
    while u < iommu_nprog:
        reg = iommu_regs[u]
        gsts = mmio_rd(reg + 0x1C, 4)
        mmio_wr(reg + 0x18, 4, (gsts & 0x96FFFFFF) | (u64(1) << 31))
        if iommu_wait32(u, 0x1C, u64(1) << 31, u64(1) << 31) == 0:
            puts("VT-d: translation enable timed out\n")
            return
        u += 1
    iommu_te = 1
    puts("VT-d: translation on, ")
    put_u64(iommu_nprog)
    puts(" of ")
    put_u64(iommu_n)
    puts(" remapping unit")
    if iommu_n != 1:
        puts("s")
    puts(" programmed (default deny; kernel-driven devices pass-through; drv 403 returns an IOVA for a DMA grant)\n")

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
    iommu_grant_iova = iova
    iommu_grant_pa = pa
    iommu_grant_n = n
    iommu_grant_bdf = bdf
    if iommu_apply(bdf) == 0:
        iommu_grant_n = 0
        iommu_grant_iova = 0
        return 0
    iommu_inv()
    return iova

def iommu_unmap_grant():
    i: u64
    bdf: u64
    if iommu_te == 0 or iommu_grant_n == 0:
        iommu_grant_n = 0
        iommu_grant_iova = 0
        return
    bdf = iommu_grant_bdf
    iommu_grant_n = 0
    iommu_grant_iova = 0
    iommu_apply(bdf)
    i = 0
    while i < 16:
        mmio_wr(iommu_sl_va[3] + i * 8, 8, 0)
        i += 1
    iommu_inv()
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

# Walk every requester ID (256 buses x 256 devfn). A present root entry must point at one of the
# kernel's context tables; a present context entry must belong to a claimed (pass-through,
# domain 1) or granted (translate, domain 2, the grant's second-level root) device; every claimed
# or granted device must have its entry. Returns 1 when there is no stray and no missing entry.
def iommu_audit() -> u64:
    bus: u64 = 0
    present: u64 = 0
    stray: u64 = 0
    missing: u64 = 0
    d: u64 = 0
    while bus < 256:
        re = mmio_rd(iommu_rt_va + bus * 16, 8)
        k = iommu_ctx_find(bus)
        if (re & 1) == 0:
            if k != 0:
                missing += 1
            d = 0
            while d < 256:
                if iommu_want_lo((bus << 8) | d) != 0:
                    missing += 1
                d += 1
        elif k == 0 or (re & ~u64(0xFFF)) != iommu_ctx_pa[k - 1]:
            stray += 1
        else:
            va = iommu_ctx_va[k - 1]
            d = 0
            while d < 256:
                rid = (bus << 8) | d
                lo = mmio_rd(va + d * 16, 8)
                hi = mmio_rd(va + d * 16 + 8, 8)
                want = iommu_want_lo(rid)
                if (lo & 1) != 0:
                    present += 1
                    if want == 0 or lo != want or hi != iommu_want_hi(rid):
                        stray += 1
                elif want != 0:
                    missing += 1
                d += 1
        bus += 1
    iommu_audit_present = present
    iommu_audit_stray = stray
    iommu_audit_missing = missing
    return stray == 0 and missing == 0

# scanned functions that are neither claimed nor granted (their DMA is blocked)
def iommu_denied_count() -> u64:
    i: u64 = 0
    n: u64 = 0
    while i < pci_n:
        if iommu_want_lo(pci_bdf[i]) == 0:
            n += 1
        i += 1
    return n

def iommu_put_bdf(rid: u64):
    put_hex8((rid >> 8) & 0xFF)
    putc(58)
    put_hex8((rid >> 3) & 0x1F)
    putc(46)
    put_u64(rid & 7)

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
    puts("VT-d: default deny: ")
    put_u64(iommu_claim_n)
    puts(" kernel-driven device(s) pass-through, 1 granted (")
    iommu_put_bdf(iommu_grant_bdf)
    puts("), ")
    put_u64(iommu_denied_count())
    puts(" scanned device(s) and every unscanned requester ID blocked\n")
    ok = iommu_audit()
    puts("VT-d: context audit: 65536 requester IDs, ")
    put_u64(iommu_audit_present)
    puts(" present, ")
    put_u64(iommu_audit_stray)
    puts(" stray, ")
    put_u64(iommu_audit_missing)
    puts(" missing, ")
    put_u64(iommu_ctx_n)
    puts(" context table(s)\n")
    if ok == 0:
        panic("VT-d: context audit failed")
    # negative case: a stray entry for a requester ID nobody owns must be caught. The bus of the
    # grant already has a context table; pick a devfn there that is neither claimed nor granted.
    k = iommu_ctx_find((iommu_grant_bdf >> 8) & 0xFF)
    d: u64 = 0xFF
    while d > 0 and iommu_want_lo((iommu_grant_bdf & 0xFF00) | d) != 0:
        d -= 1
    if k == 0 or iommu_want_lo((iommu_grant_bdf & 0xFF00) | d) != 0:
        panic("VT-d: no free requester ID for the audit check")
    e = iommu_ctx_va[k - 1] + d * 16
    mmio_wr(e + 8, 8, (IOMMU_DID_PT << 8) | iommu_aw)
    mmio_wr(e, 8, 1 | (u64(2) << 2))
    bad = iommu_audit()
    s = iommu_audit_stray
    mmio_wr(e, 8, 0)
    mmio_wr(e + 8, 8, 0)
    if bad != 0 or s != 1 or iommu_audit() == 0:
        panic("VT-d: context audit missed a stray entry")
    puts("VT-d: context audit catches a stray pass-through entry for ")
    iommu_put_bdf((iommu_grant_bdf & 0xFF00) | d)
    putc(10)

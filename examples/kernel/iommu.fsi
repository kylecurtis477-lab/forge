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
# requester-ID aliases (iommu_aliases): [0] is the device itself, the last one is the ID its DMA
# carries at the root complex; iommu_alias_br is the bridge that introduced the last alias + 1
iommu_alias: u64[4]
iommu_alias_n: u64
iommu_alias_br: u64
iommu_grant_rid: u64[4]
iommu_grant_nrid: u64
# DMA self-test domain (iommu_dma_selftest, QEMU edu device): translate, domain 3
IOMMU_DID_TEST = 3
IOMMU_TEST_IOVA = 0x0000000000800000
iommu_test_rid: u64[4]
iommu_test_nrid: u64
iommu_tsl_va: u64[4]
iommu_tsl_pa: u64[4]
# queued invalidation (needed to invalidate the interrupt-entry cache) and interrupt remapping
iommu_qi: u64
iommu_iq_va: u64[4]
iommu_iq_pa: u64[4]
iommu_iq_tail: u64[4]
iommu_qs_va: u64
iommu_qs_pa: u64
iommu_qseq: u64
iommu_ir: u64
iommu_irt_va: u64
iommu_irt_pa: u64
IOMMU_IRTE_VBLK = 0
IOMMU_IRT_ENTRIES = 256
VEC_IOMMU_TEST = 0x51
iommu_test_irqs: u64
# last fault drained by iommu_fault_drain
iommu_f_sid: u64
iommu_f_reason: u64
iommu_f_addr: u64
iommu_f_read: u64

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

def iommu_qi_submit(u: u64, lo: u64, hi: u64):
    t = iommu_iq_tail[u]
    mmio_wr(iommu_iq_va[u] + t * 16 + 8, 8, hi)
    mmio_wr(iommu_iq_va[u] + t * 16, 8, lo)
    iommu_iq_tail[u] = (t + 1) & 0xFF

# Queued invalidation on unit u: the given descriptors (0 = none) then a wait descriptor that writes
# a fresh sequence number to the status word; returns 1 once the hardware has written it.
def iommu_qi_run(u: u64, d1: u64, d2: u64, d3: u64) -> u64:
    if d1 != 0:
        iommu_qi_submit(u, d1, 0)
    if d2 != 0:
        iommu_qi_submit(u, d2, 0)
    if d3 != 0:
        iommu_qi_submit(u, d3, 0)
    iommu_qseq = (iommu_qseq + 1) & 0x7FFFFFFF
    sa = iommu_qs_va + u * 8
    mmio_wr(sa, 4, 0)
    iommu_qi_submit(u, 0x5 | (u64(1) << 5) | (iommu_qseq << 32), iommu_qs_pa + u * 8)
    mmio_wr(iommu_regs[u] + 0x88, 8, iommu_iq_tail[u] << 4)
    i: u64 = 0
    while i < 1000000:
        if mmio_rd(sa, 4) == iommu_qseq:
            return 1
        i += 1
    puts("VT-d: queued invalidation timed out\n")
    return 0

def iommu_inv():
# on every programmed unit: global context-cache and IOTLB invalidation, through the invalidation
# queue once it is on, else the legacy registers (after a write-buffer flush if required)
    u: u64 = 0
    if iommu_qi != 0:
        while u < iommu_nprog:
            iommu_qi_run(u, 0x1 | (u64(1) << 4), 0x2 | (u64(1) << 4) | (u64(1) << 6) | (u64(1) << 7), 0)
            u += 1
        return
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
    i: u64 = 0
    if iommu_grant_n == 0:
        return 0
    while i < iommu_grant_nrid:
        if iommu_grant_rid[i] == rid:
            return 1
        i += 1
    return 0

def iommu_is_test(rid: u64) -> u64:
    i: u64 = 0
    while i < iommu_test_nrid:
        if iommu_test_rid[i] == rid:
            return 1
        i += 1
    return 0

def iommu_tsl_top_pa() -> u64:
    if iommu_aw == 2:
        return iommu_tsl_pa[0]
    return iommu_tsl_pa[1]

# offset of PCI capability `id` in bdf's config space (0 = none)
def pci_cap_find(bdf: u64, id: u64) -> u64:
    if ((pci_rd32(bdf, 4) >> 16) & 0x10) == 0:
        return 0
    p = pci_rd32(bdf, 0x34) & 0xFC
    n: u64 = 0
    while p >= 0x40 and n < 48:
        w = pci_rd32(bdf, p)
        if (w & 0xFF) == id:
            return p
        p = (w >> 8) & 0xFC
        n += 1
    return 0

# PCI Express device/port type (0x7 = PCIe-to-PCI/PCI-X bridge), or 0xFF for a conventional device
def pcie_type(bdf: u64) -> u64:
    c = pci_cap_find(bdf, 0x10)
    if c == 0:
        return 0xFF
    return (pci_rd32(bdf, c) >> 20) & 0xF

# bdf + 1 of the PCI-to-PCI bridge whose secondary bus is `bus` (0 = none: a root bus)
def pci_parent_bridge(bus: u64) -> u64:
    i: u64 = 0
    while i < pci_n:
        if (pci_cls[i] >> 8) == 0x0604 and ((pci_rd32(pci_bdf[i], 0x18) >> 8) & 0xFF) == bus:
            return pci_bdf[i] + 1
        i += 1
    return 0

# The requester IDs a device's DMA can carry (PCI DMA aliasing). Walking up from the device: behind
# a PCIe-to-PCI bridge, conventional transactions carry (secondary bus, devfn 0); behind a
# conventional PCI-to-PCI bridge, the bridge's own ID; a PCIe port does not alias. Every ID on the
# way is kept so a context entry can be installed for each. Returns the count (>= 1).
def iommu_aliases(bdf: u64) -> u64:
    iommu_alias[0] = bdf
    iommu_alias_n = 1
    iommu_alias_br = 0
    cur = bdf
    lv: u64 = 0
    while lv < 8 and ((cur >> 8) & 0xFF) != 0:
        bus = (cur >> 8) & 0xFF
        br = pci_parent_bridge(bus)
        if br == 0:
            return iommu_alias_n
        t = pcie_type(br - 1)
        a: u64 = 0
        if t == 7:
            a = bus << 8
        elif t == 0xFF:
            a = br - 1
        else:
            return iommu_alias_n
        if iommu_alias[iommu_alias_n - 1] != a and iommu_alias_n < 4:
            iommu_alias[iommu_alias_n] = a
            iommu_alias_n += 1
            iommu_alias_br = br
        cur = br - 1
        lv += 1
    return iommu_alias_n

# the context entry a requester ID should have: [lo, hi] packed as lo (hi is derived from the
# domain); 0 = no entry (DMA blocked)
def iommu_want_lo(rid: u64) -> u64:
    if iommu_is_test(rid):
        return 1 | iommu_tsl_top_pa()
    if iommu_is_granted(rid):
        return 1 | iommu_sl_top_pa()
    if iommu_is_claimed(rid):
        return 1 | (u64(2) << 2)
    return 0

def iommu_want_hi(rid: u64) -> u64:
    if iommu_is_test(rid):
        return (IOMMU_DID_TEST << 8) | iommu_aw
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
# Each of the device's requester-ID aliases gets the same entry.
def iommu_claim(bdf: u64):
    n = iommu_aliases(bdf)
    i: u64 = 0
    ch: u64 = 0
    while i < n:
        rid = iommu_alias[i]
        if iommu_is_claimed(rid) == 0:
            if iommu_claim_n >= IOMMU_CLAIM_MAX:
                puts("VT-d: claim table full\n")
                return
            iommu_claim_rid[iommu_claim_n] = rid
            iommu_claim_n += 1
            if iommu_te != 0:
                iommu_apply(rid)
                ch = 1
        i += 1
    if ch != 0:
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
        iommu_tsl_va[i] = iommu_page()
        iommu_tsl_pa[i] = iommu_tmp_pa
        if iommu_sl_va[i] == 0 or iommu_tsl_va[i] == 0:
            puts("VT-d: no frames for remapping tables\n")
            return
        i += 1
    # fault events are polled (iommu_fault_drain), not delivered: keep the fault interrupt masked
    u = 0
    while u < iommu_n:
        mmio_wr(iommu_regs[u] + 0x38, 4, u64(1) << 31)
        u += 1
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
    iommu_ir_enable()
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
    iommu_grant_nrid = iommu_aliases(bdf)
    i = 0
    while i < iommu_grant_nrid:
        iommu_grant_rid[i] = iommu_alias[i]
        i += 1
    i = 0
    while i < iommu_grant_nrid:
        if iommu_apply(iommu_grant_rid[i]) == 0:
            iommu_grant_n = 0
            iommu_grant_iova = 0
            return 0
        i += 1
    iommu_inv()
    return iova

def iommu_unmap_grant():
    i: u64
    if iommu_te == 0 or iommu_grant_n == 0:
        iommu_grant_n = 0
        iommu_grant_iova = 0
        return
    iommu_grant_n = 0
    iommu_grant_iova = 0
    i = 0
    while i < iommu_grant_nrid:
        iommu_apply(iommu_grant_rid[i])
        i += 1
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

def iommu_reason_str(r: u64) -> ptr[u8]:
    if r == 1:
        return "root entry not present"
    if r == 2:
        return "context entry not present"
    if r == 3:
        return "invalid context entry"
    if r == 4:
        return "address beyond the guest width"
    if r == 5:
        return "write not permitted"
    if r == 6:
        return "read not permitted"
    if r == 7:
        return "second-level entry invalid"
    if r == 0x21:
        return "interrupt index beyond the table"
    if r == 0x22:
        return "interrupt entry not present"
    if r == 0x24:
        return "reserved bits in interrupt entry"
    if r == 0x25:
        return "compatibility-format interrupt blocked"
    if r == 0x26:
        return "interrupt source-ID check failed"
    return "other"

# Read and clear every pending fault record on every unit (FSTS, then each FRCD at the
# capability's fault-recording offset). Prints each fault unless quiet; the last one is kept in
# iommu_f_*. Returns the number of records drained.
def iommu_fault_drain(quiet: u64) -> u64:
    u: u64 = 0
    n: u64 = 0
    while u < iommu_nprog:
        reg = iommu_regs[u]
        cap = iommu_caps[u]
        nfr = ((cap >> 40) & 0xFF) + 1
        fro = ((cap >> 24) & 0x3FF) * 16
        i: u64 = 0
        while i < nfr:
            hi = mmio_rd(reg + fro + i * 16 + 8, 8)
            if (hi >> 63) != 0:
                lo = mmio_rd(reg + fro + i * 16, 8)
                iommu_f_sid = hi & 0xFFFF
                iommu_f_reason = (hi >> 32) & 0xFF
                iommu_f_read = (hi >> 62) & 1
                iommu_f_addr = lo & ~u64(0xFFF)
                n += 1
                if quiet == 0:
                    puts("VT-d fault: unit ")
                    put_u64(u)
                    puts(", source ")
                    iommu_put_bdf(iommu_f_sid)
                    puts(", reason 0x")
                    put_hex8(iommu_f_reason)
                    puts(" (")
                    puts(iommu_reason_str(iommu_f_reason))
                    puts("), ")
                    if iommu_f_read != 0:
                        puts("read of ")
                    else:
                        puts("write to ")
                    put_hex(iommu_f_addr)
                    putc(10)
                # F is write-1-to-clear (bit 127 of the record = bit 31 of its last dword)
                mmio_wr(reg + fro + i * 16 + 12, 4, u64(1) << 31)
            i += 1
        # clear primary-fault overflow and the other write-1-to-clear status bits
        mmio_wr(reg + 0x34, 4, mmio_rd(reg + 0x34, 4) & 0x7D)
        u += 1
    return n

# the last drained fault came from rid with one of the reasons r1 / r2 (and at addr, unless 0)
def iommu_fault_is(rid: u64, r1: u64, r2: u64, at: u64) -> u64:
    if iommu_f_sid != rid:
        return 0
    if iommu_f_reason != r1 and iommu_f_reason != r2:
        return 0
    if at != 0 and iommu_f_addr != at:
        return 0
    return 1

# QEMU's edu device (1234:11e8): DMA engine at BAR0 +0x80 src, +0x88 dst, +0x90 count, +0x98 cmd
# (bit 0 start/busy, bit 1 edu-to-RAM); its 4 KiB buffer is at device address 0x40000.
EDU_ID = 0x11E81234
EDU_BUF = 0x40000

def edu_dma(regs: u64, src: u64, dst: u64, cnt: u64, to_ram: u64) -> u64:
    mmio_wr(regs + 0x80, 8, src)
    mmio_wr(regs + 0x88, 8, dst)
    mmio_wr(regs + 0x90, 8, cnt)
    mmio_wr(regs + 0x98, 8, 1 | (to_ram << 1))
    i: u64 = 0
    while i < 200000000:
        if (mmio_rd(regs + 0x98, 8) & 1) == 0:
            return 1
        i += 1
    return 0

def iommu_tsl_build(pa: u64, n: u64):
# test second-level tree: IOMMU_TEST_IOVA + i*PAGE -> pa + i*PAGE for i < n, nothing else
    i: u64 = 0
    while i < 4:
        k: u64 = 0
        while k < PAGE:
            mmio_wr(iommu_tsl_va[i] + k, 8, 0)
            k += 8
        i += 1
    if iommu_aw == 2:
        mmio_wr(iommu_tsl_va[0] + iommu_sl_idx(IOMMU_TEST_IOVA, 39) * 8, 8, iommu_tsl_pa[1] | 3)
    mmio_wr(iommu_tsl_va[1] + iommu_sl_idx(IOMMU_TEST_IOVA, 30) * 8, 8, iommu_tsl_pa[2] | 3)
    mmio_wr(iommu_tsl_va[2] + iommu_sl_idx(IOMMU_TEST_IOVA, 21) * 8, 8, iommu_tsl_pa[3] | 3)
    off = iommu_sl_idx(IOMMU_TEST_IOVA, 12)
    i = 0
    while i < n:
        mmio_wr(iommu_tsl_va[3] + (off + i) * 8, 8, (pa + i * PAGE) | 3)
        i += 1

def iommu_test_set(n: u64):
# install (n = alias count) or remove (n = 0) the test domain for the aliases in iommu_alias
    i: u64 = 0
    old = iommu_test_nrid
    k: u64 = 0
    if n == 0:
        iommu_test_nrid = 0
        while k < old:
            iommu_apply(iommu_test_rid[k])
            k += 1
    else:
        while i < n:
            iommu_test_rid[i] = iommu_alias[i]
            i += 1
        iommu_test_nrid = n
        while k < n:
            iommu_apply(iommu_test_rid[k])
            k += 1
    iommu_inv()

# DMA fault recording and requester-ID aliasing, observed in hardware (QEMU's VT-d model): the edu
# device sits behind a PCIe-to-PCI bridge, so its DMA carries the alias (secondary bus, devfn 0).
# 1. edu is not claimed or granted: its DMA must fault, recorded with the alias as source.
# 2. a test domain on every alias maps two pages at IOMMU_TEST_IOVA: 64 bytes go into edu and back
#    out to the second page, identical, with no fault.
# 3. DMA to IOMMU_TEST_IOVA + 16 pages (outside the mapping) must fault at that address.
def iommu_dma_selftest():
    sz: u64 = 0
    apa: u64 = 0
    if iommu_te == 0:
        return
    f = pci_find_id(EDU_ID)
    if f == 0:
        puts("VT-d: no edu test device, DMA fault tests skipped\n")
        return
    bdf = f - 1
    na = iommu_aliases(bdf)
    rid = iommu_alias[na - 1]
    puts("VT-d: edu ")
    iommu_put_bdf(bdf)
    puts(" DMA requester ID ")
    iommu_put_bdf(rid)
    if iommu_alias_br != 0:
        if pcie_type(iommu_alias_br - 1) == 7:
            puts(" (alias via PCIe-to-PCI bridge ")
        else:
            puts(" (alias via PCI bridge ")
        iommu_put_bdf(iommu_alias_br - 1)
        putc(41)
    putc(10)
    pa = pci_bar(bdf, 0, addr(sz))
    if pa == 0 or sz < 0x1000:
        panic("VT-d: edu BAR0 missing")
    regs = mmio_map(pa, 0x1000)
    if regs == 0 or (mmio_rd(regs, 4) & 0xFF) != 0xED:
        panic("VT-d: edu registers not readable")
    # memory decoding + bus master, deliberately without iommu_claim: edu stays denied
    pci_wr32(bdf, 4, pci_rd32(bdf, 4) | 6)
    buf = dma_alloc(2, addr(apa))
    if buf == 0:
        panic("VT-d: no frames for the edu test")
    i: u64 = 0
    while i < 64:
        mmio_wr(buf + i, 8, 0x0F1E2D3C4B5A6978 ^ (i * 0x0101010101010101))
        i += 8
    iommu_fault_drain(1)
    # 1. denied
    if edu_dma(regs, apa, EDU_BUF, 64, 0) == 0:
        panic("VT-d: edu DMA timed out")
    if iommu_fault_drain(0) == 0 or iommu_fault_is(rid, 1, 2, 0) == 0:
        panic("VT-d: denied edu DMA did not fault with its requester ID")
    puts("VT-d: denied edu DMA faulted: source ")
    iommu_put_bdf(iommu_f_sid)
    puts(", reason 0x")
    put_hex8(iommu_f_reason)
    puts(" (")
    puts(iommu_reason_str(iommu_f_reason))
    puts(")\n")
    # 2. translated through the alias context
    iommu_tsl_build(apa, 2)
    iommu_test_set(na)
    if iommu_audit() == 0:
        panic("VT-d: context audit failed with the test domain")
    if edu_dma(regs, IOMMU_TEST_IOVA, EDU_BUF, 64, 0) == 0 or edu_dma(regs, EDU_BUF, IOMMU_TEST_IOVA + PAGE, 64, 1) == 0:
        panic("VT-d: edu DMA timed out")
    nf = iommu_fault_drain(0)
    i = 0
    same: u64 = 1
    while i < 64:
        if mmio_rd(buf + PAGE + i, 8) != mmio_rd(buf + i, 8):
            same = 0
        i += 8
    if nf != 0 or same == 0:
        panic("VT-d: edu DMA through the test domain failed")
    puts("VT-d: edu DMA through the ")
    put_u64(na)
    puts(" requester-ID context(s): 64 bytes into edu and back to the second page of a 2-page mapping, identical, 0 faults\n")
    # 3. outside the mapping
    if edu_dma(regs, IOMMU_TEST_IOVA + 16 * PAGE, EDU_BUF, 64, 0) == 0:
        panic("VT-d: edu DMA timed out")
    if iommu_fault_drain(0) == 0 or iommu_fault_is(rid, 6, 7, IOMMU_TEST_IOVA + 16 * PAGE) == 0:
        panic("VT-d: out-of-mapping edu DMA did not fault at its IOVA")
    puts("VT-d: out-of-grant edu DMA faulted at IOVA ")
    put_hex(iommu_f_addr)
    puts(": source ")
    iommu_put_bdf(iommu_f_sid)
    puts(", reason 0x")
    put_hex8(iommu_f_reason)
    puts(" (")
    puts(iommu_reason_str(iommu_f_reason))
    puts(")\n")
    iommu_ir_selftest(bdf, regs, rid)
    # tear down: bus mastering off, test domain removed, tables audited again, frames returned
    pci_wr32(bdf, 4, pci_rd32(bdf, 4) & ~u64(4))
    iommu_test_set(0)
    if iommu_audit() == 0:
        panic("VT-d: context audit failed after the edu test")
    frame_free(apa)
    frame_free(apa + PAGE)

# DMA policy (A.2 gap 7). With remapping active, deny is the default and grants are confined.
# Without a usable remapping unit (no DMAR table, no DRHD, or iommu_enable failed) the written
# policy is permissive: kernel drivers and drv 402 grants keep working with host physical
# addresses, and the boot log says so. iommu_strict (IOMMU_STRICT_DEFAULT at build time; the kernel
# has no command line yet) refuses drv 402 grants instead: drv_dma_check returns EPERM.
IOMMU_STRICT_DEFAULT = 0
iommu_strict: u64 = IOMMU_STRICT_DEFAULT

def iommu_grant_allowed() -> u64:
    if iommu_te != 0:
        return 1
    if iommu_strict != 0:
        return 0
    return 1

def iommu_policy_report():
    if iommu_te != 0:
        puts("VT-d policy: remapping active (deny by default, grants confined)\n")
        return
    puts("VT-d policy: no remapping unit, DMA unconfined (permissive default: kernel drivers and drv 402 grants use host physical addresses)\n")
    # negative case: the same check sys_drv 402 makes must refuse under the strict policy
    old = iommu_strict
    iommu_strict = 1
    r = drv_dma_check()
    iommu_strict = old
    if r != EPERM or drv_dma_check() != 0:
        panic("VT-d policy: strict mode did not refuse drv 402")
    puts("VT-d policy: strict mode refuses drv 402 without a remapping unit: EPERM\n")

# GCMD write that keeps the persistent enables (TE, EAFL, QIE, IRE, CFI) and adds `bit`; waits for the
# matching GSTS bit
def iommu_gcmd_set(u: u64, bit: u64) -> u64:
    reg = iommu_regs[u]
    mmio_wr(reg + 0x18, 4, (mmio_rd(reg + 0x1C, 4) & 0x96FFFFFF) | bit)
    return iommu_wait32(u, 0x1C, bit, bit)

# Interrupt remapping (A.2 gap 2), when every unit has ecap.IR and ecap.QI. Per unit: an
# invalidation queue (IQA/IQT, QIE) so the interrupt-entry cache can be invalidated, then one shared
# 256-entry remapping table (IRTA, SIRTP), compatibility-format interrupts allowed (CFI: the IOAPIC
# still sends them), and IRE. Devices then get remappable-format MSI/MSI-X messages whose handle
# selects an IRTE (iommu_ir_route); each IRTE is source-validated against the device's requester ID.
def iommu_ir_enable():
    u: u64 = 0
    iommu_ir = 0
    iommu_qi = 0
    while u < iommu_nprog:
        e = mmio_rd(iommu_regs[u] + 0x10, 8)
        if (e & 8) == 0 or (e & 2) == 0:
            puts("VT-d: no interrupt remapping (ecap IR/QI clear), MSI stays compatibility format\n")
            return
        u += 1
    iommu_qs_va = iommu_page()
    iommu_qs_pa = iommu_tmp_pa
    iommu_irt_va = iommu_page()
    iommu_irt_pa = iommu_tmp_pa
    if iommu_qs_va == 0 or iommu_irt_va == 0:
        puts("VT-d: no frames for interrupt remapping\n")
        return
    u = 0
    while u < iommu_nprog:
        iommu_iq_va[u] = iommu_page()
        iommu_iq_pa[u] = iommu_tmp_pa
        if iommu_iq_va[u] == 0:
            puts("VT-d: no frames for interrupt remapping\n")
            return
        iommu_iq_tail[u] = 0
        mmio_wr(iommu_regs[u] + 0x88, 8, 0)
        mmio_wr(iommu_regs[u] + 0x90, 8, iommu_iq_pa[u])
        if iommu_gcmd_set(u, u64(1) << 26) == 0:
            puts("VT-d: queued invalidation enable timed out\n")
            return
        u += 1
    iommu_qi = 1
    # IRTA: table address, S = 7 (2^(7+1) = 256 entries), xAPIC-format destinations (EIME = 0)
    u = 0
    while u < iommu_nprog:
        mmio_wr(iommu_regs[u] + 0xB8, 8, iommu_irt_pa | 7)
        if iommu_gcmd_set(u, u64(1) << 24) == 0:
            puts("VT-d: set interrupt table timed out\n")
            return
        if iommu_qi_run(u, 0x4, 0, 0) == 0:
            return
        # CFI is best effort: a unit that does not report CFIS still passes compatibility-format
        # interrupts while EIME is clear (QEMU's model), which is what the IOAPIC needs
        iommu_gcmd_set(u, u64(1) << 23)
        if iommu_gcmd_set(u, u64(1) << 25) == 0:
            puts("VT-d: interrupt remapping enable timed out\n")
            return
        u += 1
    iommu_ir = 1
    puts("VT-d: interrupt remapping on (")
    put_u64(IOMMU_IRT_ENTRIES)
    puts("-entry table, source-validated entries, queued invalidation)\n")

# remappable-format MSI address for IRTE `idx` (handle in bits 19:5 and 2, format bit 4, no subhandle)
def iommu_ir_addr(idx: u64) -> u64:
    return 0xFEE00000 | ((idx & 0x7FFF) << 5) | (u64(1) << 4) | ((idx >> 15) << 2)

# Write IRTE idx: fixed delivery, edge, physical destination `apic`, `vector`, and source validation
# against requester ID `sid` (SVT = 1, SQ = 0: all 16 bits must match). present = 0 clears it.
# Invalidates the interrupt-entry cache. Returns 1 when written.
def iommu_irte_set(idx: u64, sid: u64, vector: u64, apic: u64, present: u64) -> u64:
    if iommu_ir == 0 or idx >= IOMMU_IRT_ENTRIES:
        return 0
    e = iommu_irt_va + idx * 16
    mmio_wr(e, 8, 0)
    if present != 0:
        mmio_wr(e + 8, 8, (sid & 0xFFFF) | (u64(1) << 18))
        mmio_wr(e, 8, 1 | ((vector & 0xFF) << 16) | (((apic & 0xFF) << 8) << 32))
    else:
        mmio_wr(e + 8, 8, 0)
    u: u64 = 0
    while u < iommu_nprog:
        iommu_qi_run(u, 0x4, 0, 0)
        u += 1
    return 1

# Route bdf's interrupts through IRTE idx (source ID = the requester ID its messages carry).
# Returns 1 when the caller should program iommu_ir_addr(idx) with data 0, else 0 (compat format).
def iommu_ir_route(idx: u64, bdf: u64, vector: u64, apic: u64) -> u64:
    if iommu_ir == 0:
        return 0
    n = iommu_aliases(bdf)
    if iommu_irte_set(idx, iommu_alias[n - 1], vector, apic, 1) == 0:
        return 0
    puts("VT-d: interrupt remapping: ")
    iommu_put_bdf(bdf)
    puts(" vector 0x")
    put_hex8(vector)
    puts(" -> IRTE ")
    put_u64(idx)
    puts(" (source ")
    iommu_put_bdf(iommu_alias[n - 1])
    puts(")\n")
    return 1

@interrupt
def on_iommu_test(frame: ptr[u64]):
    iommu_test_irqs += 1
    lapic_eoi()

# edu MSI through IRTE `idx`: raise once, wait, return how many test interrupts arrived
def edu_msi_try(bdf: u64, regs: u64, c: u64, idx: u64) -> u64:
    before = iommu_test_irqs
    pci_wr32(bdf, c + 4, iommu_ir_addr(idx))
    if ((pci_rd32(bdf, c) >> 16) & 0x80) != 0:
        pci_wr32(bdf, c + 8, 0)
        pci_wr32(bdf, c + 12, 0)
    else:
        pci_wr32(bdf, c + 8, 0)
    pci_wr32(bdf, c, pci_rd32(bdf, c) | (u64(1) << 16))
    mmio_wr(regs + 0x60, 4, 1)
    i: u64 = 0
    while i < 500000 and iommu_test_irqs == before:
        mmio_rd(regs, 4)
        i += 1
    mmio_wr(regs + 0x64, 4, 1)
    pci_wr32(bdf, c, pci_rd32(bdf, c) & ~(u64(1) << 16))
    return iommu_test_irqs - before

# Interrupt remapping observed: edu's MSI (remappable format) through an IRTE validated for edu's
# requester ID is delivered; through an IRTE validated for another requester ID, or a not-present
# IRTE, it is blocked. Fault records, if the hardware model writes them, are printed.
def iommu_ir_selftest(bdf: u64, regs: u64, rid: u64):
    if iommu_ir == 0:
        return
    c = pci_cap_find(bdf, 0x05)
    if c == 0:
        panic("VT-d: edu has no MSI capability")
    idt_set(VEC_IOMMU_TEST, u64(on_iommu_test), 0)
    iommu_fault_drain(1)
    iommu_irte_set(1, rid, VEC_IOMMU_TEST, cpu_to_apic[0], 1)
    iommu_irte_set(2, rid ^ 0x8, VEC_IOMMU_TEST, cpu_to_apic[0], 1)
    iommu_irte_set(3, 0, 0, 0, 0)
    ok = edu_msi_try(bdf, regs, c, 1)
    other = edu_msi_try(bdf, regs, c, 2)
    absent = edu_msi_try(bdf, regs, c, 3)
    nf = iommu_fault_drain(0)
    iommu_irte_set(1, 0, 0, 0, 0)
    iommu_irte_set(2, 0, 0, 0, 0)
    if ok != 1 or other != 0 or absent != 0:
        put_u64(ok)
        putc(32)
        put_u64(other)
        putc(32)
        put_u64(absent)
        putc(10)
        panic("VT-d: interrupt remapping self-test failed")
    puts("VT-d IR: edu MSI via IRTE 1 (source ")
    iommu_put_bdf(rid)
    puts(") delivered 1; via an IRTE validated for ")
    iommu_put_bdf(rid ^ 0x8)
    puts(" blocked; via a not-present IRTE blocked; ")
    put_u64(nf)
    puts(" fault record(s)\n")

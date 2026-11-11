# PCI configuration space via mechanism #1 (ports 0xCF8/0xCFC); bus 0..255 brute-force scan.
pci_count: u64
pci_vblk: u64  # bdf (bus<<8 | dev<<3 | fn) of the first virtio-blk device, +1 (0 = none)

def pci_addr(bdf: u64, off: u64) -> u32:
    return u32(0x80000000 | (bdf << 8) | (off & 0xFC))

def pci_rd32(bdf: u64, off: u64) -> u64:
    pio_wr(0xCF8, 4, pci_addr(bdf, off))
    return pio_rd(0xCFC, 4)

def pci_wr32(bdf: u64, off: u64, v: u64):
    pio_wr(0xCF8, 4, pci_addr(bdf, off))
    pio_wr(0xCFC, 4, v)

def pci_busmaster(bdf: u64, bits: u64):
# set command-register bits for a device the kernel drives itself; bit 2 (bus master) first gives the
# device a pass-through IOMMU context (iommu_claim), since under VT-d every other device is denied
    if (bits & 4) != 0:
        iommu_claim(bdf)
    pci_wr32(bdf, 4, pci_rd32(bdf, 4) | bits)

def pci_scan():
    pio_add(0xCF8, 8)
    pci_n = 0
    pci_count = 0
    pci_vblk = 0
    bus: u64 = 0
    while bus < 256:
        dev: u64 = 0
        while dev < 32:
            fn: u64 = 0
            nfn: u64 = 1
            while fn < nfn:
                bdf = (bus << 8) | (dev << 3) | fn
                id = pci_rd32(bdf, 0)
                if (id & 0xFFFF) != 0xFFFF:
                    if fn == 0 and ((pci_rd32(bdf, 0x0C) >> 16) & 0x80) != 0:
                        nfn = 8
                    cls = pci_rd32(bdf, 8)
                    pci_count += 1
                    if pci_n < PCI_MAX:
                        pci_bdf[pci_n] = bdf
                        pci_id[pci_n] = id
                        pci_cls[pci_n] = cls >> 8
                        pci_n += 1
                    puts("PCI ")
                    put_hex8(bus)
                    putc(58)
                    put_hex8(dev)
                    putc(46)
                    put_u64(fn)
                    puts(" ")
                    put_hex16(id & 0xFFFF)
                    putc(58)
                    put_hex16(id >> 16)
                    puts(" class ")
                    put_hex8(cls >> 24)
                    putc(46)
                    put_hex8((cls >> 16) & 0xFF)
                    if (id & 0xFFFF) == 0x1AF4 and ((id >> 16) == 0x1001 or (id >> 16) == 0x1042):
                        puts("  virtio-blk")
                        if (id >> 16) == 0x1001:
                            puts(" (transitional)")
                        else:
                            puts(" (modern)")
                        if pci_vblk == 0:
                            pci_vblk = bdf + 1
                    putc(10)
                fn += 1
            dev += 1
        bus += 1
    puts("PCI: ")
    put_u64(pci_count)
    puts(" functions\n")

# Phase 8 driver support: every function found by pci_scan, so drivers can look devices up by class.
PCI_MAX = 64
pci_bdf: u64[64]
pci_id: u64[64]
pci_cls: u64[64]  # class << 16 | subclass << 8 | prog-if
pci_n: u64

def pci_find(cls: u64, nth: u64) -> u64:
# bdf + 1 of the nth function with this class code (0 = none)
    i: u64 = 0
    k: u64 = 0
    while i < pci_n:
        if pci_cls[i] == cls:
            if k == nth:
                return pci_bdf[i] + 1
            k += 1
        i += 1
    return 0

def pci_find_id(id: u64) -> u64:
    i: u64 = 0
    while i < pci_n:
        if pci_id[i] == id:
            return pci_bdf[i] + 1
        i += 1
    return 0

def pci_bar(bdf: u64, i: u64, size_out: ptr[u64]) -> u64:
# physical base of memory BAR i (64-bit aware) and its size; 0 for an I/O or empty BAR.
# Sizing writes all-ones with memory decoding off, then restores the BAR and the command register.
    off = 0x10 + i * 4
    b = pci_rd32(bdf, off)
    if (b & 1) != 0:
        return 0
    is64 = (b & 6) == 4
    cmd = pci_rd32(bdf, 4)
    pci_wr32(bdf, 4, cmd & ~u64(3))
    pci_wr32(bdf, off, 0xFFFFFFFF)
    lo = pci_rd32(bdf, off) & 0xFFFFFFF0
    pci_wr32(bdf, off, b)
    hi: u64 = 0xFFFFFFFF
    bh: u64 = 0
    if is64:
        bh = pci_rd32(bdf, off + 4)
        pci_wr32(bdf, off + 4, 0xFFFFFFFF)
        hi = pci_rd32(bdf, off + 4)
        pci_wr32(bdf, off + 4, bh)
    pci_wr32(bdf, 4, cmd)
    m = (hi << 32) | lo
    size_out[0] = ~m + 1
    return (b & 0xFFFFFFF0) | (bh << 32)

def pci_map_bar(bdf: u64, i: u64, maxlen: u64) -> u64:
# map memory BAR i uncached (at most maxlen bytes) and enable memory decoding + bus mastering
    sz: u64 = 0
    pa = pci_bar(bdf, i, addr(sz))
    if pa == 0 or sz == 0:
        return 0
    if sz > maxlen:
        sz = maxlen
    pci_busmaster(bdf, 6)
    return mmio_map(pa, sz)

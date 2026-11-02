# PCI configuration space via mechanism #1 (ports 0xCF8/0xCFC); bus 0..255 brute-force scan.
pci_count: u64
pci_vblk: u64      # bus<<16 | dev<<11 | fn<<8 of the first virtio-blk device, +1 (0 = none)

def pci_addr(bdf: u64, off: u64) -> u32:
    return u32(0x80000000 | (bdf << 8) | (off & 0xFC))

def pci_rd32(bdf: u64, off: u64) -> u64:
    outl(0xCF8, pci_addr(bdf, off))
    return u64(inl(0xCFC))

def pci_wr32(bdf: u64, off: u64, v: u64):
    outl(0xCF8, pci_addr(bdf, off))
    outl(0xCFC, u32(v))

def pci_scan():
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
                    if id == 0x10011AF4:
                        puts("  virtio-blk (transitional)")
                        if pci_vblk == 0:
                            pci_vblk = bdf + 1
                    putc(10)
                fn += 1
            dev += 1
        bus += 1
    puts("PCI: ")
    put_u64(pci_count)
    puts(" functions\n")

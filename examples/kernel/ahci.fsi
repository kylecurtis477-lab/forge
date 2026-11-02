# Phase 8: AHCI (SATA), read-only. One command slot per port, polled. The only commands the driver
# can build are IDENTIFY DEVICE (0xEC) and READ DMA EXT (0x25); there is no write path.
ah_regs: u64
ah_port: u64  # port register block of the first SATA disk
ah_mem: u64  # one page: command list 0x000, received FIS 0x400, command table 0x800
ah_mem_pa: u64
ah_buf: u64
ah_buf_pa: u64
ah_sectors: u64
ah_reads: u64

def ah_wait_clear(r: u64, bits: u64) -> u64:
    t: u64 = 0
    while (mmio_rd(r, 4) & bits) != 0:
        t += 1
        if t > 500000:
            return 0
        tsc_delay_us(2)
    return 1

def ah_cmd(cmd: u64, lba: u64, count: u64, bytes: u64) -> u64:
# issue an ATA command in slot 0 with one PRD covering ah_buf; returns 1 on success
    p = ah_port
    mmio_wr(p + 0x10, 4, 0xFFFFFFFF)  # clear PxIS
    mmio_wr(ah_mem, 4, 5 | (1 << 16))  # 5-dword FIS, read (W=0), 1 PRD
    mmio_wr(ah_mem + 4, 4, 0)
    mmio_wr(ah_mem + 8, 8, ah_mem_pa + 0x800)
    t = ah_mem + 0x800
    i: u64 = 0
    while i < 0x100:
        mmio_wr(t + i, 8, 0)
        i += 8
    mmio_wr(t, 4, 0x27 | (0x80 << 8) | (cmd << 16))
    mmio_wr(t + 4, 4, (lba & 0xFFFFFF) | (0x40 << 24))
    mmio_wr(t + 8, 4, (lba >> 24) & 0xFFFFFF)
    mmio_wr(t + 12, 4, count & 0xFFFF)
    mmio_wr(t + 0x80, 8, ah_buf_pa)
    mmio_wr(t + 0x8C, 4, bytes - 1)
    mfence()
    if ah_wait_clear(p + 0x20, 0x88) == 0:  # TFD BSY/DRQ
        return 0
    mmio_wr(p + 0x38, 4, 1)
    k: u64 = 0
    while (mmio_rd(p + 0x38, 4) & 1) != 0:
        if (mmio_rd(p + 0x10, 4) & 0x40000000) != 0:  # task file error
            return 0
        k += 1
        if k > 1000000:
            return 0
        tsc_delay_us(2)
    if (mmio_rd(p + 0x20, 4) & 1) != 0:
        return 0
    return 1

def ahci_init() -> u64:
    f = pci_find(0x010601, 0)
    if f == 0:
        puts("AHCI: not present\n")
        return 0
    ah_regs = pci_map_bar(f - 1, 5, 0x1100)
    if ah_regs == 0:
        puts("AHCI: ABAR not mappable\n")
        return 0
    mmio_wr(ah_regs + 4, 4, mmio_rd(ah_regs + 4, 4) | 0x80000000)  # GHC.AE
    mmio_wr(ah_regs + 4, 4, mmio_rd(ah_regs + 4, 4) & ~u64(2))  # no interrupts
    pi = mmio_rd(ah_regs + 0x0C, 4)
    vs = mmio_rd(ah_regs + 0x10, 4)
    np: u64 = 0
    n: u64 = 0
    while n < 32:
        if (pi >> n) & 1:
            np += 1
            p = ah_regs + 0x100 + n * 0x80
            if (mmio_rd(p + 0x28, 4) & 0xF) == 3 and mmio_rd(p + 0x24, 4) == 0x101 and ah_port == 0:
                ah_port = p
                puts("AHCI: SATA disk on port ")
                put_u64(n)
                putc(10)
        n += 1
    puts("AHCI: version ")
    put_u64(vs >> 16)
    putc(46)
    put_u64((vs >> 8) & 0xFF)
    puts(", ")
    put_u64(np)
    puts(" ports implemented\n")
    if ah_port == 0:
        puts("AHCI: no SATA disk\n")
        return 0
    p = ah_port
    # stop the port before pointing it at our memory
    mmio_wr(p + 0x18, 4, mmio_rd(p + 0x18, 4) & ~u64(1))
    if ah_wait_clear(p + 0x18, 0x8000) == 0:
        return 0
    mmio_wr(p + 0x18, 4, mmio_rd(p + 0x18, 4) & ~u64(0x10))
    if ah_wait_clear(p + 0x18, 0x4000) == 0:
        return 0
    ah_mem = dma_alloc(1, addr(ah_mem_pa))
    ah_buf = dma_alloc(1, addr(ah_buf_pa))
    if ah_mem == 0 or ah_buf == 0:
        panic("AHCI: out of DMA memory")
    mmio_wr(p, 8, ah_mem_pa)
    mmio_wr(p + 8, 8, ah_mem_pa + 0x400)
    mmio_wr(p + 0x30, 4, 0xFFFFFFFF)  # clear SERR
    mmio_wr(p + 0x14, 4, 0)  # no port interrupts
    mmio_wr(p + 0x18, 4, mmio_rd(p + 0x18, 4) | 0x10)  # FRE
    mmio_wr(p + 0x18, 4, mmio_rd(p + 0x18, 4) | 1)  # ST
    if ah_cmd(0xEC, 0, 0, 512) == 0:
        puts("AHCI: IDENTIFY failed\n")
        return 0
    ah_sectors = mmio_rd(ah_buf + 200, 8)
    puts("AHCI: model '")
    i: u64 = 0
    e: u64 = 40
    while e > 0 and mmio_rd(ah_buf + 54 + ((e - 1) ^ 1), 1) == 32:
        e -= 1
    while i < e:
        c = mmio_rd(ah_buf + 54 + (i ^ 1), 1)
        if c < 32 or c > 126:
            c = 63
        putc(u8(c))
        i += 1
    puts("', ")
    put_u64(ah_sectors)
    puts(" sectors (LBA48)\n")
    return 1

def ahci_read(lba: u64) -> u64:
# 8 sectors (4 KiB) at lba; returns the DMA buffer, 0 on failure
    if ah_port == 0 or lba + 8 > ah_sectors:
        return 0
    if ah_cmd(0x25, lba, 8, 4096) == 0:
        return 0
    ah_reads += 1
    return ah_buf

# Phase 8: AHCI (SATA), read-only. One command slot per port. The only commands the driver can build are
# IDENTIFY DEVICE (0xEC) and READ DMA EXT (0x25); there is no write path. After IDENTIFY (polled), ah_irq_init
# turns on the controller's MSI (vector 0x54 -> cpu0, D2H Register FIS + task-file-error interrupts): a reader
# with interrupts on and the scheduler running sleeps on ah_wq until the slot's CI bit clears (the handler
# acknowledges PxIS/IS and wakes it, both under rq_lock, so no wakeup is lost); early boot, interrupts-off
# callers and interrupt remapping keep polling.
ah_regs: u64
ah_port: u64  # port register block of the first SATA disk
ah_mem: u64  # one page: command list 0x000, received FIS 0x400, command table 0x800
ah_mem_pa: u64
ah_buf: u64
ah_buf_pa: u64
ah_sectors: u64
ah_reads: u64
ah_bdf: u64
VEC_AHCI = 0x54
ah_msi: u64  # 1: port interrupts arrive by MSI on VEC_AHCI
ah_irqs: u64
ah_sleeps: u64
ah_err: u64  # set by the handler when PxIS reported a task file error
ah_wq: WaitQ

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
    ah_err = 0
    mmio_wr(p + 0x38, 4, 1)
    if ah_msi and sched_on and (read_rflags() & 0x200) != 0:
    # sleeping path: the handler wakes ah_wq under rq_lock, and CI is checked under it
        f = irq_save()
        spin_lock(addr(rq_lock))
        while (mmio_rd(p + 0x38, 4) & 1) != 0 and ah_err == 0:
            ah_sleeps += 1
            wq_block_l(addr(ah_wq))
        spin_unlock(addr(rq_lock))
        irq_restore(f)
    k: u64 = 0
    while (mmio_rd(p + 0x38, 4) & 1) != 0 or ah_err != 0:
        if ah_err != 0 or (mmio_rd(p + 0x10, 4) & 0x40000000) != 0:  # task file error
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
    ah_bdf = f - 1
    ah_msi = 0
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
    ah_irq_init()
    return 1

@interrupt
def on_ahci(frame: ptr[u64]):
    spin_lock(addr(rq_lock))
    ah_irqs += 1
    if ah_port != 0:
        s = mmio_rd(ah_port + 0x10, 4)
        if (s & 0x40000000) != 0:
            ah_err = 1
        mmio_wr(ah_port + 0x10, 4, s)  # PxIS (RW1C)
        mmio_wr(ah_regs + 8, 4, mmio_rd(ah_regs + 8, 4))  # IS (RW1C)
    wq_wake_all_l(addr(ah_wq))
    spin_unlock(addr(rq_lock))
    lapic_eoi()

def ah_irq_init():
# PCI MSI (capability 0x05), single message -> VEC_AHCI on cpu0; port IE: DHRS (bit 0) + TFES (bit 30)
    if iommu_ir != 0:
        puts("AHCI: interrupt remapping on and no IRTE for AHCI yet: commands stay polled\n")
        return
    c = pci_rd32(ah_bdf, 0x34) & 0xFC
    g: u64 = 0
    while c != 0 and g < 48:
        h = pci_rd32(ah_bdf, c)
        if (h & 0xFF) == 0x05:
            break
        c = (h >> 8) & 0xFC
        g += 1
    if c == 0 or g == 48:
        puts("AHCI: no MSI, commands stay polled\n")
        return
    h = pci_rd32(ah_bdf, c)
    idt_set(VEC_AHCI, u64(on_ahci), 0)
    pci_wr32(ah_bdf, c + 4, 0xFEE00000 | (cpu_to_apic[0] << 12))
    d = c + 8
    if ((h >> 16) & 0x80) != 0:  # 64-bit address
        pci_wr32(ah_bdf, c + 8, 0)
        d = c + 12
    pci_wr32(ah_bdf, d, VEC_AHCI)
    # MSI enable, one message (MME = 0)
    pci_wr32(ah_bdf, c, (h & ~u64(0x700000)) | 0x10000)
    p = ah_port
    mmio_wr(p + 0x10, 4, 0xFFFFFFFF)
    mmio_wr(ah_regs + 8, 4, 0xFFFFFFFF)
    mmio_wr(p + 0x14, 4, 0x40000001)  # PxIE: TFES | DHRS
    mmio_wr(ah_regs + 4, 4, mmio_rd(ah_regs + 4, 4) | 2)  # GHC.IE
    ah_msi = 1
    puts("AHCI: command completions on MSI vector 0x54 -> cpu0, sleeping reads\n")

def ah_irq_report():
    puts("AHCI: ")
    put_u64(ah_reads)
    puts(" read(s), ")
    put_u64(ah_irqs)
    puts(" completion interrupt(s), ")
    put_u64(ah_sleeps)
    puts(" sleep(s)\n")

def ahci_read(lba: u64) -> u64:
# 8 sectors (4 KiB) at lba; returns the DMA buffer, 0 on failure
    if ah_port == 0 or lba + 8 > ah_sectors:
        return 0
    if ah_cmd(0x25, lba, 8, 4096) == 0:
        return 0
    ah_reads += 1
    return ah_buf

# Phase 8: hardware driver probe + boot self-test, and quiesce before kexec (no device may keep
# DMA running into the old kernel's frames).
def hw_fnv(a: u64, n: u64) -> u64:
    h: u64 = 0xcbf29ce484222325
    i: u64 = 0
    while i < n:
        h = (h ^ mmio_rd(a + i, 1)) * 0x100000001b3
        i += 1
    return h

def hw_report_read(name: ptr[u8], b: u64):
    puts(name)
    if b == 0:
        puts(": read of LBA 0 failed\n")
        return
    puts(": read LBA 0 (4096 bytes), fnv1a64=")
    put_hexn(hw_fnv(b, 4096), 16)
    putc(10)

def hw_test():
    net_init()
    if nvme_init():
        hw_report_read("NVMe", nvme_read(0))
        nv_irq_test()
    if ahci_init():
        hw_report_read("AHCI", ahci_read(0))
        ah_irq_test()
    if xhci_init():
        xhci_scan()
    if e1000_init():
        e1000_arp()
    if virtio_net_init():
        virtio_net_arp()
    puts("device windows: ")
    put_u64(mmio_n)
    puts(" MMIO/DMA, ")
    put_u64(pio_n)
    puts(" port ranges; out-of-window accesses ")
    put_u64(mmio_denied)
    putc(10)

def hw_quiesce():
    if e1_regs != 0:
        e1_wr(0x100, 0)
        e1_wr(0x400, 0)
        e1_wr(0, e1_rd(0) | 0x04000000)
    if vn_io != 0:
        pio_wr(vn_io + VIO_STATUS, 1, 0)
    if xh_op != 0:
        mmio_wr(xh_op, 4, 0)
        xh_wait(xh_op + 4, 1, 1)
        mmio_wr(xh_op, 4, 2)
        xh_wait(xh_op, 2, 0)
    if nv_regs != 0:
        mmio_wr(nv_regs + 0x14, 4, 0)
        nv_wait_rdy(0)
    if ah_port != 0:
        mmio_wr(ah_regs + 4, 4, mmio_rd(ah_regs + 4, 4) & ~u64(2))  # GHC.IE off before kexec
        mmio_wr(ah_port + 0x14, 4, 0)
        mmio_wr(ah_port + 0x18, 4, mmio_rd(ah_port + 0x18, 4) & ~u64(0x11))
        ah_wait_clear(ah_port + 0x18, 0xC000)

def nv_irq_test():
# 8 more reads (LBA 8..64 in 4 KiB steps); with MSI-X each completion must raise an interrupt
    i0 = nv_irqs
    ok: u64 = 0
    k: u64 = 1
    while k <= 8:
        if nvme_read(k * (4096 / nv_lba_sz)) != 0:
            ok += 1
        k += 1
    nv_irq_report()
    if nv_msix and ok == 8 and nv_irqs - i0 >= 8:
        puts("NVMe: interrupt-driven reads OK (8 of 8, ")
        put_u64(nv_irqs - i0)
        puts(" interrupts)\n")

def ah_irq_test():
# 8 more reads (LBA 8..64 in 4 KiB steps); with MSI each command completion must raise an interrupt
    i0 = ah_irqs
    ok: u64 = 0
    k: u64 = 1
    while k <= 8:
        if ahci_read(k * 8) != 0:
            ok += 1
        k += 1
    ah_irq_report()
    if ah_msi and ok == 8 and ah_irqs - i0 >= 8:
        puts("AHCI: interrupt-driven reads OK (8 of 8, ")
        put_u64(ah_irqs - i0)
        puts(" interrupts)\n")

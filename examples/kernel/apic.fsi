# Legacy PIC off, local APIC (timer calibrated against PIT channel 2), IOAPIC routing of ISA IRQs.
VEC_TIMER = 0x40
VEC_PIT = 0x41
VEC_SPURIOUS = 0xFF

lapic: u64
ioapic: u64
lapic_ticks: u64
pit_irqs: u64
lapic_per_10ms: u64
tsc_per_ms: u64
sched_on: u64

@unsafe
def lapic_rd(r: u64) -> u32:
    return vload32(lapic + r)

@unsafe
def lapic_wr(r: u64, v: u32):
    vstore32(lapic + r, v)

apic_to_cpu: u8[256]

def lapic_cpu() -> u64:
    if lapic == 0:
        return 0
    return u64(apic_to_cpu[lapic_rd(0x20) >> 24])

def tsc_delay_us(us: u64):
    t = rdtsc() + tsc_per_ms * us / 1000
    while rdtsc() < t:
        cpu_relax()

def lapic_eoi():
    lapic_wr(0xB0, 0)

@unsafe
def ioapic_rd(r: u32) -> u32:
    vstore32(ioapic, r)
    return vload32(ioapic + 0x10)

@unsafe
def ioapic_wr(r: u32, v: u32):
    vstore32(ioapic, r)
    vstore32(ioapic + 0x10, v)

@unsafe
def pic_disable():
    outb(0x20, 0x11)
    outb(0xA0, 0x11)
    outb(0x21, 0x20)
    outb(0xA1, 0x28)
    outb(0x21, 4)
    outb(0xA1, 2)
    outb(0x21, 1)
    outb(0xA1, 1)
    outb(0x21, 0xFF)
    outb(0xA1, 0xFF)

@interrupt
def on_lapic_timer(frame: ptr[u64]):
    if lapic_cpu() == 0:
        atomic_fetch_add(addr(lapic_ticks), 1, acq_rel)
    lapic_eoi()
    if sched_on:
        spin_lock(addr(rq_lock))
        if lapic_cpu() == 0 and sleepq.head != 0:
            sleep_tick_l()
        schedule_l(0)

@interrupt
def on_pit(frame: ptr[u64]):
    pit_irqs += 1
    lapic_eoi()

@interrupt
def on_spurious(frame: ptr[u64]):
    cpu_relax()

@unsafe
def pit2_wait_10ms_start():
# PIT channel 2, mode 0, 11932 ticks of 1.193182 MHz = 10 ms; OUT2 is visible in port 0x61 bit 5
    outb(0x61, (inb(0x61) & 0xFD) | 1)
    outb(0x43, 0xB0)
    outb(0x42, 0x9C)
    outb(0x42, 0x2E)
    v = inb(0x61) & 0xFE
    outb(0x61, v)
    outb(0x61, v | 1)

@unsafe
def lapic_init():
    lapic = p2v(lapic_phys)
    # make sure the APIC is globally enabled (IA32_APIC_BASE bit 11)
    wrmsr(0x1B, rdmsr(0x1B) | 0x800)
    lapic_wr(0x80, 0)
    lapic_wr(0xF0, 0x100 | VEC_SPURIOUS)
    puts("LAPIC id=")
    put_u64(u64(lapic_rd(0x20) >> 24))
    puts(" version=")
    put_hex(u64(lapic_rd(0x30) & 0xFF))
    putc(10)
    # calibrate: count LAPIC ticks (divide by 16) during 10 ms of PIT channel 2
    lapic_wr(0x3E0, 3)
    lapic_wr(0x320, 0x10000 | VEC_TIMER)
    pit2_wait_10ms_start()
    lapic_wr(0x380, 0xFFFFFFFF)
    t0 = rdtsc()
    guard = 0
    while (inb(0x61) & 0x20) == 0 and guard < 100000000:
        guard += 1
    lapic_per_10ms = u64(0xFFFFFFFF - lapic_rd(0x390))
    tsc_10ms = rdtsc() - t0
    tsc_per_ms = tsc_10ms / 10
    lapic_wr(0x380, 0)
    if lapic_per_10ms == 0:
        lapic_per_10ms = 100000
    puts("LAPIC timer: ")
    put_u64(lapic_per_10ms)
    puts(" ticks/10ms (div 16), TSC ~")
    put_u64(tsc_10ms / 10000)
    puts(" MHz\n")
    # periodic 100 Hz
    lapic_wr(0x320, 0x20000 | VEC_TIMER)
    lapic_wr(0x380, u32(lapic_per_10ms))

def ioapic_route(irq: u64, vec: u64):
    gsi = u64(irq_gsi[irq]) - ioapic_gsi_base
    fl = irq_flags[irq]
    lo = vec
    if (fl & 3) == 3:
        lo = lo | 0x2000
    if ((fl >> 2) & 3) == 3:
        lo = lo | 0x8000
    hi = u64(lapic_rd(0x20) >> 24) << 24
    ioapic_wr(u32(0x11 + gsi * 2), u32(hi))
    ioapic_wr(u32(0x10 + gsi * 2), u32(lo))

def ioapic_mask(irq: u64):
    gsi = u64(irq_gsi[irq]) - ioapic_gsi_base
    ioapic_wr(u32(0x10 + gsi * 2), 0x10000)

def ioapic_mask_pin(pin: u64):
# mask one IOAPIC redirection entry by pin number (not an ISA IRQ: irq_gsi only covers IRQs 0-15)
    ioapic_wr(u32(0x10 + pin * 2), 0x10000)

def ioapic_init():
    ioapic = p2v(ioapic_phys)
    ver = ioapic_rd(1)
    n = ((ver >> 16) & 0xFF) + 1
    puts("IOAPIC id=")
    put_u64(u64(ioapic_rd(0) >> 24) & 15)
    puts(" pins=")
    put_u64(u64(n))
    putc(10)
    i: u32 = 0
    while i < n:
        ioapic_wr(0x10 + i * 2, 0x10000)
        i += 1

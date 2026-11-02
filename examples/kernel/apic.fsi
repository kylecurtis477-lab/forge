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

x2apic_on: u64  # Phase 8: x2APIC mode, registers are MSRs 0x800 + offset/16
x2apic_ipis: u64
x2_icr_hi: u64[256]
tsc_deadline_on: u64  # Phase 8: LAPIC timer in TSC-deadline mode (CPUID.1:ECX[24]); 0x380 writes become deadlines
tsc_10ms: u64
deadline_arms: u64
hpet_fs: u64  # HPET counter period in femtoseconds (0 = no HPET)
hpet_tsc_10ms: u64
pit_tsc_10ms: u64

# xAPIC register offsets are kept as the interface; in x2APIC mode they map to MSRs, and the ID register
# is returned shifted left 24 like xAPIC so every caller that does >> 24 still works (ids < 256 here)
@unsafe
def lapic_rd(r: u64) -> u32:
    if x2apic_on:
        x = rdmsr(0x800 + (r >> 4))
        if r == 0x20:
            x = x << 24
        return u32(x & 0xFFFFFFFF)
    return vload32(lapic + r)

@unsafe
def lapic_wr(r: u64, v: u32):
    if r == 0x380 and tsc_deadline_on and v != 0:
    # TSC-deadline mode ignores the initial count: arm IA32_TSC_DEADLINE v LAPIC ticks from now instead
        deadline_arms += 1
        wrmsr(0x6E0, rdtsc() + u64(v) * tsc_10ms / lapic_per_10ms)
        return
    if x2apic_on:
    # x2APIC has one 64-bit ICR (MSR 0x830, destination in bits 63:32); the xAPIC ICR-high write (0x310,
    # destination in bits 31:24) is kept per CPU and merged into the ICR-low write, so callers are unchanged
        hi = 0
        if r == 0x310 or r == 0x300:
            me = rdmsr(0x802) & 0xFF
            if r == 0x310:
                x2_icr_hi[me] = u64(v) >> 24
                return
            hi = x2_icr_hi[me]
            x2apic_ipis += 1
        wrmsr(0x800 + (r >> 4), u64(v) | (hi << 32))
        return
    vstore32(lapic + r, v)

apic_to_cpu: u8[256]
pic_seq: u64[10] = [0x2011, 0xA011, 0x2120, 0xA128, 0x2104, 0xA102, 0x2101, 0xA101, 0x21FF, 0xA1FF]

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

def ioapic_rd(r: u32) -> u32:
    mmio_wr(ioapic, 4, r)
    return mmio_rd(ioapic + 0x10, 4)

def ioapic_wr(r: u32, v: u32):
    mmio_wr(ioapic, 4, r)
    mmio_wr(ioapic + 0x10, 4, v)

@unsafe
def pic_disable():
# ICW1..ICW4 to master (0x20/0x21) and slave (0xA0/0xA1), vectors 0x20/0x28, then mask everything;
# one (port << 8 | value) pair per step
    i = 0
    while i < 10:
        outb(pic_seq[i] >> 8, pic_seq[i] & 0xFF)
        i += 1

@interrupt
def on_lapic_timer(frame: ptr[u64]):
    if lapic_cpu() == 0:
        atomic_fetch_add(addr(lapic_ticks), 1, acq_rel)
    lapic_eoi()
    if tsc_deadline_on:
        lapic_wr(0x380, u32(lapic_per_10ms))
    if sched_on:
        spin_lock(addr(rq_lock))
        if lapic_cpu() == 0 and sleepq.head != 0:
            sleep_tick_l()
        schedule_l(0)

def lapic_timer_start():
    if tsc_deadline_on:
        lapic_wr(0x320, 0x40000 | VEC_TIMER)
    else:
        lapic_wr(0x320, 0x20000 | VEC_TIMER)
    lapic_wr(0x380, u32(lapic_per_10ms))

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
    # make sure the APIC is globally enabled (IA32_APIC_BASE bit 11), in x2APIC mode if available
    cpuid4(1, 0)
    x2apic_on = (u64(cpuid_out[2]) >> 21) & 1
    wrmsr(0x1B, rdmsr(0x1B) | 0x800 | (x2apic_on << 10))  # x2APIC mode is bit 10
    lapic_wr(0x80, 0)
    lapic_wr(0xF0, 0x100 | VEC_SPURIOUS)
    if x2apic_on:
        puts("x2APIC mode, ")
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
    if hpet_phys != 0:
    # cross-check the TSC against the HPET main counter over 10 ms (period in fs is in GCAP_ID bits 63:32)
        hb = p2v(hpet_phys)
        cap = vload64(hb)
        hpet_fs = cap >> 32
        if hpet_fs == 0 or hpet_fs > 100000000:
            hpet_fs = 0
        else:
            vstore64(hb + 0x10, vload64(hb + 0x10) | 1)
            mask: u64 = 0xFFFFFFFFFFFFFFFF
            if (cap & 0x2000) == 0:
                mask = 0xFFFFFFFF
            want = 10000000000000 / hpet_fs
            c0 = vload64(hb + 0xF0)
            t1 = rdtsc()
            c = c0
            guard = 0
            while ((c - c0) & mask) < want and guard < 100000000:
                c = vload64(hb + 0xF0)
                guard += 1
            hpet_tsc_10ms = rdtsc() - t1
            pit_tsc_10ms = tsc_10ms
            if guard < 100000000 and hpet_tsc_10ms != 0 and tsc_10ms != 0:
            # HPET is the better reference: rescale the LAPIC count measured over the PIT window
                lapic_per_10ms = lapic_per_10ms * hpet_tsc_10ms / tsc_10ms
                tsc_10ms = hpet_tsc_10ms
    tsc_per_ms = tsc_10ms / 10
    lapic_wr(0x380, 0)
    if lapic_per_10ms == 0:
        lapic_per_10ms = 100000
    puts("LAPIC timer: ")
    put_u64(lapic_per_10ms)
    puts(" ticks/10ms (div 16), TSC ~")
    put_u64(tsc_10ms / 10000)
    puts(" MHz\n")
    if hpet_fs != 0:
        puts("HPET: period ")
        put_u64(hpet_fs)
        puts(" fs, TSC ~")
        put_u64(hpet_tsc_10ms / 10000)
        puts(" MHz by HPET\n")
    # 100 Hz: TSC-deadline one-shots re-armed from the handler when offered, else LAPIC periodic mode
    cpuid4(1, 0)
    tsc_deadline_on = (u64(cpuid_out[2]) >> 24) & 1
    lapic_timer_start()

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
    ioapic = mmio_add(p2v(ioapic_phys), 0x20)
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

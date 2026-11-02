# ACPI: RSDP -> XSDT/RSDT -> MADT (LAPIC address, IOAPICs, interrupt source overrides, CPU count).
SIG_APIC = 0x43495041
SIG_HPET = 0x54455048

lapic_phys: u64
hpet_phys: u64  # Phase 8: HPET block from the ACPI HPET table (0 = none)
ioapic_phys: u64
ioapic_gsi_base: u64
ncpus: u64
madt_apic: u64[8]
irq_gsi: u32[16]
irq_flags: u16[16]

@unsafe
def acpi_sum(p: u64, n: u64) -> u64:
    s: u64 = 0
    i = 0
    while i < n:
        s += vload8(p + i)
        i += 1
    return s & 0xFF

@unsafe
def rsdp_scan() -> u64:
# legacy BIOS area fallback when the boot loader gave us no RSDP
    p = p2v(0xE0000)
    while p < p2v(0x100000):
        if vload64(p) == 0x2052545020445352 and acpi_sum(p, 20) == 0:
            return p - p2v(0)
        p += 16
    return 0

@unsafe
def madt_parse(m: u64):
    lapic_phys = vload32(m + 36)
    end = m + vload32(m + 4)
    p = m + 44
    while p < end:
        t = vload8(p)
        n = vload8(p + 1)
        if n == 0:
            break
        if t == 0 and (vload32(p + 4) & 3) != 0:
            if ncpus < 8:
                madt_apic[ncpus] = vload8(p + 3)
            ncpus += 1
        if t == 1 and ioapic_phys == 0:
            ioapic_phys = vload32(p + 4)
            ioapic_gsi_base = vload32(p + 8)
        if t == 2:
            src = vload8(p + 3)
            if src < 16:
                irq_gsi[src] = vload32(p + 4)
                irq_flags[src] = vload16(p + 8)
        if t == 5:
            lapic_phys = vload64(p + 4)
        p += n

rsdp_save: u8[64]  # copy of the RSDP for a kexec successor (the original may be reclaimed)

@unsafe
def acpi_init(rsdp_phys: u64) -> u64:
    i = 0
    while i < 16:
        irq_gsi[i] = u32(i)
        irq_flags[i] = 0
        i += 1
    if rsdp_phys == 0:
        rsdp_phys = rsdp_scan()
    if rsdp_phys == 0:
        puts("ACPI: no RSDP\n")
        return 0
    r = p2v(rsdp_phys)
    if vload64(r) != 0x2052545020445352 or acpi_sum(r, 20) != 0:
        puts("ACPI: bad RSDP\n")
        return 0
    memcpy(u64(addr(rsdp_save[0])), r, 36)
    rev = vload8(r + 15)
    sdt = u64(vload32(r + 16))
    esz = 4
    if rev >= 2 and vload64(r + 24) != 0:
        sdt = vload64(r + 24)
        esz = 8
    s = p2v(sdt)
    if acpi_sum(s, vload32(s + 4)) != 0:
        puts("ACPI: bad root table checksum\n")
        return 0
    puts("ACPI rev ")
    put_u64(rev)
    if esz == 8:
        puts(" XSDT:")
    else:
        puts(" RSDT:")
    n = (vload32(s + 4) - 36) / esz
    found = 0
    i = 0
    while i < n:
        if esz == 8:
            tp = vload64(s + 36 + i * 8)
        else:
            tp = u64(vload32(s + 36 + i * 4))
        t = p2v(tp)
        sig = vload32(t)
        putc(32)
        put_sig(sig)
        if sig == SIG_APIC and acpi_sum(t, vload32(t + 4)) == 0:
            madt_parse(t)
            found = 1
        if sig == SIG_HPET and acpi_sum(t, vload32(t + 4)) == 0:
            hpet_phys = vload64(t + 44)  # base address (Generic Address Structure at offset 40, address at +4)
        i += 1
    putc(10)
    if found:
        puts("MADT: cpus=")
        put_u64(ncpus)
        puts(" lapic=")
        put_hex(lapic_phys)
        puts(" ioapic=")
        put_hex(ioapic_phys)
        puts(" irq0->gsi ")
        put_u64(irq_gsi[0])
        putc(10)
    return found

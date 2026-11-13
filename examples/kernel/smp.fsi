# SMP bring-up: INIT-SIPI-SIPI to every enabled LAPIC in the MADT. The real-mode trampoline is copied to a free
# page below 1 MiB (identity-mapped only while APs start), switches to long mode on the kernel's own page
# tables and jumps to ap_main on a per-CPU guarded stack.
PERCPU_STACKS = 0xFFFFFFFFC0200000

asm(
    ".section .rodata",
    ".p2align 12",
    ".globl ap_tramp",
    "ap_tramp:",
    ".code16",
    "cli", "cld",
    "mov %cs, %ax", "mov %ax, %ds",
    "mov %cs, %bx", "movzwl %bx, %ebx", "shl $4, %ebx",
    # patch the far-jump targets and the GDT base with the trampoline's physical address (in ebx)
    "lea (ap_gdt - ap_tramp)(%ebx), %eax", "mov %eax, (ap_gdtr - ap_tramp + 2)",
    "lea (ap_pm - ap_tramp)(%ebx), %eax", "mov %eax, (ap_jpm - ap_tramp)",
    "lea (ap_lm - ap_tramp)(%ebx), %eax", "mov %eax, (ap_jlm - ap_tramp)",
    "lgdtl (ap_gdtr - ap_tramp)",
    "mov %cr0, %eax", "or $1, %eax", "mov %eax, %cr0",
    "ljmpl *(ap_jpm - ap_tramp)",
    ".code32",
    "ap_pm:",
    "mov $0x10, %ax", "mov %ax, %ds", "mov %ax, %es", "mov %ax, %ss",
    "mov $0xA0, %eax", "mov %eax, %cr4",
    "mov (ap_cr3 - ap_tramp)(%ebx), %eax", "mov %eax, %cr3",
    "mov $0xC0000080, %ecx", "rdmsr", "or $0x901, %eax", "wrmsr",
    "mov %cr0, %eax", "or $0x80010000, %eax", "mov %eax, %cr0",
    "ljmpl *(ap_jlm - ap_tramp)(%ebx)",
    ".code64",
    "ap_lm:",
    "mov $0x10, %ax", "mov %ax, %ds", "mov %ax, %es", "mov %ax, %ss",
    "mov %ebx, %ebx",
    "mov (ap_stack - ap_tramp)(%rbx), %rsp",
    "mov (ap_entry - ap_tramp)(%rbx), %rax",
    "xor %ebp, %ebp",
    "jmp *%rax",
    ".p2align 3",
    "ap_gdt: .quad 0, 0x00CF9B000000FFFF, 0x00CF93000000FFFF, 0x00AF9B000000FFFF",
    "ap_gdtr: .short 31", ".long 0",
    ".p2align 2",
    "ap_jpm: .long 0", ".short 8",
    ".p2align 2",
    "ap_jlm: .long 0", ".short 0x18",
    ".p2align 3",
    "ap_cr3: .quad 0",
    "ap_stack: .quad 0",
    "ap_entry: .quad 0",
    "ap_tramp_end:",
    ".p2align 3",
    "ap_offs: .quad ap_tramp, ap_tramp_end - ap_tramp, ap_cr3 - ap_tramp, ap_stack - ap_tramp, ap_entry - ap_tramp",
    ".text"
)

@naked
def ap_off(i: u64) -> u64:
    asm("lea ap_offs(%rip),%rax", "mov (%rax,%rdi,8),%rax", "ret")

ap_boot_cpu: u64
ap_up: u64
# AP start-up claim (fix after inv142 O0): 0 while the BSP waits, 1 once the AP has claimed its slot (first thing in
# ap_main), 2 once the BSP gave up on it. Both sides atomic_swap their value in; whoever saw 0 decides. An AP that
# sees 2 spins with interrupts off and the BSP sends it INIT, so a late AP can never run on into the trampoline page
# (unmapped after bring-up) or into a cpu slot that the next AP is given.
ap_claim: u64
AP_WAIT_MS = 1000
ap_parked: u64
# forge.ap_delay=MS (test knob): the last AP waits MS ms at the top of ap_main before it claims
# its slot, so a slow AP is made real: MS < AP_WAIT_MS comes up late, MS > AP_WAIT_MS is parked
ap_delay_ms: u64
ap_delay_cpu: u64
ncpu_online: u64
cpu_to_apic: u64[8]

def percpu_stack_base(cpu: u64) -> u64:
    return PERCPU_STACKS + cpu * 0x40000

def lapic_ipi(apic: u64, cmd: u32):
    lapic_wr(0x310, u32(apic << 24))
    lapic_wr(0x300, cmd)
    guard = 0
    while x2apic_on == 0 and (lapic_rd(0x300) & 0x1000) != 0 and guard < 1000000:
        cpu_relax()
        guard += 1

def tramp_page() -> u64:
# a page of usable RAM below 640 KiB that nobody else uses (the low 1 MiB is never handed out)
    pg: u64 = 0x8000
    while pg < 0x9F000:
        i = 0
        while i < bi.nregions:
            r = addr(bi.regions[i])
            if r.typ == MEM_USABLE and r.base <= pg and pg + PAGE <= r.base + r.len:
                return pg
            i += 1
        pg += PAGE
    return 0

@export
@unsafe
def ap_main():
    cpu = ap_boot_cpu
    if ap_delay_ms != 0 and cpu == ap_delay_cpu:
        tsc_delay_us(ap_delay_ms * 1000)
    if atomic_swap(addr(ap_claim), 1, acq_rel) == 2:
    # the BSP gave up on this AP and is sending INIT: touch nothing (no per-CPU state); interrupts are still
    # off from the trampoline, so spin until INIT puts this CPU back into wait-for-SIPI
        while 1:
            cpu_relax()
    gdt_load(cpu)
    lidt(addr(idtr))
    cpu_features_cpu()
    wrmsr(0x1B, rdmsr(0x1B) | 0x800 | (x2apic_on << 10))
    lapic_wr(0x80, 0)
    lapic_wr(0xF0, 0x100 | VEC_SPURIOUS)
    cpu_syscall_init(cpu)
    sched_cpu_init(cpu, 1)
    lapic_wr(0x3E0, 3)
    lapic_timer_start()
    atomic_fetch_add(addr(ncpu_online), 1, acq_rel)
    atomic_store(addr(ap_up), 1, seq_cst)
    idle_loop(0)

def smp_ap_delay() -> u64:
# forge.ap_delay=MS on the command line (decimal, at most 5000), 0 when absent
    v: u64 = 0
    i: u64 = 0
    while i < 255 and bi.cmdline[i] != 0:
        while i < 255 and bi.cmdline[i] == 32:
            i += 1
        s = i
        while i < 255 and bi.cmdline[i] != 0 and bi.cmdline[i] != 32:
            i += 1
        if i > s and cmd_tok_pre(s, i - s, "forge.ap_delay="):
            v = 0
            k = s + 15
            while k < i and bi.cmdline[k] >= 48 and bi.cmdline[k] <= 57:
                v = v * 10 + u64(bi.cmdline[k]) - 48
                k += 1
    if v > 5000:
        v = 5000
    return v

@unsafe
def smp_init():
    ncpu_online = 1
    bsp = u64(lapic_rd(0x20) >> 24)
    apic_to_cpu[bsp] = 0
    cpu_to_apic[0] = bsp
    n = ncpus
    if n > MAX_CPUS:
        n = MAX_CPUS
    if n <= 1:
        return
    tp = tramp_page()
    if tp == 0:
        puts("SMP: no trampoline page below 640 KiB\n")
        return
    memcpy(p2v(tp), ap_off(0), ap_off(1))
    map_page(kernel_pml4, tp, tp, 0)
    puts("SMP: trampoline at ")
    put_hex(tp)
    putc(10)
    ap_delay_ms = smp_ap_delay()
    ap_delay_cpu = n - 1
    if ap_delay_ms != 0:
        puts("SMP: test knob forge.ap_delay: cpu ")
        put_u64(ap_delay_cpu)
        puts(" starts ")
        put_u64(ap_delay_ms)
        puts(" ms late\n")
    cpu: u64 = 1
    i = 0
    while i < n:
        apic = madt_apic[i]
        i += 1
        if apic == bsp:
            continue
        sb = percpu_stack_base(cpu)
        map_stack(kernel_pml4, sb + 0x20000, KSTACK_PAGES)
        map_stack(kernel_pml4, sb + 0x30000, IST_PAGES)
        map_stack(kernel_pml4, sb + 0x40000, IST_PAGES)
        tss_install(cpu, sb + 0x20000, sb + 0x30000, sb + 0x40000)
        apic_to_cpu[apic] = u8(cpu)
        cpu_to_apic[cpu] = apic
        vstore64(p2v(tp) + ap_off(2), kernel_pml4)
        vstore64(p2v(tp) + ap_off(3), sb + 0x20000 - 8)
        vstore64(p2v(tp) + ap_off(4), u64(ap_main))
        ap_boot_cpu = cpu
        ap_up = 0
        ap_claim = 0
        t0 = rdtsc()
        lapic_ipi(apic, 0x4500)
        tsc_delay_us(10000)
        lapic_ipi(apic, 0x4600 | u32(tp >> 12))
        tsc_delay_us(200)
        if atomic_load(addr(ap_up), acquire) == 0:
            lapic_ipi(apic, 0x4600 | u32(tp >> 12))
        # wait up to AP_WAIT_MS (was 100 ms: inv142 O0, a kexec'd kernel's AP missed it and the machine reset)
        w = 0
        while atomic_load(addr(ap_up), acquire) == 0 and w < AP_WAIT_MS * 10:
            tsc_delay_us(100)
            w += 1
        if atomic_load(addr(ap_up), acquire) == 0 and atomic_swap(addr(ap_claim), 2, acq_rel) == 0:
        # it never claimed its slot: hold it in wait-for-SIPI before the trampoline page goes away
            lapic_ipi(apic, 0x4500)
            tsc_delay_us(10000)
            ap_parked += 1
            puts("SMP: AP apic ")
            put_u64(apic)
            puts(" did not start within ")
            put_u64(AP_WAIT_MS)
            puts(" ms: INIT sent, parked\n")
            continue
        # it claimed its slot (possibly right at the deadline): it is past the trampoline and comes up shortly
        w = 0
        while atomic_load(addr(ap_up), acquire) == 0 and w < AP_WAIT_MS * 10:
            tsc_delay_us(100)
            w += 1
        if atomic_load(addr(ap_up), acquire) == 0:
            panic("SMP: an AP claimed its slot but never came up")
        if tsc_per_ms != 0 and rdtsc() - t0 > tsc_per_ms * 100:
            puts("SMP: AP apic ")
            put_u64(apic)
            puts(" came up late, after ")
            put_u64((rdtsc() - t0) / tsc_per_ms)
            puts(" ms\n")
        puts("SMP: cpu ")
        put_u64(cpu)
        puts(" (apic ")
        put_u64(apic)
        puts(") online\n")
        cpu += 1
    unmap_page(kernel_pml4, tp)
    # the APs walked through the identity-mapped trampoline: drop it from every TLB
    tlb_shootdown(0, tp, 1)

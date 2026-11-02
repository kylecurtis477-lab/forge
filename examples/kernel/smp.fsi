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
        lapic_ipi(apic, 0x4500)
        tsc_delay_us(10000)
        lapic_ipi(apic, 0x4600 | u32(tp >> 12))
        tsc_delay_us(200)
        if atomic_load(addr(ap_up), acquire) == 0:
            lapic_ipi(apic, 0x4600 | u32(tp >> 12))
        w = 0
        while atomic_load(addr(ap_up), acquire) == 0 and w < 1000:
            tsc_delay_us(100)
            w += 1
        if atomic_load(addr(ap_up), acquire) == 0:
            puts("SMP: AP apic ")
            put_u64(apic)
            puts(" did not start\n")
            continue
        puts("SMP: cpu ")
        put_u64(cpu)
        puts(" (apic ")
        put_u64(apic)
        puts(") online\n")
        cpu += 1
    unmap_page(kernel_pml4, tp)
    # the APs walked through the identity-mapped trampoline: drop it from every TLB
    tlb_shootdown(0, tp, 1)

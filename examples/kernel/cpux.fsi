# CPU features: FPU/SSE/AVX state (fxsave or xsave) switched with threads, SMEP/SMAP, user-memory
# copies that survive faults (exception-table style fixups), and cross-CPU TLB shootdown.
fpu_mode: u64  # 1 = fxsave, 2 = xsave
fpu_size: u64
fpu_xcr0: u64
fpu_template: u64  # clean state, copied into every new thread
smep_on: u64
smap_on: u64
cpu_feat7b: u64
cpuid_out: u32[4]
uaccess_faults: u64

asm(".text",
    ".globl cpuid4",
    "cpuid4:",                     # cpuid4(leaf, subleaf) -> results in cpuid_out
    "  push %rbx",
    "  mov %rdi, %rax", "mov %rsi, %rcx",
    "  cpuid",
    "  mov %eax, cpuid_out(%rip)", "mov %ebx, cpuid_out+4(%rip)",
    "  mov %ecx, cpuid_out+8(%rip)", "mov %edx, cpuid_out+12(%rip)",
    "  pop %rbx", "ret",
    ".globl xsetbv0",
    "xsetbv0:",                    # xsetbv0(value): XCR0 = value
    "  mov %rdi, %rax", "mov %rdi, %rdx", "shr $32, %rdx", "xor %ecx, %ecx", "xsetbv", "ret",
    ".globl fpu_save",
    "fpu_save:",
    "  cmpq $2, fpu_mode(%rip)", "jne 1f",
    "  mov $-1, %eax", "mov $-1, %edx", "xsave64 (%rdi)", "ret",
    "1: fxsave64 (%rdi)", "ret",
    ".globl fpu_restore",
    "fpu_restore:",
    "  cmpq $2, fpu_mode(%rip)", "jne 1f",
    "  mov $-1, %eax", "mov $-1, %edx", "xrstor64 (%rdi)", "ret",
    "1: fxrstor64 (%rdi)", "ret",
    ".globl fpu_reset",
    "fpu_reset:",
    "  fninit", "pushq $0x1F80", "ldmxcsr (%rsp)", "add $8, %rsp", "ret",
    # copy_user(dst, src, n) -> 0, or -14 (EFAULT) if a page fault hits the copy
    ".globl copy_user",
    "copy_user:",
    "  cmpb $0, smap_on(%rip)", "je 1f", "stac",
    "1: mov %rdx, %rcx",
    "copy_user_insn:",
    "  rep movsb",
    "  cmpb $0, smap_on(%rip)", "je 2f", "clac",
    "2: xor %eax, %eax", "ret",
    "uaccess_fail:",
    "  cmpb $0, smap_on(%rip)", "je 3f", "clac",
    "3: mov $-14, %rax", "ret",
    # copy_str_user(dst, src, max) -> length (excluding NUL), -14 on fault, -36 if no NUL within max
    ".globl copy_str_user",
    "copy_str_user:",
    "  cmpb $0, smap_on(%rip)", "je 1f", "stac",
    "1: xor %eax, %eax",
    "4: cmp %rdx, %rax", "jae 6f",
    "copy_str_insn:",
    "  movb (%rsi,%rax), %cl",
    "  movb %cl, (%rdi,%rax)",
    "  test %cl, %cl", "jz 5f",
    "  inc %rax", "jmp 4b",
    "5: cmpb $0, smap_on(%rip)", "je 7f", "clac",
    "7: ret",
    "6: cmpb $0, smap_on(%rip)", "je 8f", "clac",
    "8: mov $-36, %rax", "ret",
    # smap_probe(uaddr): kernel read of a user page WITHOUT stac -> 1 if blocked, 0 if it went through
    ".globl smap_probe",
    "smap_probe:",
    "smap_probe_insn:",
    "  mov (%rdi), %rax",
    "  xor %eax, %eax", "ret",
    "probe_blocked:",
    "  mov $1, %eax", "ret",
    # smep_probe(ufn): kernel call into user code -> 1 if blocked, 0 if it ran
    ".globl smep_probe",
    "smep_probe:",
    "  call *%rdi",
    "  xor %eax, %eax", "ret",
    "smep_blocked:",
    "  add $8, %rsp", "mov $1, %eax", "ret",
    ".globl uaccess_labels",
    "uaccess_labels:",             # uaccess_labels(i) -> address of label i
    "  lea uaccess_tab(%rip), %rax", "mov (%rax,%rdi,8), %rax", "ret",
    ".section .rodata",
    ".p2align 3",
    "uaccess_tab: .quad copy_user_insn, copy_str_insn, uaccess_fail, smap_probe_insn, probe_blocked, smep_blocked",
    ".text")
extern def cpuid4(leaf: u64, sub: u64)
extern def xsetbv0(v: u64)
extern def fpu_save(p: u64)
extern def fpu_restore(p: u64)
extern def fpu_reset()
extern def copy_user(dst: u64, src: u64, n: u64) -> u64
extern def copy_str_user(dst: u64, src: u64, max: u64) -> u64
extern def smap_probe(p: u64) -> u64
extern def smep_probe(p: u64) -> u64
extern def uaccess_labels(i: u64) -> u64

smep_probing: u64

@unsafe
def uaccess_fixup(frame: ptr[u64], err: u64, cr2: u64) -> u64:
# kernel-mode #PF: if it hit one of the user-access instructions, resume at the failure path
    rip = frame[1]
    if rip == uaccess_labels(0) or rip == uaccess_labels(1):
        frame[1] = uaccess_labels(2)
        atomic_add64(u64(addr(uaccess_faults)), 1)
        return 1
    if rip == uaccess_labels(3):
        frame[1] = uaccess_labels(4)
        return 1
    if smep_probing and (err & 0x10) and rip < USER_TOP and rip == cr2:
        frame[1] = uaccess_labels(5)
        return 1
    return 0

@unsafe
def fpu_area_new() -> u64:
# 64-byte aligned save area; the raw pointer is stored just below it for kfree
    raw = kmalloc(fpu_size + 64 + 8)
    a = (raw + 8 + 63) & ~u64(63)
    vstore64(a - 8, raw)
    if fpu_template != 0:
        memcpy(a, fpu_template, fpu_size)
    else:
        memset(a, 0, fpu_size)
    return a

@unsafe
def fpu_area_free(a: u64):
    if a != 0:
        kfree(vload64(a - 8))

def cpu_features_cpu():
# per CPU: x87/SSE on, the same XCR0 everywhere, SMEP/SMAP
    write_cr0((read_cr0() | 0x22) & ~u64(0xC))  # MP|NE on, EM|TS off
    c4 = read_cr4() | 0x600  # OSFXSR | OSXMMEXCPT
    if fpu_mode == 2:
        c4 = c4 | 0x40000  # OSXSAVE
    if smep_on:
        c4 = c4 | 0x100000
    if smap_on:
        c4 = c4 | 0x200000
    write_cr4(c4)
    if fpu_mode == 2:
        xsetbv0(fpu_xcr0)
    fpu_reset()

@unsafe
def cpu_features_init():
    cpuid4(1, 0)
    ecx1 = u64(cpuid_out[2])
    cpuid4(0, 0)
    maxl = u64(cpuid_out[0])
    if maxl >= 7:
        cpuid4(7, 0)
        cpu_feat7b = u64(cpuid_out[1])
    smep_on = (cpu_feat7b >> 7) & 1
    smap_on = (cpu_feat7b >> 20) & 1
    fpu_mode = 1
    fpu_size = 512
    if (ecx1 >> 26) & 1:
        cpuid4(0xD, 0)
        sup = u64(cpuid_out[0]) | (u64(cpuid_out[3]) << 32)
        fpu_xcr0 = sup & 0xE7  # x87, SSE, AVX, AVX-512 (opmask/ZMM)
        if (fpu_xcr0 & 0xE0) != 0xE0:
            fpu_xcr0 = fpu_xcr0 & 7
        fpu_mode = 2
    cpu_features_cpu()
    if fpu_mode == 2:
        cpuid4(0xD, 0)
        fpu_size = u64(cpuid_out[1])  # size for the XCR0 now enabled
    fpu_template = 0
    tpl = fpu_area_new()
    memset(tpl, 0, fpu_size)
    fpu_save(tpl)
    fpu_template = tpl
    puts("CPU: FPU state ")
    if fpu_mode == 2:
        puts("xsave xcr0=")
        put_hex(fpu_xcr0)
    else:
        puts("fxsave")
    puts(" (")
    put_u64(fpu_size)
    puts(" bytes/thread), SMEP ")
    puts_onoff(smep_on)
    puts(", SMAP ")
    puts_onoff(smap_on)
    putc(10)

def puts_onoff(v: u64):
    if v:
        puts("on")
    else:
        puts("off")

# ---- TLB shootdown ----
VEC_TLB = 0xF0
tlb_lock: u64
tlb_active: u64
tlb_cr3: u64
tlb_va: u64
tlb_n: u64
tlb_acks: u64
tlb_pending: u64[8]
tlb_ipis: u64
tlb_rounds: u64
tlb_park: u64
stop_rounds: u64
stop_parked: u64

@unsafe
def stop_machine_begin() -> u64:
# park every other online CPU in its IPI handler (or in the spin loop it is in) with interrupts off;
# returns the caller's saved interrupt flag. Reuses the shootdown slot, so it serialises with it.
    f = irq_save()
    spin_lock(u64(addr(tlb_lock)))
    nc = atomic_load64(u64(addr(ncpu_online)))
    if nc > 1:
        tlb_cr3 = 1  # matches no address space: parked CPUs flush nothing
        tlb_n = 0
        atomic_store64(u64(addr(tlb_acks)), 0)
        atomic_store64(u64(addr(tlb_park)), stop_rounds + 1)
        me = lapic_cpu()
        cnt: u64 = 0
        c: u64 = 0
        while c < nc:
            if c != me:
                atomic_store64(u64(addr(tlb_pending[c])), 1)
                cnt += 1
            c += 1
        atomic_store64(u64(addr(tlb_active)), 1)
        c = 0
        while c < nc:
            if c != me:
                lapic_ipi(cpu_to_apic[c], u32(VEC_TLB))
            c += 1
        while atomic_load64(u64(addr(tlb_acks))) < cnt:
            cpu_relax()
        stop_parked = cnt
    stop_rounds += 1
    return f

@unsafe
def stop_machine_end(f: u64):
    cpuid4(0, 0)
    atomic_store64(u64(addr(tlb_park)), 0)
    atomic_store64(u64(addr(tlb_active)), 0)
    spin_unlock(u64(addr(tlb_lock)))
    irq_restore(f)

@unsafe
def tlb_local(cr3: u64, va: u64, n: u64):
    if cr3 != 0 and (read_cr3() & ADDR_MASK) != cr3:
        return
    if n == 0 or n > 32:
        if cr3 == 0:
            c4 = read_cr4()
            write_cr4(c4 & ~u64(0x80))
            write_cr4(c4)
        else:
            write_cr3(read_cr3())
        return
    i = 0
    while i < n:
        invlpg(va + i * PAGE)
        i += 1

tlb_halt: u64  # kexec: parked CPUs halt for good (the successor restarts them with INIT-SIPI)

@unsafe
def tlb_poll():
# safe with interrupts off: called from spin loops so a CPU that waits on a lock still answers
    if atomic_load64(u64(addr(tlb_active))) == 0:
        return
    c = lapic_cpu()
    if atomic_load64(u64(addr(tlb_pending[c]))) != 0:
        tlb_local(tlb_cr3, tlb_va, tlb_n)
        atomic_store64(u64(addr(tlb_pending[c])), 0)
        atomic_add64(u64(addr(tlb_acks)), 1)
        g = atomic_load64(u64(addr(tlb_park)))
        if g != 0:
        # stop-machine: stay parked (interrupts off) until the initiator ends round g, then serialise
        # so this CPU refetches any instruction bytes the initiator rewrote. Waiting on the round
        # number (not on 0) means a CPU that misses the brief 0 is not trapped into the next round.
            while tlb_halt:
                hlt()
            while atomic_load64(u64(addr(tlb_park))) == g:
                cpu_relax()
            cpuid4(0, 0)

@interrupt
def on_tlb_ipi(frame: ptr[u64]):
    tlb_poll()
    lapic_eoi()

@unsafe
def tlb_shootdown(cr3: u64, va: u64, n: u64):
# cr3 = 0: kernel (global) mapping, flush everywhere; otherwise only CPUs running that address space
    f = irq_save()
    tlb_local(cr3, va, n)
    if atomic_load64(u64(addr(ncpu_online))) > 1:
        spin_lock(u64(addr(tlb_lock)))
        tlb_cr3 = cr3
        tlb_va = va
        tlb_n = n
        atomic_store64(u64(addr(tlb_acks)), 0)
        me = lapic_cpu()
        cnt: u64 = 0
        c: u64 = 0
        nc = atomic_load64(u64(addr(ncpu_online)))
        while c < nc:
            if c != me:
                atomic_store64(u64(addr(tlb_pending[c])), 1)
                cnt += 1
            c += 1
        atomic_store64(u64(addr(tlb_active)), 1)
        c = 0
        while c < nc:
            if c != me:
                lapic_ipi(cpu_to_apic[c], u32(VEC_TLB))
            c += 1
        while atomic_load64(u64(addr(tlb_acks))) < cnt:
            cpu_relax()
        atomic_store64(u64(addr(tlb_active)), 0)
        tlb_ipis += cnt
        tlb_rounds += 1
        spin_unlock(u64(addr(tlb_lock)))
    irq_restore(f)

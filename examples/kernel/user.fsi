# User mode: ring 3 processes with private address spaces, syscall/sysret, ELF64 loader.
#
# syscall ABI: rax = number, rdi/rsi/rdx = args, result in rax (rcx/r11 clobbered by the CPU).
# Entry swaps to the per-CPU block via swapgs only long enough to find the kernel stack, then swaps
# back, so kernel C code always runs with the user GS state (same as interrupts from ring 3) and a
# thread may block or migrate inside a syscall.
SEL_UDATA = 0x1B
SEL_UCODE = 0x23
USER_TOP = 0x0000800000000000
USTACK_TOP = 0x00007FFFFFFFF000
USTACK_PAGES = 16
PTE_U = 4
MSR_EFER = 0xC0000080
MSR_STAR = 0xC0000081
MSR_LSTAR = 0xC0000082
MSR_FMASK = 0xC0000084
MSR_GS_BASE = 0xC0000101
MSR_KGS_BASE = 0xC0000102

con_lock: u64
syscalls: u64
user_kills: u64

asm(".text",
    ".globl syscall_entry",
    "syscall_entry:",
    "  swapgs",
    "  mov %rsp, %gs:8",
    "  mov %gs:0, %rsp",
    "  pushq %gs:8",
    "  swapgs",
    # frame (80 bytes, see SF_* below): pad, r15, r14, r13, r12, rbp, rbx, r11 (rflags), rcx (rip), user rsp
    "  push %rcx",
    "  push %r11",
    "  push %rbx",
    "  push %rbp",
    "  push %r12",
    "  push %r13",
    "  push %r14",
    "  push %r15",
    "  sub $8, %rsp",
    "  mov %rsp, %r9",
    "  mov %r10, %r8",
    "  mov %rdx, %rcx",
    "  mov %rsi, %rdx",
    "  mov %rdi, %rsi",
    "  mov %rax, %rdi",
    "  call syscall_dispatch",
    "syscall_ret:",
    "  cli",
    "  add $8, %rsp",
    "  pop %r15",
    "  pop %r14",
    "  pop %r13",
    "  pop %r12",
    "  pop %rbp",
    "  pop %rbx",
    "  pop %r11",
    "  pop %rcx",
    "  pop %rsp",
    "  xor %esi, %esi",
    "  xor %edi, %edi",
    "  xor %edx, %edx",
    "  xor %r8d, %r8d",
    "  xor %r9d, %r9d",
    "  xor %r10d, %r10d",
    "  sysretq",
    # a forked child starts here (rdi = its copy of the parent's syscall frame) and returns 0 to user mode
    "fork_child_entry:",
    "  mov %rdi, %rsp",
    "  xor %eax, %eax",
    "  jmp syscall_ret",
    # a process handed over by kexec resumes here: the interrupted syscall returns 1
    "kx_resume_entry:",
    "  mov %rdi, %rsp",
    "  mov $1, %eax",
    "  jmp syscall_ret",
    # a process parked at a syscall boundary by kexec re-issues that syscall in the new kernel:
    # rdi = its syscall frame, with (nr, a, b, c, d) stored in the 40 bytes just below it
    "kx_restart_entry:",
    "  mov %rdi, %rsp",
    "  mov -40(%rsp), %rdi",
    "  mov -32(%rsp), %rsi",
    "  mov -24(%rsp), %rdx",
    "  mov -16(%rsp), %rcx",
    "  mov -8(%rsp), %r8",
    "  mov %rsp, %r9",
    "  call syscall_dispatch",
    "  jmp syscall_ret",
    ".globl kx_restart_entry_addr",
    "kx_restart_entry_addr: lea kx_restart_entry(%rip), %rax",
    "  ret",
    ".globl kx_resume_entry_addr",
    "kx_resume_entry_addr: lea kx_resume_entry(%rip), %rax",
    "  ret",
    ".globl fork_child_entry_addr",
    "fork_child_entry_addr: lea fork_child_entry(%rip), %rax",
    "  ret",
    ".globl enter_ring3",
    "enter_ring3:",
    "  cli",
    "  pushq $0x1B",
    "  push %rsi",
    "  pushq $0x202",
    "  pushq $0x23",
    "  push %rdi",
    "  xor %eax, %eax",
    "  xor %ebx, %ebx",
    "  xor %ecx, %ecx",
    "  xor %edx, %edx",
    "  xor %esi, %esi",
    "  xor %edi, %edi",
    "  xor %ebp, %ebp",
    "  xor %r8d, %r8d",
    "  xor %r9d, %r9d",
    "  xor %r10d, %r10d",
    "  xor %r11d, %r11d",
    "  xor %r12d, %r12d",
    "  xor %r13d, %r13d",
    "  xor %r14d, %r14d",
    "  xor %r15d, %r15d",
    "  iretq")

extern def enter_ring3(rip: u64, rsp: u64)
extern def fork_child_entry_addr() -> u64
extern def kx_resume_entry_addr() -> u64
extern def kx_restart_entry_addr() -> u64
SF_SIZE = 80
SF_RFLAGS = 7
SF_RIP = 8
SF_RSP = 9

@naked
def syscall_entry_addr() -> u64:
    asm("lea syscall_entry(%rip), %rax", "ret")

@unsafe
def cpu_syscall_init(cpu: u64):
    percpu_kstack[cpu * 4 + 2] = cpu
    wrmsr(MSR_EFER, rdmsr(MSR_EFER) | 1)
    wrmsr(MSR_STAR, (u64(0x10) << 48) | (u64(SEL_KCODE) << 32))
    wrmsr(MSR_LSTAR, syscall_entry_addr())
    wrmsr(MSR_FMASK, 0x47700)
    wrmsr(MSR_GS_BASE, 0)
    wrmsr(MSR_KGS_BASE, u64(addr(percpu_kstack[cpu * 4])))

elf_wx_rejects: u64  # images refused for a writable+executable segment or a mixed-permission page

# ---- user address spaces ----
@unsafe
def upt_next(table: u64, idx: u64) -> u64:
    e = p2v(table) + idx * 8
    v = vload64(e)
    if (v & PTE_P) == 0:
        nf = frame_alloc()
        vstore64(e, nf | PTE_P | PTE_W | PTE_U)
        return nf
    return v & ADDR_MASK

def user_pte(pml4: u64, va: u64) -> u64:
    t = upt_next(pml4, (va >> 39) & 511)
    t = upt_next(t, (va >> 30) & 511)
    t = upt_next(t, (va >> 21) & 511)
    return p2v(t) + ((va >> 12) & 511) * 8

@unsafe
def user_page_ok(pml4: u64, va: u64, write: u64) -> u64:
    t = pml4
    lvl = 0
    while lvl < 4:
        v = vload64(p2v(t) + ((va >> (39 - 9 * lvl)) & 511) * 8)
        if (v & (PTE_P | PTE_U)) != (PTE_P | PTE_U):
            return 0
        if lvl == 3 and write and (v & PTE_W) == 0:
            return 0
        t = v & ADDR_MASK
        lvl += 1
    return 1

def user_range_ok(pml4: u64, va: u64, len: u64, write: u64) -> u64:
    if len > 0x10000 or va >= USER_TOP or va + len > USER_TOP or va + len < va:
        return 0
    p = va & ~u64(PAGE - 1)
    while p < va + len:
        if user_page_ok(pml4, p, write) == 0:
            return 0
        p += PAGE
    return 1

@unsafe
def as_new() -> u64:
    pml4 = frame_alloc()
    i = 256
    while i < 512:
        vstore64(p2v(pml4) + i * 8, vload64(p2v(kernel_pml4) + i * 8))
        i += 1
    return pml4

@unsafe
def as_free_level(t: u64, lvl: u64) -> u64:
    n: u64 = 0
    i = 0
    while i < 512:
        v = vload64(p2v(t) + i * 8)
        if v & PTE_P:
            if lvl == 1:
                frame_free(v & ADDR_MASK)
                n += 1
            else:
                n += as_free_level(v & ADDR_MASK, lvl - 1)
        i += 1
    frame_free(t)
    return n + 1

@unsafe
def as_free(pml4: u64) -> u64:
# frees every user page and table (lower half only), then the PML4 itself
    n: u64 = 0
    i = 0
    while i < 256:
        v = vload64(p2v(pml4) + i * 8)
        if v & PTE_P:
            n += as_free_level(v & ADDR_MASK, 3)
        i += 1
    frame_free(pml4)
    return n + 1

# ---- ELF64 loader ----
@unsafe
def ld16(p: u64) -> u64:
    return vload64(p) & 0xFFFF

@unsafe
def ld32(p: u64) -> u64:
    return vload64(p) & 0xFFFFFFFF

elf_end: u64

@unsafe
def elf_load(pml4: u64, img: u64, len: u64) -> u64:
# returns the entry point, or 0 if the image is rejected
    if len < 64 or ld32(img) != 0x464C457F or (vload64(img + 4) & 0xFF) != 2 or ld16(img + 18) != 62:
        return 0
    if ld16(img + 16) != 2:
        return 0
    entry = vload64(img + 24)
    phoff = vload64(img + 32)
    phent = ld16(img + 54)
    phnum = ld16(img + 56)
    if phent != 56 or phoff + phnum * 56 > len:
        return 0
    i = 0
    nload = 0
    elf_end = 0
    while i < phnum:
        ph = img + phoff + i * 56
        if ld32(ph) == 1:
            fl = ld32(ph + 4)
            off = vload64(ph + 8)
            va = vload64(ph + 16)
            fsz = vload64(ph + 32)
            msz = vload64(ph + 40)
            if (fl & 3) == 3:
                elf_wx_rejects += 1
                return 0
            if fsz > msz or off + fsz > len or va >= USER_TOP or va + msz > USER_TOP or va < 0x10000:
                return 0
            pf = PTE_P | PTE_U
            if fl & 2:
                pf = pf | PTE_W
            if (fl & 1) == 0:
                pf = pf | PTE_NX
            p = va & ~u64(PAGE - 1)
            while p < va + msz:
                e = user_pte(pml4, p)
                v = vload64(e)
                if (v & PTE_P) == 0:
                    v = frame_alloc() | pf
                    vstore64(e, v)
                elif (v & (PTE_W | PTE_NX)) != (pf & (PTE_W | PTE_NX)):
                # W^X per page: segments with different permissions may not share a page
                    elf_wx_rejects += 1
                    return 0
                fr = v & ADDR_MASK
                # copy the file bytes that land in this page
                lo = p
                if lo < va:
                    lo = va
                hi = p + PAGE
                if hi > va + fsz:
                    hi = va + fsz
                if lo < hi:
                    memcpy(p2v(fr) + (lo - p), img + off + (lo - va), hi - lo)
                p += PAGE
            if va + msz > elf_end:
                elf_end = va + msz
            nload += 1
        i += 1
    if nload == 0 or entry >= USER_TOP:
        return 0
    return entry

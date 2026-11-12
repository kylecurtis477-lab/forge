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
MSR_FS_BASE = 0xC0000100  # 11.1b: per-thread user FS base (sched.fsi switches it)

con_lock: u64
syscalls: u64
user_kills: u64

asm(".text",
    ".globl syscall_entry",
    "syscall_entry:",
    "  swapgs",
    "  mov %rsp, %gs:8",
    "  mov %gs:0, %rsp",
    # 11.1: the argument registers are saved too (and restored on return): the Linux ABI says a
    # syscall preserves every register but rax, rcx and r11, and Linux mmap/futex take r8/r9
    "  push %r9",
    "  push %r8",
    "  push %r10",
    "  push %rdx",
    "  push %rsi",
    "  push %rdi",
    "  pushq %gs:8",
    "  swapgs",
    # frame (128 bytes, see SF_* below): pad, r15, r14, r13, r12, rbp, rbx, r11 (rflags), rcx (rip),
    # user rsp, rdi, rsi, rdx, r10, r8, r9
    "  push %rcx",
    "  push %r11",
    "  push %rbx",
    "  push %rbp",
    "  push %r12",
    "  push %r13",
    "  push %r14",
    "  push %r15",
    # word 0 (SF pad): 0 = leave by sysret; rt_sigreturn sets it to &thread.sig_rx (iretq exit below)
    "  pushq $0",
    "  mov %rsp, %r9",
    "  mov %r10, %r8",
    "  mov %rdx, %rcx",
    "  mov %rsi, %rdx",
    "  mov %rdi, %rsi",
    "  mov %rax, %rdi",
    "  call syscall_dispatch",
    "syscall_ret:",
    "  cli",
    "  mov (%rsp), %r11",
    "  test %r11, %r11",
    "  jnz sigret_exit",
    "  add $8, %rsp",
    "  pop %r15",
    "  pop %r14",
    "  pop %r13",
    "  pop %r12",
    "  pop %rbp",
    "  pop %rbx",
    "  pop %r11",
    "  pop %rcx",
    # the user's own argument registers come back (never kernel values: exec and a fresh frame zero them)
    "  mov 8(%rsp), %rdi",
    "  mov 16(%rsp), %rsi",
    "  mov 24(%rsp), %rdx",
    "  mov 32(%rsp), %r10",
    "  mov 40(%rsp), %r8",
    "  mov 48(%rsp), %r9",
    "  pop %rsp",
    "  sysretq",
    # rt_sigreturn's exit: sysret would leave rip in rcx and rflags in r11, so a handler that resumed a
    # faulting instruction would get both clobbered; iretq restores every register (r11 = &sig_rx here)
    "sigret_exit:",
    "  add $8, %rsp",
    "  pop %r15",
    "  pop %r14",
    "  pop %r13",
    "  pop %r12",
    "  pop %rbp",
    "  pop %rbx",
    # now 0 rflags, 8 rip, 16 user rsp, 24 rdi, 32 rsi, 40 rdx, 48 r10, 56 r8, 64 r9: load the six
    # argument registers, then build the iret frame (rip, cs, rflags, rsp, ss) over slots 32..64
    "  mov 24(%rsp), %rdi",
    "  mov 32(%rsp), %rsi",
    "  mov 40(%rsp), %rdx",
    "  mov 48(%rsp), %r10",
    "  mov 56(%rsp), %r8",
    "  mov 64(%rsp), %r9",
    "  mov 8(%rsp), %rcx",
    "  mov %rcx, 32(%rsp)",
    "  movq $0x23, 40(%rsp)",
    "  mov 0(%rsp), %rcx",
    "  mov %rcx, 48(%rsp)",
    "  mov 16(%rsp), %rcx",
    "  mov %rcx, 56(%rsp)",
    "  movq $0x1B, 64(%rsp)",
    "  mov 0(%r11), %rcx",
    "  mov 8(%r11), %r11",
    "  add $32, %rsp",
    "  iretq",
    # a forked child starts here (rdi = its copy of the parent's syscall frame) and returns 0 to user mode
    "fork_child_entry:",
    "  mov %rdi, %rsp",
    "  movq $0, (%rsp)",
    "  xor %eax, %eax",
    "  jmp syscall_ret",
    # a process handed over by kexec resumes here: the interrupted syscall returns 1
    "kx_resume_entry:",
    "  mov %rdi, %rsp",
    "  movq $0, (%rsp)",
    "  mov $1, %eax",
    "  jmp syscall_ret",
    # a process parked at a syscall boundary by kexec re-issues that syscall in the new kernel:
    # rdi = its syscall frame, with (nr, a, b, c, d) stored in the 40 bytes just below it
    "kx_restart_entry:",
    "  mov %rdi, %rsp",
    "  movq $0, (%rsp)",
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
SF_SIZE = 128
SF_RFLAGS = 7
SF_RIP = 8
SF_RSP = 9
SF_RDI = 10  # 10..15: rdi, rsi, rdx, r10, r8, r9 (the six syscall argument registers)
SF_R8 = 14
SF_R9 = 15

# 11.1d: typed views of the two saved user contexts, so the signal code reads and edits them as
# struct fields (no raw loads): the syscall frame above, and what an @interrupt_err wrapper leaves
# below its frame pointer (the 9 caller-saved registers it pushed, then the CPU's error code + iret
# frame). syscall_dispatch and on_page_fault make the one cast each.
struct SFrame:
    pad: u64
    r15: u64
    r14: u64
    r13: u64
    r12: u64
    rbp: u64
    rbx: u64
    rflags: u64
    rip: u64
    rsp: u64
    rdi: u64
    rsi: u64
    rdx: u64
    r10: u64
    r8: u64
    r9: u64

struct IFrame:
    r11: u64
    r10: u64
    r9: u64
    r8: u64
    rdi: u64
    rsi: u64
    rdx: u64
    rcx: u64
    rax: u64
    err: u64
    rip: u64
    cs: u64
    rflags: u64
    rsp: u64
    ss: u64

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
    pcid_forget(pml4)
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
elf_bad_rejects: u64
ELF_MAX_MAP = 0x4000000  # 64 MiB of PT_LOAD memory per image

# ELF64 header and program header viewed as u64 words (fields packed inside a word are masked out)
struct Ehdr:
    w0: u64
    w1: u64
    w2: u64
    entry: u64
    phoff: u64
    shoff: u64
    w6: u64
    w7: u64

struct Phdr:
    w0: u64
    off: u64
    va: u64
    pa: u64
    fsz: u64
    msz: u64
    align: u64

@unsafe
def elf_load(pml4: u64, img: u64, len: u64, endp: u64) -> u64:
# returns the entry point, or 0 if the image is rejected; stores the image end at endp (if nonzero).
# The end is per call, never a global: CPUs load images concurrently (a shared elf_end once gave
# a forked process another image's brk base, so brk memory landed on its own text page).
    if len < 64:
        return 0
    eh = ptr[Ehdr](img)
    if (eh.w0 & 0xFFFFFFFFFF) != 0x02464C457F or ((eh.w2 >> 16) & 0xFFFF) != 62:
        return 0
    if (eh.w2 & 0xFFFF) != 2:
        return 0
    entry = eh.entry
    phoff = eh.phoff
    phent = (eh.w6 >> 48) & 0xFFFF
    phnum = eh.w7 & 0xFFFF
    if phent != 56 or phoff > len or phnum * 56 > len - phoff:
        return 0
    i = 0
    nload = 0
    elf_end: u64 = 0
    mapped: u64 = 0
    while i < phnum:
        ph = ptr[Phdr](img + phoff + i * 56)
        if (ph.w0 & 0xFFFFFFFF) == 1:
            fl = (ph.w0 >> 32) & 0xFFFFFFFF
            off = ph.off
            va = ph.va
            fsz = ph.fsz
            msz = ph.msz
            if (fl & 3) == 3:
                elf_wx_rejects += 1
                return 0
            # wrap-safe: off/fsz against the image, va/msz against USER_TOP
            if fsz > msz or off > len or fsz > len - off or va < 0x10000 or va >= USER_TOP or msz > USER_TOP - va:
                elf_bad_rejects += 1
                return 0
            # cap the total mapped size so a huge p_memsz cannot exhaust physical memory
            if msz > ELF_MAX_MAP or mapped + msz > ELF_MAX_MAP:
                elf_bad_rejects += 1
                return 0
            mapped += msz
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
                    nf = frame_try_alloc()
                    if nf == 0:
                    # out of frames: fail soft; the caller frees the half-built address space
                        elf_bad_rejects += 1
                        return 0
                    v = nf | pf
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
    if endp != 0:
        vstore64(endp, elf_end)
    return entry

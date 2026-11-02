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
    "  push %rcx",
    "  push %r11",
    "  sub $8, %rsp",
    "  mov %rdx, %rcx",
    "  mov %rsi, %rdx",
    "  mov %rdi, %rsi",
    "  mov %rax, %rdi",
    "  call syscall_dispatch",
    "  add $8, %rsp",
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

@naked
def syscall_entry_addr() -> u64:
    asm("lea syscall_entry(%rip), %rax", "ret")

def cpu_syscall_init(cpu: u64):
    percpu_kstack[cpu * 4 + 2] = cpu
    wrmsr(MSR_EFER, rdmsr(MSR_EFER) | 1)
    wrmsr(MSR_STAR, (u64(0x10) << 48) | (u64(SEL_KCODE) << 32))
    wrmsr(MSR_LSTAR, syscall_entry_addr())
    wrmsr(MSR_FMASK, 0x47700)
    wrmsr(MSR_GS_BASE, 0)
    wrmsr(MSR_KGS_BASE, u64(addr(percpu_kstack[cpu * 4])))

# ---- user address spaces ----
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

def as_new() -> u64:
    pml4 = frame_alloc()
    i = 256
    while i < 512:
        vstore64(p2v(pml4) + i * 8, vload64(p2v(kernel_pml4) + i * 8))
        i += 1
    return pml4

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
def ld16(p: u64) -> u64:
    return vload64(p) & 0xFFFF

def ld32(p: u64) -> u64:
    return vload64(p) & 0xFFFFFFFF

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
    while i < phnum:
        ph = img + phoff + i * 56
        if ld32(ph) == 1:
            fl = ld32(ph + 4)
            off = vload64(ph + 8)
            va = vload64(ph + 16)
            fsz = vload64(ph + 32)
            msz = vload64(ph + 40)
            if (fl & 3) == 3 or fsz > msz or off + fsz > len or va >= USER_TOP or va + msz > USER_TOP or va < 0x10000:
                return 0
            pf = PTE_P | PTE_U
            if fl & 2:
                pf = pf | PTE_W
            if (fl & 1) == 0:
                pf = pf | PTE_NX
            p = va & ~u64(PAGE - 1)
            while p < va + msz:
                e = user_pte(pml4, p)
                if (vload64(e) & PTE_P) == 0:
                    vstore64(e, frame_alloc() | pf)
                fr = vload64(e) & ADDR_MASK
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
            nload += 1
        i += 1
    if nload == 0 or entry >= USER_TOP:
        return 0
    return entry

def user_main(arg: u64):
    t = cur_thread()
    enter_ring3(t.proc, USTACK_TOP - 8)

def proc_spawn(img: u64, len: u64) -> ptr[Thread]:
    pml4 = as_new()
    entry = elf_load(pml4, img, len)
    if entry == 0:
        as_free(pml4)
        return ptr[Thread](0)
    i = 0
    while i < USTACK_PAGES:
        vstore64(user_pte(pml4, USTACK_TOP - (i + 1) * PAGE), frame_alloc() | PTE_P | PTE_W | PTE_U | PTE_NX)
        i += 1
    t = thread_new(u64(user_main), 0)
    t.cr3 = pml4
    t.user = 1
    t.proc = entry
    thread_start_ready(t)
    return t

def proc_reap(t: ptr[Thread]) -> u64:
    # waits for a user thread to die, frees it and its address space; returns the exit code
    while atomic_load64(u64(addr(t.state))) != T_DEAD:
        yield_cpu()
    code = t.exit_code
    pml4 = t.cr3
    thread_free(t)
    as_free(pml4)
    return code

# ---- syscalls ----
def user_ptr_ok(va: u64, len: u64) -> u64:
    # only the range check: the copy itself survives unmapped pages through the #PF fixup
    return va < USER_TOP and va + len <= USER_TOP and va + len >= va

def con_write_user(p: u64, n: u64) -> u64:
    kb: u8[256]
    done: u64 = 0
    spin_lock(u64(addr(con_lock)))
    while done < n:
        k = n - done
        if k > 256:
            k = 256
        if copy_user(u64(addr(kb[0])), p + done, k) != 0:
            spin_unlock(u64(addr(con_lock)))
            return 0xFFFFFFFFFFFFFFF2
        i = 0
        while i < k:
            putc(kb[i])
            i += 1
        done += k
    spin_unlock(u64(addr(con_lock)))
    return n

@export
def syscall_dispatch(nr: u64, a: u64, b: u64, c: u64) -> u64:
    # interrupts are off (FMASK clears IF)
    atomic_add64(u64(addr(syscalls)), 1)
    t = cur_thread()
    if nr == 1:
        if user_ptr_ok(a, b) == 0:
            return 0xFFFFFFFFFFFFFFF2
        return con_write_user(a, b)
    if nr == 24:
        schedule(0)
        return 0
    if nr == 39:
        return t.id
    if nr == 60:
        t.exit_code = a & 0xFF
        thread_exit()
    if nr == 96:
        return lapic_ticks
    if nr == 97:
        return lapic_cpu()
    if nr == 200:
        # self-test probes: 1 = kernel read of user memory without stac, 2 = kernel call into user code
        if user_ptr_ok(b, 8) == 0:
            return 0xFFFFFFFFFFFFFFF2
        if a == 1:
            return smap_probe(b)
        if a == 2:
            smep_probing = 1
            r = smep_probe(b)
            smep_probing = 0
            return r
        if a == 3:
            return u64(smep_on) | (u64(smap_on) << 1)
        return 0xFFFFFFFFFFFFFFEA
    return 0xFFFFFFFFFFFFFFDA

def user_kill(name: ptr[u8], vec: u64, rip: u64, addr_: u64):
    # called from an exception taken in ring 3: report, mark, and never return to that thread
    t = cur_thread()
    atomic_add64(u64(addr(user_kills)), 1)
    spin_lock(u64(addr(con_lock)))
    puts("user fault: pid ")
    put_u64(t.id)
    puts(" ")
    puts(name)
    puts(" at rip=")
    put_hex(rip)
    puts(" addr=")
    put_hex(addr_)
    puts(" -> killed\n")
    spin_unlock(u64(addr(con_lock)))
    t.exit_code = 0x100 + vec
    thread_exit()

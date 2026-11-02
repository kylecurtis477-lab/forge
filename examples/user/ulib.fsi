# Minimal user-space runtime for Forge programs running on the Forge kernel (ring 3, syscall ABI:
# rax = number, rdi/rsi/rdx = args, result in rax; rcx/r11 clobbered).
SYS_WRITE = 1
SYS_YIELD = 24
SYS_GETPID = 39
SYS_EXIT = 60
SYS_TICKS = 96
SYS_CPU = 97
SYS_PROBE = 200

# fill xmm0-15 with v, v+1, ..., yield n times, then count registers that lost their value
@naked
def xmm_yield_check(v: u64, n: u64) -> u64:
    asm("push %rbx", "push %r12", "mov %rdi, %rbx", "mov %rsi, %r12",
        "lea 0(%rbx), %rax", "movq %rax, %xmm0", "lea 1(%rbx), %rax", "movq %rax, %xmm1",
        "lea 2(%rbx), %rax", "movq %rax, %xmm2", "lea 3(%rbx), %rax", "movq %rax, %xmm3",
        "lea 4(%rbx), %rax", "movq %rax, %xmm4", "lea 5(%rbx), %rax", "movq %rax, %xmm5",
        "lea 6(%rbx), %rax", "movq %rax, %xmm6", "lea 7(%rbx), %rax", "movq %rax, %xmm7",
        "lea 8(%rbx), %rax", "movq %rax, %xmm8", "lea 9(%rbx), %rax", "movq %rax, %xmm9",
        "lea 10(%rbx), %rax", "movq %rax, %xmm10", "lea 11(%rbx), %rax", "movq %rax, %xmm11",
        "lea 12(%rbx), %rax", "movq %rax, %xmm12", "lea 13(%rbx), %rax", "movq %rax, %xmm13",
        "lea 14(%rbx), %rax", "movq %rax, %xmm14", "lea 15(%rbx), %rax", "movq %rax, %xmm15",
        "1: test %r12, %r12", "jz 2f",
        "mov $24, %eax", "syscall",
        "mov $2000, %ecx", "3: dec %ecx", "jnz 3b",
        "dec %r12", "jmp 1b",
        "2: xor %r8d, %r8d",
        "movq %xmm0, %rax", "lea 0(%rbx), %rdx", "cmp %rax, %rdx", "setne %cl", "movzbl %cl, %ecx", "add %rcx, %r8",
        "movq %xmm1, %rax", "lea 1(%rbx), %rdx", "cmp %rax, %rdx", "setne %cl", "movzbl %cl, %ecx", "add %rcx, %r8",
        "movq %xmm2, %rax", "lea 2(%rbx), %rdx", "cmp %rax, %rdx", "setne %cl", "movzbl %cl, %ecx", "add %rcx, %r8",
        "movq %xmm3, %rax", "lea 3(%rbx), %rdx", "cmp %rax, %rdx", "setne %cl", "movzbl %cl, %ecx", "add %rcx, %r8",
        "movq %xmm4, %rax", "lea 4(%rbx), %rdx", "cmp %rax, %rdx", "setne %cl", "movzbl %cl, %ecx", "add %rcx, %r8",
        "movq %xmm5, %rax", "lea 5(%rbx), %rdx", "cmp %rax, %rdx", "setne %cl", "movzbl %cl, %ecx", "add %rcx, %r8",
        "movq %xmm6, %rax", "lea 6(%rbx), %rdx", "cmp %rax, %rdx", "setne %cl", "movzbl %cl, %ecx", "add %rcx, %r8",
        "movq %xmm7, %rax", "lea 7(%rbx), %rdx", "cmp %rax, %rdx", "setne %cl", "movzbl %cl, %ecx", "add %rcx, %r8",
        "movq %xmm8, %rax", "lea 8(%rbx), %rdx", "cmp %rax, %rdx", "setne %cl", "movzbl %cl, %ecx", "add %rcx, %r8",
        "movq %xmm9, %rax", "lea 9(%rbx), %rdx", "cmp %rax, %rdx", "setne %cl", "movzbl %cl, %ecx", "add %rcx, %r8",
        "movq %xmm10, %rax", "lea 10(%rbx), %rdx", "cmp %rax, %rdx", "setne %cl", "movzbl %cl, %ecx", "add %rcx, %r8",
        "movq %xmm11, %rax", "lea 11(%rbx), %rdx", "cmp %rax, %rdx", "setne %cl", "movzbl %cl, %ecx", "add %rcx, %r8",
        "movq %xmm12, %rax", "lea 12(%rbx), %rdx", "cmp %rax, %rdx", "setne %cl", "movzbl %cl, %ecx", "add %rcx, %r8",
        "movq %xmm13, %rax", "lea 13(%rbx), %rdx", "cmp %rax, %rdx", "setne %cl", "movzbl %cl, %ecx", "add %rcx, %r8",
        "movq %xmm14, %rax", "lea 14(%rbx), %rdx", "cmp %rax, %rdx", "setne %cl", "movzbl %cl, %ecx", "add %rcx, %r8",
        "movq %xmm15, %rax", "lea 15(%rbx), %rdx", "cmp %rax, %rdx", "setne %cl", "movzbl %cl, %ecx", "add %rcx, %r8",
        "mov %r8, %rax", "pop %r12", "pop %rbx", "ret")

@naked
def user_nop():
    asm("ret")

@naked
def syscall3(n: u64, a: u64, b: u64, c: u64) -> u64:
    asm("mov %rdi,%rax", "mov %rsi,%rdi", "mov %rdx,%rsi", "mov %rcx,%rdx", "syscall", "ret")

@naked
def read_cs() -> u64:
    asm("mov %cs,%rax", "ret")

def sys_write(p: u64, n: u64) -> u64:
    return syscall3(SYS_WRITE, 1, p, n)

def sys_exit(code: u64):
    syscall3(SYS_EXIT, code, 0, 0)

def sys_yield():
    syscall3(SYS_YIELD, 0, 0, 0)

def sys_getpid() -> u64:
    return syscall3(SYS_GETPID, 0, 0, 0)

def sys_cpu() -> u64:
    return syscall3(SYS_CPU, 0, 0, 0)

# line buffer: build a line, then write it with one syscall so lines from different processes never interleave
lbuf: u8[256]
llen: u64

@unsafe
def out(s: ptr[u8]):
    i = 0
    while s[i] != 0 and llen < 255:
        lbuf[llen] = s[i]
        llen += 1
        i += 1

def out_u64(v: u64):
    d: u8[24]
    i = 23
    while 1:
        i -= 1
        d[i] = u8(48 + v % 10)
        v = v / 10
        if v == 0:
            break
    while i < 23 and llen < 255:
        lbuf[llen] = d[i]
        llen += 1
        i += 1

def flush():
    sys_write(u64(addr(lbuf[0])), llen)
    llen = 0

# ---- processes, memory, files ----
SYS_READ = 0
SYS_OPEN = 2
SYS_CLOSE = 3
SYS_STAT = 4
SYS_MMAP = 9
SYS_MUNMAP = 11
SYS_BRK = 12
SYS_FORK = 57
SYS_EXEC = 59
SYS_WAIT = 61
SYS_GETDENTS = 78
SYS_GETPPID = 110
SYS_GETARGS = 300

def sys_fork() -> u64:
    return syscall3(SYS_FORK, 0, 0, 0)

def sys_exec(path: ptr[u8], args: ptr[u8]) -> u64:
    return syscall3(SYS_EXEC, u64(path), u64(args), 0)

def sys_wait(pid: u64, status: ptr[u64]) -> u64:
    return syscall3(SYS_WAIT, pid, u64(status), 0)

def sys_brk(p: u64) -> u64:
    return syscall3(SYS_BRK, p, 0, 0)

def sys_mmap(len: u64) -> u64:
    return syscall3(SYS_MMAP, 0, len, 0)

def sys_munmap(p: u64, len: u64) -> u64:
    return syscall3(SYS_MUNMAP, p, len, 0)

def sys_open(path: ptr[u8]) -> u64:
    return syscall3(SYS_OPEN, u64(path), 0, 0)

def sys_close(fd: u64) -> u64:
    return syscall3(SYS_CLOSE, fd, 0, 0)

def sys_read(fd: u64, p: u64, n: u64) -> u64:
    return syscall3(SYS_READ, fd, p, n)

def sys_stat(path: ptr[u8], st: ptr[u64]) -> u64:
    return syscall3(SYS_STAT, u64(path), u64(st), 0)

def sys_getdents(fd: u64, p: u64, n: u64) -> u64:
    return syscall3(SYS_GETDENTS, fd, p, n)

def sys_getargs(p: u64, n: u64) -> u64:
    return syscall3(SYS_GETARGS, p, n, 0)

def is_err(v: u64) -> u64:
    return v > 0xFFFFFFFFFFFFF000

@unsafe
def streq(a: ptr[u8], b: ptr[u8]) -> u64:
    i = 0
    while a[i] == b[i]:
        if a[i] == 0:
            return 1
        i += 1
    return 0

def outc(c: u64):
    if llen < 255:
        lbuf[llen] = u8(c)
        llen += 1

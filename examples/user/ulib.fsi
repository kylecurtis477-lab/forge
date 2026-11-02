# Minimal user-space runtime for Forge programs running on the Forge kernel (ring 3, syscall ABI:
# rax = number, rdi/rsi/rdx = args, result in rax; rcx/r11 clobbered).
SYS_WRITE = 1
SYS_YIELD = 24
SYS_GETPID = 39
SYS_EXIT = 60
SYS_TICKS = 96
SYS_CPU = 97

@naked
def syscall3(n: u64, a: u64, b: u64, c: u64) -> u64:
    asm("mov %rdi,%rax", "mov %rsi,%rdi", "mov %rdx,%rsi", "mov %rcx,%rdx", "syscall", "ret")

@naked
def read_cs() -> u64:
    asm("mov %cs,%rax", "ret")

def sys_write(p: u64, n: u64) -> u64:
    return syscall3(SYS_WRITE, p, n, 0)

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

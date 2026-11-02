# Minimal Linux user-mode harness for freestanding tests: _start, write/exit syscalls, number printing.
SYS_WRITE = 1
SYS_EXIT = 60

@naked
@export
def _start():
    asm("xor %rbp,%rbp", "and $-16,%rsp", "call main", "mov %rax,%rdi", "mov $60,%eax", "syscall")

def sys3(n: u64, a: u64, b: u64, c: u64) -> i64:
    asm("mov %rdi,%rax", "mov %rsi,%rdi", "mov %rdx,%rsi", "mov %rcx,%rdx", "syscall", "leave", "ret")
    return 0

def strlen(s: ptr[u8]) -> u64:
    n: u64 = 0
    while s[n] != 0:
        n += 1
    return n

def puts(s: ptr[u8]):
    sys3(SYS_WRITE, 1, u64(s), strlen(s))

def put_u64(v: u64):
    buf: u8[24]
    i = 23
    buf[i] = 0
    while 1:
        i -= 1
        buf[i] = u8(48 + v % 10)
        v = v / 10
        if v == 0:
            break
    puts(addr(buf[i]))

def put_i64(v: i64):
    if v < 0:
        puts("-")
        put_u64(u64(0 - v))
    else:
        put_u64(u64(v))

def nl():
    puts("\n")

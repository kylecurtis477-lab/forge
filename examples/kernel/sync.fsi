# Spinlocks on the language's atomics, interrupt-flag save/restore, per-CPU syscall area.
heap_lock: u64
pmm_lock: u64
# per CPU: [0] kernel stack top of the running thread, [1] user rsp scratch (syscall entry), [2] cpu index
percpu_kstack: u64[32]

@naked
def read_rflags() -> u64:
    asm("pushfq", "pop %rax", "ret")


@unsafe
def spin_lock(l: u64):
    while atomic_xchg64(l, 1) != 0:
        while atomic_load64(l) != 0:
            tlb_poll()
            cpu_relax()

@unsafe
def spin_unlock(l: u64):
    atomic_store64(l, 0)

@unsafe
def irq_save() -> u64:
    f = read_rflags()
    cli()
    return f

@unsafe
def irq_restore(f: u64):
    if f & 0x200:
        sti()

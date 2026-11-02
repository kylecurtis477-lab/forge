# Spinlocks on the language's typed atomics (Phase 4.6), interrupt-flag save/restore, per-CPU syscall area.
heap_lock: u64
pmm_lock: u64
# per CPU: [0] kernel stack top of the running thread, [1] user rsp scratch (syscall entry), [2] cpu index
percpu_kstack: u64[32]

@naked
def read_rflags() -> u64:
    asm("pushfq", "pop %rax", "ret")


def spin_lock(l: ptr[u64]):
# test-and-test-and-set; the swap is a full barrier (acquire for the critical section)
    while atomic_swap(l, 1, acquire) != 0:
        while atomic_load(l, relaxed) != 0:
            tlb_poll()
            cpu_relax()

def spin_unlock(l: ptr[u64]):
    atomic_store(l, 0, release)

@unsafe
def irq_save() -> u64:
    f = read_rflags()
    cli()
    return f

@unsafe
def irq_restore(f: u64):
    if f & 0x200:
        sti()

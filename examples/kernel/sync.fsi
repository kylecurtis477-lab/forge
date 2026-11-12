# Spinlocks on the language's typed atomics (Phase 4.6), interrupt-flag save/restore, per-CPU syscall area.
heap_lock: u64
pmm_lock: u64
# per CPU: [0] kernel stack top of the running thread, [1] user rsp scratch (syscall entry), [2] cpu index
percpu_kstack: u64[32]

@naked
def read_rflags() -> u64:
    asm("pushfq", "pop %rax", "ret")


# Fail-fast lost-lock check: a CPU that waits longer than SPIN_LIMIT_MS (TSC-timed, once calibrated) for one
# spinlock records the lock in spin_wait[cpu] and the first one to time out prints every CPU's wait, the lock
# word, the kernel slide (for symbolizing) and whether lapic_ticks still advance, then panics, instead of the
# boot hanging until the harness kills it.
SPIN_LIMIT_MS = 10000
spin_wait: u64[32]
spin_reported: u64

def spin_lock(l: ptr[u64]):
# test-and-test-and-set; the swap is a full barrier (acquire for the critical section)
    while atomic_swap(l, 1, acquire) != 0:
        t0 = rdtsc()
        while atomic_load(l, relaxed) != 0:
            tlb_poll()
            cpu_relax()
            if tsc_per_ms != 0 and rdtsc() - t0 > tsc_per_ms * SPIN_LIMIT_MS:
                spin_stuck(l, t0)

def stall_report(what: ptr[u8], p: ptr[u64], ms: u64, tk: u64):
# shared by the lost-lock and stalled-tick checks: one reporter, the others record their wait and park
    a = u64(p)
    c = lapic_cpu()
    tsc_delay_us(300000)  # let the other stuck CPUs record theirs
    if atomic_swap(addr(spin_reported), 1, acq_rel) != 0:
        while 1:
            cpu_relax()
    puts("PANIC-STALL: ")
    puts(what)
    puts(" on cpu ")
    put_u64(c)
    puts(" after ")
    put_u64(ms)
    puts(" ms: object 0x")
    put_hexn(a, 16)
    puts(" (link 0x")
    put_hexn(a - kslide, 16)
    puts(", word ")
    put_u64(atomic_load(p, relaxed))
    puts("), lapic_ticks ")
    put_u64(tk)
    puts(" -> ")
    put_u64(atomic_load(addr(lapic_ticks), relaxed))
    puts(" over 300 ms, tlb_active ")
    put_u64(atomic_load(addr(tlb_active), relaxed))
    puts(", waits:")
    i: u64 = 0
    while i < ncpus and i < 32:
        puts(" cpu")
        put_u64(i)
        putc(61)
        w = atomic_load(addr(spin_wait[i]), relaxed)
        if w == 0:
            putc(48)
        else:
            puts("0x")
            put_hexn(w - kslide, 16)
        i += 1
    putc(10)
    panic("lost spinlock or stalled timer (see PANIC-STALL line)")

def spin_stuck(l: ptr[u64], t0: u64):
    atomic_store(addr(spin_wait[lapic_cpu()]), u64(l), release)
    stall_report("spinlock wait", l, (rdtsc() - t0) / tsc_per_ms, atomic_load(addr(lapic_ticks), relaxed))

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

# Nondeterminism hook (plan v1.3 item 6): the single point every nondeterministic input of the kernel
# passes through -- syscall results (syscall_dispatch), timer interrupts (on_lapic_timer), scheduler choices
# (schedule_l) and random values (nd_rand: KASLR slide, stack canary, Linux AT_RANDOM, the object-store
# key entropy). nd_hook(kind, a, v) returns v unchanged; it is where a later replay mode can substitute a
# recorded value. Recording is OFF by default (nd_rec = 0): the hook then only reads one global and returns.
# With recording on, each event is counted per kind and appended to a fixed ring of ND_RING entries
# (kind, a, v; the slot index is taken atomically, so CPUs never share a slot). Nothing is persisted.
# Self-test: nd_selftest() (boot, kernel.forge) checks that nothing is recorded while off, then records a
# window and requires events of all four kinds.
ND_SYSCALL = 1  # a = syscall number, v = result
ND_TIMER = 2  # a = cpu, v = lapic_ticks
ND_SCHED = 3  # a = cpu, v = the picked thread
ND_RNG = 4  # a = call site id, v = the random value
ND_RING = 256
nd_rec: u64  # 0 = off (default), 1 = recording
nd_seq: u64  # events recorded so far (also the next ring slot, mod ND_RING)
nd_cnt: u64[5]  # per kind
nd_ring: u64[768]  # ND_RING x (kind, a, v)

def nd_hook(kind: u64, a: u64, v: u64) -> u64:
    if nd_rec == 0:
        return v
    s = atomic_fetch_add(addr(nd_seq), 1, acq_rel) % ND_RING
    k = kind
    if k > 4:
        k = 0
    atomic_fetch_add(addr(nd_cnt[k]), 1, acq_rel)
    nd_ring[s * 3] = kind
    nd_ring[s * 3 + 1] = a
    nd_ring[s * 3 + 2] = v
    return v

def nd_rand(site: u64, v: u64) -> u64:
# a random value computed by the caller (from rdtsc-derived entropy), passed through the hook unchanged
    return nd_hook(ND_RNG, site, v)

def nd_reset():
    nd_seq = 0
    i = 0
    while i < 5:
        nd_cnt[i] = 0
        i += 1

def nd_ring_ok() -> u64:
# every recorded slot holds a known kind
    n = nd_seq
    if n > ND_RING:
        n = ND_RING
    i: u64 = 0
    while i < n:
        if nd_ring[i * 3] < ND_SYSCALL or nd_ring[i * 3] > ND_RNG:
            return 0
        i += 1
    return 1

def nd_window_begin():
# boot self-test: everything before this ran with recording off, so nothing may have been recorded
    if nd_rec != 0 or nd_seq != 0:
        panic("nondet: recorded with recording off")
    puts("nondet: recording off by default (0 events recorded since boot)\n")
    nd_rec = 1

def nd_window_end():
    nd_rec = 0
    puts("nondet: shell session recorded ")
    put_u64(nd_seq)
    puts(" events: ")
    put_u64(nd_cnt[ND_SYSCALL])
    puts(" syscall results, ")
    put_u64(nd_cnt[ND_TIMER])
    puts(" timer interrupts, ")
    put_u64(nd_cnt[ND_SCHED])
    puts(" scheduler choices, ")
    put_u64(nd_cnt[ND_RNG])
    puts(" random values\n")
    if nd_cnt[ND_SYSCALL] == 0 or nd_cnt[ND_TIMER] == 0 or nd_cnt[ND_SCHED] == 0 or nd_cnt[ND_RNG] == 0:
        panic("nondet: a hook point recorded nothing")
    if nd_cnt[0] != 0 or nd_ring_ok() == 0:
        panic("nondet: bad event in the ring")
    s0 = nd_seq
    nd_hook(ND_RNG, 9, 1)
    if nd_seq != s0:
        panic("nondet: recorded after recording was switched off")
    nd_reset()
    puts("nondet hook OK\n")

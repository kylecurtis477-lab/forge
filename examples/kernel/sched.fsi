# Preemptive kernel threads: spinlocks (atomics), one global run queue shared by all CPUs, per-CPU current and
# idle threads, round-robin on the 100 Hz LAPIC timer. The run-queue lock is held across the stack switch and
# released by whichever thread resumes, so a thread can never be picked up by another CPU half-saved.
T_READY = 0
T_RUNNING = 1
T_DEAD = 2
T_IDLE = 3
T_BLOCKED = 4
KTHREAD_STACK = 16384
KTHREAD_PAGES = 4

struct Thread:
    rsp: u64
    stack: u64
    kstack_top: u64
    state: u64
    id: u64
    next: u64
    cr3: u64
    cpus_seen: u64
    switches: u64
    iters: u64
    user: u64
    exit_code: u64
    proc: u64
    fpu: u64
    pin: u64
    wake_at: u64
    sc: u64[5]  # the syscall in progress (nr, a, b, c, d): a kexec handoff re-issues it
    kx_parked: u64

asm(
    ".text",
    # ctx_switch(save_rsp_slot, new_rsp): callee-saved registers live on each thread's stack
    ".globl ctx_switch",
    "ctx_switch:",
    "push %rbp", "push %rbx", "push %r12", "push %r13", "push %r14", "push %r15",
    "mov %rsp, (%rdi)",
    "mov %rsi, %rsp",
    "pop %r15", "pop %r14", "pop %r13", "pop %r12", "pop %rbx", "pop %rbp",
    "ret",
    # first switch into a new thread lands here with r12 = fn, r13 = arg
    "thread_start:",
    "call sched_tail",
    "mov %r13, %rdi",
    "call *%r12",
    "call thread_exit",
    "ud2",
    ".globl thread_start_addr",
    "thread_start_addr: lea thread_start(%rip), %rax",
    "ret"
)
extern def ctx_switch(save: u64, rsp: u64)
extern def thread_start_addr() -> u64

rq_lock: u64
rq_head: u64
rq_tail: u64
current: u64[8]
idle_thr: u64[8]
next_tid: u64
ctx_switches: u64

@unsafe
def rq_push(t: ptr[Thread]):
    t.next = 0
    if rq_tail == 0:
        rq_head = u64(t)
    else:
        ptr[Thread](rq_tail).next = u64(t)
    rq_tail = u64(t)

@unsafe
def rq_pop(cpu: u64) -> u64:
# first runnable thread allowed on this CPU (pin = 0: anywhere, else cpu + 1)
    prev: u64 = 0
    t = rq_head
    while t != 0:
        p = ptr[Thread](t).pin
        if p == 0 or p == cpu + 1:
            nx = ptr[Thread](t).next
            if prev == 0:
                rq_head = nx
            else:
                ptr[Thread](prev).next = nx
            if rq_tail == t:
                rq_tail = prev
            return t
        prev = t
        t = ptr[Thread](t).next
    return 0

@unsafe
def cur_thread() -> ptr[Thread]:
# irqs off across the CPU-id read and the current[] load: a preemption (and migration)
# in between would return another CPU's thread (seen as cur_proc()==0 under chantest)
    f = irq_save()
    t = current[lapic_cpu()]
    irq_restore(f)
    return ptr[Thread](t)

@unsafe
def thread_new(fn: u64, arg: u64) -> ptr[Thread]:
    t = ptr[Thread](kmalloc(sizeof(Thread)))
    memset(u64(t), 0, sizeof(Thread))
    t.stack = kstack_alloc(KTHREAD_PAGES)
    t.kstack_top = t.stack + KTHREAD_STACK
    t.id = atomic_fetch_add(addr(next_tid), 1, acq_rel) + 1
    t.cr3 = kernel_pml4
    t.fpu = fpu_area_new()
    top = t.kstack_top
    sp = top - 72
    vstore64(sp, 0)
    vstore64(sp + 8, 0)
    vstore64(sp + 16, arg)
    vstore64(sp + 24, fn)
    vstore64(sp + 32, 0)
    vstore64(sp + 40, 0)
    vstore64(sp + 48, thread_start_addr())
    vstore64(sp + 56, 0)
    vstore64(sp + 64, 0)
    t.rsp = sp
    t.state = T_READY
    return t

def thread_start_ready(t: ptr[Thread]):
    f = irq_save()
    spin_lock(addr(rq_lock))
    rq_push(t)
    spin_unlock(addr(rq_lock))
    irq_restore(f)

def thread_spawn(fn: u64, arg: u64) -> ptr[Thread]:
    t = thread_new(fn, arg)
    thread_start_ready(t)
    return t

def thread_free(t: ptr[Thread]):
# only for T_DEAD threads: the lock round-trip guarantees the CPU that switched away from it is done
    f = irq_save()
    spin_lock(addr(rq_lock))
    st = t.state
    spin_unlock(addr(rq_lock))
    irq_restore(f)
    if st != T_DEAD:
        panic("thread_free: thread still alive")
    kstack_free(t.stack, KTHREAD_PAGES)
    fpu_area_free(t.fpu)
    kfree(u64(t))

@export
@unsafe
def sched_tail():
    spin_unlock(addr(rq_lock))
    sti()

def schedule(exiting: u64):
# interrupts are off (timer handler, yield, exit)
    spin_lock(addr(rq_lock))
    schedule_l(exiting)

@unsafe
def schedule_l(exiting: u64):
# rq_lock held, interrupts off; returns with rq_lock released. A T_BLOCKED caller is parked on a
# wait queue already and is not put back on the run queue.
    cpu = lapic_cpu()
    cur = ptr[Thread](current[cpu])
    if exiting:
        cur.state = T_DEAD
    nxt = ptr[Thread](rq_pop(cpu))
    if u64(nxt) == 0:
        if cur.state == T_RUNNING or cur.state == T_IDLE:
            spin_unlock(addr(rq_lock))
            return
        nxt = ptr[Thread](idle_thr[cpu])
    if cur.state == T_RUNNING:
        cur.state = T_READY
        rq_push(cur)
    if nxt.state != T_IDLE:
        nxt.state = T_RUNNING
    nxt.cpus_seen = nxt.cpus_seen | (u64(1) << cpu)
    nxt.switches += 1
    ctx_switches += 1
    current[cpu] = u64(nxt)
    tss[cpu].rsp0 = nxt.kstack_top
    percpu_kstack[cpu * 4] = nxt.kstack_top
    if nxt.cr3 != cur.cr3:
        switch_cr3(cpu, nxt.cr3)
    fpu_save(cur.fpu)
    fpu_restore(nxt.fpu)
    ctx_switch(u64(addr(cur.rsp)), nxt.rsp)
    spin_unlock(addr(rq_lock))

def yield_cpu():
    f = irq_save()
    schedule(0)
    irq_restore(f)

@export
@unsafe
def thread_exit():
    cli()
    schedule(1)
    panic("dead thread resumed")

def thread_guard_hit(cr2: u64) -> u64:
# called from #DF: if cr2 is in the current thread's guard page, return the rsp to restart it on
    if u64(cur_thread()) == 0:
        return 0
    t = cur_thread()
    if t.stack == 0 or cr2 < t.stack - PAGE or cr2 >= t.stack:
        return 0
    kstack_guard_faults += 1
    return t.kstack_top - 8

@export
def thread_overflow_exit():
# a kernel thread ran into its guard page: #DF moved it here, onto the top of its own stack
    puts("kernel thread ")
    put_u64(cur_thread().id)
    puts(" overflowed its stack into the guard page: killed, kernel carries on\n")
    thread_exit()

@unsafe
def idle_loop(arg: u64):
    while 1:
        sti()
        hlt()

@unsafe
def sched_cpu_init(cpu: u64, boot_ctx_is_idle: u64):
# the code running now becomes this CPU's boot thread: the idle thread on APs, `main` on the BSP
    t = ptr[Thread](kmalloc(sizeof(Thread)))
    memset(u64(t), 0, sizeof(Thread))
    t.cr3 = kernel_pml4
    t.fpu = fpu_area_new()
    t.cpus_seen = u64(1) << cpu
    if boot_ctx_is_idle:
        t.state = T_IDLE
        t.kstack_top = tss[cpu].rsp0
        idle_thr[cpu] = u64(t)
    else:
        t.state = T_RUNNING
        t.kstack_top = tss[cpu].rsp0
        t.id = atomic_fetch_add(addr(next_tid), 1, acq_rel) + 1
        it = thread_new(u64(idle_loop), 0)
        it.state = T_IDLE
        idle_thr[cpu] = u64(it)
    current[cpu] = u64(t)
    percpu_kstack[cpu * 4] = t.kstack_top

# ---- wait queues, sleeping mutexes, condition variables, timed sleep ----
# All wait-queue state is protected by rq_lock, so "check condition, enqueue, block" and "wake" can never
# interleave: no lost wakeups. Blocked threads use Thread.next as their queue link.
struct WaitQ:
    head: u64
    tail: u64

struct Mutex:
    locked: u64
    owner: u64
    q: WaitQ
    contended: u64

struct CondVar:
    q: WaitQ

sleepq: WaitQ
wq_blocks: u64
wq_wakes: u64

@unsafe
def wq_push(q: ptr[WaitQ], t: ptr[Thread]):
    t.next = 0
    if q.tail == 0:
        q.head = u64(t)
    else:
        ptr[Thread](q.tail).next = u64(t)
    q.tail = u64(t)

@unsafe
def wq_pop(q: ptr[WaitQ]) -> u64:
    t = q.head
    if t != 0:
        q.head = ptr[Thread](t).next
        if q.head == 0:
            q.tail = 0
    return t

@unsafe
def wq_block_l(q: ptr[WaitQ]):
# rq_lock held, irqs off: park the current thread on q and run something else; returns with rq_lock re-held
    cur = ptr[Thread](current[lapic_cpu()])
    cur.state = T_BLOCKED
    wq_push(q, cur)
    wq_blocks += 1
    schedule_l(0)
    spin_lock(addr(rq_lock))

def wake_l(t: ptr[Thread]):
    t.state = T_READY
    rq_push(t)
    wq_wakes += 1

@unsafe
def wq_wake_one_l(q: ptr[WaitQ]) -> u64:
    t = wq_pop(q)
    if t != 0:
        wake_l(ptr[Thread](t))
        return 1
    return 0

def wq_wake_all_l(q: ptr[WaitQ]) -> u64:
    n: u64 = 0
    while wq_wake_one_l(q):
        n += 1
    return n

def wq_wait(q: ptr[WaitQ]):
    f = irq_save()
    spin_lock(addr(rq_lock))
    wq_block_l(q)
    spin_unlock(addr(rq_lock))
    irq_restore(f)

def wq_wake_all(q: ptr[WaitQ]) -> u64:
    f = irq_save()
    spin_lock(addr(rq_lock))
    n = wq_wake_all_l(q)
    spin_unlock(addr(rq_lock))
    irq_restore(f)
    return n

def mutex_lock(m: ptr[Mutex]):
    f = irq_save()
    spin_lock(addr(rq_lock))
    cur = current[lapic_cpu()]
    if m.locked and m.owner == cur:
        panic("mutex_lock: recursive lock")
    while m.locked:
        m.contended += 1
        wq_block_l(addr(m.q))
    m.locked = 1
    m.owner = current[lapic_cpu()]
    spin_unlock(addr(rq_lock))
    irq_restore(f)

def mutex_unlock_l(m: ptr[Mutex]):
    if m.locked == 0 or m.owner != current[lapic_cpu()]:
        panic("mutex_unlock: not owner")
    m.locked = 0
    m.owner = 0
    wq_wake_one_l(addr(m.q))

def mutex_unlock(m: ptr[Mutex]):
    f = irq_save()
    spin_lock(addr(rq_lock))
    mutex_unlock_l(m)
    spin_unlock(addr(rq_lock))
    irq_restore(f)

def cond_wait(c: ptr[CondVar], m: ptr[Mutex]):
# atomically release m and sleep on c, then re-acquire m
    f = irq_save()
    spin_lock(addr(rq_lock))
    mutex_unlock_l(m)
    wq_block_l(addr(c.q))
    spin_unlock(addr(rq_lock))
    irq_restore(f)
    mutex_lock(m)

def cond_signal(c: ptr[CondVar]):
    f = irq_save()
    spin_lock(addr(rq_lock))
    wq_wake_one_l(addr(c.q))
    spin_unlock(addr(rq_lock))
    irq_restore(f)

def cond_broadcast(c: ptr[CondVar]):
    wq_wake_all(addr(c.q))

@unsafe
def sleep_ticks(n: u64):
    f = irq_save()
    spin_lock(addr(rq_lock))
    cur = ptr[Thread](current[lapic_cpu()])
    cur.wake_at = lapic_ticks + n
    wq_block_l(addr(sleepq))
    spin_unlock(addr(rq_lock))
    irq_restore(f)

@unsafe
def sleep_tick_l():
# cpu 0 timer, rq_lock held: wake every sleeper whose deadline passed
    prev: u64 = 0
    t = sleepq.head
    while t != 0:
        nx = ptr[Thread](t).next
        if ptr[Thread](t).wake_at <= lapic_ticks:
            if prev == 0:
                sleepq.head = nx
            else:
                ptr[Thread](prev).next = nx
            if sleepq.tail == t:
                sleepq.tail = prev
            wake_l(ptr[Thread](t))
        else:
            prev = t
        t = nx

def thread_reap(t: ptr[Thread]):
    while atomic_load(addr(t.state), acquire) != T_DEAD:
        yield_cpu()
    thread_free(t)

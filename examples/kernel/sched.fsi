# Preemptive kernel threads: spinlocks (atomics), one global run queue shared by all CPUs, per-CPU current and
# idle threads, round-robin on the 100 Hz LAPIC timer. The run-queue lock is held across the stack switch and
# released by whichever thread resumes, so a thread can never be picked up by another CPU half-saved.
T_READY = 0
T_RUNNING = 1
T_DEAD = 2
T_IDLE = 3
KTHREAD_STACK = 16384

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

def rq_push(t: ptr[Thread]):
    t.next = 0
    if rq_tail == 0:
        rq_head = u64(t)
    else:
        ptr[Thread](rq_tail).next = u64(t)
    rq_tail = u64(t)

def rq_pop() -> u64:
    t = rq_head
    if t != 0:
        rq_head = ptr[Thread](t).next
        if rq_head == 0:
            rq_tail = 0
    return t

def cur_thread() -> ptr[Thread]:
    return ptr[Thread](current[lapic_cpu()])

def thread_new(fn: u64, arg: u64) -> ptr[Thread]:
    t = ptr[Thread](kmalloc(sizeof(Thread)))
    memset(u64(t), 0, sizeof(Thread))
    t.stack = kmalloc(KTHREAD_STACK)
    t.kstack_top = (t.stack + KTHREAD_STACK) & ~u64(15)
    t.id = atomic_add64(u64(addr(next_tid)), 1) + 1
    t.cr3 = kernel_pml4
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
    spin_lock(u64(addr(rq_lock)))
    rq_push(t)
    spin_unlock(u64(addr(rq_lock)))
    irq_restore(f)

def thread_spawn(fn: u64, arg: u64) -> ptr[Thread]:
    t = thread_new(fn, arg)
    thread_start_ready(t)
    return t

def thread_free(t: ptr[Thread]):
    # only for T_DEAD threads: the lock round-trip guarantees the CPU that switched away from it is done
    f = irq_save()
    spin_lock(u64(addr(rq_lock)))
    st = t.state
    spin_unlock(u64(addr(rq_lock)))
    irq_restore(f)
    if st != T_DEAD:
        panic("thread_free: thread still alive")
    kfree(t.stack)
    kfree(u64(t))

@export
def sched_tail():
    spin_unlock(u64(addr(rq_lock)))
    sti()

def schedule(exiting: u64):
    # interrupts are off (timer handler, yield, exit)
    cpu = lapic_cpu()
    cur = ptr[Thread](current[cpu])
    spin_lock(u64(addr(rq_lock)))
    if exiting:
        cur.state = T_DEAD
    nxt = ptr[Thread](rq_pop())
    if u64(nxt) == 0:
        if cur.state == T_RUNNING or cur.state == T_IDLE:
            spin_unlock(u64(addr(rq_lock)))
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
        write_cr3(nxt.cr3)
    ctx_switch(u64(addr(cur.rsp)), nxt.rsp)
    spin_unlock(u64(addr(rq_lock)))

def yield_cpu():
    f = irq_save()
    schedule(0)
    irq_restore(f)

@export
def thread_exit():
    cli()
    schedule(1)
    panic("dead thread resumed")

def idle_loop(arg: u64):
    while 1:
        sti()
        hlt()

def sched_cpu_init(cpu: u64, boot_ctx_is_idle: u64):
    # the code running now becomes this CPU's boot thread: the idle thread on APs, `main` on the BSP
    t = ptr[Thread](kmalloc(sizeof(Thread)))
    memset(u64(t), 0, sizeof(Thread))
    t.cr3 = kernel_pml4
    t.cpus_seen = u64(1) << cpu
    if boot_ctx_is_idle:
        t.state = T_IDLE
        t.kstack_top = tss[cpu].rsp0
        idle_thr[cpu] = u64(t)
    else:
        t.state = T_RUNNING
        t.kstack_top = tss[cpu].rsp0
        t.id = atomic_add64(u64(addr(next_tid)), 1) + 1
        it = thread_new(u64(idle_loop), 0)
        it.state = T_IDLE
        idle_thr[cpu] = u64(it)
    current[cpu] = u64(t)
    percpu_kstack[cpu * 4] = t.kstack_top

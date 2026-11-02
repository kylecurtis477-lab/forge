# User mode: placeholder until syscall support lands.
def cpu_syscall_init(cpu: u64):
    percpu_kstack[cpu * 4 + 2] = cpu

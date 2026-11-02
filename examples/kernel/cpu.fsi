# GDT with a 64-bit TSS (IST1 = #DF stack, IST2 = NMI/#MC stack), IDT, exception handlers.
@packed
struct GdtDesc:
    limit: u16
    base: u64

@packed
struct IdtEntry:
    off_lo: u16
    sel: u16
    ist: u8
    attr: u8
    off_mid: u16
    off_hi: u32
    zero: u32

@packed
struct Tss:
    r0: u32
    rsp0: u64
    rsp1: u64
    rsp2: u64
    r1: u64
    ist: u64[7]
    r2: u64
    r3: u16
    iomap: u16

SEL_KCODE = 0x08
SEL_KDATA = 0x10
SEL_TSS = 0x28

MAX_CPUS = 8
# GDT: null, kcode 0x08, kdata 0x10, udata 0x18 (DPL3), ucode 0x20 (DPL3), then one 16-byte TSS descriptor per CPU
gdt: u64[21]
gdtr: GdtDesc
tss: Tss[8]
idt: IdtEntry[256]
idtr: GdtDesc
bp_count: u64
df_rsp: u64
df_recover: u64

@unsafe
def tss_install(cpu: u64, kstack: u64, ist1: u64, ist2: u64):
    t = addr(tss[cpu])
    memset(u64(t), 0, sizeof(Tss))
    t.ist[0] = ist1
    t.ist[1] = ist2
    t.rsp0 = kstack
    t.iomap = u16(sizeof(Tss))
    base = u64(t)
    lim = sizeof(Tss) - 1
    gdt[5 + 2 * cpu] = (lim & 0xFFFF) | ((base & 0xFFFFFF) << 16) | (u64(0x89) << 40) | (((lim >> 16) & 0xF) << 48) | (((base >> 24) & 0xFF) << 56)
    gdt[6 + 2 * cpu] = base >> 32

@unsafe
def gdt_load(cpu: u64):
    lgdt(addr(gdtr))
    asm("push $8", "lea 1f(%rip),%rax", "push %rax", "lretq", "1:", "mov $16,%ax", "mov %ax,%ds", "mov %ax,%es", "mov %ax,%ss", "xor %eax,%eax", "mov %ax,%fs", "mov %ax,%gs")
    ltr(u16(SEL_TSS + 16 * cpu))

def gdt_init():
    gdt[0] = 0
    gdt[1] = 0x00AF9A000000FFFF
    gdt[2] = 0x00CF92000000FFFF
    gdt[3] = 0x00CFF2000000FFFF
    gdt[4] = 0x00AFFA000000FFFF
    tss_install(0, KSTACK_TOP, IST1_TOP, IST2_TOP)
    gdtr.limit = u16(sizeof(u64[21]) - 1)
    gdtr.base = u64(addr(gdt))
    gdt_load(0)

def idt_set(n: u32, handler: u64, ist: u8):
    e = addr(idt[n])
    e.off_lo = u16(handler)
    e.sel = SEL_KCODE
    e.ist = ist
    e.attr = 0x8E
    e.off_mid = u16(handler >> 16)
    e.off_hi = u32(handler >> 32)
    e.zero = 0

@unsafe
def fault(name: ptr[u8], frame: ptr[u64], has_err: u64):
    if (frame[1 + has_err] & 3) == 3:
        user_kill(name, 0, frame[has_err], 0)
    puts("\nEXCEPTION ")
    puts(name)
    if has_err:
        puts(" err=")
        put_hex(frame[0])
        frame = frame + 1
    puts(" rip=")
    put_hex(frame[0])
    puts(" rsp=")
    put_hex(frame[3])
    putc(10)
    panic("unhandled exception")

@interrupt
def on_de(frame: ptr[u64]):
    fault("#DE", frame, 0)

@interrupt
def on_ud(frame: ptr[u64]):
    fault("#UD", frame, 0)

@interrupt
def on_nmi(frame: ptr[u64]):
    fault("NMI", frame, 0)

@interrupt
def on_mc(frame: ptr[u64]):
    fault("#MC", frame, 0)

@interrupt_err
def on_gp(frame: ptr[u64]):
    fault("#GP", frame, 1)

@interrupt
def on_breakpoint(frame: ptr[u64]):
    bp_count += 1

@interrupt_err
@unsafe
def on_page_fault(frame: ptr[u64]):
    cr2 = read_cr2()
    if frame[0] & 4:
        user_kill("#PF", 14, frame[1], cr2)
    if uaccess_fixup(frame, frame[0], cr2):
        return
    if (frame[0] & 1) == 0 and demand_fault(cr2):
        return
    puts("\n#PF cr2=")
    put_hex(cr2)
    fault("#PF", frame, 1)

@interrupt_err
@unsafe
def on_double_fault(frame: ptr[u64]):
# runs on IST1 even though the faulting stack is gone; frame = [err, rip, cs, rflags, rsp, ss]
    df_rsp = u64(frame)
    if df_recover == 0:
        fault("#DF", frame, 1)
    puts("#DF on IST1: handler frame at ")
    put_hex(df_rsp)
    puts(", faulting rsp=")
    put_hex(frame[4])
    putc(10)
    # resume at the recovery continuation on a fresh kernel stack (iretq to it)
    frame[1] = df_recover
    frame[4] = KSTACK_TOP - 8
    df_recover = 0

@unsafe
def idt_init():
    idt_set(0, u64(on_de), 0)
    idt_set(2, u64(on_nmi), 2)
    idt_set(3, u64(on_breakpoint), 0)
    idt_set(6, u64(on_ud), 0)
    idt_set(8, u64(on_double_fault), 1)
    idt_set(13, u64(on_gp), 0)
    idt_set(14, u64(on_page_fault), 0)
    idt_set(18, u64(on_mc), 2)
    idtr.limit = u16(sizeof(IdtEntry) * 256 - 1)
    idtr.base = u64(addr(idt))
    lidt(addr(idtr))

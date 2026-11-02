# Physical frame allocator (bitmap), 4-level paging (HHDM + per-section kernel mappings + guarded stacks),
# demand paging region, and the kernel heap (slab size classes + page-backed large blocks).
PAGE = 4096
KBASE = 0xFFFFFFFF80000000
HHDM = 0xFFFF800000000000
DEMAND_BASE = 0xFFFF900000000000
DEMAND_SIZE = 0x1000000
HEAP_BIG_BASE = 0xFFFFC00000000000
KSTACK_TOP = 0xFFFFFFFFC0040000
KSTACK_PAGES = 16
IST1_TOP = 0xFFFFFFFFC0080000
IST2_TOP = 0xFFFFFFFFC00C0000
IST_PAGES = 4
PTE_P = 1
PTE_W = 2
PTE_PWT = 8
PTE_PCD = 0x10
PTE_PS = 0x80
PTE_G = 0x100
PTE_NX = 0x8000000000000000
ADDR_MASK = 0x000FFFFFFFFFF000
MAX_FRAMES = 4194304

asm(
    ".section .rodata",
    ".p2align 3",
    "ksyms: .quad __text_start, __text_end, __rodata_start, __rodata_end, __data_start, __data_end, __kernel_phys_start, __kernel_phys_end",
    ".text"
)

@naked
def ksym(i: u64) -> u64:
    asm("lea ksyms(%rip),%rax", "mov (%rax,%rdi,8),%rax", "ret")

frame_bits: u64[65536]
frames_total: u64
frames_free: u64
frame_hint: u64
frame_limit: u64
phys_top: u64
kdelta: u64
p2v_base: u64
kernel_pml4: u64
demand_faults: u64

def p2v(pa: u64) -> u64:
    return pa + p2v_base

def kv2p(va: u64) -> u64:
    return va - KBASE + kdelta

def frame_set(f: u64):
    frame_bits[f >> 6] = frame_bits[f >> 6] | (u64(1) << (f & 63))

def frame_clr(f: u64):
    frame_bits[f >> 6] = frame_bits[f >> 6] & ~(u64(1) << (f & 63))

def frame_used(f: u64) -> u64:
    return (frame_bits[f >> 6] >> (f & 63)) & 1

def reserve_range(base: u64, len: u64):
    f = base / PAGE
    end = (base + len + PAGE - 1) / PAGE
    while f < end and f < MAX_FRAMES:
        if frame_used(f) == 0:
            frame_set(f)
            frames_free -= 1
        f += 1

def pmm_init(bi: ptr[BootInfo]):
    memset(u64(addr(frame_bits[0])), 0xFF, sizeof(u64[65536]))
    frames_free = 0
    frames_total = 0
    phys_top = 0
    i = 0
    while i < bi.nregions:
        r = addr(bi.regions[i])
        top = r.base + r.len
        if r.typ != MEM_RESERVED and r.typ != MEM_BAD and top > phys_top:
            phys_top = top
        if r.typ == MEM_USABLE:
            f = (r.base + PAGE - 1) / PAGE
            end = top / PAGE
            while f < end and f < MAX_FRAMES:
                if frame_used(f):
                    frame_clr(f)
                    frames_free += 1
                f += 1
        i += 1
    frames_total = frames_free
    reserve_range(0, 0x100000)
    reserve_range(ksym(6) + kdelta, ksym(7) - ksym(6))
    if bi.info_len != 0:
        reserve_range(bi.info_base, bi.info_len)
    frame_hint = 256
    frame_limit = 0x40000000 / PAGE

def frame_alloc() -> u64:
    w = frame_hint >> 6
    lim = frame_limit >> 6
    while w < lim:
        if frame_bits[w] != 0xFFFFFFFFFFFFFFFF:
            b = 0
            while (frame_bits[w] >> b) & 1:
                b += 1
            f = w * 64 + b
            frame_set(f)
            frames_free -= 1
            frame_hint = f
            pa = f * PAGE
            memset(p2v(pa), 0, PAGE)
            return pa
        w += 1
    if frame_hint != 0:
        frame_hint = 0
        return frame_alloc()
    panic("out of physical memory")
    return 0

def frame_free(pa: u64):
    f = pa / PAGE
    if frame_used(f) == 0:
        panic("double free of frame")
    frame_clr(f)
    frames_free += 1
    if f < frame_hint:
        frame_hint = f

# ---- paging ----

def pt_next(table: u64, idx: u64, create: u64) -> u64:
    e = p2v(table) + idx * 8
    v = vload64(e)
    if (v & PTE_P) == 0:
        if create == 0:
            return 0
        nf = frame_alloc()
        vstore64(e, nf | PTE_P | PTE_W)
        return nf
    if v & PTE_PS:
        panic("pt_next: huge page in the way")
    return v & ADDR_MASK

def pte_addr(pml4: u64, va: u64, create: u64) -> u64:
    # virtual address of the level-1 entry for va (0 if missing and !create)
    t = pt_next(pml4, (va >> 39) & 511, create)
    if t == 0:
        return 0
    t = pt_next(t, (va >> 30) & 511, create)
    if t == 0:
        return 0
    t = pt_next(t, (va >> 21) & 511, create)
    if t == 0:
        return 0
    return p2v(t) + ((va >> 12) & 511) * 8

def map_page(pml4: u64, va: u64, pa: u64, flags: u64):
    vstore64(pte_addr(pml4, va, 1), (pa & ADDR_MASK) | flags | PTE_P)

def map_2m(pml4: u64, va: u64, pa: u64, flags: u64):
    t = pt_next(pml4, (va >> 39) & 511, 1)
    t = pt_next(t, (va >> 30) & 511, 1)
    vstore64(p2v(t) + ((va >> 21) & 511) * 8, pa | flags | PTE_P | PTE_PS)

def unmap_page(pml4: u64, va: u64) -> u64:
    e = pte_addr(pml4, va, 0)
    if e == 0:
        return 0
    v = vload64(e)
    vstore64(e, 0)
    invlpg(va)
    return v & ADDR_MASK

def virt_to_phys(pml4: u64, va: u64) -> u64:
    # walks all levels honouring 1G/2M pages; returns 1 (odd, never a real result) when unmapped
    t = pml4
    lvl = 3
    while lvl >= 0:
        sh = 12 + 9 * u64(lvl)
        v = vload64(p2v(t) + ((va >> sh) & 511) * 8)
        if (v & PTE_P) == 0:
            return 1
        if lvl == 0 or (v & PTE_PS):
            m = (u64(1) << sh) - 1
            return (v & ADDR_MASK & ~m) | (va & m)
        t = v & ADDR_MASK
        lvl -= 1
    return 1

def map_kernel_range(pml4: u64, start: u64, end: u64, flags: u64):
    va = start
    while va < end:
        map_page(pml4, va, kv2p(va), flags | PTE_G)
        va += PAGE

def map_stack(pml4: u64, top: u64, pages: u64):
    # pages below `top`; the page under them stays unmapped as a guard
    i = 1
    while i <= pages:
        map_page(pml4, top - i * PAGE, frame_alloc(), PTE_W | PTE_NX | PTE_G)
        i += 1

def is_mmio_2m(pa: u64) -> u64:
    return pa == 0xFEC00000 or pa == 0xFEE00000

def paging_init(fb_phys: u64, fb_len: u64) -> u64:
    # enable NX + global pages
    wrmsr(0xC0000080, rdmsr(0xC0000080) | 0x800)
    write_cr4(read_cr4() | 0x80)
    pml4 = frame_alloc()
    # HHDM: all RAM and the 32-bit MMIO hole, 2 MiB pages, never executable
    top = phys_top
    if top < 0x100000000:
        top = 0x100000000
    pa = 0
    while pa < top:
        fl = PTE_W | PTE_NX | PTE_G
        if is_mmio_2m(pa):
            fl = fl | PTE_PCD | PTE_PWT
        map_2m(pml4, HHDM + pa, pa, fl)
        pa += 0x200000
    if fb_len != 0 and fb_phys + fb_len > top:
        pa = fb_phys & ~u64(0x1FFFFF)
        while pa < fb_phys + fb_len:
            map_2m(pml4, HHDM + pa, pa, PTE_W | PTE_NX | PTE_G)
            pa += 0x200000
    # kernel image with per-section permissions (W^X)
    map_kernel_range(pml4, ksym(0), ksym(1), 0)
    map_kernel_range(pml4, ksym(2), ksym(3), PTE_NX)
    map_kernel_range(pml4, ksym(4), ksym(5), PTE_W | PTE_NX)
    map_stack(pml4, KSTACK_TOP, KSTACK_PAGES)
    map_stack(pml4, IST1_TOP, IST_PAGES)
    map_stack(pml4, IST2_TOP, IST_PAGES)
    return pml4

def paging_activate(pml4: u64):
    # no identity map from here on: physical memory is only reachable through the HHDM
    write_cr3(pml4)
    kernel_pml4 = pml4
    p2v_base = HHDM
    frame_limit = MAX_FRAMES
    if phys_top / PAGE < frame_limit:
        frame_limit = (phys_top + PAGE - 1) / PAGE

def demand_fault(va: u64) -> u64:
    # #PF hook: lazily back the demand region with zeroed frames
    if va < DEMAND_BASE or va >= DEMAND_BASE + DEMAND_SIZE or kernel_pml4 == 0:
        return 0
    map_page(kernel_pml4, va & ~u64(PAGE - 1), frame_alloc(), PTE_W | PTE_NX)
    demand_faults += 1
    return 1

# ---- kernel heap ----
# Every block has a 16-byte header: [class index or 0x8000.. | npages][magic]. Small classes 32..4096 bytes
# (incl. header) come from slabs carved out of HHDM frames; bigger requests get fresh virtual pages.

NCLASS = 8
HEAP_MAGIC = 0x4B4D414C4C4F4321
slab_free: u64[8]
heap_big_next: u64
heap_live: u64

def class_size(c: u64) -> u64:
    return u64(32) << c

def kmalloc(n: u64) -> u64:
    need = n + 16
    c = 0
    while c < NCLASS and class_size(c) < need:
        c += 1
    if c < NCLASS:
        if slab_free[c] == 0:
            pg = p2v(frame_alloc())
            sz = class_size(c)
            off = 0
            while off + sz <= PAGE:
                vstore64(pg + off + 16, slab_free[c])
                slab_free[c] = pg + off + 16
                off += sz
        p = slab_free[c]
        slab_free[c] = vload64(p)
        vstore64(p - 16, c)
        vstore64(p - 8, HEAP_MAGIC)
        heap_live += 1
        return p
    if heap_big_next == 0:
        heap_big_next = HEAP_BIG_BASE
    np = (need + PAGE - 1) / PAGE
    base = heap_big_next
    heap_big_next += (np + 1) * PAGE
    i = 0
    while i < np:
        map_page(kernel_pml4, base + i * PAGE, frame_alloc(), PTE_W | PTE_NX)
        i += 1
    vstore64(base, 0x8000000000000000 | np)
    vstore64(base + 8, HEAP_MAGIC)
    heap_live += 1
    return base + 16

def kfree(p: u64):
    if p == 0:
        return
    if vload64(p - 8) != HEAP_MAGIC:
        panic("kfree: bad pointer or heap corruption")
    h = vload64(p - 16)
    vstore64(p - 8, 0)
    heap_live -= 1
    if h & 0x8000000000000000:
        np = h & 0xFFFFFFFF
        base = p - 16
        i = 0
        while i < np:
            frame_free(unmap_page(kernel_pml4, base + i * PAGE))
            i += 1
        return
    vstore64(p, slab_free[h])
    slab_free[h] = p

# ---- reclaim boot-time memory ----
# Called once ACPI has been parsed and the kernel runs on its own page tables: the loader's image, its page
# tables and BootInfo (LoaderCode/LoaderData), ACPI reclaimable tables and the multiboot2 info block are
# no longer referenced. The kernel image itself (also LoaderData under forgex64.efi) and the low 1 MiB stay.
reclaimed_loader: u64
reclaimed_acpi: u64

def reclaim_range(base: u64, len: u64) -> u64:
    kstart = ksym(6) + kdelta
    kend = ksym(7) + kdelta
    f = (base + PAGE - 1) / PAGE
    end = (base + len) / PAGE
    n: u64 = 0
    while f < end and f < MAX_FRAMES:
        pa = f * PAGE
        if pa >= 0x100000 and (pa + PAGE <= kstart or pa >= kend) and frame_used(f):
            frame_clr(f)
            frames_free += 1
            frames_total += 1
            n += 1
        f += 1
    return n

def pmm_reclaim(bi: ptr[BootInfo]):
    i = 0
    while i < bi.nregions:
        r = addr(bi.regions[i])
        if r.typ == MEM_LOADER:
            reclaimed_loader += reclaim_range(r.base, r.len)
            r.typ = MEM_USABLE
        elif r.typ == MEM_ACPI_RECLAIM:
            reclaimed_acpi += reclaim_range(r.base, r.len)
            r.typ = MEM_USABLE
        i += 1
    if bi.info_len != 0:
        reclaimed_loader += reclaim_range(bi.info_base, bi.info_len)
        bi.info_len = 0
    bi.rsdp = 0

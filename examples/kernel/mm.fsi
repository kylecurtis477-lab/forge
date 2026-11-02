# Physical frame allocator (bitmap + buddy free maps), 4-level paging (HHDM + per-section kernel mappings + guarded stacks),
# demand paging region, and the kernel heap (slab size classes + page-backed large blocks).
PAGE = 4096
KBASE = 0xFFFFFFFF80000000
HHDM = 0xFFFF800000000000
DEMAND_BASE = 0xFFFF900000000000
DEMAND_SIZE = 0x1000000
HEAP_BIG_BASE = 0xFFFFC00000000000
KSTACK_TOP = 0xFFFFFFFFC0040000
KSTACK_PAGES = 16
KSTACKS_BASE = 0xFFFFC04000000000  # kernel thread stacks: same PML4 slot as the big heap, bump VA, guard page below each
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
    "ksyms: .quad __text_start, __text_end, __rodata_start, __rodata_end, __data_start, __data_end, __kernel_phys_start, __kernel_phys_end, __krel_start, __krel_end",
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
kslide: u64
krel_count: u64
p2v_base: u64
kernel_pml4: u64
demand_faults: u64

def p2v(pa: u64) -> u64:
    return pa + p2v_base

def kv2p(va: u64) -> u64:
# both image aliases (KBASE while booting, KBASE + kslide after self-relocation) map to the same frames;
# kslide >= 2 MiB exceeds the image, so the two never overlap
    if kslide != 0 and va >= KBASE + kslide + 0x100000:
        return va - KBASE - kslide + kdelta
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

@unsafe
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
    buddy_build()

# ---- buddy allocator (Phase 8) ----
# frame_bits stays the authoritative used/free map; on top of it, per-order free-block bitmaps (orders 0..10,
# 4 KiB .. 4 MiB) give O(order) splitting/coalescing. Every block taken from a free map is re-validated against
# frame_bits, so code that edits frame_bits directly (kexec grab, boot reserves) can only leave stale entries
# that get dropped, never a double allocation. If the maps run dry they are rebuilt from frame_bits once.
BUDDY_MAX = 10
BNONE = 0xFFFFFFFFFFFFFFFF
bfree: u64[131072]
bcount: u64[11]
bhint: u64[11]
buddy_splits: u64
buddy_merges: u64
buddy_rebuilds: u64

def boff(k: u64) -> u64:
    return 131072 - (131072 >> k)

def bbit_get(k: u64, b: u64) -> u64:
    return (bfree[boff(k) + (b >> 6)] >> (b & 63)) & 1

def bbit_set(k: u64, b: u64):
    if bbit_get(k, b) == 0:
        w = boff(k) + (b >> 6)
        bfree[w] = bfree[w] | (u64(1) << (b & 63))
        bcount[k] += 1
        if (b >> 6) < bhint[k]:
            bhint[k] = b >> 6

def bbit_clr(k: u64, b: u64):
    if bbit_get(k, b) != 0:
        w = boff(k) + (b >> 6)
        bfree[w] = bfree[w] & ~(u64(1) << (b & 63))
        bcount[k] -= 1

def run_free(f: u64, n: u64) -> u64:
    j: u64 = 0
    while j < n:
        if ((f + j) & 63) == 0 and n - j >= 64:
            if frame_bits[(f + j) >> 6] != 0:
                return 0
            j += 64
        else:
            if frame_used(f + j):
                return 0
            j += 1
    return 1

def buddy_insert(f: u64, k: u64):
# frames f .. f+2^k-1 are already free in frame_bits: coalesce with free buddies, then publish
    while k < BUDDY_MAX:
        b = (f >> k) ^ 1
        if bbit_get(k, b) == 0:
            break
        bbit_clr(k, b)
        if run_free(b << k, u64(1) << k) == 0:
            break
        buddy_merges += 1
        f = f & ~((u64(1) << (k + 1)) - 1)
        k += 1
    bbit_set(k, f >> k)

def bfind(k: u64) -> u64:
    words = 65536 >> k
    w = bhint[k]
    o = boff(k)
    while w < words:
        x = bfree[o + w]
        if x != 0:
            bhint[k] = w
            i: u64 = 0
            while ((x >> i) & 1) == 0:
                i += 1
            return w * 64 + i
        w += 1
    bhint[k] = words
    return BNONE

def buddy_build():
    i: u64 = 0
    while i < 131072:
        bfree[i] = 0
        i += 1
    k: u64 = 0
    while k <= BUDDY_MAX:
        bcount[k] = 0
        bhint[k] = 0
        k += 1
    f: u64 = 0
    while f < MAX_FRAMES:
        if frame_bits[f >> 6] == 0xFFFFFFFFFFFFFFFF and (f & 63) == 0:
            f += 64
        elif frame_used(f):
            f += 1
        else:
            k = BUDDY_MAX
            while k > 0 and ((f & ((u64(1) << k) - 1)) != 0 or run_free(f, u64(1) << k) == 0):
                k -= 1
            bbit_set(k, f >> k)
            f += u64(1) << k

def buddy_take(k: u64, lim: u64) -> u64:
# lowest free block of order k whose frames all lie below frame `lim`; marks them used. BNONE if none.
    j = k
    while j <= BUDDY_MAX:
        b = BNONE
        if bcount[j] != 0:
            b = bfind(j)
        while b != BNONE:
            f = b << j
            if f + (u64(1) << j) > lim:
                b = BNONE
                break
            bbit_clr(j, b)
            if run_free(f, u64(1) << j):
                while j > k:
                    j -= 1
                    bbit_set(j, (f >> j) + 1)
                    buddy_splits += 1
                n = u64(1) << k
                i: u64 = 0
                while i < n:
                    frame_set(f + i)
                    i += 1
                frames_free -= n
                return f
            b = BNONE
            if bcount[j] != 0:
                b = bfind(j)
        j += 1
    return BNONE

def buddy_take_retry(k: u64, lim: u64) -> u64:
    f = buddy_take(k, lim)
    if f == BNONE:
        buddy_rebuilds += 1
        buddy_build()
        f = buddy_take(k, lim)
    return f

def frame_alloc() -> u64:
    f = irq_save()
    spin_lock(addr(pmm_lock))
    pa = frame_alloc_nolock()
    spin_unlock(addr(pmm_lock))
    irq_restore(f)
    return pa

@unsafe
def frame_alloc_nolock() -> u64:
    f = buddy_take_retry(0, frame_limit)
    if f == BNONE:
        panic("out of physical memory")
        return 0
    pa = f * PAGE
    memset(p2v(pa), 0, PAGE)
    return pa

@unsafe
def frame_alloc_contig(n: u64) -> u64:
# n physically contiguous zeroed frames below 4 GiB (DMA rings), aligned to the next power of two; 0 if none
    if n == 0 or n > (u64(1) << BUDDY_MAX):
        return 0
    k: u64 = 0
    while (u64(1) << k) < n:
        k += 1
    f = irq_save()
    spin_lock(addr(pmm_lock))
    lim = frame_limit
    if lim > 0x100000000 / PAGE:
        lim = 0x100000000 / PAGE
    res = buddy_take_retry(k, lim)
    if res != BNONE:
    # hand the unused tail of the power-of-two block straight back
        i = n
        while i < (u64(1) << k):
            frame_clr(res + i)
            frames_free += 1
            buddy_insert(res + i, 0)
            i += 1
    spin_unlock(addr(pmm_lock))
    irq_restore(f)
    if res == BNONE:
        return 0
    memset(p2v(res * PAGE), 0, n * PAGE)
    return res * PAGE

def frame_free(pa: u64):
    fl = irq_save()
    spin_lock(addr(pmm_lock))
    frame_free_nolock(pa)
    spin_unlock(addr(pmm_lock))
    irq_restore(fl)

def frame_free_nolock(pa: u64):
    f = pa / PAGE
    if frame_used(f) == 0:
        panic("double free of frame")
    frame_clr(f)
    frames_free += 1
    buddy_insert(f, 0)

# ---- paging ----

@unsafe
def pt_next(table: u64, idx: u64, create: u64) -> u64:
    e = p2v(table) + idx * 8
    v = vload64(e)
    if (v & PTE_P) == 0:
        if create == 0:
            return 0
        nf = frame_alloc()
        if create == 2:
            atomic_fetch_add(addr(kpt_pages), 1, acq_rel)
        vstore64(e, nf | PTE_P | PTE_W)
        return nf
    if v & PTE_PS:
        panic("pt_next: huge page in the way")
    return v & ADDR_MASK

def pte_addr(pml4: u64, va: u64, create: u64) -> u64:
# virtual address of the level-1 entry for va (0 if missing and !create)
    if create != 0 and pml4 == kernel_pml4:
        create = 2
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

@unsafe
def map_page(pml4: u64, va: u64, pa: u64, flags: u64):
    vstore64(pte_addr(pml4, va, 1), (pa & ADDR_MASK) | flags | PTE_P)

@unsafe
def map_2m(pml4: u64, va: u64, pa: u64, flags: u64):
    t = pt_next(pml4, (va >> 39) & 511, 1)
    t = pt_next(t, (va >> 30) & 511, 1)
    vstore64(p2v(t) + ((va >> 21) & 511) * 8, pa | flags | PTE_P | PTE_PS)

@unsafe
def unmap_page(pml4: u64, va: u64) -> u64:
    e = pte_addr(pml4, va, 0)
    if e == 0:
        return 0
    v = vload64(e)
    vstore64(e, 0)
    invlpg(va)
    return v & ADDR_MASK

@unsafe
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
# the image at its link address and, for KASLR, again at link address + kslide (same frames)
    va = start
    while va < end:
        map_page(pml4, va, kv2p(va), flags | PTE_G)
        if kslide != 0:
            map_page(pml4, va + kslide, kv2p(va), flags | PTE_G)
        va += PAGE

def map_stack(pml4: u64, top: u64, pages: u64):
# pages below `top`; the page under them stays unmapped as a guard
    i = 1
    while i <= pages:
        map_page(pml4, top - i * PAGE, frame_alloc(), PTE_W | PTE_NX | PTE_G)
        i += 1

def is_mmio_2m(pa: u64) -> u64:
    return pa == 0xFEC00000 or pa == 0xFEE00000

@unsafe
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
    map_kernel_range(pml4, ksym(8), ksym(9), PTE_NX)
    map_stack(pml4, KSTACK_TOP, KSTACK_PAGES)
    map_stack(pml4, IST1_TOP, IST_PAGES)
    map_stack(pml4, IST2_TOP, IST_PAGES)
    # pre-create every kernel-half PML4 entry so per-process PML4 copies always see new kernel mappings
    i = 256
    while i < 512:
        pt_next(pml4, i, 1)
        i += 1
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
kpt_pages: u64  # kernel page-table pages: created on demand, never freed (the kernel half is shared)
slab_pages: u64  # slab pages are kept for reuse, never returned to the frame allocator

# ---- vmalloc: virtual range allocator for the big heap (Phase 8) ----
# Freed ranges go to a sorted, coalescing free list (first fit), so big-heap VA is reused instead of only bumped.
# Each range carries a never-mapped guard page at its top. If the list is full a range is dropped (VA leak only).
VA_SLOTS = 256
va_base: u64[256]
va_npg: u64[256]
va_n: u64
va_reused: u64
va_hiwater: u64

def va_alloc(np: u64) -> u64:
    i: u64 = 0
    while i < va_n:
        if va_npg[i] >= np:
            b = va_base[i]
            va_base[i] = b + np * PAGE
            va_npg[i] -= np
            if va_npg[i] == 0:
                j = i
                while j + 1 < va_n:
                    va_base[j] = va_base[j + 1]
                    va_npg[j] = va_npg[j + 1]
                    j += 1
                va_n -= 1
            va_reused += 1
            return b
        i += 1
    if heap_big_next == 0:
        heap_big_next = HEAP_BIG_BASE
    b = heap_big_next
    heap_big_next += np * PAGE
    va_hiwater += np
    return b

def va_free(b: u64, np: u64):
    i: u64 = 0
    while i < va_n and va_base[i] < b:
        i += 1
    if i > 0 and va_base[i - 1] + va_npg[i - 1] * PAGE == b:
        va_npg[i - 1] += np
        if i < va_n and b + np * PAGE == va_base[i]:
            va_npg[i - 1] += va_npg[i]
            j = i
            while j + 1 < va_n:
                va_base[j] = va_base[j + 1]
                va_npg[j] = va_npg[j + 1]
                j += 1
            va_n -= 1
        return
    if i < va_n and b + np * PAGE == va_base[i]:
        va_base[i] = b
        va_npg[i] += np
        return
    if va_n == VA_SLOTS:
        return
    j = va_n
    while j > i:
        va_base[j] = va_base[j - 1]
        va_npg[j] = va_npg[j - 1]
        j -= 1
    va_base[i] = b
    va_npg[i] = np
    va_n += 1

def va_free_pages() -> u64:
    t: u64 = 0
    i: u64 = 0
    while i < va_n:
        t += va_npg[i]
        i += 1
    return t

def class_size(c: u64) -> u64:
    return u64(32) << c

def kmalloc(n: u64) -> u64:
    f = irq_save()
    spin_lock(addr(heap_lock))
    p = kmalloc_nolock(n)
    spin_unlock(addr(heap_lock))
    irq_restore(f)
    return p

@unsafe
def kmalloc_nolock(n: u64) -> u64:
    need = n + 16
    c = 0
    while c < NCLASS and class_size(c) < need:
        c += 1
    if c < NCLASS:
        if slab_free[c] == 0:
            pg = p2v(frame_alloc())
            slab_pages += 1
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
    np = (need + PAGE - 1) / PAGE
    base = va_alloc(np + 1)
    i = 0
    while i < np:
        map_page(kernel_pml4, base + i * PAGE, frame_alloc(), PTE_W | PTE_NX)
        i += 1
    vstore64(base, 0x8000000000000000 | np)
    vstore64(base + 8, HEAP_MAGIC)
    heap_live += 1
    return base + 16

def kfree(p: u64):
    f = irq_save()
    spin_lock(addr(heap_lock))
    kfree_nolock(p)
    spin_unlock(addr(heap_lock))
    irq_restore(f)

@unsafe
def kfree_nolock(p: u64):
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
        if np > 512:
            i = 0
            while i < np:
                frame_free(unmap_page(kernel_pml4, base + i * PAGE))
                tlb_shootdown(0, base + i * PAGE, 1)
                i += 1
            va_free(base, np + 1)
            return
        # unmap everything, shoot down stale translations on all CPUs, only then reuse the frames
        # (the frame list is parked in the first frame, reached through the HHDM)
        f0 = unmap_page(kernel_pml4, base)
        i = 1
        while i < np:
            vstore64(p2v(f0) + i * 8, unmap_page(kernel_pml4, base + i * PAGE))
            i += 1
        tlb_shootdown(0, base, np)
        i = 1
        while i < np:
            frame_free(vload64(p2v(f0) + i * 8))
            i += 1
        frame_free(f0)
        va_free(base, np + 1)
        return
    vstore64(p, slab_free[h])
    slab_free[h] = p

kstack_next: u64
kstack_guard_faults: u64

def kstack_alloc(np: u64) -> u64:
# np mapped pages above one never-mapped guard page; the VA range is never reused
    f = irq_save()
    spin_lock(addr(heap_lock))
    if kstack_next == 0:
        kstack_next = KSTACKS_BASE
    base = kstack_next + PAGE
    kstack_next += (np + 1) * PAGE
    i = 0
    while i < np:
        map_page(kernel_pml4, base + i * PAGE, frame_alloc(), PTE_W | PTE_NX)
        i += 1
    spin_unlock(addr(heap_lock))
    irq_restore(f)
    return base

def kstack_free(base: u64, np: u64):
    i = 0
    while i < np:
        fr = unmap_page(kernel_pml4, base + i * PAGE)
        tlb_shootdown(0, base + i * PAGE, 1)
        frame_free(fr)
        i += 1

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
            buddy_insert(f, 0)
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
        # ACPI reclaimable tables stay: a kexec successor parses them again
        i += 1
    if bi.info_len != 0:
        reclaimed_loader += reclaim_range(bi.info_base, bi.info_len)
        bi.info_len = 0
    bi.rsdp = 0

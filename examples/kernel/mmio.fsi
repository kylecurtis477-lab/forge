# Phase 8: checked device access. Device memory (BAR windows mapped uncached through mmio_map, DMA
# buffers from dma_alloc) and I/O port ranges are registered once; mmio_rd/mmio_wr and pio_rd/pio_wr
# refuse (panic) any access that is not wholly inside a registered window, so driver code needs no
# unsafe operations of its own. These four accessors are the only raw device access the drivers use.
MMIO_MAX = 48
PIO_MAX = 16
mmio_va: u64[48]
mmio_len: u64[48]
mmio_n: u64
pio_lo: u64[16]
pio_len: u64[16]
pio_n: u64
mmio_denied: u64

def mmio_add(va: u64, len: u64) -> u64:
    if mmio_ok(va, 1) and mmio_ok(va + len - 1, 1):
        return va
    if mmio_n >= MMIO_MAX or len == 0:
        panic("mmio: window table full")
    mmio_va[mmio_n] = va
    mmio_len[mmio_n] = len
    mmio_n += 1
    return va

def mmio_map(pa: u64, len: u64) -> u64:
# map a device window uncached (PCD|PWT), NX, in the kernel's virtual range allocator; 0 on failure
    off = pa & (PAGE - 1)
    np = (off + len + PAGE - 1) / PAGE
    va = va_alloc(np)
    if va == 0:
        return 0
    i: u64 = 0
    while i < np:
        map_page(kernel_pml4, va + i * PAGE, (pa & ~(PAGE - 1)) + i * PAGE, PTE_W | PTE_PCD | PTE_PWT | PTE_NX)
        i += 1
    return mmio_add(va + off, len)

def dma_alloc(np: u64, phys_out: ptr[u64]) -> u64:
# physically contiguous, zeroed, registered for mmio_rd/mmio_wr; returns the kernel virtual address
    pa = frame_alloc_contig(np)
    if pa == 0:
        return 0
    va = mmio_add(p2v(pa), np * PAGE)
    i: u64 = 0
    while i < np * PAGE:
        mmio_wr(va + i, 8, 0)
        i += 8
    phys_out[0] = pa
    return va

def pio_add(lo: u64, len: u64):
    if pio_ok(lo, len):
        return
    if pio_n >= PIO_MAX:
        panic("pio: range table full")
    pio_lo[pio_n] = lo
    pio_len[pio_n] = len
    pio_n += 1

def mmio_ok(a: u64, w: u64) -> u64:
    if (a & (w - 1)) != 0:
        return 0
    i: u64 = 0
    while i < mmio_n:
        if a >= mmio_va[i] and a + w <= mmio_va[i] + mmio_len[i]:
            return 1
        i += 1
    return 0

def pio_ok(p: u64, w: u64) -> u64:
    i: u64 = 0
    while i < pio_n:
        if p >= pio_lo[i] and p + w <= pio_lo[i] + pio_len[i]:
            return 1
        i += 1
    return 0

def mmio_refuse(a: u64):
    mmio_denied += 1
    puts("device access outside a registered window: ")
    put_hex(a)
    putc(10)
    panic("mmio/pio window check failed")

@unsafe
def mmio_rd(a: u64, w: u64) -> u64:
    if (w != 1 and w != 2 and w != 4 and w != 8) or mmio_ok(a, w) == 0:
        mmio_refuse(a)
    if w == 1:
        return u64(vload8(a))
    if w == 2:
        return u64(vload16(a))
    if w == 4:
        return u64(vload32(a))
    return vload64(a)

@unsafe
def mmio_wr(a: u64, w: u64, v: u64):
    if (w != 1 and w != 2 and w != 4 and w != 8) or mmio_ok(a, w) == 0:
        mmio_refuse(a)
    if w == 1:
        vstore8(a, u8(v & 0xFF))
    elif w == 2:
        vstore16(a, u16(v & 0xFFFF))
    elif w == 4:
        vstore32(a, u32(v & 0xFFFFFFFF))
    else:
        vstore64(a, v)

@unsafe
def pio_rd(p: u64, w: u64) -> u64:
    if (w != 1 and w != 2 and w != 4) or pio_ok(p, w) == 0:
        mmio_refuse(p)
    if w == 1:
        return u64(inb(u16(p)))
    if w == 2:
        return u64(inw(u16(p)))
    return u64(inl(u16(p)))

@unsafe
def pio_wr(p: u64, w: u64, v: u64):
    if (w != 1 and w != 2 and w != 4) or pio_ok(p, w) == 0:
        mmio_refuse(p)
    if w == 1:
        outb(u16(p), u8(v & 0xFF))
    elif w == 2:
        outw(u16(p), u16(v & 0xFFFF))
    else:
        outl(u16(p), u32(v & 0xFFFFFFFF))

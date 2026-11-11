# Console: COM1 serial always; GOP/VBE linear framebuffer text (8x16 font) once mapped; VGA text as fallback.
include "font.fsi"

COM1 = 0x3F8

fb_base: u64
fb_w: u64
fb_h: u64
fb_pitch: u64
fb_cols: u64
fb_rows: u64
fb_col: u64
fb_row: u64
vga_base: u64
vga_pos: u64
con_fg: u32
# Calibrated in apic.fsi (PIT/HPET); 0 until then. Used here to bound serial_putc.
tsc_per_ms: u64

@unsafe
def serial_init():
    outb(COM1 + 1, 0x00)
    outb(COM1 + 3, 0x80)
    outb(COM1 + 0, 0x03)
    outb(COM1 + 1, 0x00)
    outb(COM1 + 3, 0x03)
    outb(COM1 + 2, 0xC7)
    outb(COM1 + 4, 0x0B)

@unsafe
def serial_putc(c: u8):
# Bound the THRE wait so a backed-up host serial pipe cannot wedge the kernel
# (os/kernel-bios-smp1: post-kexec seal_test spun here until the harness 2x timeout).
# When tsc_per_ms is calibrated, wait up to 10 ms; before that, a spin cap. If THRE
# stays 0, serial_init once and retry; if still stuck, drop the character.
    tries = 0
    while tries < 2:
        t0 = rdtsc()
        spins = 0
        while True:
            if (inb(COM1 + 5) & 0x20) != 0:
                outb(COM1, c)
                return
            if tsc_per_ms != 0 and rdtsc() - t0 >= tsc_per_ms * 10:
                break
            spins += 1
            if tsc_per_ms == 0 and spins >= 10000000:
                break
            cpu_relax()
        if tries == 0:
            serial_init()
        tries += 1

@unsafe
def serial_poll() -> i64:
    if (inb(COM1 + 5) & 1) == 0:
        return -1
    return i64(inb(COM1))

# Double-buffered text console: con_back holds what the screen should show, con_front what was last
# drawn. Writes go to con_back; fb_flush redraws only cells that differ, so scrolling never reads the
# (uncached) framebuffer and redraws only changed cells.
CON_MAX = 32768
con_back: u8[32768]
con_front: u8[32768]
con_scrolls: u64
con_redrawn: u64

@unsafe
def fb_attach(virt: u64, w: u64, h: u64, pitch: u64):
    fb_base = virt
    fb_w = w
    fb_h = h
    fb_pitch = pitch
    fb_cols = w / 8
    fb_rows = h / 16
    while fb_cols * fb_rows > CON_MAX:
        fb_rows -= 1
    fb_col = 0
    fb_row = 0
    con_fg = 0x00FFFFFF
    i = 0
    while i < fb_cols * fb_rows:
        con_back[i] = 32
        con_front[i] = 32
        i += 1
    y = 0
    while y < h:
        x = 0
        while x < w:
            vstore32(virt + y * pitch + x * 4, 0)
            x += 1
        y += 1

@unsafe
def fb_glyph(cx: u64, cy: u64, c: u8):
    g = 0
    if c >= 32 and c <= 126:
        g = u64(c - 32) * 16
    row = 0
    while row < 16:
        bits = font8x16[g + row]
        line = fb_base + (cy * 16 + row) * fb_pitch + cx * 32
        x = 0
        while x < 8:
            px: u32 = 0
            if (bits >> (7 - x)) & 1:
                px = con_fg
            vstore32(line + x * 4, px)
            x += 1
        row += 1

def fb_flush():
    i = 0
    n = fb_cols * fb_rows
    while i < n:
        if con_back[i] != con_front[i]:
            fb_glyph(i % fb_cols, i / fb_cols, con_back[i])
            con_front[i] = con_back[i]
            con_redrawn += 1
        i += 1

def fb_consistent() -> u64:
    i = 0
    while i < fb_cols * fb_rows:
        if con_back[i] != con_front[i]:
            return 0
        i += 1
    return 1

def fb_newline():
    fb_col = 0
    fb_row += 1
    if fb_row >= fb_rows:
        n = (fb_rows - 1) * fb_cols
        i = 0
        while i < n:
            con_back[i] = con_back[i + fb_cols]
            i += 1
        while i < n + fb_cols:
            con_back[i] = 32
            i += 1
        fb_row = fb_rows - 1
        con_scrolls += 1
        fb_flush()

def fb_putc(c: u8):
    if c == 10:
        fb_newline()
        return
    if c == 13:
        return
    if c < 32 or c > 126:
        c = 32
    k = fb_row * fb_cols + fb_col
    con_back[k] = c
    if con_front[k] != c:
        fb_glyph(fb_col, fb_row, c)
        con_front[k] = c
        con_redrawn += 1
    fb_col += 1
    if fb_col >= fb_cols:
        fb_newline()

@unsafe
def vga_putc(c: u8):
    if c == 10:
        vga_pos = (vga_pos / 80 + 1) * 80
    else:
        vstore16(vga_base + vga_pos * 2, u16(c) | 0x0F00)
        vga_pos += 1
    if vga_pos >= 80 * 25:
        vga_pos = 0

def putc(c: u8):
    serial_putc(c)
    if fb_base != 0:
        fb_putc(c)
    elif vga_base != 0:
        vga_putc(c)

@unsafe
def puts(s: ptr[u8]):
    i = 0
    while s[i] != 0:
        putc(s[i])
        i += 1

def put_hex(v: u64):
    puts("0x")
    i = 60
    while i >= 0:
        d = u8((v >> i) & 15)
        if d < 10:
            putc(48 + d)
        else:
            putc(87 + d)
        i -= 4

def put_u64(v: u64):
    buf: u8[24]
    i = 23
    buf[23] = 0
    while 1:
        i -= 1
        buf[i] = u8(48 + v % 10)
        v = v / 10
        if v == 0:
            break
    puts(addr(buf[i]))

def put_sig(v: u32):
    i = 0
    while i < 4:
        putc(u8((v >> (i * 8)) & 0xFF))
        i += 1

@unsafe
def qemu_exit(code: u32):
# isa-debug-exit device (iobase 0xf4): qemu exits with (code << 1) | 1; no-op on real hardware
    outl(0xF4, code)
    while 1:
        cli()
        hlt()

@unsafe
def panic(msg: ptr[u8]):
    cli()
    puts("PANIC: ")
    puts(msg)
    putc(10)
    qemu_exit(0x11)

@unsafe
def forge_bounds_fail(line: u64, idx: u64, n: u64, file: ptr[u8]):
# bounds-checked indexing failed (Phase 4.1): report the source position, then panic
    cli()
    puts("PANIC: bounds check failed: ")
    puts(file)
    putc(58)
    put_u64(line)
    puts(": index ")
    put_u64(idx)
    puts(" out of range for length ")
    put_u64(n)
    putc(10)
    qemu_exit(0x11)

def put_hexn(v: u64, digits: u64):
    i = digits
    while i > 0:
        i -= 1
        d = (v >> (i * 4)) & 15
        if d < 10:
            putc(48 + d)
        else:
            putc(87 + d)

def put_hex8(v: u64):
    put_hexn(v, 2)

def put_hex16(v: u64):
    put_hexn(v, 4)
